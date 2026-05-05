namespace Caching.NET.Options;

/// <summary>
/// Configuration options for caching (InMemory, Redis, or Hybrid).
/// v2 schema. KeyPrefix is mandatory. RedisInstanceName has been removed.
/// </summary>
public sealed class CacheOptions
{
    /// <summary>
    /// Required key prefix prepended to every cache key by the routing layer. Must be non-empty
    /// and match <c>^[a-zA-Z0-9][a-zA-Z0-9._:-]*$</c> (no whitespace, '*' or '?'). Replaces v1's
    /// <c>RedisInstanceName</c>: applies uniformly across InMemory/Redis/Hybrid (not just Redis).
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Cache mode: InMemory, Redis, or Hybrid. Default: InMemory (zero-config friendly).
    /// In v1 this defaulted to Hybrid (which allowed Redis-less hybrid); v2 makes
    /// RedisConnectionString mandatory for both Redis and Hybrid, so InMemory is a safer default.
    /// </summary>
    public CacheMode Mode { get; set; } = CacheMode.InMemory;

    /// <summary>
    /// Connection string for Redis. Required when Mode is Redis or Hybrid (omit for in-memory-only Hybrid is no longer supported in v2).
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// When true (v2 default), Redis TLS certificate validation is strict: any SSL policy errors
    /// cause the connection to be rejected. When false, allow RemoteCertificateNameMismatch only.
    /// Default flipped from false (v1) to true (v2) for production safety.
    /// </summary>
    public bool StrictRedisCertificateValidation { get; set; } = true;

    /// <summary>
    /// Whether caching is enabled. Hot-reloadable. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true (default), cache failures fall back to the source (factory) instead of throwing.
    /// </summary>
    public bool FailOpen { get; set; } = true;

    /// <summary>
    /// When true, cache write/read failures throw instead of being logged and ignored.
    /// Only applies when <see cref="FailOpen"/> is false.
    /// </summary>
    public bool ThrowOnFailure { get; set; }

    /// <summary>
    /// Default expiration for cache entries. Default 10 minutes.
    /// </summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// TTL jitter as a fraction of the expiration window (0.0 disables jitter; 0.10 = ±10%).
    /// </summary>
    public double TtlJitterPercentage { get; set; } = 0.10;

    /// <summary>
    /// Maximum length of a cache key in characters. Required (default 512).
    /// </summary>
    public int MaximumKeyLength { get; set; } = 512;

    /// <summary>
    /// Maximum size of a cache entry in bytes. Default 1 MiB.
    /// </summary>
    public long MaximumPayloadBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Number of striped lock slots for stampede protection. Rounded up to power of 2. Default 1024.
    /// </summary>
    public int StripeLockCount { get; set; } = 1024;

    /// <summary>
    /// Maximum concurrent in-flight stale-while-revalidate refreshes. Default 256.
    /// </summary>
    public int StaleRefreshConcurrency { get; set; } = 256;

    /// <summary>
    /// Per-call timeout for the factory delegate passed to GetOrCreateAsync. Default 30s.
    /// </summary>
    public TimeSpan FactoryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-op timeout for individual Redis operations (read/write/delete). Default 2s.
    /// </summary>
    public TimeSpan RedisOperationTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Optional size limit for the in-memory cache in megabytes. When set, IMemoryCache uses size-based eviction
    /// and consumers must declare entry Size. Default null (no limit) until Task 11 wires Size hints into entries.
    /// </summary>
    public long? MemorySizeLimitMb { get; set; }

    /// <summary>
    /// Optional in-memory tier expiration for Hybrid mode. When null, Hybrid uses internal default.
    /// </summary>
    public TimeSpan? HybridLocalCacheExpiration { get; set; }

    /// <summary>
    /// When true, raw cache keys appear in log messages. Default false (only hash). Dev-only toggle.
    /// </summary>
    public bool IncludeRawKeyInLogs { get; set; }

    /// <summary>
    /// When true, key hash (xxHash64 hex) is added as a tag on Activity. Default false.
    /// </summary>
    public bool IncludeKeyHashInTraces { get; set; }

    /// <summary>
    /// Returns <see cref="FactoryTimeout"/>. Provided for service-layer callers that previously
    /// consumed a nullable string-parsed TimeSpan; in v2 the timeout is always set.
    /// </summary>
    public TimeSpan? GetFactoryTimeout() => FactoryTimeout;

    /// <summary>
    /// Returns <see cref="DefaultExpiration"/>.
    /// </summary>
    public TimeSpan? GetDefaultExpiration() => DefaultExpiration;

    /// <summary>
    /// Returns <see cref="HybridLocalCacheExpiration"/>.
    /// </summary>
    public TimeSpan? GetDefaultLocalExpiration() => HybridLocalCacheExpiration;
}
