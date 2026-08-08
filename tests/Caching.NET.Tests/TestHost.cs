using Caching.NET;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

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

    public static IFusionCache Cache(this ServiceProvider provider) => provider.GetRequiredService<IFusionCache>();

    public static IFusionCache NamedCache(this ServiceProvider provider, string cacheName)
        => provider.GetRequiredKeyedService<IFusionCache>(cacheName);
}
