using Caching.NET;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests;

/// <summary>
/// Builds a service provider wired the way a consuming application would wire one — through
/// AddCaching only.
/// </summary>
internal static class TestHost
{
    public static ServiceProvider Build(Action<CachingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddCaching(configure);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    public static ServiceProvider BuildInMemory(Action<CachingBuilder>? extra = null)
        => Build(cache =>
        {
            cache.UseInMemory().WithApplicationPrefix("tests");
            extra?.Invoke(cache);
        });

    /// <summary>
    /// Registers a single cache under a name. A cache's name always comes from its registration, so
    /// this is the only way to give one a <c>cache.name</c> other than <c>default</c> — which
    /// matters for any assertion that has to isolate one cache's measurements from the rest of the
    /// process.
    /// </summary>
    public static ServiceProvider BuildNamed(string cacheName, Action<CachingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddCaching(cacheName, configure);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    public static ICacheService Cache(this ServiceProvider provider) => provider.GetRequiredService<ICacheService>();

    public static ICacheService NamedCache(this ServiceProvider provider, string cacheName)
        => provider.GetRequiredKeyedService<ICacheService>(cacheName);

    public static ICacheService DisabledCache(this ServiceProvider provider) => provider.Cache();

    /// <summary>
    /// The jitter policy Caching.NET resolved for a cache. Used by tests that call the override
    /// mapper directly, which the adapter always feeds the cache's own policy.
    /// </summary>
    internal static global::Caching.NET.Internal.JitterPolicy JitterPolicy(
        this ServiceProvider provider, string cacheName = CachingDefaults.DefaultCacheName)
        => global::Caching.NET.Internal.CacheEngineFactory.JitterPolicyFor(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<global::Caching.NET.Options.CachingOptions>>()
                .Get(cacheName));

    /// <summary>
    /// The raw engine cache behind the default Caching.NET cache. Used only by tests that assert how
    /// Caching.NET maps onto the engine.
    /// </summary>
    internal static ZiggyCreatures.Caching.Fusion.IFusionCache EngineCache(this ServiceProvider provider)
        => ((global::Caching.NET.Internal.FusionCacheService)provider
            .GetRequiredKeyedService<global::Caching.NET.Internal.CacheInstance>(CachingDefaults.DefaultCacheName).Cache).Inner;
}
