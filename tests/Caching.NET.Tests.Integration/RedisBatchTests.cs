using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisBatchTests
{
    private readonly RedisContainerFixture _redis;
    public RedisBatchTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task SetMany_then_GetMany_round_trips_against_real_redis()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-batch");
        await using var _ = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        var items = new Dictionary<string, string>
        {
            ["a"] = "1",
            ["b"] = "2",
            ["c"] = "3",
        };
        await cache.SetManyAsync(items);
        var got = await cache.GetManyAsync<string>(new[] { "a", "b", "c", "missing" });

        Assert.Equal("1", got["a"]);
        Assert.Equal("2", got["b"]);
        Assert.Equal("3", got["c"]);
        Assert.Null(got["missing"]);
    }

    [Fact]
    public async Task RemoveMany_clears_specified_keys_only()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-rm");
        await using var _ = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        await cache.SetManyAsync(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2", ["z"] = "3" });
        await cache.RemoveManyAsync(new[] { "x", "y" });

        Assert.False(await cache.ExistsAsync("x"));
        Assert.False(await cache.ExistsAsync("y"));
        Assert.True(await cache.ExistsAsync("z"));
    }
}
