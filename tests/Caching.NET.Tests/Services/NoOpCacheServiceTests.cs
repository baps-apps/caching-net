using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Extensions;

namespace Caching.NET.Tests.Services;

public class NoOpCacheServiceTests
{
    [Fact]
    public async Task GetOrCreateAsync_RunsFactory()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        var value = await cache.GetOrCreateAsync("key", _ => Task.FromResult(42));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task SetAsync_And_RemoveAsync_NoOp()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        await cache.SetAsync("k", "v");
        await cache.RemoveAsync("k");
        await cache.RemoveByTagAsync("tag");
        // No throw
    }
}
