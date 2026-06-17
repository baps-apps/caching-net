using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Extensions;

namespace Caching.NET.Tests.Services;

public class InMemoryCacheServiceTests
{
    [Fact]
    public async Task GetOrCreateAsync_StoresAndReturnsValue()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        var value = await cache.GetOrCreateAsync("key1", _ => Task.FromResult("value1"));
        Assert.Equal("value1", value);

        var again = await cache.GetOrCreateAsync("key1", _ => Task.FromResult("other"));
        Assert.Equal("value1", again);
    }

    [Fact]
    public async Task GetOrCreateAsync_DoesNotCacheNullFactoryResult()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        var first = await cache.GetOrCreateAsync("nk", _ => Task.FromResult<string>(null!));
        Assert.Null(first);

        // null was not cached, so the next factory runs and its value is returned
        var second = await cache.GetOrCreateAsync("nk", _ => Task.FromResult("real"));
        Assert.Equal("real", second);
    }

    [Fact]
    public async Task SetAsync_And_RemoveAsync_Work()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        await cache.SetAsync("k", 100);
        var v = await cache.GetOrCreateAsync("k", _ => Task.FromResult(0));
        Assert.Equal(100, v);

        await cache.RemoveAsync("k");
        var after = await cache.GetOrCreateAsync("k", _ => Task.FromResult(999));
        Assert.Equal(999, after);
    }
}
