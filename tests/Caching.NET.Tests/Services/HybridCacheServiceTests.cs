using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Extensions;

namespace Caching.NET.Tests.Services;

public class HybridCacheServiceTests
{
    [Fact]
    public async Task GetOrCreateAsync_StoresAndReturnsValue()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:KeyPrefix"] = "test",
            ["CacheOptions:RedisConnectionString"] = "localhost:6379"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        var key = $"hybrid:stores:{Guid.NewGuid():N}";
        var value = await cache.GetOrCreateAsync(key, _ => Task.FromResult("hybrid"));
        Assert.Equal("hybrid", value);

        var again = await cache.GetOrCreateAsync(key, _ => Task.FromResult("other"));
        Assert.Equal("hybrid", again);
    }

    [Fact]
    public async Task SetAsync_RemoveAsync_DoNotThrow_WhenRedisUnavailable()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:KeyPrefix"] = "test",
            ["CacheOptions:RedisConnectionString"] = "localhost:6379"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        var key = $"hybrid:set-remove:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "data");
        await cache.RemoveAsync(key);
        _ = await cache.GetOrCreateAsync(key, _ => Task.FromResult("miss"));
    }

    [Fact]
    public async Task GetAsync_ValueTypeMiss_DoesNotPoisonFutureGetOrCreate()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:KeyPrefix"] = "test",
            ["CacheOptions:RedisConnectionString"] = "localhost:6379"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        var key = $"hybrid:int:miss:{Guid.NewGuid():N}";
        var miss = await cache.GetAsync<int>(key);
        Assert.Equal(0, miss);

        var created = await cache.GetOrCreateAsync(key, _ => Task.FromResult(123));
        Assert.Equal(123, created);
    }
}
