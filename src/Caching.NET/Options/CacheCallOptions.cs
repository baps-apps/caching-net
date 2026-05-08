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
    /// <para>
    /// <b>Backend availability:</b> <see cref="CacheMode.InMemory"/> resolves in every app-level mode
    /// (<c>IMemoryCache</c> + <c>InMemoryCacheService</c> are registered for InMemory, Redis, and Hybrid
    /// when <see cref="CacheOptions.Enabled"/> is <c>true</c>). <see cref="CacheMode.Redis"/> and
    /// <see cref="CacheMode.Hybrid"/> overrides only resolve when the corresponding service was
    /// registered at startup; specifying them in an InMemory-only app throws.
    /// </para>
    /// <para>
    /// <b>Caveats when mixing modes per call:</b> all modes share the same <see cref="CacheOptions.KeyPrefix"/>,
    /// so a local-only write is not visible to other instances and a follow-up read without the override
    /// will miss against the configured backend. Apply the override consistently to all reads and writes
    /// for a given logical key. Hybrid-only features (<see cref="Tags"/>, <see cref="SlidingExpiration"/>,
    /// <see cref="AllowStaleFor"/>) are not honoured when overriding a Hybrid app to InMemory.
    /// </para>
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
    /// Honoured only by <c>GetOrCreateAsync</c> (including extension overloads that route to it).
    /// Set operations ignore this flag; use <c>RefreshAsync</c> or a remove-then-set sequence instead.
    /// </summary>
    public bool ForceRefresh { get; init; }

    /// <summary>
    /// When true (default), concurrent GetOrCreateAsync calls for the same key are coalesced via
    /// the striped lock manager: the first caller runs the factory while others await its result.
    /// Set false to opt out of coalescing for a specific call.
    /// </summary>
    public bool CoalesceConcurrent { get; init; } = true;

    /// <summary>
    /// Per-call factory timeout. When null, the application-level
    /// <see cref="CacheOptions.FactoryTimeout"/> applies.
    /// </summary>
    public TimeSpan? FactoryTimeout { get; init; }

    /// <summary>
    /// Per-call absolute expiration override. When null, the call-site
    /// <c>expiration</c> argument or the application default applies.
    /// </summary>
    public TimeSpan? AbsoluteExpiration { get; init; }

    /// <summary>
    /// Per-call sliding expiration. Resets the entry's TTL on each access.
    /// Honoured by InMemory and Redis modes; ignored by Hybrid (HybridCache
    /// does not expose sliding-expiration semantics).
    /// </summary>
    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>
    /// Stale-while-revalidate window: after the absolute expiration the entry
    /// continues to serve for up to this duration while a single background
    /// refresh runs. Honoured by InMemory and Redis modes; ignored by Hybrid.
    /// </summary>
    public TimeSpan? AllowStaleFor { get; init; }

    /// <summary>
    /// Tag identifiers associated with this entry. Tags are honoured only when
    /// <see cref="CacheOptions.Mode"/> is <see cref="CacheMode.Hybrid"/>;
    /// in other modes they are ignored unless <c>CachingBuilder.RequireTagSupport()</c>
    /// has been called (which fails startup if Mode is not Hybrid).
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Per-call jitter override (0.0 disables; 0.50 = ±50%). Range 0.0–0.5.
    /// When null, <see cref="CacheOptions.TtlJitterPercentage"/> applies.
    /// </summary>
    public double? JitterPercentage { get; init; }
}
