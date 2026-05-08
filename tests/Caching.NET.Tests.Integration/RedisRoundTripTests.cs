using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisRoundTripTests
{
    private readonly RedisContainerFixture _redis;
    public RedisRoundTripTests(RedisContainerFixture redis) => _redis = redis;

    public sealed class Order { public int Id { get; set; } public string? Customer { get; set; } }

    [Fact]
    public async Task Get_after_Set_returns_value_from_real_redis()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-roundtrip");
        await using var scope = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        await cache.SetAsync("k", new Order { Id = 1, Customer = "Acme" });
        var got = await cache.GetAsync<Order>("k");

        Assert.NotNull(got);
        Assert.Equal(1, got!.Id);
        Assert.Equal("Acme", got.Customer);
    }
}
