using System.Collections.Frozen;

namespace Caching.NET.Internal;

/// <summary>
/// Default <see cref="ICacheProvider"/>. Built once from the registrations the container already
/// holds; the lookup table is frozen at construction, so resolution is a dictionary probe with no
/// locking and no mutable shared state.
/// </summary>
internal sealed class CacheProvider : ICacheProvider
{
    private readonly FrozenDictionary<string, CacheRegistration> _byName;
    private readonly CacheRegistration? _default;

    public CacheProvider(IEnumerable<CacheRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var ordered = registrations.ToArray();
        CacheNames = Array.ConvertAll(ordered, static r => r.CacheName);
        _byName = ordered.ToFrozenDictionary(static r => r.CacheName, StringComparer.Ordinal);
        _default = Array.Find(ordered, static r => r.IsDefault);
    }

    public IReadOnlyList<string> CacheNames { get; }

    public ICacheService Default => _default is not null
        ? _default.Instance.Cache
        : throw new InvalidOperationException(
            "No default Caching.NET cache is registered. Call services.AddCaching(...) without a cache name, or resolve a named cache with ICacheProvider.GetCache(name).");

    public ICacheService GetCache(string cacheName) => Require(cacheName).Instance.Cache;

    public ICacheService? GetCacheOrNull(string cacheName)
        => _byName.TryGetValue(cacheName, out var registration) ? registration.Instance.Cache : null;

    public ICacheGuard GetGuard(string cacheName) => Require(cacheName).Instance.Guard;

    private CacheRegistration Require(string cacheName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);

        if (_byName.TryGetValue(cacheName, out var registration))
        {
            return registration;
        }

        throw new InvalidOperationException(
            $"No Caching.NET cache is registered with the name '{cacheName}'. Registered caches: {(CacheNames.Count == 0 ? "(none)" : string.Join(", ", CacheNames))}. Register it with services.AddCaching(\"{cacheName}\", ...).");
    }
}
