using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Internal;

/// <summary>
/// Owns every disposable resource that belongs to one Caching.NET cache instance: the cache, its
/// memory layer, its Redis connection, its distributed adapter and its telemetry subscriptions.
/// Registered as a keyed singleton so the container disposes the whole graph in one place.
/// </summary>
internal sealed class CacheInstance : IDisposable
{
    private readonly IDisposable?[] _ownedResources;
    private bool _disposed;

    public CacheInstance(
        string cacheName,
        IFusionCache cache,
        CacheGuard guard,
        Telemetry.CacheTelemetryContext telemetry,
        params IDisposable?[] ownedResources)
    {
        CacheName = cacheName;
        Cache = cache;
        Guard = guard;
        Telemetry = telemetry;
        _ownedResources = ownedResources;
    }

    public string CacheName { get; }

    public IFusionCache Cache { get; }

    public CacheGuard Guard { get; }

    public Telemetry.CacheTelemetryContext Telemetry { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Registration order is teardown order: telemetry subscriptions first, then the cache, then
        // its layers, and the Redis connection last, so nothing observes a half-torn-down graph.
        foreach (var resource in _ownedResources)
        {
            resource?.Dispose();
        }
    }
}
