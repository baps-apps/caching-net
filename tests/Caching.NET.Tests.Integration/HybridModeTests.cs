using Caching.NET.Tests.Integration.Fixtures;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Hybrid mode: L1 memory in front of L2 Redis, with backplane invalidation across instances.
/// </summary>
[Collection(RedisCollection.Name)]
public class HybridModeTests
{
    private readonly RedisFixture _redis;

    public HybridModeTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private CacheHost Host(string prefix, bool backplane = true, TimeSpan? localExpiration = null)
        => CacheHost.Create(cache =>
        {
            cache.UseHybrid(_redis.ConnectionString, enableBackplane: backplane)
                .WithApplicationPrefix(prefix)
                .WithJitter(TimeSpan.Zero)
                .WithDefaultExpiration(TimeSpan.FromMinutes(5))
                // L2 writes are awaited so cross-instance assertions are deterministic; backplane
                // delivery stays asynchronous and is polled for, as it is in production.
                .WithResilience(r => r.AllowBackgroundDistributedOperations = false);

            if (localExpiration is { } local)
            {
                cache.WithLocalExpiration(local);
            }
        });

    [Fact]
    public async Task L1Hit_ServesWithoutTouchingTheFactory()
    {
        using var host = Host("hybrid-l1", backplane: false);
        var calls = 0;

        for (var i = 0; i < 5; i++)
        {
            await host.Cache.GetOrSetAsync<int>("Order:1", async _ =>
            {
                Interlocked.Increment(ref calls);
                return 1;
            });
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task L1MissWithL2Hit_WarmsL1AndSkipsTheFactory()
    {
        using var writer = Host("hybrid-warm", backplane: false);
        using var reader = Host("hybrid-warm", backplane: false);

        await writer.Cache.SetAsync("Order:2", 42);

        var factoryCalls = 0;

        // First read on the second instance: L1 miss, L2 hit, factory must not run.
        var first = await reader.Cache.GetOrSetAsync<int>("Order:2", async _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return -1;
        });

        Assert.Equal(42, first);
        Assert.Equal(0, factoryCalls);

        // Second read is now served from the warmed L1; still no factory.
        Assert.Equal(42, await reader.Cache.GetOrDefaultAsync<int>("Order:2"));
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task L1AndL2Miss_RunsTheFactoryAndPopulatesBothLayers()
    {
        using var host = Host("hybrid-miss", backplane: false);
        using var other = Host("hybrid-miss", backplane: false);

        var value = await host.Cache.GetOrSetAsync<int>("Order:3", async _ => 7);
        Assert.Equal(7, value);

        // The second instance has an empty L1, so seeing the value proves it reached L2.
        Assert.Equal(7, await other.Cache.GetOrDefaultAsync<int>("Order:3"));
    }

    [Fact]
    public async Task L1Expiry_WhileL2IsStillValid_RefetchesFromL2()
    {
        using var host = Host("hybrid-l1-expiry", backplane: false, localExpiration: TimeSpan.FromMilliseconds(150));
        var factoryCalls = 0;

        await host.Cache.GetOrSetAsync<int>("Order:4", async _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return 11;
        });

        await Task.Delay(400);

        var value = await host.Cache.GetOrSetAsync<int>("Order:4", async _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return -1;
        });

        Assert.Equal(11, value);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task BackplaneInvalidation_EvictsL1OnEveryOtherInstance()
    {
        using var podA = Host("hybrid-backplane");
        using var podB = Host("hybrid-backplane");

        await podA.Cache.SetAsync("Order:5", "v1");

        // Warm pod B's L1 with the original value.
        Assert.Equal("v1", await podB.Cache.GetOrDefaultAsync<string>("Order:5"));

        await podA.Cache.SetAsync("Order:5", "v2");

        var observed = await WaitForAsync(
            async () => await podB.Cache.GetOrDefaultAsync<string>("Order:5") == "v2",
            TimeSpan.FromSeconds(10));

        Assert.True(observed, "pod B never observed the value written by pod A");
    }

    [Fact]
    public async Task BackplaneRemoval_EvictsL1OnEveryOtherInstance()
    {
        using var podA = Host("hybrid-backplane-remove");
        using var podB = Host("hybrid-backplane-remove");

        await podA.Cache.SetAsync("Order:6", "v1");
        Assert.Equal("v1", await podB.Cache.GetOrDefaultAsync<string>("Order:6"));

        await podA.Cache.RemoveAsync("Order:6");

        var evicted = await WaitForAsync(
            async () => !(await podB.Cache.TryGetAsync<string>("Order:6")).HasValue,
            TimeSpan.FromSeconds(10));

        Assert.True(evicted, "pod B kept serving a removed entry from its local layer");
    }

    [Fact]
    public async Task WithoutABackplane_LocalCopiesAreBoundedByTheLocalExpiration()
    {
        using var podA = Host("hybrid-no-backplane", backplane: false, localExpiration: TimeSpan.FromMilliseconds(200));
        using var podB = Host("hybrid-no-backplane", backplane: false, localExpiration: TimeSpan.FromMilliseconds(200));

        await podA.Cache.SetAsync("Order:7", "v1");
        Assert.Equal("v1", await podB.Cache.GetOrDefaultAsync<string>("Order:7"));

        await podA.Cache.SetAsync("Order:7", "v2");

        // No backplane, so pod B converges only once its local copy expires. This is the documented
        // trade-off, and the local expiration is what bounds it.
        var converged = await WaitForAsync(
            async () => await podB.Cache.GetOrDefaultAsync<string>("Order:7") == "v2",
            TimeSpan.FromSeconds(10));

        Assert.True(converged);
    }

    [Fact]
    public async Task TagInvalidation_PropagatesToEveryInstance()
    {
        using var podA = Host("hybrid-tags");
        using var podB = Host("hybrid-tags");

        await podA.Cache.SetAsync("Product:1", 1, tags: ["category:tools"]);
        Assert.Equal(1, await podB.Cache.GetOrDefaultAsync<int>("Product:1"));

        await podA.Cache.RemoveByTagAsync("category:tools");

        var invalidated = await WaitForAsync(
            async () => !(await podB.Cache.TryGetAsync<int>("Product:1")).HasValue,
            TimeSpan.FromSeconds(10));

        Assert.True(invalidated);
    }

    [Fact]
    public async Task Clear_PropagatesToEveryInstance()
    {
        using var podA = Host("hybrid-clear");
        using var podB = Host("hybrid-clear");

        await podA.Cache.SetAsync("k", 1);
        Assert.Equal(1, await podB.Cache.GetOrDefaultAsync<int>("k"));

        await podA.Cache.ClearAsync();

        var cleared = await WaitForAsync(
            async () => !(await podB.Cache.TryGetAsync<int>("k")).HasValue,
            TimeSpan.FromSeconds(10));

        Assert.True(cleared);
    }

    [Fact]
    public async Task StampedeProtection_HoldsAcrossManyConcurrentCallers()
    {
        using var host = Host("hybrid-stampede", backplane: false);
        var calls = 0;
        using var release = new SemaphoreSlim(0);

        var callers = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
            await host.Cache.GetOrSetAsync<int>("hot", async _ =>
            {
                Interlocked.Increment(ref calls);
                await release.WaitAsync();
                return 3;
            }))).ToArray();

        while (Volatile.Read(ref calls) == 0)
        {
            await Task.Yield();
        }

        release.Release(64);
        var results = await Task.WhenAll(callers);

        Assert.All(results, v => Assert.Equal(3, v));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PodRestart_RebuildsL1FromL2WithoutRunningTheFactory()
    {
        var factoryCalls = 0;

        using (var pod = Host("hybrid-restart", backplane: false))
        {
            await pod.Cache.GetOrSetAsync<int>("Order:8", async _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return 21;
            });
        }

        // A fresh process: empty L1, same Redis.
        using var restarted = Host("hybrid-restart", backplane: false);

        var value = await restarted.Cache.GetOrSetAsync<int>("Order:8", async _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return -1;
        });

        Assert.Equal(21, value);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task DistributedAndLocalExpirations_CanDiffer()
    {
        using var host = CacheHost.Create(cache => cache
            .UseHybrid(_redis.ConnectionString, enableBackplane: false)
            .WithApplicationPrefix("hybrid-durations")
            .WithJitter(TimeSpan.Zero)
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false)
            .WithDefaultExpiration(TimeSpan.FromMinutes(10))
            .WithLocalExpiration(TimeSpan.FromSeconds(30))
            .WithDistributedExpiration(TimeSpan.FromMinutes(30)));

        await host.Cache.SetAsync("Order:9", 1);

        Assert.Equal(1, await host.Cache.GetOrDefaultAsync<int>("Order:9"));
        Assert.Equal(TimeSpan.FromSeconds(30), host.Cache.DefaultEntryOptions.MemoryCacheDuration);
        Assert.Equal(TimeSpan.FromMinutes(30), host.Cache.DefaultEntryOptions.DistributedCacheDuration);
    }

    [Fact]
    public async Task PrefixIsolation_HoldsForBackplaneTrafficToo()
    {
        using var appOne = Host("hybrid-iso-one");
        using var appTwo = Host("hybrid-iso-two");

        await appOne.Cache.SetAsync("k", "one");
        await appTwo.Cache.SetAsync("k", "two");

        await appOne.Cache.RemoveAsync("k");

        // App two must be untouched by app one's invalidation.
        Assert.Equal("two", await appTwo.Cache.GetOrDefaultAsync<string>("k"));
    }

    [Fact]
    public async Task EagerRefresh_RefreshesInTheBackgroundWithoutBlockingTheCaller()
    {
        using var host = CacheHost.Create(cache => cache
            .UseHybrid(_redis.ConnectionString, enableBackplane: false)
            .WithApplicationPrefix("hybrid-eager")
            .WithJitter(TimeSpan.Zero)
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false)
            .WithDefaultExpiration(TimeSpan.FromSeconds(2))
            .WithEagerRefresh(0.1f));

        var calls = 0;

        Task<int> Load() => host.Cache.GetOrSetAsync<int>("Order:10", async _ =>
        {
            await Task.Yield();
            return Interlocked.Increment(ref calls);
        }).AsTask();

        Assert.Equal(1, await Load());

        // Past the eager-refresh threshold the caller still gets the current value immediately.
        await Task.Delay(500);
        Assert.Equal(1, await Load());

        var refreshed = await WaitForAsync(
            async () => await host.Cache.GetOrDefaultAsync<int>("Order:10") == 2,
            TimeSpan.FromSeconds(10));

        Assert.True(refreshed, "the background eager refresh never completed");
    }

    [Fact]
    public async Task BackplaneChannel_IsScopedToTheApplicationPrefix()
    {
        using var host = Host("hybrid-channel");
        await host.Cache.SetAsync("k", 1);

        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            var server = connection.GetServer(connection.GetEndPoints()[0]);
            var channels = await server.SubscriptionChannelsAsync();
            Assert.Contains(channels, c => c.ToString().Contains("hybrid-channel", StringComparison.Ordinal));
        }
    }

    // Polls a condition instead of sleeping for a fixed duration: backplane delivery is
    // asynchronous, so the test waits for the observable outcome and fails fast when it arrives.
    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return await condition();
    }
}
