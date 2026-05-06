using Caching.NET.Options;
using Caching.NET.Resilience;
using Caching.NET.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Caching.NET;

/// <summary>
/// Fluent builder for configuring Caching.NET services.
/// Returned internally by <c>AddCaching</c> overloads; each method returns <c>this</c> for chaining.
/// </summary>
public sealed class CachingBuilder
{
    private readonly IServiceCollection? _services;

    /// <summary>Construct without service-collection access (legacy path).</summary>
    public CachingBuilder() { }

    /// <summary>Construct with service-collection access (used by v2 AddCaching overloads).</summary>
    internal CachingBuilder(IServiceCollection services) { _services = services; }

    internal CacheMode? Mode { get; private set; }
    internal bool? Enabled { get; private set; }
    internal string? RedisConnectionString { get; private set; }
    internal Action<ConfigurationOptions>? RedisConfigurationAction { get; private set; }
    internal string? InstanceName { get; private set; }
    internal TimeSpan? DefaultExpiration { get; private set; }
    internal TimeSpan? DefaultLocalExpiration { get; private set; }
    internal long? MaximumPayloadBytes { get; private set; }
    internal int? MaximumKeyLength { get; private set; }
    internal int? MemorySizeLimitMb { get; private set; }
    internal TimeSpan? FactoryTimeout { get; private set; }
    internal int? StripeLockCount { get; private set; }
    internal TimeSpan? RedisOperationTimeout { get; private set; }
    internal bool StrictCertificateValidation { get; private set; }
    internal bool RegisterOpenTelemetry { get; private set; }
    internal bool RegisterHealthChecks { get; private set; }
    internal string HealthCheckName { get; private set; } = "caching-net";

    /// <summary>Sets cache mode to InMemory.</summary>
    public CachingBuilder UseInMemory()
    {
        Mode = CacheMode.InMemory;
        return this;
    }

    /// <summary>Sets cache mode to Redis with the specified connection string.</summary>
    public CachingBuilder UseRedis(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Mode = CacheMode.Redis;
        RedisConnectionString = connectionString;
        return this;
    }

    /// <summary>Sets cache mode to Redis with programmatic StackExchange.Redis configuration.</summary>
    public CachingBuilder UseRedis(Action<ConfigurationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Mode = CacheMode.Redis;
        RedisConfigurationAction = configure;
        return this;
    }

    /// <summary>Sets cache mode to Hybrid (in-memory only, no Redis backend).</summary>
    public CachingBuilder UseHybrid()
    {
        Mode = CacheMode.Hybrid;
        return this;
    }

    /// <summary>Sets cache mode to Hybrid with the specified Redis connection string as the distributed backend.</summary>
    public CachingBuilder UseHybrid(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Mode = CacheMode.Hybrid;
        RedisConnectionString = connectionString;
        return this;
    }

    /// <summary>Sets the default cache entry expiration.</summary>
    public CachingBuilder WithDefaultExpiration(TimeSpan expiration)
    {
        DefaultExpiration = expiration;
        return this;
    }

    /// <summary>Sets the default local (in-memory) expiration for Hybrid mode.</summary>
    public CachingBuilder WithDefaultLocalExpiration(TimeSpan expiration)
    {
        DefaultLocalExpiration = expiration;
        return this;
    }

    /// <summary>Sets the maximum payload size in bytes. Entries larger than this are not cached.</summary>
    public CachingBuilder WithMaximumPayloadBytes(long bytes)
    {
        MaximumPayloadBytes = bytes;
        return this;
    }

    /// <summary>Sets the maximum cache key length in characters.</summary>
    public CachingBuilder WithMaximumKeyLength(int length)
    {
        MaximumKeyLength = length;
        return this;
    }

    /// <summary>Sets the in-memory cache size limit in megabytes.</summary>
    public CachingBuilder WithMemorySizeLimit(int megabytes)
    {
        MemorySizeLimitMb = megabytes;
        return this;
    }

    /// <summary>Sets the factory execution timeout.</summary>
    public CachingBuilder WithFactoryTimeout(TimeSpan timeout)
    {
        FactoryTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the mandatory key prefix prepended to every cache key by the routing layer.
    /// Replaces v1's RedisInstanceName; applies uniformly across InMemory, Redis, and Hybrid backends.
    /// </summary>
    public CachingBuilder WithKeyPrefix(string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        InstanceName = keyPrefix;
        return this;
    }

    /// <summary>Override the number of striped lock slots (rounded up to power of 2; default 1024).</summary>
    public CachingBuilder WithStripedLocks(int stripeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
        StripeLockCount = stripeCount;
        return this;
    }

    /// <summary>Override the per-op Redis timeout (default 2s).</summary>
    public CachingBuilder WithRedisOperationTimeout(TimeSpan timeout)
    {
        RedisOperationTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Replaces the registered <see cref="ICacheSerializer"/> with the supplied implementation type
    /// (must have a parameterless constructor or be resolvable from DI).
    /// </summary>
    public CachingBuilder WithSerializer<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TSerializer>()
        where TSerializer : class, ICacheSerializer
    {
        if (_services is null)
            throw new InvalidOperationException(
                "WithSerializer<T>() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.RemoveAll<ICacheSerializer>();
        _services.AddSingleton<ICacheSerializer, TSerializer>();
        return this;
    }

    /// <summary>Replaces the registered <see cref="ICacheSerializer"/> with the supplied instance.</summary>
    public CachingBuilder WithSerializer(ICacheSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        if (_services is null)
            throw new InvalidOperationException(
                "WithSerializer(...) requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.RemoveAll<ICacheSerializer>();
        _services.AddSingleton(serializer);
        return this;
    }

    /// <summary>Configure the Polly resilience pipeline knobs (timeout, breaker, retry).</summary>
    public CachingBuilder WithResilience(Action<ResiliencePipelineRegistryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_services is null)
            throw new InvalidOperationException(
                "WithResilience(...) requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.Configure(configure);
        return this;
    }

    /// <summary>
    /// In v2, telemetry is always emitted via the static <see cref="Telemetry.CacheInstruments"/>
    /// (Meter and ActivitySource named "Caching.NET"). Consumers wire OpenTelemetry directly:
    /// <c>builder.Services.AddOpenTelemetry().WithMetrics(b =&gt; b.AddMeter(CacheInstruments.MeterName))</c>.
    /// This method is preserved for v1 source compat and is now a no-op.
    /// </summary>
    public CachingBuilder WithOpenTelemetry()
    {
        RegisterOpenTelemetry = true;
        return this;
    }

    /// <summary>Registers <see cref="Health.CachingHealthCheck"/> with the health check system.</summary>
    public CachingBuilder WithHealthChecks(string name = "caching-net")
    {
        RegisterHealthChecks = true;
        HealthCheckName = name;
        return this;
    }

    /// <summary>Enables strict Redis TLS certificate validation.</summary>
    public CachingBuilder WithStrictCertificateValidation()
    {
        StrictCertificateValidation = true;
        return this;
    }

    /// <summary>Explicitly disables caching. ICacheService is still registered but short-circuits to factories.</summary>
    public CachingBuilder Disable()
    {
        Enabled = false;
        return this;
    }

    /// <summary>Apply ±<paramref name="percentage"/> jitter to all entry TTLs (clamped to 0–0.5).</summary>
    public CachingBuilder WithTtlJitter(double percentage)
    {
        if (_services is null)
            throw new InvalidOperationException(
                "WithTtlJitter() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.PostConfigure<CacheOptions>(o => o.TtlJitterPercentage = Math.Clamp(percentage, 0.0, 0.5));
        return this;
    }

    /// <summary>Cap concurrent in-flight stale-while-revalidate background refreshes.</summary>
    public CachingBuilder WithStaleRefreshConcurrency(int maxConcurrent)
    {
        if (_services is null)
            throw new InvalidOperationException(
                "WithStaleRefreshConcurrency() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.PostConfigure<CacheOptions>(o => o.StaleRefreshConcurrency = maxConcurrent);
        return this;
    }

    /// <summary>
    /// Mark the application as requiring tag support. Startup validation fails
    /// when <see cref="CacheOptions.Mode"/> is not <see cref="CacheMode.Hybrid"/>.
    /// </summary>
    public CachingBuilder RequireTagSupport()
    {
        if (_services is null)
            throw new InvalidOperationException(
                "RequireTagSupport() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.PostConfigure<CacheOptions>(o => o.RequireTagSupport = true);
        return this;
    }

    /// <summary>Use the bundled MessagePack serializer.</summary>
    public CachingBuilder WithMessagePackSerializer()
    {
        if (_services is null)
            throw new InvalidOperationException(
                "WithMessagePackSerializer() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.RemoveAll<Serialization.ICacheSerializer>();
        _services.AddSingleton<Serialization.ICacheSerializer, Serialization.MessagePackCacheSerializer>();
        return this;
    }

    /// <summary>
    /// Applies the builder's fluent settings onto a <see cref="CacheOptions"/> instance,
    /// overriding any values previously set by configuration binding.
    /// </summary>
    internal void ApplyTo(CacheOptions options)
    {
        if (Enabled.HasValue)
            options.Enabled = Enabled.Value;
        if (Mode.HasValue)
            options.Mode = Mode.Value;
        if (RedisConnectionString is not null)
            options.RedisConnectionString = RedisConnectionString;
        if (InstanceName is not null)
            options.KeyPrefix = InstanceName;
        if (DefaultExpiration.HasValue)
            options.DefaultExpiration = DefaultExpiration.Value;
        if (DefaultLocalExpiration.HasValue)
            options.HybridLocalCacheExpiration = DefaultLocalExpiration.Value;
        if (MaximumPayloadBytes.HasValue)
            options.MaximumPayloadBytes = MaximumPayloadBytes.Value;
        if (MaximumKeyLength.HasValue)
            options.MaximumKeyLength = MaximumKeyLength.Value;
        if (MemorySizeLimitMb.HasValue)
            options.MemorySizeLimitMb = MemorySizeLimitMb.Value;
        if (FactoryTimeout.HasValue)
            options.FactoryTimeout = FactoryTimeout.Value;
        if (StripeLockCount.HasValue)
            options.StripeLockCount = StripeLockCount.Value;
        if (RedisOperationTimeout.HasValue)
            options.RedisOperationTimeout = RedisOperationTimeout.Value;
        if (StrictCertificateValidation)
            options.StrictRedisCertificateValidation = true;
    }
}
