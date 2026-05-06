using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisKeyPrefixIsolationTests
{
    private readonly RedisContainerFixture _redis;
    public RedisKeyPrefixIsolationTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task Different_prefixes_do_not_see_each_others_keys()
    {
        var (spA, cacheA) = IntegrationServiceProvider.Build(_redis.ConnectionString, "svc-a");
        var (spB, cacheB) = IntegrationServiceProvider.Build(_redis.ConnectionString, "svc-b");
        await using var _a = (Microsoft.Extensions.DependencyInjection.ServiceProvider)spA;
        await using var _b = (Microsoft.Extensions.DependencyInjection.ServiceProvider)spB;

        await cacheA.SetAsync("shared", "from-a");
        Assert.Equal("from-a", await cacheA.GetAsync<string>("shared"));
        Assert.Null(await cacheB.GetAsync<string>("shared"));
    }
}
