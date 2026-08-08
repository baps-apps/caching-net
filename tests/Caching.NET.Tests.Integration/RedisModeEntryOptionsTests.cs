using Caching.NET.Options;
using Caching.NET.Tests.Integration.Fixtures;
using StackExchange.Redis;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Redis mode's "Redis is authoritative" guarantee, asserted against a per-call override.
/// </summary>
/// <remarks>
/// <para>
/// In v2 this file pinned a hazard: entry options were the engine's own type, a caller could build
/// one from scratch, and doing so replaced the cache's defaults wholesale — including the layer-skip
/// flags that are how Redis mode is enforced. An override therefore silently reintroduced a local
/// copy and Redis stopped being authoritative.
/// </para>
/// <para>
/// <see cref="CacheEntryOverrides"/> removes the hazard by construction: overrides are applied on top
/// of the cache's configured options, never in place of them, so there is no object a caller can
/// build that escapes the mode. That is a claim about behaviour, not just about types, so it is
/// asserted here against a real Redis rather than only against the mapper.
/// </para>
/// </remarks>
[Collection(RedisCollection.Name)]
public class RedisModeEntryOptionsTests
{
    private readonly RedisFixture _redis;

    public RedisModeEntryOptionsTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private CacheHost Host(string prefix) => CacheHost.Create(cache => cache
        .UseRedis(_redis.ConnectionString)
        .WithApplicationPrefix(prefix)
        .WithJitter(TimeSpan.Zero)
        .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

    [Fact]
    public async Task PerCallOverrides_KeepRedisAuthoritative()
    {
        using var host = Host("redis-opts-authoritative");
        var options = new CacheEntryOverrides
        {
            LocalExpiration = TimeSpan.FromMinutes(5),
            DistributedExpiration = TimeSpan.FromMinutes(5)
        };

        await host.Cache.SetAsync("Order:1", 1, options);
        await WipeAsync("*redis-opts-authoritative*");

        // The write carried an explicit local expiration, which in v2 would have been enough to put
        // a copy in memory. Redis mode still keeps nothing locally, so deleting the Redis entry is
        // immediately visible — both to a call that repeats the overrides and to one that does not.
        Assert.False((await host.Cache.TryGetAsync<int>("Order:1", options)).HasValue);
        Assert.False((await host.Cache.TryGetAsync<int>("Order:1")).HasValue);
    }

    [Fact]
    public async Task OverridesChangeOnlyWhatTheySet()
    {
        using var host = Host("redis-opts-additive");

        // Both halves must hold together: one proves a named property really did change, the other
        // proves an unnamed one did not. Either alone can be satisfied by a broken mapper.
        var shortLived = new CacheEntryOverrides
        {
            LocalExpiration = TimeSpan.FromMilliseconds(300),
            DistributedExpiration = TimeSpan.FromMilliseconds(300)
        };

        // Changed. The cache's configured lifetime is CachingOptions.DefaultExpiration — 10 minutes
        // — so an entry that is gone within a second can only be carrying the override's 300 ms.
        // Jitter is pinned to zero by Host(), so 300 ms means 300 ms.
        await host.Cache.SetAsync("Order:2", 2, shortLived);
        Assert.Equal(2, await host.Cache.GetOrDefaultAsync<int>("Order:2"));

        await Task.Delay(900);
        Assert.False((await host.Cache.TryGetAsync<int>("Order:2")).HasValue);

        // Unchanged. The mode's layer enforcement was not named, so it must survive: Redis is still
        // authoritative and wiping it leaves nothing behind — for a read that repeats the overrides
        // as well as for one that does not. An options object built from scratch rather than from
        // the cache's own defaults would lose the skip-memory flags and leave a local copy
        // answering the first of these.
        await host.Cache.SetAsync("Order:3", 3, shortLived);
        await WipeAsync("*redis-opts-additive:Order:3*");

        Assert.False((await host.Cache.TryGetAsync<int>("Order:3", shortLived)).HasValue);
        Assert.False((await host.Cache.TryGetAsync<int>("Order:3")).HasValue);
    }

    private async Task WipeAsync(string pattern)
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            var database = connection.GetDatabase();
            foreach (var key in await RedisModeTests.FindKeysAsync(connection, pattern))
            {
                await database.KeyDeleteAsync(key);
            }
        }
    }
}
