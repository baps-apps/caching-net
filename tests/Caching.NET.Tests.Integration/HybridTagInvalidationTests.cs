using Caching.NET.Extensions;
using Caching.NET.Options;
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class HybridTagInvalidationTests
{
    private readonly RedisContainerFixture _redis;
    public HybridTagInvalidationTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task SetAsync_with_tags_then_RemoveByTag_evicts_entry()
    {
        var (sp, cache) = IntegrationServiceProvider.BuildHybrid(_redis.ConnectionString, "hybrid-tag-set");
        await using var scope = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        var tag = $"tag:{Guid.NewGuid():N}";
        var key = $"set:{Guid.NewGuid():N}";

        await cache.SetAsync(key, "tagged-value", new CacheCallOptions { Tags = new[] { tag } });
        // Confirm the Set round-tripped to the cache: factory must NOT run.
        Assert.Equal("tagged-value", await cache.GetOrCreateAsync(key, _ => Task.FromResult("MISS")));

        await cache.RemoveByTagAsync(tag);

        // Entry evicted by tag: factory runs and its value is returned.
        Assert.Equal("refreshed", await cache.GetOrCreateAsync(key, _ => Task.FromResult("refreshed")));
    }

    [Fact]
    public async Task RemoveByTag_evicts_all_entries_sharing_the_tag()
    {
        var (sp, cache) = IntegrationServiceProvider.BuildHybrid(_redis.ConnectionString, "hybrid-tag-multi");
        await using var scope = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        var tag = $"category:{Guid.NewGuid():N}";
        var key1 = $"a:{Guid.NewGuid():N}";
        var key2 = $"b:{Guid.NewGuid():N}";
        var opts = new CacheCallOptions { Tags = new[] { tag } };

        await cache.GetOrCreateAsync(key1, _ => Task.FromResult("one"), opts);
        await cache.GetOrCreateAsync(key2, _ => Task.FromResult("two"), opts);

        await cache.RemoveByTagAsync(tag);

        Assert.Equal("one-fresh", await cache.GetOrCreateAsync(key1, _ => Task.FromResult("one-fresh")));
        Assert.Equal("two-fresh", await cache.GetOrCreateAsync(key2, _ => Task.FromResult("two-fresh")));
    }
}
