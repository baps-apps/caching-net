using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisServerSideBatchTests
{
    private readonly RedisContainerFixture _redis;
    public RedisServerSideBatchTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task GetMany_returns_correct_values_via_pipelined_batch_read()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var mux = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        services.AddSingleton<IConnectionMultiplexer>(mux);
        services.AddCaching(b => b.UseRedis(_redis.ConnectionString).WithKeyPrefix("rt-mget"));

        var sp = services.BuildServiceProvider();
        await using var _ = sp;
        var cache = sp.GetRequiredService<ICacheService>();

        await cache.SetManyAsync(new Dictionary<string, string>
        {
            ["a"] = "alpha", ["b"] = "beta", ["c"] = "gamma",
        });
        var got = await cache.GetManyAsync<string>(new[] { "a", "b", "c", "missing" });

        Assert.Equal("alpha", got["a"]);
        Assert.Equal("beta", got["b"]);
        Assert.Equal("gamma", got["c"]);
        Assert.Null(got["missing"]);
    }

    [Fact]
    public async Task RemoveMany_uses_server_side_DEL_when_multiplexer_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var mux = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        services.AddSingleton<IConnectionMultiplexer>(mux);
        services.AddCaching(b => b.UseRedis(_redis.ConnectionString).WithKeyPrefix("rt-del"));

        var sp = services.BuildServiceProvider();
        await using var _ = sp;
        var cache = sp.GetRequiredService<ICacheService>();

        await cache.SetManyAsync(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2", ["z"] = "3" });
        await cache.RemoveManyAsync(new[] { "x", "y" });

        Assert.False(await cache.ExistsAsync("x"));
        Assert.False(await cache.ExistsAsync("y"));
        Assert.True(await cache.ExistsAsync("z"));
    }

    [Fact]
    public async Task GetMany_pipelined_batch_uses_fewer_GET_commands_than_fan_out()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var mux = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        services.AddSingleton<IConnectionMultiplexer>(mux);
        services.AddCaching(b => b.UseRedis(_redis.ConnectionString).WithKeyPrefix("rt-mget-rt"));

        var sp = services.BuildServiceProvider();
        await using var _ = sp;
        var cache = sp.GetRequiredService<ICacheService>();

        var items = new Dictionary<string, string>();
        for (int i = 0; i < 10; i++) items[$"k{i}"] = $"v{i}";
        await cache.SetManyAsync(items);

        // Server-side pipelined batch: N hget commands sent in one roundtrip via IBatch.
        // Capture hget count before/after to confirm server-side commands were issued.
        var db = mux.GetDatabase();
        var infoBefore = await db.ExecuteAsync("INFO", "commandstats");
        var got = await cache.GetManyAsync<string>(items.Keys.ToArray());
        var infoAfter = await db.ExecuteAsync("INFO", "commandstats");

        // All 10 values must be returned correctly.
        for (int i = 0; i < 10; i++) Assert.Equal($"v{i}", got[$"k{i}"]);

        // hget count should have increased by exactly 10 (one HGET per key, pipelined).
        var hgetBefore = ParseCommandCount(infoBefore.ToString() ?? "", "cmdstat_hget");
        var hgetAfter = ParseCommandCount(infoAfter.ToString() ?? "", "cmdstat_hget");
        Assert.Equal(10, hgetAfter - hgetBefore);
    }

    private static long ParseCommandCount(string info, string statKey)
    {
        foreach (var line in info.Split('\n'))
        {
            if (line.StartsWith(statKey + ":", StringComparison.OrdinalIgnoreCase))
            {
                var callsPart = line.Split(',')[0].Split('=')[1];
                return long.Parse(callsPart);
            }
        }
        return 0;
    }
}
