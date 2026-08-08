namespace Caching.NET.Options;

/// <summary>
/// Default per-entry behavior applied to every cache operation that does not supply its own
/// entry options.
/// </summary>
public sealed class CacheEntryOptions
{
    /// <summary>
    /// Lifetime of the copy stored in the distributed (Redis) layer. When <c>null</c> the entry
    /// uses <see cref="CachingOptions.DefaultExpiration"/>. Only meaningful for
    /// <see cref="CacheMode.Redis"/> and <see cref="CacheMode.Hybrid"/>.
    /// </summary>
    public TimeSpan? DistributedExpiration { get; set; }

    /// <summary>
    /// Lifetime of the copy stored in the in-process memory layer (L1). When <c>null</c> the entry
    /// uses <see cref="CachingOptions.DefaultExpiration"/>. A shorter L1 duration bounds how
    /// long a pod can serve data written by another pod when no backplane is enabled.
    /// Ignored in <see cref="CacheMode.Redis"/>, which does not keep entries in memory.
    /// </summary>
    public TimeSpan? LocalExpiration { get; set; }

    /// <summary>
    /// Fraction of the entry lifetime after which a read triggers a non-blocking background
    /// refresh while still returning the current value. For example <c>0.8</c> refreshes in the
    /// last 20% of the entry's life. <c>null</c> disables eager refresh. Valid range is
    /// <c>(0.0, 1.0)</c>.
    /// </summary>
    public float? EagerRefreshThreshold { get; set; }

    /// <summary>
    /// Ceiling on the random amount added to an entry's duration so that entries created together do
    /// not all expire together. Must not be negative. Default 2 seconds. Set to
    /// <see cref="TimeSpan.Zero"/> to disable jitter entirely.
    /// </summary>
    /// <remarks>
    /// This is a <b>cap</b>, not the jitter itself: the applied window is
    /// <c>min(duration × <see cref="JitterFraction"/>, this)</c>. It only becomes the jitter outright
    /// when <see cref="JitterFraction"/> is <c>null</c>.
    /// </remarks>
    public TimeSpan JitterMaxDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Jitter as a fraction of the entry's own lifetime. Valid range <c>(0.0, 1.0]</c>. Default
    /// <c>0.1</c> (10%). Set to <c>null</c> to use <see cref="JitterMaxDuration"/> as a flat absolute
    /// window instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jitter exists to stop entries created together from expiring together, so it only makes sense
    /// relative to how long the entry lives. A flat window does not scale: 2 seconds against a
    /// 10-minute entry is a rounding error, but against a 300 ms entry it is <b>seven times</b> the
    /// requested lifetime — the entry outlives its own duration by a factor of seven, which is not
    /// spreading load, it is ignoring the caller.
    /// </para>
    /// <para>
    /// The applied window is <c>min(duration × this, <see cref="JitterMaxDuration"/>)</c>, where
    /// <c>duration</c> is the <i>shortest</i> lifetime governing the entry — its logical duration, or
    /// <see cref="LocalExpiration"/>/<see cref="DistributedExpiration"/> when either is set and
    /// shorter. With the defaults a 10-minute entry still gets 2 s (60 s proposed, capped), so
    /// long-lived entries are unaffected, while a 300 ms entry gets 30 ms.
    /// </para>
    /// </remarks>
    public double? JitterFraction { get; set; } = 0.1;

    /// <summary>Eviction priority for the in-process memory layer. Default <see cref="CacheEntryPriority.Normal"/>.</summary>
    public CacheEntryPriority Priority { get; set; } = CacheEntryPriority.Normal;

    /// <summary>
    /// Optional relative size charged against <see cref="MemorySizeLimit"/> for entries that do not
    /// declare their own size. Required when a memory size limit is configured, and must be greater
    /// than zero — a size of <c>0</c> charges nothing, so the limit could never be reached.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Optional cap on the in-process memory layer, expressed as a ceiling on the <b>sum of the
    /// <see cref="Size"/> values the cached entries declare</b>. It is <b>not</b> a byte or megabyte
    /// budget: Caching.NET cannot measure the memory footprint of an arbitrary cached object, so
    /// nothing here is weighed in bytes.
    /// </summary>
    /// <remarks>
    /// With the default <see cref="Size"/> of <c>1</c> (what
    /// <see cref="CachingBuilder.WithMemorySizeLimit(long, long)"/> sets) every entry charges one
    /// unit, so this is simply a cap on the <b>number of entries</b> held in memory. To make it
    /// approximate bytes, give each entry a <see cref="Size"/> in the unit you choose (per call via
    /// <c>CacheEntryOverrides.Size</c>, or here as the default) and set this limit in the same unit.
    /// When a limit is set, an entry with no size — neither per call nor via <see cref="Size"/> —
    /// is <b>not cached at all</b>.
    /// </remarks>
    public long? MemorySizeLimit { get; set; }

    /// <summary>
    /// When <c>true</c>, values handed back from the memory layer are deep-cloned so callers cannot
    /// mutate the cached instance. Costs an extra serialization round-trip per hit. Default <c>false</c>.
    /// </summary>
    public bool EnableAutoClone { get; set; }
}
