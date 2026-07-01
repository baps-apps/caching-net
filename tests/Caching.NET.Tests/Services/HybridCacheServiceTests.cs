using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Extensions;
using Caching.NET.Options;

namespace Caching.NET.Tests.Services;

public class HybridCacheServiceTests
{
    private static Abstractions.ICacheService BuildHybridCache()
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
        return services.BuildServiceProvider().GetRequiredService<Abstractions.ICacheService>();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithTags_RemoveByTag_EvictsEntry()
    {
        var cache = BuildHybridCache();
        var tag = $"tag:{Guid.NewGuid():N}";
        var key = $"hybrid:tagged-goc:{Guid.NewGuid():N}";

        var created = await cache.GetOrCreateAsync(
            key, _ => Task.FromResult("created"), new CacheCallOptions { Tags = new[] { tag } });
        Assert.Equal("created", created);

        await cache.RemoveByTagAsync(tag);

        var after = await cache.GetOrCreateAsync(key, _ => Task.FromResult("refreshed"));
        Assert.Equal("refreshed", after);
    }

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
    public async Task GetOrCreateAsync_DoesNotCacheNullFactoryResult()
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

        var key = $"hybrid:null:{Guid.NewGuid():N}";
        var first = await cache.GetOrCreateAsync(key, _ => Task.FromResult<string>(null!));
        Assert.Null(first);

        // null was not cached, so the next factory runs and its value is returned
        var second = await cache.GetOrCreateAsync(key, _ => Task.FromResult("real"));
        Assert.Equal("real", second);
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
