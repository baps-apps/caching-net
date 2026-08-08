namespace Caching.NET.Options;

/// <summary>
/// Cross-instance invalidation channel. When enabled, a write, removal or tag invalidation on one
/// pod evicts the corresponding L1 entry on every other pod, so the in-process layer cannot serve
/// data another pod has already replaced.
/// </summary>
public sealed class CacheBackplaneOptions
{
    /// <summary>
    /// Enable the backplane. Requires a Redis connection. Only meaningful for
    /// <see cref="CacheMode.Hybrid"/>: <see cref="CacheMode.Redis"/> has no L1 entries to
    /// invalidate and <see cref="CacheMode.InMemory"/> has no shared channel.
    /// Default <c>false</c>; <c>AddCaching</c> turns it on automatically for Hybrid mode
    /// unless it is explicitly set.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Pub/sub channel prefix. Defaults to the cache key prefix so that applications sharing a
    /// Redis instance do not receive each other's invalidations.
    /// </summary>
    public string? ChannelPrefix { get; set; }

    /// <summary>
    /// Block startup until the backplane subscription is established. Default <c>false</c> so a
    /// slow or unavailable Redis cannot stall a pod's readiness.
    /// </summary>
    public bool WaitForInitialSubscribe { get; set; }
}
