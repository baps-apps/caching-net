namespace Caching.NET.Options;

/// <summary>
/// Per-call options for cache operations.
/// Allows callers to override the application-level <see cref="CacheOptions.Mode"/>
/// for a specific operation. Use for enterprise patterns: bypass cache, force refresh, or route to a different tier.
/// </summary>
public sealed class CacheCallOptions
{
    /// <summary>
    /// Optional cache mode override for this call.
    /// When <c>null</c>, the application-level <see cref="CacheOptions.Mode"/> is used.
    /// </summary>
    public CacheMode? OverrideMode { get; init; }

    /// <summary>
    /// When true, the cache is not read or written for this call; the factory is always executed and the result returned without caching.
    /// Useful for debugging or emergency "cache off" at a specific callsite.
    /// </summary>
    public bool? BypassCache { get; init; }

    /// <summary>
    /// When true, the factory is always executed; the result is then written to the cache and returned.
    /// Use to refresh stale data without removing the key first.
    /// </summary>
    public bool? ForceRefresh { get; init; }

    /// <summary>
    /// When true, concurrent GetOrCreateAsync calls for the same key on this process are coalesced:
    /// the first caller runs the factory while others await its result. Useful for non-Hybrid modes
    /// (InMemory/Redis) to reduce stampede-like behavior without enabling Hybrid.
    /// </summary>
    public bool? CoalesceConcurrent { get; init; }
}

