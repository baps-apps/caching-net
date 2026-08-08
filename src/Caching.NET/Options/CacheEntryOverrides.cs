namespace Caching.NET.Options;

/// <summary>
/// Per-call overrides applied on top of the cache's configured defaults. Every property is
/// nullable; <c>null</c> means "use the configured value".
/// </summary>
/// <remarks>
/// Overrides are <b>additive</b>. Supplying one property changes that property and nothing else —
/// the cache mode, the key guard, and every unspecified setting are preserved. There is no way to
/// build an options object that escapes the configured defaults.
/// <para>
/// There is deliberately no single overall duration knob. A cache entry has two independent
/// lifetimes, and collapsing them into one hides which layer a value expired from. Set
/// <see cref="LocalExpiration"/> for the in-process copy, <see cref="DistributedExpiration"/> for the
/// shared copy, or <b>both</b> to give the entry one lifetime everywhere.
/// </para>
/// <para>
/// Unlike the same settings on <see cref="CachingOptions"/>, values set here are <b>not validated</b>:
/// they are applied per call, long after startup validation has run, so an out-of-range
/// <see cref="EagerRefreshThreshold"/> or a negative expiration is accepted silently rather than
/// failing the application. Configure the defaults in <see cref="CachingOptions"/>, where
/// <c>CachingOptionsValidator</c> checks them, and keep per-call overrides to values you already
/// trust.
/// </para>
/// </remarks>
/// <example>
/// <code><![CDATA[
/// await cache.SetAsync("Order:42", order, new CacheEntryOverrides
/// {
///     DistributedExpiration = TimeSpan.FromMinutes(1)
/// });
/// ]]></code>
/// </example>
public sealed class CacheEntryOverrides
{
    /// <summary>Lifetime of the copy held in the in-process memory layer.</summary>
    public TimeSpan? LocalExpiration { get; set; }

    /// <summary>Lifetime of the copy held in the distributed layer.</summary>
    public TimeSpan? DistributedExpiration { get; set; }

    /// <summary>
    /// Flat maximum random amount added to the entry's duration to spread expirations. Setting this
    /// applies it as an <b>absolute</b> window for this call, bypassing
    /// <see cref="CacheEntryOptions.JitterFraction"/> entirely.
    /// </summary>
    public TimeSpan? JitterMaxDuration { get; set; }

    /// <summary>
    /// Jitter as a fraction of this entry's own lifetime, overriding
    /// <see cref="CacheEntryOptions.JitterFraction"/>. Valid range <c>(0.0, 1.0]</c>. Ignored when
    /// <see cref="JitterMaxDuration"/> is also set on this call.
    /// </summary>
    /// <remarks>
    /// When a call overrides <see cref="LocalExpiration"/> or <see cref="DistributedExpiration"/>,
    /// jitter is recomputed from the new, shorter lifetime — so shortening an entry for one call
    /// shortens its jitter with it, instead of leaving a window sized for the configured default.
    /// </remarks>
    public double? JitterFraction { get; set; }

    /// <summary>
    /// Fraction of the entry lifetime after which a read triggers a non-blocking background
    /// refresh while still returning the current value. Valid range <c>(0.0, 1.0)</c>.
    /// </summary>
    public float? EagerRefreshThreshold { get; set; }

    /// <summary>Whether an expired value may be served when the factory fails or times out.</summary>
    public bool? FailSafe { get; set; }

    /// <summary>How long past expiration a value stays eligible for fail-safe.</summary>
    public TimeSpan? FailSafeMaxDuration { get; set; }

    /// <summary>Minimum interval between two factory retries while fail-safe is serving.</summary>
    public TimeSpan? FailSafeThrottleDuration { get; set; }

    /// <summary>After this, a stale value is returned and the factory continues in the background.</summary>
    public TimeSpan? FactorySoftTimeout { get; set; }

    /// <summary>After this, the factory is abandoned even when no stale value exists.</summary>
    public TimeSpan? FactoryHardTimeout { get; set; }

    /// <summary>After this, the distributed layer is skipped when a memory value is available.</summary>
    public TimeSpan? DistributedSoftTimeout { get; set; }

    /// <summary>After this, the distributed layer is abandoned for this operation.</summary>
    public TimeSpan? DistributedHardTimeout { get; set; }

    /// <summary>Whether distributed writes may complete after the caller has been released.</summary>
    public bool? AllowBackgroundDistributedOperations { get; set; }

    /// <summary>Whether backplane publishes may complete after the caller has been released.</summary>
    public bool? AllowBackgroundBackplaneOperations { get; set; }

    /// <summary>Whether values handed back from the memory layer are deep-cloned.</summary>
    public bool? EnableAutoClone { get; set; }

    /// <summary>Eviction priority in the in-process memory layer.</summary>
    public CacheEntryPriority? Priority { get; set; }

    /// <summary>Relative size charged against the memory layer's configured size limit.</summary>
    public long? Size { get; set; }

    /// <summary>
    /// Suppresses the cross-instance invalidation broadcast for this write. Other instances keep
    /// serving their current in-process copy until it expires on its own.
    /// </summary>
    /// <remarks>
    /// Intended for bulk warm-up: writing many entries at startup without publishing one
    /// invalidation per entry to every other instance.
    /// </remarks>
    public bool? SkipBackplaneNotification { get; set; }
}
