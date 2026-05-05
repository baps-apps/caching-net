using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Caching.NET.Abstractions;
using Caching.NET.Options;
using Caching.NET.Services;
using Caching.NET.Tests.Fakes;

namespace Caching.NET.Tests.Services;

public class RedisCacheServiceTests
{
    [Fact]
    public async Task GetOrCreateAsync_StoresAndReturnsValue_WithFakeCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache, FakeDistributedCache>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CacheOptions()));
        services.Configure<CacheSerializerOptions>(_ => { });
        services.AddSingleton<RedisCacheService>();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        var value = await cache.GetOrCreateAsync("key1", _ => Task.FromResult("value1"));
        Assert.Equal("value1", value);

        var again = await cache.GetOrCreateAsync("key1", _ => Task.FromResult("other"));
        Assert.Equal("value1", again);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenGetAsyncThrows_FailOpen_ExecutesFactory()
    {
        var mockCache = new Mock<IDistributedCache>();
        mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockCache.Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CacheOptions { FailOpen = true }));
        services.Configure<CacheSerializerOptions>(_ => { });
        services.AddSingleton<RedisCacheService>();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        var value = await cache.GetOrCreateAsync("k", _ => Task.FromResult(42));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenGetAsyncThrows_AndFailOpenFalse_PropagatesException()
    {
        var mockCache = new Mock<IDistributedCache>();
        mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockCache.Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CacheOptions { FailOpen = false, ThrowOnFailure = true }));
        services.Configure<CacheSerializerOptions>(_ => { });
        services.AddSingleton<RedisCacheService>();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync("k", _ => Task.FromResult(42)));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenKeyExceedsMaximumKeyLength_SkipsCacheAndRunsFactory()
    {
        var fake = new FakeDistributedCache();
        var options = new CacheOptions { MaximumKeyLength = 5 };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache>(fake);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.Configure<CacheSerializerOptions>(_ => { });
        services.AddSingleton<RedisCacheService>();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        var value = await cache.GetOrCreateAsync("longkey", _ => Task.FromResult("v"));
        Assert.Equal("v", value);

        var again = await cache.GetOrCreateAsync("longkey", _ => Task.FromResult("v2"));
        Assert.Equal("v2", again);
    }

    [Fact]
    public async Task SetAsync_RemoveAsync_Work_WithFakeCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache, FakeDistributedCache>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CacheOptions()));
        services.Configure<CacheSerializerOptions>(_ => { });
        services.AddSingleton<RedisCacheService>();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        await cache.SetAsync("k", "data");
        var v = await cache.GetOrCreateAsync("k", _ => Task.FromResult("miss"));
        Assert.Equal("data", v);
        await cache.RemoveAsync("k");
        var after = await cache.GetOrCreateAsync("k", _ => Task.FromResult("miss"));
        Assert.Equal("miss", after);
    }

    [Fact]
    public async Task RemoveByTagAsync_IsNoOp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache, FakeDistributedCache>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CacheOptions()));
        services.Configure<CacheSerializerOptions>(_ => { });
        services.AddSingleton<RedisCacheService>();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        await cache.RemoveByTagAsync("tag");
        await cache.RemoveByTagAsync(new[] { "a", "b" });
    }
}
