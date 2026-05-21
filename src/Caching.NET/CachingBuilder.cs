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
/// <example>
/// Fluent:
/// <code><![CDATA[
/// services.AddCaching(b => b
///     .UseHybrid("rediss://redis:6380")
///     .WithKeyPrefix("svc-prod")
///     .UseProductionDefaults()
///     .WithTtlJitter(0.10)
///     .WithHealthChecks());
/// ]]></code>
/// Configuration (appsettings.json) equivalent for the bindable subset:
/// <code><![CDATA[
/// {
///   "CacheOptions": {
///     "Enabled": true,
///     "Mode": "Hybrid",
///     "KeyPrefix": "svc-prod",
///     "RedisConnectionString": "rediss://redis:6380",
///     "TtlJitterPercentage": 0.10,
///     "IncludeRawKeyInLogs": false,
///     "StrictRedisCertificateValidation": true
///   }
/// }
/// ]]></code>
/// <code><![CDATA[
/// services.AddCaching(builder.Configuration);
/// ]]></code>
/// </example>
public sealed class CachingBuilder
{
    private readonly IServiceCollection? _services;

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
    /// <summary>
    /// Fluent intent for <see cref="Caching.NET.Options.CacheOptions.StrictRedisCertificateValidation"/>.
    /// <c>null</c> = leave config-bound value; otherwise replaces it when <see cref="ApplyTo"/> runs.
    /// </summary>
    internal bool? StrictRedisCertificateValidation { get; private set; }
    internal bool RegisterOpenTelemetry { get; private set; }
    internal bool RegisterHealthChecks { get; private set; }
    internal string HealthCheckName { get; private set; } = "caching-net";
    internal bool HealthCheckSplit { get; private set; }
    internal Func<string, bool>? KeyValidator { get; private set; }
    internal Func<string, string>? KeyTransformer { get; private set; }

    /// <summary>Sets cache mode to InMemory. Default for <see cref="CacheOptions.Mode"/> when unset: <see cref="CacheMode.InMemory"/>.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseInMemory().WithKeyPrefix("svc-dev"));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "Enabled": true, "Mode": "InMemory", "KeyPrefix": "svc-dev" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder UseInMemory()
    {
        Mode = CacheMode.InMemory;
        return this;
    }

    /// <summary>Sets cache mode to Redis with the specified connection string.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseRedis("rediss://elasticache.example:6380").WithKeyPrefix("svc-prod"));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// {
    ///   "CacheOptions": {
    ///     "Enabled": true,
    ///     "Mode": "Redis",
    ///     "KeyPrefix": "svc-prod",
    ///     "RedisConnectionString": "rediss://elasticache.example:6380"
    ///   }
    /// }
    /// ]]></code>
    /// </example>
    public CachingBuilder UseRedis(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Mode = CacheMode.Redis;
        RedisConnectionString = connectionString;
        return this;
    }

    /// <summary>Sets cache mode to Redis with programmatic StackExchange.Redis configuration.</summary>
    /// <example>
    /// Fluent only — <c>ConfigurationOptions</c> programmatic setup is not bindable from JSON.
    /// (For a connection-string-only config, use <see cref="UseRedis(string)"/> + <c>RedisConnectionString</c> in JSON.)
    /// <code><![CDATA[
    /// services.AddCaching(b => b
    ///     .UseRedis(cfg =>
    ///     {
    ///         cfg.EndPoints.Add("redis-primary:6379");
    ///         cfg.Password = secrets.RedisPassword;
    ///         cfg.Ssl = true;
    ///     })
    ///     .WithKeyPrefix("svc-prod"));
    /// ]]></code>
    /// </example>
    public CachingBuilder UseRedis(Action<ConfigurationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Mode = CacheMode.Redis;
        RedisConfigurationAction = configure;
        return this;
    }

    /// <summary>Sets cache mode to Hybrid (in-memory only, no Redis backend).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid().WithKeyPrefix("svc-dev"));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "Enabled": true, "Mode": "Hybrid", "KeyPrefix": "svc-dev" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder UseHybrid()
    {
        Mode = CacheMode.Hybrid;
        return this;
    }

    /// <summary>Sets cache mode to Hybrid with the specified Redis connection string as the distributed backend.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b
    ///     .UseHybrid("rediss://elasticache.example:6380")
    ///     .WithKeyPrefix("svc-prod"));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// {
    ///   "CacheOptions": {
    ///     "Enabled": true,
    ///     "Mode": "Hybrid",
    ///     "KeyPrefix": "svc-prod",
    ///     "RedisConnectionString": "rediss://elasticache.example:6380"
    ///   }
    /// }
    /// ]]></code>
    /// </example>
    public CachingBuilder UseHybrid(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Mode = CacheMode.Hybrid;
        RedisConnectionString = connectionString;
        return this;
    }

    /// <summary>Sets the default cache entry expiration. Default when unset: 10 minutes.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithDefaultExpiration(TimeSpan.FromMinutes(5)));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "DefaultExpiration": "00:05:00" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithDefaultExpiration(TimeSpan expiration)
    {
        DefaultExpiration = expiration;
        return this;
    }

    /// <summary>Sets the default local (in-memory) expiration for Hybrid mode. Default when unset: null (inherits <see cref="CacheOptions.DefaultExpiration"/>).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithDefaultLocalExpiration(TimeSpan.FromSeconds(30)));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "HybridLocalCacheExpiration": "00:00:30" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithDefaultLocalExpiration(TimeSpan expiration)
    {
        DefaultLocalExpiration = expiration;
        return this;
    }

    /// <summary>Sets the maximum payload size in bytes. Entries larger than this are not cached. Default when unset: 1,048,576 (1 MiB).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithMaximumPayloadBytes(512 * 1024)); // 512 KiB cap
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "MaximumPayloadBytes": 524288 } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithMaximumPayloadBytes(long bytes)
    {
        MaximumPayloadBytes = bytes;
        return this;
    }

    /// <summary>Sets the maximum cache key length in characters (full physical key, including <see cref="CacheOptions.KeyPrefix"/> + separator). Default when unset: 512.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseRedis("...").WithKeyPrefix("svc-prod")
    ///     .WithMaximumKeyLength(256));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "MaximumKeyLength": 256 } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithMaximumKeyLength(int length)
    {
        MaximumKeyLength = length;
        return this;
    }

    /// <summary>Sets the in-memory cache size limit in megabytes. Default when unset: null (no cap; eviction by GC pressure only).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseInMemory().WithKeyPrefix("svc-prod")
    ///     .WithMemorySizeLimit(256)); // 256 MiB cap on IMemoryCache
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "MemorySizeLimitMb": 256 } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithMemorySizeLimit(int megabytes)
    {
        MemorySizeLimitMb = megabytes;
        return this;
    }

    /// <summary>Sets the factory execution timeout (bounds the <c>GetOrCreateAsync</c> factory delegate). Default when unset: 30 seconds.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithFactoryTimeout(TimeSpan.FromSeconds(5)));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "FactoryTimeout": "00:00:05" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithFactoryTimeout(TimeSpan timeout)
    {
        FactoryTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the mandatory key prefix prepended to every cache key by the routing layer.
    /// Replaces v1's RedisInstanceName; applies uniformly across InMemory, Redis, and Hybrid backends.
    /// No default — required when <see cref="CacheOptions.Enabled"/> is true. Must match <c>^[a-zA-Z0-9][a-zA-Z0-9._-]*$</c> and must not contain <c>':'</c>.
    /// </summary>
    /// <example>
    /// Fluent (convention: <c>serviceName-environment</c>; must not contain <c>':'</c>):
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("catalog-prod"));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "KeyPrefix": "catalog-prod" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithKeyPrefix(string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        InstanceName = keyPrefix;
        return this;
    }

    /// <summary>Override the number of striped lock slots (rounded up to power of 2; default 1024).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithStripedLocks(4096));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "StripeLockCount": 4096 } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithStripedLocks(int stripeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
        StripeLockCount = stripeCount;
        return this;
    }

    /// <summary>Override the per-op Redis timeout (default 2s).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseRedis("...").WithKeyPrefix("svc-prod")
    ///     .WithRedisOperationTimeout(TimeSpan.FromMilliseconds(500)));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "RedisOperationTimeout": "00:00:00.500" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithRedisOperationTimeout(TimeSpan timeout)
    {
        RedisOperationTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Replaces the registered <see cref="ICacheSerializer"/> with the supplied implementation type
    /// (must have a parameterless constructor or be resolvable from DI).
    /// Default <see cref="ICacheSerializer"/> when unset: <see cref="JsonCacheSerializer"/>.
    /// </summary>
    /// <example>
    /// Fluent only — serializer types are not bindable from JSON. To pre-register a serializer outside the
    /// fluent overload, register <see cref="ICacheSerializer"/> on <see cref="IServiceCollection"/> before
    /// calling <c>AddCaching(IConfiguration)</c>.
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseRedis("...").WithKeyPrefix("svc-prod")
    ///     .WithSerializer<MyCustomSerializer>());
    /// ]]></code>
    /// </example>
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

    /// <summary>Replaces the registered <see cref="ICacheSerializer"/> with the supplied instance. Default <see cref="ICacheSerializer"/> when unset: <see cref="JsonCacheSerializer"/>.</summary>
    /// <example>
    /// Fluent only — serializer instances are not bindable from JSON.
    /// <code><![CDATA[
    /// // AOT/trim-safe: pass a JsonSerializerContext to the JsonCacheSerializer.
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithSerializer(new JsonCacheSerializer(MyJsonContext.Default)));
    /// ]]></code>
    /// </example>
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

    /// <summary>
    /// Configure Redis resilience (timeout, circuit breaker, retry, optional concurrency limiter).
    /// Uses library-defined <see cref="CacheResilienceOptions"/> — Polly is not surfaced on the public API.
    /// </summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseRedis("...").WithKeyPrefix("svc-prod")
    ///     .WithResilience(r =>
    ///     {
    ///         r.Timeout = TimeSpan.FromMilliseconds(500);
    ///         r.RetryCount = 1;
    ///         r.FailureRatio = 0.5;
    ///         r.MinimumThroughput = 20;
    ///         r.BreakDuration = TimeSpan.FromSeconds(30);
    ///     }));
    /// ]]></code>
    /// Configuration — <see cref="CacheResilienceOptions"/> is a separate options type and is NOT bound by
    /// <c>AddCaching(IConfiguration)</c>. Bind it explicitly before/after <c>AddCaching</c>:
    /// <code><![CDATA[
    /// services.Configure<CacheResilienceOptions>(builder.Configuration.GetSection("CacheResilience"));
    /// services.AddCaching(builder.Configuration);
    /// ]]></code>
    /// <code><![CDATA[
    /// {
    ///   "CacheResilience": {
    ///     "Timeout": "00:00:00.500",
    ///     "RetryCount": 1,
    ///     "FailureRatio": 0.5,
    ///     "MinimumThroughput": 20,
    ///     "BreakDuration": "00:00:30"
    ///   }
    /// }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithResilience(Action<CacheResilienceOptions> configure)
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
    /// <example>
    /// Fluent only — no-op v1-compat hook; no configuration equivalent.
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").WithOpenTelemetry());
    ///
    /// // Actual telemetry wiring (do this in host startup regardless of which AddCaching overload you use):
    /// services.AddOpenTelemetry()
    ///     .WithMetrics(m => m.AddMeter(CacheInstruments.MeterName))
    ///     .WithTracing(t => t.AddSource(CacheInstruments.ActivitySourceName));
    /// ]]></code>
    /// </example>
    public CachingBuilder WithOpenTelemetry()
    {
        RegisterOpenTelemetry = true;
        return this;
    }

    /// <summary>
    /// Registers cache health checks with the ASP.NET Core health check system.
    /// By default registers a single <see cref="Health.CachingHealthCheck"/> (readiness: PING + probe).
    /// When <paramref name="splitLivenessReadiness"/> is true, registers
    /// <see cref="Health.CachingLivenessHealthCheck"/> (connection-level only) and
    /// <see cref="Health.CachingHealthCheck"/> as <c>{name}-liveness</c> / <c>{name}-readiness</c> with tags <c>liveness</c> / <c>readiness</c>.
    /// </summary>
    /// <example>
    /// Fluent only — health-check registration is a service-collection operation, not a <see cref="CacheOptions"/> setting.
    /// If you registered via <c>AddCaching(IConfiguration)</c>, call
    /// <see cref="Extensions.ServiceCollectionExtensions.AddCachingHealthChecks"/> on
    /// <c>services.AddHealthChecks()</c> directly.
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithHealthChecks(name: "caching-net", splitLivenessReadiness: true));
    ///
    /// // Equivalent when using AddCaching(IConfiguration):
    /// services.AddCaching(builder.Configuration);
    /// services.AddHealthChecks().AddCachingHealthChecks(name: "caching-net", splitLivenessReadiness: true);
    /// ]]></code>
    /// </example>
    public CachingBuilder WithHealthChecks(string name = "caching-net", bool splitLivenessReadiness = false)
    {
        RegisterHealthChecks = true;
        HealthCheckName = name;
        HealthCheckSplit = splitLivenessReadiness;
        return this;
    }

    /// <summary>Enables strict Redis TLS certificate validation (hostname must match the certificate). Default when unset: true (strict — flipped from v1).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseRedis("rediss://...").WithKeyPrefix("svc-prod")
    ///     .WithStrictCertificateValidation());
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "StrictRedisCertificateValidation": true } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithStrictCertificateValidation()
    {
        StrictRedisCertificateValidation = true;
        return this;
    }

    /// <summary>
    /// Allows TLS connections when the server certificate does not match the Redis host name
    /// (e.g. custom DNS to AWS ElastiCache). Other validation errors (untrusted chain, etc.) are still rejected.
    /// Sets <see cref="Caching.NET.Options.CacheOptions.StrictRedisCertificateValidation"/> to <c>false</c>.
    /// </summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseRedis("rediss://my-alias:6380").WithKeyPrefix("svc-prod")
    ///     .WithPermissiveRedisTls());
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "StrictRedisCertificateValidation": false } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithPermissiveRedisTls()
    {
        StrictRedisCertificateValidation = false;
        return this;
    }

    /// <summary>Explicitly disables caching. ICacheService is still registered but short-circuits to factories. Default for <see cref="CacheOptions.Enabled"/> when unset: true.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").Disable());
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "Enabled": false, "KeyPrefix": "svc-prod" } }
    /// ]]></code>
    /// </example>
    public CachingBuilder Disable()
    {
        Enabled = false;
        return this;
    }

    /// <summary>
    /// Re-enables caching when overriding a config file that set <see cref="CacheOptions.Enabled"/> to false
    /// (fluent wins over bound configuration via <see cref="Extensions.ServiceCollectionExtensions.AddCaching(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, System.Action{CachingBuilder})"/>).
    /// </summary>
    /// <example>
    /// Fluent override (config-file says <c>Enabled=false</c>, host forces it on):
    /// <code><![CDATA[
    /// services.AddCaching(configuration, b => b.Enable());
    /// ]]></code>
    /// Pure configuration:
    /// <code><![CDATA[
    /// { "CacheOptions": { "Enabled": true } }
    /// ]]></code>
    /// </example>
    public CachingBuilder Enable()
    {
        Enabled = true;
        return this;
    }

    /// <summary>
    /// Development-oriented defaults: raw keys in logs for easier local debugging.
    /// Requires <c>AddCaching(IServiceCollection, ...)</c>; throws if the builder has no service collection.
    /// </summary>
    /// <example>
    /// Fluent (preset):
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseInMemory().WithKeyPrefix("svc-dev").UseDevelopmentDefaults());
    /// ]]></code>
    /// Configuration (appsettings.json) — set the underlying flags directly:
    /// <code><![CDATA[
    /// { "CacheOptions": { "IncludeRawKeyInLogs": true } }
    /// ]]></code>
    /// </example>
    public CachingBuilder UseDevelopmentDefaults()
    {
        if (_services is null)
            throw new InvalidOperationException(
                "UseDevelopmentDefaults() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.PostConfigure<CacheOptions>(o => o.IncludeRawKeyInLogs = true);
        return this;
    }

    /// <summary>
    /// Production-oriented defaults: hashed keys in logs and strict Redis TLS certificate validation.
    /// Requires <c>AddCaching(IServiceCollection, ...)</c>; throws if the builder has no service collection.
    /// </summary>
    /// <example>
    /// Fluent (preset):
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("rediss://...").WithKeyPrefix("svc-prod").UseProductionDefaults());
    /// ]]></code>
    /// Configuration (appsettings.json) — set the underlying flags directly:
    /// <code><![CDATA[
    /// {
    ///   "CacheOptions": {
    ///     "IncludeRawKeyInLogs": false,
    ///     "StrictRedisCertificateValidation": true
    ///   }
    /// }
    /// ]]></code>
    /// </example>
    public CachingBuilder UseProductionDefaults()
    {
        if (_services is null)
            throw new InvalidOperationException(
                "UseProductionDefaults() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.PostConfigure<CacheOptions>(o =>
        {
            o.IncludeRawKeyInLogs = false;
            o.StrictRedisCertificateValidation = true;
        });
        return this;
    }

    /// <summary>
    /// Optional user-key validation on the segment before <see cref="CacheOptions.KeyPrefix"/> is applied.
    /// Return false to skip caching for that key (reads miss / writes no-op). Fluent-only; not bound from JSON.
    /// Default when unset: null (all keys accepted).
    /// </summary>
    /// <example>
    /// Fluent only — delegates are not bindable from JSON.
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithKeyValidator(k => !k.StartsWith("Anon:")));
    /// ]]></code>
    /// </example>
    public CachingBuilder WithKeyValidator(Func<string, bool> validateKey)
    {
        ArgumentNullException.ThrowIfNull(validateKey);
        if (_services is null)
            throw new InvalidOperationException(
                "WithKeyValidator(...) requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        KeyValidator = validateKey;
        return this;
    }

    /// <summary>
    /// Optional normalization of the user key segment before prefixing (e.g. trim, lower-case segments).
    /// Fluent-only; not bound from JSON.
    /// Default when unset: null (no transformation).
    /// </summary>
    /// <example>
    /// Fluent only — delegates are not bindable from JSON.
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithKeyTransformer(k => k.Trim().ToLowerInvariant()));
    /// ]]></code>
    /// </example>
    public CachingBuilder WithKeyTransformer(Func<string, string> transformKey)
    {
        ArgumentNullException.ThrowIfNull(transformKey);
        if (_services is null)
            throw new InvalidOperationException(
                "WithKeyTransformer(...) requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        KeyTransformer = transformKey;
        return this;
    }

    /// <summary>Apply ±<paramref name="percentage"/> jitter to all entry TTLs (clamped to 0–0.5). Default when unset: 0.10 (±10%).</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").WithTtlJitter(0.20));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "TtlJitterPercentage": 0.20 } }
    /// ]]></code>
    /// </example>
    public CachingBuilder WithTtlJitter(double percentage)
    {
        if (_services is null)
            throw new InvalidOperationException(
                "WithTtlJitter() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.PostConfigure<CacheOptions>(o => o.TtlJitterPercentage = Math.Clamp(percentage, 0.0, 0.5));
        return this;
    }

    /// <summary>Cap concurrent in-flight stale-while-revalidate background refreshes. Default when unset: 256.</summary>
    /// <example>
    /// Fluent:
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    ///     .WithStaleRefreshConcurrency(512));
    /// ]]></code>
    /// Configuration (appsettings.json):
    /// <code><![CDATA[
    /// { "CacheOptions": { "StaleRefreshConcurrency": 512 } }
    /// ]]></code>
    /// </example>
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
    /// Default for <see cref="CacheOptions.RequireTagSupport"/> when unset: false.
    /// </summary>
    /// <example>
    /// Fluent (recommended — sets the flag during DI registration):
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").RequireTagSupport());
    /// ]]></code>
    /// Configuration (appsettings.json) — bindable, but the fluent method is the documented entry point:
    /// <code><![CDATA[
    /// { "CacheOptions": { "Mode": "Hybrid", "RequireTagSupport": true } }
    /// ]]></code>
    /// </example>
    public CachingBuilder RequireTagSupport()
    {
        if (_services is null)
            throw new InvalidOperationException(
                "RequireTagSupport() requires a CachingBuilder constructed via AddCaching(IServiceCollection,...). " +
                "Use the AddCaching(builder => ...) overload.");
        _services.PostConfigure<CacheOptions>(o => o.RequireTagSupport = true);
        return this;
    }

    /// <summary>Use the bundled MessagePack serializer. Default <see cref="ICacheSerializer"/> when unset: <see cref="JsonCacheSerializer"/> (reflection-based <c>System.Text.Json</c>).</summary>
    /// <example>
    /// Fluent only — serializer selection is not bindable from JSON.
    /// <code><![CDATA[
    /// services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").WithMessagePackSerializer());
    /// ]]></code>
    /// </example>
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
        if (StrictRedisCertificateValidation.HasValue)
            options.StrictRedisCertificateValidation = StrictRedisCertificateValidation.Value;
        if (KeyValidator is not null)
            options.KeyValidator = KeyValidator;
        if (KeyTransformer is not null)
            options.KeyTransformer = KeyTransformer;
    }
}
