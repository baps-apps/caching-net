using Caching.NET;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Benchmark;

/// <summary>
/// Builds benchmark caches the same way an application would: through AddCaching only.
/// </summary>
public static class CacheHostFactory
{
    public static (ServiceProvider Provider, ICacheService Cache) Create(
        Action<CachingBuilder> configure,
        string? cacheName = null)
    {
        var services = new ServiceCollection();

        void Configure(CachingBuilder cache)
        {
            cache.WithApplicationPrefix("bench").WithJitter(TimeSpan.Zero);
            configure(cache);
        }

        // The cache name comes from the registration, so a benchmark that wants a distinct
        // cache.name dimension has to register a named cache rather than set a property.
        if (cacheName is null)
        {
            services.AddCaching(Configure);
        }
        else
        {
            services.AddCaching(cacheName, Configure);
        }

        var provider = services.BuildServiceProvider();
        var cache = cacheName is null
            ? provider.GetRequiredService<ICacheService>()
            : provider.GetRequiredKeyedService<ICacheService>(cacheName);

        return (provider, cache);
    }

    public sealed record Payload(int Id, string Name, string Description, DateTimeOffset UpdatedAt)
    {
        public static Payload Sample(int id) => new(
            id,
            $"item-{id}",
            new string('d', 256),
            DateTimeOffset.UnixEpoch.AddSeconds(id));
    }
}
