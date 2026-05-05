namespace Caching.NET.Options;

/// <summary>
/// Per-call options for cache operations. Allows callers to override the application-level
/// <see cref="CacheOptions.Mode"/> for a specific operation, bypass the cache, force a refresh,
/// or opt out of stampede coalescing.
/// </summary>
public sealed class CacheCallOptions
{
    /// <summary>
    /// Optional cache mode override for this call. When <c>null</c>, the application-level
    /// <see cref="CacheOptions.Mode"/> is used.
    /// </summary>
    public CacheMode? Mode { get; init; }

    /// <summary>
    /// When true, the cache is not read or written for this call; the factory is always executed
    /// and the result returned without caching. Default: false.
    /// </summary>
    public bool BypassCache { get; init; }

    /// <summary>
    /// When true, the factory is always executed; the result is then written to the cache and returned.
    /// Use to refresh stale data without removing the key first. Default: false.
    /// </summary>
    public bool ForceRefresh { get; init; }

    /// <summary>
    /// When true (default), concurrent GetOrCreateAsync calls for the same key are coalesced via
    /// the striped lock manager: the first caller runs the factory while others await its result.
    /// Set false to opt out of coalescing for a specific call.
    /// </summary>
    public bool CoalesceConcurrent { get; init; } = true;
}
