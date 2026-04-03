using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;

namespace Caching.NET.Tests.Services;

public class BoundaryTests
{
    [Fact]
    public async Task GetOrCreateAsync_NullKey_Throws()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            cache.GetOrCreateAsync(null!, _ => Task.FromResult(1)));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhitespaceKey_Throws()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.GetOrCreateAsync("   ", _ => Task.FromResult(1)));
    }

    [Fact]
    public async Task RemoveAsync_NullKeys_DoesNotThrow()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.RemoveAsync((IEnumerable<string>?)null!);
    }

    [Fact]
    public async Task SetAsync_NullKey_Throws()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "true", ["CacheOptions:Mode"] = "InMemory" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            cache.SetAsync(null!, "v"));
    }
}
