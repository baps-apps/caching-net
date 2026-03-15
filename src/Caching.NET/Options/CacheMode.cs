namespace Caching.NET.Options;

/// <summary>
/// Cache mode: in-memory only, Redis only, or Hybrid (in-memory + optional Redis with stampede protection).
/// </summary>
public enum CacheMode
{
    /// <summary>In-process memory cache only.</summary>
    InMemory = 0,

    /// <summary>Distributed Redis cache only.</summary>
    Redis = 1,

    /// <summary>Hybrid: in-memory + optional Redis tier with stampede protection. Use RedisConnectionString for Redis; omit for in-memory-only.</summary>
    Hybrid = 2
}
