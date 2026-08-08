using Caching.NET.Extensions;
using Caching.NET.Tests.Integration.Fixtures;
using StackExchange.Redis;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Redis mode: Redis is authoritative and no instance may serve a value from its own memory.
/// </summary>
[Collection(RedisCollection.Name)]
public class RedisModeTests
{
    private readonly RedisFixture _redis;

    public RedisModeTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private CacheHost Host(string prefix) => CacheHost.Create(cache => cache
        .UseRedis(_redis.ConnectionString)
        .WithApplicationPrefix(prefix)
        .WithJitter(TimeSpan.Zero)
        .WithDefaultExpiration(TimeSpan.FromMinutes(5))
        // Production defaults complete distributed writes in the background. These tests assert on
        // what another instance can see immediately after a write, so the write must be awaited.
        .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

    [Fact]
    public async Task WriteThenRead_RoundTripsThroughRedis()
    {
        using var host = Host("redis-roundtrip");

        await host.Cache.SetAsync("Product:1", new Product(1, "widget"));

        var value = await host.Cache.GetOrDefaultAsync<Product>("Product:1");
        Assert.Equal(new Product(1, "widget"), value);
    }

    [Fact]
    public async Task ValueWrittenByOneInstance_IsVisibleToAnother()
    {
        using var writer = Host("redis-shared");
        using var reader = Host("redis-shared");

        await writer.Cache.SetAsync("Order:9", 99);

        Assert.Equal(99, await reader.Cache.GetOrDefaultAsync<int>("Order:9"));
    }

    [Fact]
    public async Task RemovalOnOneInstance_IsImmediatelyVisibleOnAnother()
    {
        using var first = Host("redis-remove");
        using var second = Host("redis-remove");

        await first.Cache.SetAsync("Order:1", 1);
        Assert.Equal(1, await second.Cache.GetOrDefaultAsync<int>("Order:1"));

        await first.Cache.RemoveAsync("Order:1");

        // No local copy exists to go stale, so the removal is visible without a backplane.
        Assert.False((await second.Cache.TryGetAsync<int>("Order:1")).HasValue);
    }

    [Fact]
    public async Task ReadsAlwaysConsultRedisRatherThanALocalCopy()
    {
        using var host = Host("redis-authoritative");
        await host.Cache.SetAsync("Order:2", 1);

        // Delete the entry behind the cache's back. A memory layer would still answer; Redis mode
        // must not.
        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            var deleted = await connection.GetDatabase().KeyDeleteAsync(
                (await FindKeysAsync(connection, "*redis-authoritative:Order:2*")).Single());
            Assert.True(deleted);
        }

        Assert.False((await host.Cache.TryGetAsync<int>("Order:2")).HasValue);
    }

    [Fact]
    public async Task ConcurrentGetOrSet_StillCoalescesWithoutAMemoryLayer()
    {
        using var host = Host("redis-stampede");
        using var gate = new FactoryGate();

        var callers = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            await host.Cache.GetOrSetAsync<int>("hot", async (_, token) =>
            {
                await gate.EnterAsync(token);
                return 5;
            }))).ToArray();

        var results = await gate.RunAsync(callers);

        Assert.All(results, v => Assert.Equal(5, v));
        var calls = gate.Executions;

        // Not 1. Redis mode bypasses the memory cache, so the engine's post-lock re-check — the step
        // that turns "one lock holder at a time" into "one factory execution" — can never hit, and a
        // second caller runs the factory in the window before the distributed write is visible.
        // Asserting 1 here passed only when the write happened to win that race; it failed as soon
        // as the suite ran under load. The bound is what the mode actually guarantees: coalescing,
        // not single-flight. StampedeScopeTests measures the same thing across all three modes.
        Assert.InRange(calls, 1, 2);
    }

    [Fact]
    public async Task TagInvalidation_WorksAcrossInstances()
    {
        using var first = Host("redis-tags");
        using var second = Host("redis-tags");

        await first.Cache.SetAsync("Product:1", 1, tags: ["category:tools"]);
        await first.Cache.SetAsync("Product:2", 2, tags: ["category:toys"]);

        await second.Cache.RemoveByTagAsync("category:tools");

        Assert.False((await first.Cache.TryGetAsync<int>("Product:1")).HasValue);
        Assert.True((await first.Cache.TryGetAsync<int>("Product:2")).HasValue);
    }

    /// <summary>
    /// The case <see cref="TagInvalidation_WorksAcrossInstances"/> cannot see: the reading instance
    /// has already read the key <b>before</b> the invalidation.
    /// </summary>
    /// <remarks>
    /// Tag invalidation is implemented by the engine as a marker entry that every read compares
    /// itself against. Those markers are ordinary cache entries, so unless Redis mode's
    /// skip-the-memory-layer rule is applied to them too, the first read caches "no marker exists"
    /// in-process — for the marker's own 10-day default lifetime — and every later read on that
    /// instance answers from that stale copy. Redis mode registers no backplane (the validator
    /// rejects one), so nothing would ever evict it: <c>RemoveByTag</c> would be silently
    /// unobservable on any instance that had served the key once, which is every instance under real
    /// traffic. Asserting from a warm reader is what makes the mode's "Redis is authoritative"
    /// promise cover invalidation and not just reads.
    /// </remarks>
    [Fact]
    public async Task TagInvalidation_IsSeenByAnInstanceThatAlreadyReadTheKey()
    {
        using var writer = Host("redis-tags-warm");
        using var reader = Host("redis-tags-warm");

        await writer.Cache.SetAsync("Product:1", 1, tags: ["category:tools"]);
        await writer.Cache.SetAsync("Product:2", 2, tags: ["category:toys"]);

        // Warms the reader: this is the read that used to pin a stale marker.
        Assert.True((await reader.Cache.TryGetAsync<int>("Product:1")).HasValue);
        Assert.True((await reader.Cache.TryGetAsync<int>("Product:2")).HasValue);

        await writer.Cache.RemoveByTagAsync("category:tools");

        Assert.False((await reader.Cache.TryGetAsync<int>("Product:1")).HasValue);
        Assert.True((await reader.Cache.TryGetAsync<int>("Product:2")).HasValue);
    }

    /// <summary>
    /// <see cref="ClearOnOneApplication_LeavesAnotherApplicationsEntriesAlone"/> for a warm reader.
    /// </summary>
    /// <remarks>
    /// <c>Clear</c> uses the same marker mechanism as <c>RemoveByTag</c> under a reserved tag, so it
    /// fails in exactly the same way and for exactly the same reason. See
    /// <see cref="TagInvalidation_IsSeenByAnInstanceThatAlreadyReadTheKey"/>.
    /// </remarks>
    [Fact]
    public async Task Clear_IsSeenByAnInstanceThatAlreadyReadTheKey()
    {
        using var writer = Host("redis-clear-warm");
        using var reader = Host("redis-clear-warm");

        await writer.Cache.SetAsync("k", 1);
        Assert.True((await reader.Cache.TryGetAsync<int>("k")).HasValue);

        await writer.Cache.ClearAsync();

        Assert.False((await reader.Cache.TryGetAsync<int>("k")).HasValue);
    }

    [Fact]
    public async Task ApplicationPrefix_IsolatesApplicationsSharingOneRedisDatabase()
    {
        using var appOne = Host("redis-iso-one");
        using var appTwo = Host("redis-iso-two");

        await appOne.Cache.SetAsync("shared", "one");
        await appTwo.Cache.SetAsync("shared", "two");

        Assert.Equal("one", await appOne.Cache.GetOrDefaultAsync<string>("shared"));
        Assert.Equal("two", await appTwo.Cache.GetOrDefaultAsync<string>("shared"));
    }

    [Fact]
    public async Task ClearOnOneApplication_LeavesAnotherApplicationsEntriesAlone()
    {
        using var appOne = Host("redis-clear-one");
        using var appTwo = Host("redis-clear-two");

        await appOne.Cache.SetAsync("k", 1);
        await appTwo.Cache.SetAsync("k", 2);

        await appOne.Cache.ClearAsync();

        Assert.False((await appOne.Cache.TryGetAsync<int>("k")).HasValue);
        Assert.Equal(2, await appTwo.Cache.GetOrDefaultAsync<int>("k"));
    }

    [Fact]
    public async Task CorruptRedisPayload_IsTreatedAsAMissAndOverwritten()
    {
        using var host = Host("redis-corrupt");
        await host.Cache.SetAsync("Order:5", 1);

        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            var key = (await FindKeysAsync(connection, "*redis-corrupt:Order:5*")).Single();
            await connection.GetDatabase().StringSetAsync(key, "not-a-caching-net-payload");
        }

        var rebuilt = await host.Cache.GetOrSetAsync<int>("Order:5", async (_, _) => 42);

        Assert.Equal(42, rebuilt);
    }

    [Fact]
    public async Task OversizedValue_ReachesTheCallerWhenTheDistributedWriteIsNotBackgrounded()
    {
        // The other half of the oversized-value story, and the dangerous half. With background
        // distributed operations off — the setting a read-your-writes deployment picks, and the one
        // every other test in this class uses — the serializer runs on the caller's path and the
        // engine's foreground distributed write does not honour ReThrowSerializationExceptions:
        // false. The payload guard therefore fails the request instead of quietly not caching.
        //
        // This is engine behaviour Caching.NET cannot intercept without wrapping every cache call,
        // so it is pinned here, warned about at startup, and documented — rather than left to be
        // discovered as a 500 in production.
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("redis-oversized-fg")
            .WithMaximumPayloadBytes(512)
            .WithResilience(r =>
            {
                r.AllowBackgroundDistributedOperations = false;
                r.ThrowOnSerializationErrors = false;
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await host.Cache.SetAsync("Big:2", new string('x', 50_000)));

        Assert.Contains("MaximumPayloadBytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedValue_IsNotWrittenToRedisAndDoesNotFailTheCaller()
    {
        // Background distributed operations on (the default): the write happens off the caller's
        // path, so the guard degrades to "not cached" as documented.
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("redis-oversized")
            .WithMaximumPayloadBytes(512));

        var big = new string('x', 50_000);

        // The caller still gets its value: an oversized entry degrades to "not cached", it does not
        // fail the request.
        var value = await host.Cache.GetOrSetAsync<string>("Big:1", async (_, _) => big);
        Assert.Equal(big, value);

        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            Assert.Empty(await FindKeysAsync(connection, "*redis-oversized:Big:1*"));
        }
    }

    [Fact]
    public async Task CompressionEnabled_StillRoundTrips()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("redis-compressed")
            .WithMaximumPayloadBytes(10_000_000)
            .WithCompression(thresholdBytes: 128));

        var payload = new Product(1, new string('z', 200_000));

        // Foreground write. AllowBackgroundDistributedOperations is on by default, so SetAsync can
        // return before a 200 KB Brotli-compressed payload has actually reached Redis — and Redis
        // mode keeps no L1, so the read below then legitimately misses and returns null. Observed
        // flaking roughly once in eight full-solution runs, and only here: this test has by far the
        // largest payload in the suite, so it is the only one whose write is slow enough for the
        // window to open. Awaiting the distributed write makes the round trip — which is the thing
        // under test — deterministic, without weakening the assertion.
        await host.Cache.SetAsync(
            "Big:2",
            payload,
            new Options.CacheEntryOverrides { AllowBackgroundDistributedOperations = false });

        Assert.Equal(payload, await host.Cache.GetOrDefaultAsync<Product>("Big:2"));
    }

    [Fact]
    public async Task MessagePackFormat_RoundTrips()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("redis-messagepack")
            .WithMessagePackSerialization());

        await host.Cache.SetAsync("Product:7", new Product(7, "binary"));

        Assert.Equal(new Product(7, "binary"), await host.Cache.GetOrDefaultAsync<Product>("Product:7"));
    }

    [Fact]
    public async Task RedisDatabaseSelection_IsolatesEntries()
    {
        using var db0 = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("redis-db")
            .WithRedis(r => r.Database = 0));

        using var db1 = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("redis-db")
            .WithRedis(r => r.Database = 1));

        await db0.Cache.SetAsync("same-key", "zero");

        Assert.False((await db1.Cache.TryGetAsync<string>("same-key")).HasValue);
        Assert.Equal("zero", await db0.Cache.GetOrDefaultAsync<string>("same-key"));
    }

    [Fact]
    public async Task NamedCaches_AreIsolatedOnSharedRedis()
    {
        using var host = CacheHost.CreateMulti(services =>
        {
            services.AddCaching(cache => cache
                .UseRedis(_redis.ConnectionString)
                .WithApplicationPrefix("redis-named")
                .WithResilience(r => r.AllowBackgroundDistributedOperations = false));
            services.AddCaching("short-lived", cache => cache
                .UseRedis(_redis.ConnectionString)
                .WithApplicationPrefix("redis-named")
                .WithDefaultExpiration(TimeSpan.FromSeconds(30))
                .WithResilience(r => r.AllowBackgroundDistributedOperations = false));
        });

        await host.Provider.Default.SetAsync("key", "default-value");
        await host.Provider.GetCache("short-lived").SetAsync("key", "short-value");

        Assert.Equal("default-value", await host.Provider.Default.GetOrDefaultAsync<string>("key"));
        Assert.Equal("short-value", await host.Provider.GetCache("short-lived").GetOrDefaultAsync<string>("key"));
    }

    [Fact]
    public async Task CallerCancellation_IsHonoured()
    {
        using var host = Host("redis-cancel");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.Cache.GetOrSetAsync<int>(
                "cancelled",
                async (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                    return 1;
                },
                token: cts.Token));
    }

    internal static async Task<RedisKey[]> FindKeysAsync(IConnectionMultiplexer connection, string pattern)
    {
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        var keys = new List<RedisKey>();
        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            keys.Add(key);
        }

        return [.. keys];
    }

    public sealed record Product(int Id, string Name);
}
