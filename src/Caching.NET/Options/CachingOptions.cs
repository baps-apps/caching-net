namespace Caching.NET.Options;

/// <summary>
/// Root configuration for a single Caching.NET cache instance. Bound from the
/// <c>CacheOptions</c> configuration section, or configured in code through
/// <c>services.AddCaching(options =&gt; ...)</c>.
/// </summary>
/// <remarks>
/// Every property here is engine-neutral. Caching.NET maps these values onto the internal
/// cache engine at registration time; applications never configure the engine directly.
/// </remarks>
public sealed class CachingOptions
{
    /// <summary>
    /// Master switch. When <c>false</c>, a null cache is registered: reads always miss, writes are
    /// discarded, and get-or-set factories run on every call. No memory cache, Redis connection or
    /// backplane is created. Startup validation is skipped. Read once at registration time.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cache topology. Default <see cref="CacheMode.InMemory"/>.</summary>
    public CacheMode Mode { get; set; } = CacheMode.InMemory;

    /// <summary>
    /// Logical name of this cache instance. Used for keyed dependency-injection resolution,
    /// <c>ICacheProvider</c> lookups and the low-cardinality <c>cache.name</c> telemetry dimension.
    /// Must match <c>^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$</c>. Default <c>"default"</c>.
    /// </summary>
    public string CacheName { get; set; } = CachingDefaults.DefaultCacheName;

    /// <summary>
    /// Required application namespace, for example <c>"orders-api"</c>. Combined with
    /// <see cref="EnvironmentPrefix"/> and <see cref="TenantPrefix"/> to form the physical key
    /// prefix applied to every key by the cache engine. Must be unique per application when
    /// several applications share a Redis database — this is what prevents cross-application
    /// key collisions and cross-application invalidation. Must not contain <c>':'</c>.
    /// </summary>
    public string ApplicationPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Optional environment namespace, for example <c>"prod"</c>. Appended after
    /// <see cref="ApplicationPrefix"/>. Must not contain <c>':'</c>.
    /// </summary>
    public string? EnvironmentPrefix { get; set; }

    /// <summary>
    /// Optional static tenant namespace for single-tenant-per-process deployments.
    /// Multi-tenant processes should put the tenant in the cache key instead
    /// (see <c>Caching.NET.Keys.CacheKey</c>). Must not contain <c>':'</c>.
    /// </summary>
    public string? TenantPrefix { get; set; }

    /// <summary>
    /// Default lifetime of a cache entry when a call does not specify its own duration.
    /// Must be greater than zero. Default 10 minutes.
    /// </summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Per-entry defaults: distributed/local durations, eager refresh, jitter, priority.</summary>
    public CacheEntryOptions Entry { get; set; } = new();

    /// <summary>Fail-safe, timeout, circuit-breaker and auto-recovery behaviour.</summary>
    public CacheResilienceOptions Resilience { get; set; } = new();

    /// <summary>Redis connection settings. Required for <see cref="CacheMode.Redis"/> and <see cref="CacheMode.Hybrid"/>.</summary>
    public RedisOptions Redis { get; set; } = new();

    /// <summary>Cross-instance invalidation channel settings.</summary>
    public CacheBackplaneOptions Backplane { get; set; } = new();

    /// <summary>Wire-format, payload-size and compression settings for the distributed layer.</summary>
    public CacheSerializationOptions Serialization { get; set; } = new();

    /// <summary>Key/tag limits and telemetry-redaction settings.</summary>
    public CacheSecurityOptions Security { get; set; } = new();

    /// <summary>Tracing, metrics and logging settings.</summary>
    public CacheObservabilityOptions Observability { get; set; } = new();

    /// <summary>
    /// Builds the physical key prefix applied by the cache to every key:
    /// <c>{ApplicationPrefix}[:{EnvironmentPrefix}][:{TenantPrefix}][:{CacheName}]</c>. The cache
    /// appends the <c>':'</c> separator before the caller's key.
    /// </summary>
    /// <remarks>
    /// The cache name is appended only for non-default caches. That is what keeps two named caches
    /// in one application from sharing a key space in Redis — without it, a value written to
    /// <c>short-lived</c> would be readable through <c>default</c>. The default cache is left
    /// unsuffixed so the common case produces short, readable keys.
    /// </remarks>
    public string BuildKeyPrefix()
    {
        var prefix = ApplicationPrefix;
        if (!string.IsNullOrWhiteSpace(EnvironmentPrefix))
        {
            prefix = string.Concat(prefix, ":", EnvironmentPrefix);
        }

        if (!string.IsNullOrWhiteSpace(TenantPrefix))
        {
            prefix = string.Concat(prefix, ":", TenantPrefix);
        }

        if (!string.IsNullOrWhiteSpace(CacheName)
            && !string.Equals(CacheName, CachingDefaults.DefaultCacheName, StringComparison.Ordinal))
        {
            prefix = string.Concat(prefix, ":", CacheName);
        }

        return prefix;
    }

    /// <summary>Whether this mode keeps entries in the in-process memory layer.</summary>
    public bool UsesMemoryLayer => Mode is CacheMode.InMemory or CacheMode.Hybrid;

    /// <summary>Whether this mode opens a Redis connection and uses it as a distributed layer.</summary>
    public bool UsesDistributedLayer => Mode is CacheMode.Redis or CacheMode.Hybrid;
}
