using Caching.NET.Extensions;
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class HybridCrossAppIsolationTests
{
    private readonly RedisContainerFixture _redis;
    public HybridCrossAppIsolationTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task ClearAsync_in_one_app_does_not_invalidate_another_apps_entries()
    {
        var key = $"shared:{Guid.NewGuid():N}";

        // App B writes an entry (populates its L1 + shared L2).
        var (spB1, cacheB1) = IntegrationServiceProvider.BuildHybrid(_redis.ConnectionString, "tenant-b");
        await using (var _ = (Microsoft.Extensions.DependencyInjection.ServiceProvider)spB1)
            await cacheB1.GetOrCreateAsync(key, _ => Task.FromResult("B-original"));

        // App A clears everything it owns.
        var (spA, cacheA) = IntegrationServiceProvider.BuildHybrid(_redis.ConnectionString, "tenant-a");
        await using (var _ = (Microsoft.Extensions.DependencyInjection.ServiceProvider)spA)
            await cacheA.ClearAsync();

        // Fresh App B reader (empty L1) must read through shared L2.
        var (spB2, cacheB2) = IntegrationServiceProvider.BuildHybrid(_redis.ConnectionString, "tenant-b");
        await using var __ = (Microsoft.Extensions.DependencyInjection.ServiceProvider)spB2;
        var got = await cacheB2.GetOrCreateAsync(key, _ => Task.FromResult("B-REFETCHED"));

        // If A's clear leaked across apps, B would have been invalidated and refetched.
        Assert.Equal("B-original", got);
    }

    // Note: that ClearAsync invalidates the app's OWN entries is covered deterministically by the unit test
    // ClearAsync_Hybrid_InvalidatesAllEntries (same-instance L1). A cross-instance integration assertion here
    // would only exercise HybridCache's L2 wildcard-marker propagation, which has ~second granularity and races.
}
