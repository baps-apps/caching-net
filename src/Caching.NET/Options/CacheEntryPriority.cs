namespace Caching.NET.Options;

/// <summary>
/// Eviction priority for an entry held in the in-process memory layer. Entries with a lower
/// priority are evicted first when the memory layer is under size pressure.
/// </summary>
public enum CacheEntryPriority
{
    /// <summary>Evicted first.</summary>
    Low = 0,

    /// <summary>The default.</summary>
    Normal = 1,

    /// <summary>Evicted after <see cref="Normal"/> entries.</summary>
    High = 2,

    /// <summary>Never evicted for size pressure. Still expires normally.</summary>
    NeverRemove = 3
}
