using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Registration;

public class ConfigurationBindingTests
{
    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void RootConfiguration_BindsTheCachingSection()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:EnvironmentPrefix"] = "prod",
            ["CacheOptions:DefaultExpiration"] = "00:10:00",
            ["CacheOptions:Redis:Configuration"] = "localhost:6379",
            ["CacheOptions:Redis:InstancePrefix"] = "myapp:",
            ["CacheOptions:Redis:KeyVersionPlacement"] = "Suffix",
            ["CacheOptions:Backplane:Enabled"] = "true",
            ["CacheOptions:Serialization:Format"] = "MessagePack",
            ["CacheOptions:Serialization:MaximumPayloadBytes"] = "2048",
            ["CacheOptions:Resilience:FailSafeEnabled"] = "false",
            ["CacheOptions:Security:MaximumKeyLength"] = "300",
            ["CacheOptions:Observability:EnableMetrics"] = "false"
        });

        var options = Resolve(configuration, "default");

        Assert.Equal(CacheMode.Hybrid, options.Mode);
        Assert.Equal("orders-api", options.ApplicationPrefix);
        Assert.Equal("prod", options.EnvironmentPrefix);
        Assert.Equal(TimeSpan.FromMinutes(10), options.DefaultExpiration);
        Assert.Equal("localhost:6379", options.Redis.Configuration);
        Assert.Equal("myapp:", options.Redis.InstancePrefix);
        Assert.Equal(CacheKeyVersionPlacement.Suffix, options.Redis.KeyVersionPlacement);
        Assert.True(options.Backplane.Enabled);
        Assert.Equal(CacheSerializerFormat.MessagePack, options.Serialization.Format);
        Assert.Equal(2048, options.Serialization.MaximumPayloadBytes);
        Assert.False(options.Resilience.FailSafeEnabled);
        Assert.Equal(300, options.Security.MaximumKeyLength);
        Assert.False(options.Observability.EnableMetrics);
    }

    [Fact]
    public void SectionPassedDirectly_BindsTheSameWay()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:ApplicationPrefix"] = "orders-api"
        });

        var options = Resolve(configuration.GetSection("CacheOptions"), "default");

        Assert.Equal("orders-api", options.ApplicationPrefix);
    }

    [Fact]
    public void NamedCachesSection_RegistersEachCache()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:NamedCaches:short-lived:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:NamedCaches:short-lived:DefaultExpiration"] = "00:00:30",
            ["CacheOptions:NamedCaches:reference-data:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:NamedCaches:reference-data:DefaultExpiration"] = "01:00:00"
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);

        using var provider = services.BuildServiceProvider();
        var caches = provider.GetRequiredService<ICacheProvider>();

        Assert.Equal(
            ["default", "reference-data", "short-lived"],
            caches.CacheNames.OrderBy(n => n, StringComparer.Ordinal));

        var monitor = provider.GetRequiredService<IOptionsMonitor<CachingOptions>>();
        Assert.Equal(TimeSpan.FromSeconds(30), monitor.Get("short-lived").DefaultExpiration);
        Assert.Equal(TimeSpan.FromHours(1), monitor.Get("reference-data").DefaultExpiration);
    }

    [Fact]
    public void CacheNameFromConfiguration_CannotRetargetTheRegistration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:CacheName"] = "hijacked"
        });

        var options = Resolve(configuration, "default");

        Assert.Equal("default", options.CacheName);
    }

    [Fact]
    public void KeyPrefix_CombinesApplicationEnvironmentAndTenant()
    {
        var options = new CachingOptions
        {
            ApplicationPrefix = "orders-api",
            EnvironmentPrefix = "prod",
            TenantPrefix = "acme"
        };

        Assert.Equal("orders-api:prod:acme", options.BuildKeyPrefix());
    }

    [Fact]
    public void KeyPrefix_AppendsTheCacheNameForNamedCachesOnly()
    {
        var defaultCache = new CachingOptions { ApplicationPrefix = "orders-api", CacheName = "default" };
        var namedCache = new CachingOptions { ApplicationPrefix = "orders-api", CacheName = "short-lived" };

        // Without this, two named caches in one application would share a Redis key space.
        Assert.Equal("orders-api", defaultCache.BuildKeyPrefix());
        Assert.Equal("orders-api:short-lived", namedCache.BuildKeyPrefix());
    }

    [Theory]
    [InlineData(CacheMode.InMemory, true, false)]
    [InlineData(CacheMode.Redis, false, true)]
    [InlineData(CacheMode.Hybrid, true, true)]
    public void LayerFlags_MatchTheMode(CacheMode mode, bool memory, bool distributed)
    {
        var options = new CachingOptions { Mode = mode };

        Assert.Equal(memory, options.UsesMemoryLayer);
        Assert.Equal(distributed, options.UsesDistributedLayer);
    }

    /// <summary>
    /// The binder rejects an unknown enum <i>name</i>, but accepts any number without checking that
    /// it is defined: <c>"Mode": "7"</c> binds to <c>(CacheMode)7</c>. The validator is what stops it.
    /// </summary>
    [Fact]
    public void UndefinedNumericEnumValue_BindsAndIsRejectedByValidation()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:Mode"] = "7",
            ["CacheOptions:ApplicationPrefix"] = "orders-api"
        });

        var failure = Assert.Throws<OptionsValidationException>(() => Resolve(configuration, "default"));

        Assert.Contains("not a defined CacheMode value", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Hybrid cache keeps an in-process copy on every replica, and only the backplane evicts the
    /// other replicas' copies. <c>UseHybrid(...)</c> has always turned it on; a Hybrid cache bound from
    /// configuration now gets it too unless <c>Backplane:Enabled</c> is set.
    /// </summary>
    [Fact]
    public void HybridWithoutABackplaneSetting_GetsTheBackplane()
    {
        var options = Resolve(HybridConfiguration(), "default");

        Assert.True(options.Backplane.Enabled);
    }

    [Fact]
    public void HybridWithTheBackplaneSetToFalse_KeepsItOff()
    {
        var options = Resolve(HybridConfiguration(("CacheOptions:Backplane:Enabled", "false")), "default");

        Assert.False(options.Backplane.Enabled);
    }

    [Fact]
    public void NamedHybridCacheWithoutABackplaneSetting_GetsTheBackplane()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:NamedCaches:shared:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:NamedCaches:shared:Mode"] = "Hybrid",
            ["CacheOptions:NamedCaches:shared:Redis:Configuration"] = "localhost:6379"
        });

        Assert.False(Resolve(configuration, "default").Backplane.Enabled);
        Assert.True(Resolve(configuration, "shared").Backplane.Enabled);
    }

    [Fact]
    public void RedisModeWithoutABackplaneSetting_KeepsItOff()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:Mode"] = "Redis",
            ["CacheOptions:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:Redis:Configuration"] = "localhost:6379"
        });

        Assert.False(Resolve(configuration, "default").Backplane.Enabled);
    }

    /// <summary>
    /// The default follows the <i>final</i> mode. A test host that overrides a Hybrid appsettings
    /// block with <c>UseInMemory()</c> must not be handed a backplane, which validation rejects
    /// outside Hybrid mode.
    /// </summary>
    [Fact]
    public void HybridOverriddenToInMemoryInCode_DoesNotGetTheBackplane()
    {
        var options = Resolve(HybridConfiguration(), "default", cache =>
        {
            cache.UseInMemory();
            cache.Options.Redis.Configuration = null;
        });

        Assert.False(options.Backplane.Enabled);
    }

    [Fact]
    public void BackplaneTurnedOffInCode_StaysOff()
    {
        var options = Resolve(HybridConfiguration(), "default", cache => cache.WithBackplane(false));

        Assert.False(options.Backplane.Enabled);
    }

    /// <summary>
    /// The default is applied before the fluent overrides, so code that sets the property directly —
    /// not only through <c>WithBackplane</c> — has the last word.
    /// </summary>
    [Fact]
    public void BackplaneSetToFalseOnTheOptionsInCode_StaysOff()
    {
        var options = Resolve(
            HybridConfiguration(), "default", cache => cache.Options.Backplane.Enabled = false);

        Assert.False(options.Backplane.Enabled);
    }

    /// <summary>
    /// Code that switches a non-Hybrid section to Hybrid by setting the mode directly gets no default,
    /// the same as any Hybrid set on the options in code; <c>UseHybrid(...)</c> is the way to get one.
    /// </summary>
    [Fact]
    public void ModeSwitchedToHybridOnTheOptionsInCode_GetsNoDefault()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["CacheOptions:Mode"] = "Redis",
            ["CacheOptions:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:Redis:Configuration"] = "localhost:6379"
        });

        var options = Resolve(configuration, "default", cache => cache.Options.Mode = CacheMode.Hybrid);

        Assert.False(options.Backplane.Enabled);
    }

    private static IConfiguration HybridConfiguration(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:ApplicationPrefix"] = "orders-api",
            ["CacheOptions:Redis:Configuration"] = "localhost:6379"
        };

        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return Configuration(values);
    }

    private static CachingOptions Resolve(
        IConfiguration configuration, string cacheName, Action<CachingBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        if (configure is null)
        {
            services.AddCaching(configuration);
        }
        else
        {
            services.AddCaching(configuration, configure);
        }

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<CachingOptions>>().Get(cacheName);
    }
}
