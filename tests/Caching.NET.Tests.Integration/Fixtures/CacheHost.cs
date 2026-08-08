using Caching.NET;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

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

    public IFusionCache Cache => _provider.GetRequiredService<IFusionCache>();

    public ICacheProvider Provider => _provider.GetRequiredService<ICacheProvider>();

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

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
