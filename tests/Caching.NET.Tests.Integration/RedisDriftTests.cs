using Caching.NET.Serialization;
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using StackExchange.Redis;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisDriftTests
{
    private readonly RedisContainerFixture _redis;
    public RedisDriftTests(RedisContainerFixture redis) => _redis = redis;

    public sealed class CurrentDto { public int Id { get; set; } }

    [Fact]
    public async Task SchemaDrift_returns_miss_and_runs_factory()
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        var fakeHash = 0xDEAD_BEEF_CAFE_BABEul;
        var fakePayload = "{\"Id\":99}"u8.ToArray();
        var wire = PayloadEnvelope.Write(fakePayload, PayloadEnvelope.FormatIdJson, fakeHash);
        // StackExchangeRedisCache stores entries as Redis Hashes with field "data".
        // Plant the value in Hash format so schema-drift is exercised (not WRONGTYPE error).
        await mux.GetDatabase().HashSetAsync("rt-drift:k", "data", wire);

        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-drift");
        await using var _ = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        var ran = false;
        var got = await cache.GetOrCreateAsync<CurrentDto>("k", _ =>
        {
            ran = true;
            return Task.FromResult(new CurrentDto { Id = 7 });
        });

        Assert.True(ran);
        Assert.Equal(7, got.Id);
    }
}
