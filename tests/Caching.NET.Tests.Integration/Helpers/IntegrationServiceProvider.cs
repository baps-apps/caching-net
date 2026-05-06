using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Integration.Helpers;

internal static class IntegrationServiceProvider
{
    public static (IServiceProvider sp, ICacheService cache) Build(string redisConnectionString, string keyPrefix, Action<CachingBuilder>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCaching(b =>
        {
            b.UseRedis(redisConnectionString).WithKeyPrefix(keyPrefix);
            extra?.Invoke(b);
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<ICacheService>());
    }
}
