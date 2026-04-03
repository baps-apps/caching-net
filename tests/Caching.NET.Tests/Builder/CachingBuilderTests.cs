using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Builder;

public class CachingBuilderTests
{
    [Fact]
    public void AddCaching_ZeroConfig_RegistersICacheService_HybridEnabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(CacheMode.Hybrid, options.Mode);
    }

    [Fact]
    public void AddCaching_WithConfiguration_RegistersICacheService()
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
        Assert.NotNull(cache);
    }

    [Fact]
    public void AddCaching_FluentCodeFirst_RegistersICacheService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory());
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(CacheMode.InMemory, options.Mode);
    }

    [Fact]
    public void AddCaching_FluentDisable_StillRegistersICacheService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.Disable());
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.False(options.Enabled);
    }

    [Fact]
    public void AddCaching_FluentOverridesConfig_FluentWins()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, cache => cache.UseHybrid());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(CacheMode.Hybrid, options.Mode);
    }

    [Fact]
    public void AddCaching_FluentWithOpenTelemetry_RegistersOtelTelemetry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithOpenTelemetry());
        using var provider = services.BuildServiceProvider();

        var telemetry = provider.GetRequiredService<ICacheTelemetry>();
        Assert.IsType<Caching.NET.Telemetry.OpenTelemetryCacheTelemetry>(telemetry);
    }

    [Fact]
    public void AddCaching_FluentWithExpiration_SetsOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithDefaultExpiration(TimeSpan.FromMinutes(20))
            .WithDefaultLocalExpiration(TimeSpan.FromMinutes(8))
            .WithMaximumPayloadBytes(5_000_000)
            .WithMaximumKeyLength(512)
            .WithMemorySizeLimit(256)
            .WithFactoryTimeout(TimeSpan.FromSeconds(30))
            .WithInstanceName("test:"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(20), options.GetDefaultExpiration());
        Assert.Equal(TimeSpan.FromMinutes(8), options.GetDefaultLocalExpiration());
        Assert.Equal(5_000_000, options.MaximumPayloadBytes);
        Assert.Equal(512, options.MaximumKeyLength);
        Assert.Equal(256, options.MemorySizeLimitMb);
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetFactoryTimeout());
        Assert.Equal("test:", options.RedisInstanceName);
    }

    [Fact]
    public void AddCaching_UseRedis_WithEmptyConnectionString_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Assert.Throws<ArgumentException>(() =>
            services.AddCaching(cache => cache.UseRedis("")));
    }

    [Fact]
    public async Task AddCaching_HotReload_DisabledAtRuntime_ShortCircuitsToFactory()
    {
        var configData = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        // Cache a value while enabled
        var first = await cache.GetOrCreateAsync("hotreload:1", _ => Task.FromResult("cached"));
        Assert.Equal("cached", first);

        // Verify it's cached
        var second = await cache.GetOrCreateAsync("hotreload:1", _ => Task.FromResult("other"));
        Assert.Equal("cached", second);

        // Disable via config reload
        configuration["CacheOptions:Enabled"] = "false";
        configuration.Reload();

        // Now factory should run every time
        var third = await cache.GetOrCreateAsync("hotreload:1", _ => Task.FromResult("fresh"));
        Assert.Equal("fresh", third);
    }
}
