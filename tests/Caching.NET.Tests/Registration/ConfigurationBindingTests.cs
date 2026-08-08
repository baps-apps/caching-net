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

    private static CachingOptions Resolve(IConfiguration configuration, string cacheName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<CachingOptions>>().Get(cacheName);
    }
}
