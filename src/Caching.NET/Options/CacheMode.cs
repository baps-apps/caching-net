namespace Caching.NET.Options;

/// <summary>
/// The cache topology Caching.NET wires up for a cache instance.
/// </summary>
public enum CacheMode
{
    /// <summary>
    /// In-process memory cache only. No Redis connection is opened and no distributed
    /// components are registered. Optimized for single-instance applications.
    /// </summary>
    InMemory = 0,

    /// <summary>
    /// Redis is the single authoritative cache. A local memory layer is still allocated for
    /// in-process stampede protection, but entry reads and writes bypass it, so no instance
    /// can serve a value that Redis has not confirmed.
    /// </summary>
    Redis = 1,

    /// <summary>
    /// Two-level cache: L1 in-process memory, L2 Redis, with an optional Redis backplane for
    /// cross-instance invalidation. L1 is warmed from L2 hits. Intended for multi-pod deployments.
    /// </summary>
    Hybrid = 2
}
