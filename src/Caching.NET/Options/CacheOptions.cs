using System.ComponentModel.DataAnnotations;

namespace Caching.NET.Options;

/// <summary>
/// Configuration options for caching (InMemory, Redis, or Hybrid).
/// </summary>
public class CacheOptions
{
    /// <summary>
    /// Whether caching is enabled. When false, <c>RoutingCacheService</c> short-circuits all operations:
    /// <c>GetOrCreateAsync</c> runs the factory directly, and all other methods are no-ops.
    /// <c>ICacheService</c> is always registered regardless of this flag.
    /// Default: false (opt-in). The zero-config <c>AddCaching()</c> overload sets this to true.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Cache mode: InMemory, Redis, or Hybrid.
    /// Default: Hybrid.
    /// </summary>
    public CacheMode Mode { get; set; } = CacheMode.Hybrid;

    /// <summary>
    /// Default expiration time for cache entries as a string (e.g., "00:10:00" for 10 minutes).
    /// When not specified, the concrete cache implementations fall back to their own sensible defaults
    /// (currently 10 minutes) for entries where no explicit expiration is provided.
    /// </summary>
    [RegularExpression(@"^(\d+\.)?[0-9]{1,2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?$", ErrorMessage = "DefaultExpiration must be in TimeSpan format (e.g., 00:10:00 for 10 minutes).")]
    public string? DefaultExpiration { get; set; }

    /// <summary>
    /// Default local (in-memory) cache expiration as a string (e.g., "00:05:00" for 5 minutes). Used for Hybrid mode.
    /// When not specified, Hybrid falls back to the overall expiration or an internal default
    /// (currently 5 minutes) for the in-memory tier when no explicit local expiration is provided.
    /// </summary>
    [RegularExpression(@"^(\d+\.)?[0-9]{1,2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?$", ErrorMessage = "DefaultLocalExpiration must be in TimeSpan format (e.g., 00:05:00 for 5 minutes).")]
    public string? DefaultLocalExpiration { get; set; }

    /// <summary>
    /// Maximum size of a cache entry in bytes. Entries larger than this may be skipped by the cache layer when a limit is set.
    /// When null, no explicit payload-size limit is applied by Caching.NET itself; the underlying cache implementation
    /// (Hybrid or Redis) may still enforce its own limits. For production workloads, it is strongly recommended to set
    /// a conservative value (for example, 1–10 MB) based on your data shapes and network/memory constraints.
    /// </summary>
    [Range(1024, long.MaxValue, ErrorMessage = "MaximumPayloadBytes must be at least 1 KB when specified.")]
    public long? MaximumPayloadBytes { get; set; }

    /// <summary>
    /// Maximum length of a cache key in characters. When set, keys longer than this may bypass the cache for safety
    /// (for example, in Redis mode the operation falls back to the factory and logs a warning).
    /// When null, Caching.NET does not impose its own key-length limit; it is recommended to configure a limit
    /// (for example, 512–1024 characters) in production to avoid pathological key patterns.
    /// </summary>
    [Range(1, 4096, ErrorMessage = "MaximumKeyLength must be between 1 and 4096 when specified.")]
    public int? MaximumKeyLength { get; set; }

    /// <summary>
    /// Connection string for Redis. Required when Mode is Redis; optional for Hybrid (omit for in-memory-only Hybrid).
    /// </summary>
    [MinLength(1, ErrorMessage = "RedisConnectionString cannot be empty when specified.")]
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Redis instance name for key prefixing (e.g., "MyApp:"). Optional.
    /// For multi-tenant or multi-service clusters, use a unique prefix per service (e.g., "myservice:").
    /// </summary>
    [MaxLength(256, ErrorMessage = "RedisInstanceName cannot exceed 256 characters.")]
    public string? RedisInstanceName { get; set; }

    /// <summary>
    /// When true (default), cache failures (e.g., Redis unavailable) cause the operation to fall back to the source (factory)
    /// instead of throwing. When false, exceptions from the cache layer are propagated.
    /// </summary>
    public bool FailOpen { get; set; } = true;

    /// <summary>
    /// When true, cache write/read failures throw instead of being logged and ignored. Only applies when <see cref="FailOpen"/> is false.
    /// Default: false.
    /// </summary>
    public bool ThrowOnFailure { get; set; }

    /// <summary>
    /// Optional timeout for the factory passed to GetOrCreateAsync. When set, the factory is cancelled after this duration.
    /// Use to avoid runaway factory calls in enterprise deployments. Format: "hh:mm:ss" or "d.hh:mm:ss".
    /// </summary>
    [RegularExpression(@"^(\d+\.)?[0-9]{1,2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?$", ErrorMessage = "FactoryTimeout must be in TimeSpan format (e.g., 00:00:30 for 30 seconds).")]
    public string? FactoryTimeout { get; set; }

    /// <summary>
    /// Gets the factory timeout as a TimeSpan, or null if not set.
    /// </summary>
    public TimeSpan? GetFactoryTimeout()
    {
        var timeout = ParseExpiration(FactoryTimeout, nameof(FactoryTimeout));
        if (timeout is null)
        {
            return null;
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"Invalid {nameof(FactoryTimeout)} value '{FactoryTimeout}'. Expected a positive TimeSpan up to 00:05:00. " +
                "Recommended range is between 00:00:01 and 00:05:00 depending on your downstream latency.");
        }

        return timeout;
    }

    /// <summary>
    /// Optional size limit for the in-memory cache in megabytes. When set, passed to IMemoryCache so eviction is size-based.
    /// Recommended for production when using InMemory or Hybrid to cap memory usage.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MemorySizeLimitMb must be at least 1 when specified.")]
    public int? MemorySizeLimitMb { get; set; }

    /// <summary>
    /// When true, Redis TLS certificate validation is strict: any SSL policy errors (including
    /// hostname mismatches) cause the connection to be rejected. When false (default), Caching.NET
    /// allows <see cref="System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch"/> but
    /// rejects all other SSL policy errors, matching common non-production Redis setups.
    /// </summary>
    public bool StrictRedisCertificateValidation { get; set; }

    /// <summary>
    /// Gets the default expiration as a TimeSpan, or null if not set.
    /// </summary>
    public TimeSpan? GetDefaultExpiration() => ParseExpiration(DefaultExpiration, nameof(DefaultExpiration));

    /// <summary>
    /// Gets the default local expiration as a TimeSpan, or null if not set.
    /// </summary>
    public TimeSpan? GetDefaultLocalExpiration() => ParseExpiration(DefaultLocalExpiration, nameof(DefaultLocalExpiration));

    private static TimeSpan? ParseExpiration(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!TimeSpan.TryParse(value, out var result))
            throw new InvalidOperationException(
                $"Invalid {propertyName} format: '{value}'. Expected format: 'hh:mm:ss' or 'd.hh:mm:ss' (e.g., '00:10:00' for 10 minutes).");
        return result;
    }
}
