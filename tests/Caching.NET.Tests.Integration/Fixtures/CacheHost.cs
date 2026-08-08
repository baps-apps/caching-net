using Caching.NET;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Integration.Fixtures;

/// <summary>
/// Builds an application-shaped service provider. Each instance stands in for one process ("pod"),
/// so several of them against the same Redis reproduce a multi-pod deployment.
/// </summary>
internal sealed class CacheHost : IDisposable
{
    private readonly ServiceProvider _provider;

    private CacheHost(ServiceProvider provider)
    {
        _provider = provider;
    }

    public ICacheService Cache => _provider.GetRequiredService<ICacheService>();

    public ICacheProvider Provider => _provider.GetRequiredService<ICacheProvider>();

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// Resolves a cache registered under a name. A cache's <c>cache.name</c> telemetry dimension
    /// comes from its registration, so this is how a test isolates its own measurements and spans
    /// from every other cache in the process.
    /// </summary>
    public T ResolveNamed<T>(string cacheName) where T : notnull
        => _provider.GetRequiredKeyedService<T>(cacheName);

    public static CacheHost Create(Action<CachingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddCaching(configure);
        return new CacheHost(services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        }));
    }

    public static CacheHost CreateMulti(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        register(services);
        return new CacheHost(services.BuildServiceProvider());
    }

    public void Dispose() => _provider.Dispose();
}
