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
            ["CacheOptions:Mode"] = "Hybrid"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        var value = await cache.GetOrCreateAsync("h1", _ => Task.FromResult("hybrid"));
        Assert.Equal("hybrid", value);

        var again = await cache.GetOrCreateAsync("h1", _ => Task.FromResult("other"));
        Assert.Equal("hybrid", again);
    }

    [Fact]
    public async Task SetAsync_RemoveAsync_Work()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Hybrid"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        await cache.SetAsync("hk", "data");
        var v = await cache.GetOrCreateAsync("hk", _ => Task.FromResult<string>("miss"));
        Assert.Equal("data", v);
        await cache.RemoveAsync("hk");
        var after = await cache.GetOrCreateAsync("hk", _ => Task.FromResult("miss"));
        Assert.Equal("miss", after);
    }
}
