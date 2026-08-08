using System.Text.Json;
using Caching.NET.Options;

namespace Caching.NET;

/// <summary>
/// Fluent, code-first configuration for one Caching.NET cache. Every method mutates the underlying
/// <see cref="CachingOptions"/> and returns the builder, so calls chain.
/// </summary>
/// <remarks>
/// The builder is applied after configuration binding, so fluent settings win over
/// <c>appsettings.json</c> when both are used.
/// </remarks>
/// <example>
/// <code><![CDATA[
/// services.AddCaching(cache => cache
///     .UseHybrid(redisConnectionString)
///     .WithApplicationPrefix("orders-api")
///     .WithEnvironmentPrefix("prod")
///     .WithDefaultExpiration(TimeSpan.FromMinutes(10))
///     .WithEagerRefresh(0.8f)
///     .WithHealthChecks(splitLivenessReadiness: true));
/// ]]></code>
/// </example>
public sealed class CachingBuilder
{
    private readonly CachingOptions _options;

    internal CachingBuilder(CachingOptions options)
    {
        _options = options;
    }

    internal bool RegisterHealthChecks { get; private set; }

    internal string HealthCheckName { get; private set; } = "caching-net";

    internal bool HealthCheckSplit { get; private set; }

    /// <summary>The options being configured. Use for settings without a dedicated builder method.</summary>
    public CachingOptions Options => _options;

    // ---------------------------------------------------------------- mode

    /// <summary>Uses an in-process memory cache only. No Redis connection is opened.</summary>
    public CachingBuilder UseInMemory()
    {
        _options.Mode = CacheMode.InMemory;
        return this;
    }

    /// <summary>Uses Redis as the single authoritative cache.</summary>
    /// <param name="connectionString">StackExchange.Redis connection string.</param>
    public CachingBuilder UseRedis(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _options.Mode = CacheMode.Redis;
        _options.Redis.Configuration = connectionString;
        return this;
    }

    /// <summary>
    /// Uses L1 memory plus L2 Redis. Enables the backplane unless it has already been set
    /// explicitly, so multi-pod deployments get cross-instance invalidation by default.
    /// </summary>
    /// <param name="connectionString">StackExchange.Redis connection string.</param>
    /// <param name="enableBackplane">Overrides the backplane default.</param>
    public CachingBuilder UseHybrid(string connectionString, bool enableBackplane = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _options.Mode = CacheMode.Hybrid;
        _options.Redis.Configuration = connectionString;
        _options.Backplane.Enabled = enableBackplane;
        return this;
    }

    // ---------------------------------------------------------------- toggle and presets

    /// <summary>Enables caching (the default).</summary>
    public CachingBuilder Enable()
    {
        _options.Enabled = true;
        return this;
    }

    /// <summary>
    /// Disables caching: reads miss, writes are discarded, factories run every time, and no
    /// backend is created.
    /// </summary>
    public CachingBuilder Disable()
    {
        _options.Enabled = false;
        return this;
    }

    /// <summary>
    /// Development preset: short expirations, no fail-safe, eager errors. Makes cache-related bugs
    /// visible instead of hidden behind stale data.
    /// </summary>
    public CachingBuilder UseDevelopmentDefaults()
    {
        _options.DefaultExpiration = TimeSpan.FromMinutes(1);
        _options.Resilience.FailSafeEnabled = false;
        _options.Resilience.ThrowOnDistributedCacheErrors = true;
        _options.Resilience.ThrowOnSerializationErrors = true;
        _options.Observability.LogStartupSummary = true;
        return this;
    }

    /// <summary>
    /// Production preset: fail-safe on, soft timeouts on the factory and the distributed layer,
    /// eager refresh late in the entry's life, and errors degraded rather than surfaced.
    /// </summary>
    public CachingBuilder UseProductionDefaults()
    {
        _options.Resilience.FailSafeEnabled = true;
        _options.Resilience.FailSafeMaxDuration = TimeSpan.FromHours(2);
        _options.Resilience.FactorySoftTimeout = TimeSpan.FromSeconds(1);
        _options.Resilience.FactoryHardTimeout = TimeSpan.FromSeconds(10);
        _options.Resilience.DistributedSoftTimeout = TimeSpan.FromMilliseconds(500);
        _options.Resilience.DistributedHardTimeout = TimeSpan.FromSeconds(2);
        _options.Resilience.ThrowOnDistributedCacheErrors = false;
        _options.Resilience.ThrowOnSerializationErrors = false;
        _options.Entry.EagerRefreshThreshold = 0.8f;
        return this;
    }

    // ---------------------------------------------------------------- naming and isolation

    // There is deliberately no WithCacheName: the cache name always comes from the registration
    // (AddCaching(name, ...)), which is what keyed resolution and the cache.name dimension use.
    // A builder method could only set a value the registration immediately overwrites.

    /// <summary>Sets the required application key namespace.</summary>
    /// <param name="applicationPrefix">Application identifier, for example <c>"orders-api"</c>.</param>
    public CachingBuilder WithApplicationPrefix(string applicationPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPrefix);
        _options.ApplicationPrefix = applicationPrefix;
        return this;
    }

    /// <summary>Sets the environment key namespace.</summary>
    /// <param name="environmentPrefix">Environment identifier, for example <c>"prod"</c>.</param>
    public CachingBuilder WithEnvironmentPrefix(string environmentPrefix)
    {
        _options.EnvironmentPrefix = environmentPrefix;
        return this;
    }

    /// <summary>Sets a static tenant key namespace for single-tenant-per-process deployments.</summary>
    /// <param name="tenantPrefix">Tenant identifier.</param>
    public CachingBuilder WithTenantPrefix(string tenantPrefix)
    {
        _options.TenantPrefix = tenantPrefix;
        return this;
    }

    // ---------------------------------------------------------------- expiration

    /// <summary>Sets the default entry lifetime.</summary>
    /// <param name="expiration">Lifetime; must be greater than zero.</param>
    public CachingBuilder WithDefaultExpiration(TimeSpan expiration)
    {
        _options.DefaultExpiration = expiration;
        return this;
    }

    /// <summary>Sets the L2 (Redis) lifetime, which may differ from the L1 lifetime.</summary>
    /// <param name="expiration">Distributed lifetime.</param>
    public CachingBuilder WithDistributedExpiration(TimeSpan expiration)
    {
        _options.Entry.DistributedExpiration = expiration;
        return this;
    }

    /// <summary>Sets the L1 (in-process) lifetime.</summary>
    /// <param name="expiration">Local lifetime.</param>
    public CachingBuilder WithLocalExpiration(TimeSpan expiration)
    {
        _options.Entry.LocalExpiration = expiration;
        return this;
    }

    /// <summary>Enables background refresh once an entry passes the given fraction of its lifetime.</summary>
    /// <param name="threshold">Fraction between 0 and 1 exclusive, for example <c>0.8f</c>.</param>
    public CachingBuilder WithEagerRefresh(float threshold)
    {
        _options.Entry.EagerRefreshThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Caps the random amount added to an entry's lifetime to de-synchronise expirations. Pass
    /// <see cref="TimeSpan.Zero"/> to disable jitter entirely.
    /// </summary>
    /// <remarks>
    /// This sets the <b>ceiling</b>. The applied window is
    /// <c>min(duration × Entry.JitterFraction, this)</c> — see
    /// <see cref="WithProportionalJitter(double, TimeSpan?)"/>.
    /// </remarks>
    /// <param name="jitter">Maximum jitter.</param>
    public CachingBuilder WithJitter(TimeSpan jitter)
    {
        _options.Entry.JitterMaxDuration = jitter;
        return this;
    }

    /// <summary>
    /// Sets jitter as a fraction of each entry's own lifetime, with an optional absolute ceiling.
    /// </summary>
    /// <remarks>
    /// Jitter exists to stop entries created together from expiring together, so it only makes sense
    /// relative to how long the entry lives. A flat window does not scale: 2 seconds against a
    /// 10-minute entry is a rounding error, but against a 300 ms entry it is seven times the
    /// requested lifetime. Pass <c>null</c> to return to a flat absolute window.
    /// </remarks>
    /// <param name="fraction">Fraction of the entry lifetime, in <c>(0.0, 1.0]</c>. Default is <c>0.1</c>.</param>
    /// <param name="maximum">Optional ceiling. Leave <c>null</c> to keep the configured cap.</param>
    public CachingBuilder WithProportionalJitter(double fraction, TimeSpan? maximum = null)
    {
        _options.Entry.JitterFraction = fraction;
        if (maximum is { } cap)
        {
            _options.Entry.JitterMaxDuration = cap;
        }

        return this;
    }

    /// <summary>
    /// Caps the in-process memory layer and sets the default per-entry size charged against that cap.
    /// The limit is a ceiling on the <b>sum of the entries' sizes</b>, not a byte or megabyte budget —
    /// Caching.NET cannot measure the memory footprint of an arbitrary cached object.
    /// </summary>
    /// <remarks>
    /// With the default <paramref name="defaultEntrySize"/> of <c>1</c> every entry charges one unit,
    /// so this is a cap on the <b>number of entries</b> held in memory. To approximate bytes, charge
    /// each entry a size in the unit you choose (per call via <c>CacheEntryOverrides.Size</c>, or here
    /// as the default) and express <paramref name="limit"/> in the same unit. Once a limit is set, an
    /// entry with no size at all is <b>not cached</b>.
    /// </remarks>
    /// <param name="limit">Ceiling on the summed size of the entries held in memory.</param>
    /// <param name="defaultEntrySize">Size charged per entry that does not declare its own. Must be greater than zero.</param>
    public CachingBuilder WithMemorySizeLimit(long limit, long defaultEntrySize = 1)
    {
        _options.Entry.MemorySizeLimit = limit;
        _options.Entry.Size = defaultEntrySize;
        return this;
    }

    // ---------------------------------------------------------------- resilience

    /// <summary>Configures fail-safe, timeout, circuit-breaker and auto-recovery behaviour.</summary>
    /// <param name="configure">Receives the resilience options.</param>
    public CachingBuilder WithResilience(Action<CacheResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options.Resilience);
        return this;
    }

    /// <summary>Configures how long an expired entry stays usable as a fallback.</summary>
    /// <param name="enabled">Whether fail-safe is on.</param>
    /// <param name="maxDuration">How long past expiration the entry remains usable.</param>
    /// <param name="throttleDuration">How long before the factory is retried after serving stale data.</param>
    public CachingBuilder WithFailSafe(bool enabled = true, TimeSpan? maxDuration = null, TimeSpan? throttleDuration = null)
    {
        _options.Resilience.FailSafeEnabled = enabled;
        if (maxDuration is { } max)
        {
            _options.Resilience.FailSafeMaxDuration = max;
        }

        if (throttleDuration is { } throttle)
        {
            _options.Resilience.FailSafeThrottleDuration = throttle;
        }

        return this;
    }

    /// <summary>Sets the factory soft and hard timeouts.</summary>
    /// <param name="softTimeout">When to fall back to a stale value while the factory keeps running.</param>
    /// <param name="hardTimeout">Absolute ceiling on factory execution.</param>
    public CachingBuilder WithFactoryTimeouts(TimeSpan? softTimeout = null, TimeSpan? hardTimeout = null)
    {
        if (softTimeout is { } soft)
        {
            _options.Resilience.FactorySoftTimeout = soft;
        }

        if (hardTimeout is { } hard)
        {
            _options.Resilience.FactoryHardTimeout = hard;
        }

        return this;
    }

    // ---------------------------------------------------------------- redis

    /// <summary>
    /// Configures Redis connection settings. This is the documented way to reach settings that have
    /// no dedicated builder method, for example <see cref="RedisOptions.ClientCertificate"/> or
    /// <see cref="RedisOptions.ValidateServerCertificate"/>.
    /// </summary>
    /// <param name="configure">Receives the Redis options.</param>
    public CachingBuilder WithRedis(Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options.Redis);
        return this;
    }

    /// <summary>Requires TLS and strict certificate validation.</summary>
    public CachingBuilder WithStrictRedisTls()
    {
        _options.Redis.UseTls = true;
        _options.Redis.StrictCertificateValidation = true;
        return this;
    }

    /// <summary>
    /// Enables TLS but tolerates a certificate name mismatch. Use only for a private endpoint whose
    /// DNS name differs from the certificate subject; never for a public endpoint.
    /// </summary>
    public CachingBuilder WithPermissiveRedisTls()
    {
        _options.Redis.UseTls = true;
        _options.Redis.StrictCertificateValidation = false;
        return this;
    }

    /// <summary>Enables or disables the cross-instance invalidation channel.</summary>
    /// <param name="enabled">Whether the backplane runs.</param>
    /// <param name="channelPrefix">Optional pub/sub channel prefix.</param>
    public CachingBuilder WithBackplane(bool enabled = true, string? channelPrefix = null)
    {
        _options.Backplane.Enabled = enabled;
        if (channelPrefix is not null)
        {
            _options.Backplane.ChannelPrefix = channelPrefix;
        }

        return this;
    }

    // ---------------------------------------------------------------- serialization

    /// <summary>Selects the JSON wire format (the default) and optionally its serializer options.</summary>
    /// <param name="jsonSerializerOptions">
    /// Supply a source-generated <c>TypeInfoResolver</c> here for trim- and AOT-safe serialization.
    /// </param>
    public CachingBuilder WithJsonSerialization(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _options.Serialization.Format = CacheSerializerFormat.SystemTextJson;
        _options.Serialization.JsonSerializerOptions = jsonSerializerOptions;
        return this;
    }

    /// <summary>Selects the MessagePack wire format. Smaller and faster than JSON, but not human-readable.</summary>
    public CachingBuilder WithMessagePackSerialization()
    {
        _options.Serialization.Format = CacheSerializerFormat.MessagePack;
        return this;
    }

    /// <summary>Sets the maximum serialized size of a single entry.</summary>
    /// <param name="maximumBytes">Ceiling in bytes.</param>
    public CachingBuilder WithMaximumPayloadBytes(long maximumBytes)
    {
        _options.Serialization.MaximumPayloadBytes = maximumBytes;
        return this;
    }

    /// <summary>Enables Brotli compression for distributed payloads at or above a threshold.</summary>
    /// <param name="thresholdBytes">Minimum serialized size before compression is attempted.</param>
    /// <param name="maximumDecompressedBytes">Decompression ceiling that bounds decompression-bomb exposure.</param>
    public CachingBuilder WithCompression(int thresholdBytes = 16 * 1024, int maximumDecompressedBytes = 16 * 1024 * 1024)
    {
        _options.Serialization.Compression.Enabled = true;
        _options.Serialization.Compression.ThresholdBytes = thresholdBytes;
        _options.Serialization.Compression.MaximumDecompressedBytes = maximumDecompressedBytes;
        return this;
    }

    // ---------------------------------------------------------------- security and observability

    /// <summary>Configures key, tag and telemetry-redaction limits.</summary>
    /// <param name="configure">Receives the security options.</param>
    public CachingBuilder WithSecurity(Action<CacheSecurityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options.Security);
        return this;
    }

    /// <summary>Sets the maximum physical cache-key length.</summary>
    /// <param name="maximumKeyLength">Ceiling in characters, prefix included.</param>
    public CachingBuilder WithMaximumKeyLength(int maximumKeyLength)
    {
        _options.Security.MaximumKeyLength = maximumKeyLength;
        return this;
    }

    /// <summary>Configures tracing, metrics and log levels.</summary>
    /// <param name="configure">Receives the observability options.</param>
    public CachingBuilder WithObservability(Action<CacheObservabilityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options.Observability);
        return this;
    }

    /// <summary>Turns Caching.NET tracing and metrics on or off.</summary>
    /// <param name="tracing">Whether activities are produced.</param>
    /// <param name="metrics">Whether metrics are recorded.</param>
    public CachingBuilder WithTelemetry(bool tracing = true, bool metrics = true)
    {
        _options.Observability.EnableTracing = tracing;
        _options.Observability.EnableMetrics = metrics;
        return this;
    }

    /// <summary>Registers Caching.NET health checks.</summary>
    /// <param name="name">Health-check name. Suffixed with <c>-liveness</c> and <c>-readiness</c> when split.</param>
    /// <param name="splitLivenessReadiness">Register separate liveness and readiness checks.</param>
    public CachingBuilder WithHealthChecks(string name = "caching-net", bool splitLivenessReadiness = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RegisterHealthChecks = true;
        HealthCheckName = name;
        HealthCheckSplit = splitLivenessReadiness;
        return this;
    }
}
