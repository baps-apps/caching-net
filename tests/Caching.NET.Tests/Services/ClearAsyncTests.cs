using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Extensions;

namespace Caching.NET.Tests.Services;

public class ClearAsyncTests
{
    private static Abstractions.ICacheService Build(string mode)
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = mode,
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
    public async Task ClearAsync_InMemory_RemovesAllEntries()
    {
        var cache = Build("InMemory");
        await cache.SetAsync("a", "1");
        await cache.SetAsync("b", "2");
        Assert.Equal("1", await cache.GetAsync<string>("a"));

        await cache.ClearAsync();

        Assert.Null(await cache.GetAsync<string>("a"));
        Assert.Null(await cache.GetAsync<string>("b"));
    }

    [Fact]
    public async Task ClearAsync_Hybrid_InvalidatesAllEntries()
    {
        var cache = Build("Hybrid");
        var k1 = $"clear:{Guid.NewGuid():N}";
        var k2 = $"clear:{Guid.NewGuid():N}";
        await cache.GetOrCreateAsync(k1, _ => Task.FromResult("one"));
        await cache.GetOrCreateAsync(k2, _ => Task.FromResult("two"));

        await cache.ClearAsync();

        Assert.Equal("one-fresh", await cache.GetOrCreateAsync(k1, _ => Task.FromResult("one-fresh")));
        Assert.Equal("two-fresh", await cache.GetOrCreateAsync(k2, _ => Task.FromResult("two-fresh")));
    }

    [Fact]
    public async Task ClearAsync_WhenDisabled_IsNoOp()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        var cache = services.BuildServiceProvider().GetRequiredService<Abstractions.ICacheService>();

        await cache.ClearAsync(); // must not throw
    }
}
