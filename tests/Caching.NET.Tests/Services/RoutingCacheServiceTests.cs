using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Caching.NET.Tests.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Services;

public class RoutingCacheServiceTests
{
    [Fact]
    public async Task UsesHybridByDefault_WhenModeIsHybrid()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:KeyPrefix"] = "test",
            ["CacheOptions:RedisConnectionString"] = "localhost:6379"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var first = await cache.GetOrCreateAsync("rk:hybrid", _ => Task.FromResult("hybrid"));
        var second = await cache.GetOrCreateAsync("rk:hybrid", _ => Task.FromResult("other"));

        Assert.Equal("hybrid", first);
        Assert.Equal("hybrid", second);
    }

    [Fact]
    public async Task CanOverrideToInMemory_WhenModeIsHybrid()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:KeyPrefix"] = "test",
            ["CacheOptions:RedisConnectionString"] = "localhost:6379"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var options = new CacheCallOptions
        {
            Mode = CacheMode.InMemory
        };

        var first = await cache.GetOrCreateAsync(
            "rk:inmem",
            _ => Task.FromResult("inmem"),
            options,
            expiration: null,
            localExpiration: null,
            cancellationToken: CancellationToken.None);

        var second = await cache.GetOrCreateAsync(
            "rk:inmem",
            _ => Task.FromResult("other"),
            options,
            expiration: null,
            localExpiration: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal("inmem", first);
        Assert.Equal("inmem", second);
    }

    [Fact]
    public async Task BypassCache_AlwaysRunsFactory_DoesNotCache()
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
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var callOptions = new CacheCallOptions { BypassCache = true };
        var first = await cache.GetOrCreateAsync(
            "bypass:1",
            _ => Task.FromResult("a"),
            callOptions,
            expiration: null,
            localExpiration: null,
            CancellationToken.None);
        var second = await cache.GetOrCreateAsync(
            "bypass:1",
            _ => Task.FromResult("b"),
            callOptions,
            expiration: null,
            localExpiration: null,
            CancellationToken.None);

        Assert.Equal("a", first);
        Assert.Equal("b", second);
    }

    [Fact]
    public async Task ForceRefresh_RunsFactory_ThenStoresAndReturns()
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
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("refresh:1", "old");
        var callOptions = new CacheCallOptions { ForceRefresh = true };
        var value = await cache.GetOrCreateAsync(
            "refresh:1",
            _ => Task.FromResult("new"),
            callOptions,
            expiration: null,
            localExpiration: null,
            CancellationToken.None);
        Assert.Equal("new", value);

        var next = await cache.GetOrCreateAsync("refresh:1", _ => Task.FromResult("never"));
        Assert.Equal("new", next);
    }

    [Fact]
    public async Task WhenDisabled_GetOrCreateAsync_AlwaysRunsFactory()
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
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var first = await cache.GetOrCreateAsync("disabled:1", _ => Task.FromResult("a"));
        var second = await cache.GetOrCreateAsync("disabled:1", _ => Task.FromResult("b"));

        Assert.Equal("a", first);
        Assert.Equal("b", second);
    }

    [Fact]
    public async Task WhenDisabled_SetAndRemove_AreNoOps()
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
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("disabled:set", "value");
        await cache.RemoveAsync("disabled:rem");
        await cache.RemoveManyAsync(new[] { "disabled:rem1", "disabled:rem2" });
        await cache.RemoveByTagAsync("tag");
        await cache.RemoveByTagAsync(new[] { "tag1", "tag2" });
    }

    [Fact]
    public async Task GetOrCreateAsync_records_Bypass_miss_when_BypassCache_is_set()
    {
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.misses", "Routing");
        using var _ = listener;

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
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.GetOrCreateAsync(
            "bypass:miss",
            _ => Task.FromResult("v"),
            new CacheCallOptions { BypassCache = true },
            expiration: null,
            localExpiration: null,
            CancellationToken.None);

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.miss_reason" && (string?)t.Value == "Bypass"));
    }

    [Fact]
    public async Task GetOrCreateAsync_records_Disabled_miss_when_Enabled_is_false()
    {
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.misses", "Routing");
        using var _ = listener;

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
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.GetOrCreateAsync("disabled:miss", _ => Task.FromResult("v"));

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.miss_reason" && (string?)t.Value == "Disabled"));
    }
}

