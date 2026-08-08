using Caching.NET.Tests.Integration.Fixtures;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Where Redis mode's "Redis is authoritative" guarantee starts and stops.
/// </summary>
/// <remarks>
/// Redis mode is enforced through the cache's default entry options. Any call that supplies its own
/// entry options replaces them wholesale, and the engine offers no cache-wide switch to reimpose
/// them — the only alternative would be the delegating wrapper this release exists to remove. These
/// tests pin both halves so the boundary is a known, tested property rather than a surprise.
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
    public void CreateEntryOptions_CarriesTheModesLayerEnforcement()
    {
        using var host = Host("redis-opts-derived");

        var options = host.Cache.CreateEntryOptions(o => o.Duration = TimeSpan.FromMinutes(1));

        Assert.True(options.SkipMemoryCacheRead);
        Assert.True(options.SkipMemoryCacheWrite);
    }

    [Fact]
    public void CallerConstructedEntryOptions_DoNotCarryIt()
    {
        var options = new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes(1) };

        Assert.False(options.SkipMemoryCacheRead);
        Assert.False(options.SkipMemoryCacheWrite);
    }

    [Fact]
    public async Task OptionsBuiltWithCreateEntryOptions_KeepRedisAuthoritative()
    {
        using var host = Host("redis-opts-authoritative");
        var options = host.Cache.CreateEntryOptions(o => o.Duration = TimeSpan.FromMinutes(5));

        await host.Cache.SetAsync("Order:1", 1, options);
        await WipeAsync("*redis-opts-authoritative*");

        // Nothing was kept locally, so deleting the Redis entry is immediately visible.
        Assert.False((await host.Cache.TryGetAsync<int>("Order:1", options)).HasValue);
    }

    [Fact]
    public async Task OptionsConstructedByTheCaller_ReintroduceALocalCopy()
    {
        using var host = Host("redis-opts-raw");
        var options = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromMinutes(5),
            IsFailSafeEnabled = false
        };

        await host.Cache.SetAsync("Order:2", 2, options);
        await WipeAsync("*redis-opts-raw*");

        // Documented consequence: the entry survives in L1 because the caller's options replaced the
        // mode's skip flags. Applications that need the guarantee must derive their options from
        // CreateEntryOptions.
        Assert.True((await host.Cache.TryGetAsync<int>("Order:2", options)).HasValue);

        // A call that does not override the options still sees the truth.
        Assert.False((await host.Cache.TryGetAsync<int>("Order:2")).HasValue);
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
