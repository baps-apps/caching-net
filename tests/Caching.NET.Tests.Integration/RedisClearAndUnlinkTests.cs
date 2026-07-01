using Caching.NET.Extensions;
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisClearAndUnlinkTests
{
    private readonly RedisContainerFixture _redis;
    public RedisClearAndUnlinkTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task RemoveManyAsync_removes_all_given_keys()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rm-many");
        await using var scope = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        var keys = Enumerable.Range(0, 25).Select(i => $"k:{i}:{Guid.NewGuid():N}").ToArray();
        foreach (var k in keys) await cache.SetAsync(k, $"v-{k}");
        foreach (var k in keys) Assert.Equal($"v-{k}", await cache.GetAsync<string>(k));

        await cache.RemoveManyAsync(keys);

        foreach (var k in keys) Assert.Null(await cache.GetAsync<string>(k));
    }

    [Fact]
    public async Task ClearAsync_removes_all_entries_under_the_prefix()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "clear-all");
        await using var scope = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        var keys = Enumerable.Range(0, 30).Select(i => $"c:{i}:{Guid.NewGuid():N}").ToArray();
        foreach (var k in keys) await cache.SetAsync(k, "x");
        foreach (var k in keys) Assert.True(await cache.ExistsAsync(k));

        await cache.ClearAsync();

        foreach (var k in keys) Assert.False(await cache.ExistsAsync(k));
    }

    [Fact]
    public async Task ClearAsync_is_scoped_to_its_own_prefix()
    {
        // Two apps sharing one Redis DB, distinguished only by KeyPrefix.
        var (spA, cacheA) = IntegrationServiceProvider.Build(_redis.ConnectionString, "tenant-a");
        var (spB, cacheB) = IntegrationServiceProvider.Build(_redis.ConnectionString, "tenant-b");
        await using var scopeA = (Microsoft.Extensions.DependencyInjection.ServiceProvider)spA;
        await using var scopeB = (Microsoft.Extensions.DependencyInjection.ServiceProvider)spB;

        var key = $"shared:{Guid.NewGuid():N}";
        await cacheA.SetAsync(key, "a-value");
        await cacheB.SetAsync(key, "b-value");

        await cacheA.ClearAsync();

        // A's keyspace is wiped; B's identical logical key survives (different physical prefix).
        Assert.Null(await cacheA.GetAsync<string>(key));
        Assert.Equal("b-value", await cacheB.GetAsync<string>(key));
    }
}
