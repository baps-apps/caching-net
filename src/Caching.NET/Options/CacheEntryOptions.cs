using Microsoft.Extensions.Caching.Memory;

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
    /// Maximum random amount added to an entry's duration so that entries created together do not
    /// all expire together. Must not be negative. Default 2 seconds.
    /// </summary>
    public TimeSpan JitterMaxDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Eviction priority for the in-process memory layer. Default <see cref="CacheItemPriority.Normal"/>.</summary>
    public CacheItemPriority Priority { get; set; } = CacheItemPriority.Normal;

    /// <summary>
    /// Optional relative size charged against <see cref="MemorySizeLimitMegabytes"/> for entries
    /// that do not declare their own size. Required when a memory size limit is configured.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Optional cap on the in-process memory layer, in megabytes. When set, every entry must have
    /// a size (either per call or via <see cref="Size"/>) or it will not be cached.
    /// </summary>
    public long? MemorySizeLimitMegabytes { get; set; }

    /// <summary>
    /// When <c>true</c>, values handed back from the memory layer are deep-cloned so callers cannot
    /// mutate the cached instance. Costs an extra serialization round-trip per hit. Default <c>false</c>.
    /// </summary>
    public bool EnableAutoClone { get; set; }
}
