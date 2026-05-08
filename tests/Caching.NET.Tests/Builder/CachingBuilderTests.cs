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
    public void AddCaching_ZeroConfig_Throws_When_KeyPrefix_NotProvided()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();

        var ex = Assert.Throws<OptionsValidationException>(() => services.BuildServiceProvider().ValidateCacheRegistration());
        Assert.Contains("KeyPrefix", ex.Message);
    }

    [Fact]
    public void AddCaching_WithConfiguration_RegistersICacheService()
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

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);
    }

    [Fact]
    public void AddCaching_FluentCodeFirst_RegistersICacheService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithKeyPrefix("test"));
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
        services.AddCaching(cache => cache.Disable().WithKeyPrefix("test"));
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
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, cache => cache.UseHybrid("localhost:6379"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(CacheMode.Hybrid, options.Mode);
    }

    [Fact]
    public void AddCaching_FluentWithOpenTelemetry_DoesNotThrow()
    {
        // v2: WithOpenTelemetry is a documentation no-op; telemetry is always emitted via static
        // CacheInstruments. This test asserts the builder method continues to compile and run.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithKeyPrefix("test")
            .WithOpenTelemetry());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ICacheService>());
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
            .WithKeyPrefix("test-scope"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(20), options.GetDefaultExpiration());
        Assert.Equal(TimeSpan.FromMinutes(8), options.GetDefaultLocalExpiration());
        Assert.Equal(5_000_000, options.MaximumPayloadBytes);
        Assert.Equal(512, options.MaximumKeyLength);
        Assert.Equal(256, options.MemorySizeLimitMb);
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetFactoryTimeout());
        Assert.Equal("test-scope", options.KeyPrefix);
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
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "hotreload"
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

    [Fact]
    public async Task AddCaching_TogglingEnabledWithSameSampleConfig_RoundTrips()
    {
        // Same registration code must work for Enabled=true and Enabled=false. Mirrors the
        // sample's UseHybrid + WithHealthChecks + RequireTagSupport setup with no Redis available.
        static ServiceProvider Build(bool enabled)
        {
            var config = new Dictionary<string, string?>
            {
                ["CacheOptions:Enabled"] = enabled ? "true" : "false",
                ["CacheOptions:Mode"] = "Hybrid",
                ["CacheOptions:KeyPrefix"] = "sample",
                // Connection string intentionally omitted — when disabled it MUST not be required.
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCaching(configuration, b => b
                .WithKeyPrefix("sample")
                .WithDefaultExpiration(TimeSpan.FromMinutes(10))
                .WithTtlJitter(0.10)
                .WithMaximumKeyLength(512)
                .WithStripedLocks(2048)
                .WithHealthChecks()
                .RequireTagSupport());
            return services.BuildServiceProvider();
        }

        // Disabled path: no connection string required, ICacheService resolves and short-circuits.
        using (var disabled = Build(enabled: false))
        {
            disabled.ValidateCacheRegistration();
            var cache = disabled.GetRequiredService<ICacheService>();
            int factoryHits = 0;
            var v1 = await cache.GetOrCreateAsync("k", _ => { factoryHits++; return Task.FromResult(42); });
            var v2 = await cache.GetOrCreateAsync("k", _ => { factoryHits++; return Task.FromResult(42); });
            Assert.Equal(42, v1);
            Assert.Equal(42, v2);
            Assert.Equal(2, factoryHits); // disabled = factory always runs
        }

        // Enabled=true with the same code path requires the connection string. That validates
        // the inverse contract: missing config surfaces only when caching is actually on.
        var enabledEx = Record.Exception(() => Build(enabled: true).GetRequiredService<ICacheService>());
        Assert.NotNull(enabledEx);
    }

    [Fact]
    public void FluentEnable_Overrides_ConfigDisabled()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "app"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, b => b.Enable().UseInMemory().WithKeyPrefix("app"));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.True(options.Enabled);
    }

    [Fact]
    public async Task WithKeyValidator_RejectsKey_SkipsCache()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "kv"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, b => b
            .UseInMemory()
            .WithKeyPrefix("kv")
            .WithKeyValidator(k => k != "bad"));
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        int hits = 0;
        var a = await cache.GetOrCreateAsync("bad", _ => { hits++; return Task.FromResult(1); });
        var b = await cache.GetOrCreateAsync("bad", _ => { hits++; return Task.FromResult(2); });
        Assert.Equal(1, a);
        Assert.Equal(2, b);
        Assert.Equal(2, hits);

        hits = 0;
        var c = await cache.GetOrCreateAsync("ok", _ => { hits++; return Task.FromResult(10); });
        var d = await cache.GetOrCreateAsync("ok", _ => { hits++; return Task.FromResult(99); });
        Assert.Equal(10, c);
        Assert.Equal(10, d);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void AddCaching_DisabledWithRedisMode_WithoutRedisConnection_DoesNotThrow()
    {
        // When Enabled is false, no caching infrastructure is registered at any level — no
        // backend, no validator pressure, no Redis connection required. RoutingCacheService is
        // still resolvable and short-circuits every call to the factory.
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:Mode"] = "Redis",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);

        using var provider = services.BuildServiceProvider();
        provider.ValidateCacheRegistration();

        var cache = provider.GetRequiredService<Abstractions.ICacheService>();
        Assert.NotNull(cache);
    }
}
