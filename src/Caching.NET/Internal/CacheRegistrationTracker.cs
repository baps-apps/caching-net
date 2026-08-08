using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Internal;

/// <summary>
/// Tracks the cache names registered against one <see cref="IServiceCollection"/> so that a
/// duplicate registration fails at startup with an actionable message instead of silently
/// producing two caches that share a key space.
/// </summary>
/// <remarks>
/// The tracker instance is stored in the service collection itself, not in a static field, so
/// independent hosts in the same process (tests, multi-tenant hosting) never interfere.
/// </remarks>
internal sealed class CacheRegistrationTracker
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private bool _defaultRegistered;

    public static CacheRegistrationTracker ForServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(CacheRegistrationTracker)
                && descriptor.ImplementationInstance is CacheRegistrationTracker existing)
            {
                return existing;
            }
        }

        var tracker = new CacheRegistrationTracker();
        services.AddSingleton(tracker);
        return tracker;
    }

    public void Claim(string cacheName, bool isDefault)
    {
        if (!_names.Add(cacheName))
        {
            throw new InvalidOperationException(
                $"A Caching.NET cache named '{cacheName}' is already registered. Cache names must be unique: give the second registration a different CacheName, or remove the duplicate AddCaching call.");
        }

        if (isDefault)
        {
            if (_defaultRegistered)
            {
                throw new InvalidOperationException(
                    "A default Caching.NET cache is already registered. Only one unnamed AddCaching registration is allowed; register additional caches with AddCaching(\"name\", ...).");
            }

            _defaultRegistered = true;
        }
    }

    public IReadOnlyCollection<string> Names => _names;
}
