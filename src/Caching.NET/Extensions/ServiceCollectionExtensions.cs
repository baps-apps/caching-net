using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using StackExchange.Redis;
using Caching.NET.Abstractions;
using Caching.NET.Configuration;
using Caching.NET.Internal;
using Caching.NET.Keys;
using Caching.NET.Options;
using Caching.NET.Services;

namespace Caching.NET.Extensions;

/// <summary>
/// Extension methods for registering Caching.NET (InMemory, Redis, or Hybrid).
/// Supports zero-config, config-file, fluent builder, and combined registration patterns.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const int DefaultExpirationMinutes = 10;
    private const int DefaultLocalExpirationMinutes = 5;

    /// <summary>
    /// Registers Caching.NET with sensible defaults: InMemory mode, enabled, 10-minute expiration.
    /// No configuration section required.
    /// </summary>
    public static IServiceCollection AddCaching(this IServiceCollection services)
    {
        return AddCachingCore(services, configuration: null, configure: null);
    }

    /// <summary>
    /// Registers Caching.NET using the <c>CacheOptions</c> configuration section.
    /// When <c>Enabled</c> is false, <see cref="ICacheService"/> is still registered but short-circuits to factories.
    /// </summary>
    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddCachingCore(services, configuration, configure: null);
    }

    /// <summary>
    /// Registers Caching.NET using a fluent builder for code-first configuration.
    /// </summary>
    public static IServiceCollection AddCaching(this IServiceCollection services, Action<CachingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddCachingCore(services, configuration: null, configure);
    }

    /// <summary>
    /// Registers Caching.NET using a configuration section as the base, with fluent builder overrides applied on top.
    /// Fluent settings take precedence over config-file values.
    /// </summary>
    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration, Action<CachingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);
        return AddCachingCore(services, configuration, configure);
    }

    /// <summary>
    /// Registers Caching.NET health check(s) on the builder.
    /// By default registers one <see cref="Health.CachingHealthCheck"/> (readiness).
    /// When <paramref name="splitLivenessReadiness"/> is true, registers <see cref="Health.CachingLivenessHealthCheck"/> and <see cref="Health.CachingHealthCheck"/> with names <c>{name}-liveness</c> / <c>{name}-readiness</c> and tags <c>liveness</c> / <c>readiness</c>.
    /// </summary>
    public static IHealthChecksBuilder AddCachingHealthChecks(
        this IHealthChecksBuilder builder,
        string name = "caching-net",
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus failureStatus =
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        bool splitLivenessReadiness = false)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (splitLivenessReadiness)
        {
            return builder.AddCheck<Health.CachingLivenessHealthCheck>(
                    name: $"{name}-liveness",
                    failureStatus: failureStatus,
                    tags: new[] { "liveness" })
                .AddCheck<Health.CachingHealthCheck>(
                    name: $"{name}-readiness",
                    failureStatus: failureStatus,
                    tags: new[] { "readiness" });
        }

        return builder.AddCheck<Health.CachingHealthCheck>(
            name: name,
            failureStatus: failureStatus);
    }

    /// <summary>
    /// Validates that <see cref="ICacheService"/> can be resolved.
    /// Call this after building the service provider to fail fast on DI misconfiguration.
    /// </summary>
    public static IServiceProvider ValidateCacheRegistration(this IServiceProvider serviceProvider)
    {
        _ = serviceProvider.GetRequiredService<ICacheService>();
        return serviceProvider;
    }

    private static IServiceCollection AddCachingCore(
        IServiceCollection services,
        IConfiguration? configuration,
        Action<CachingBuilder>? configure)
    {
        // 1. Bind config section if provided
        // Register the v2 IValidateOptions implementation (regex + range checks with redacted error messages).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, Validation.CacheOptionsValidator>());

        if (configuration is not null)
        {
            IConfigurationSection section = configuration.GetSection(CacheConfigurationKeys.CacheOptions);
            services.AddOptions<CacheOptions>()
                .Bind(section)
                .Validate(ValidateCacheOptions, "Invalid CacheOptions configuration when Enabled is true.")
                .ValidateOnStart();
        }
        else
        {
            services.AddOptions<CacheOptions>()
                .Validate(ValidateCacheOptions, "Invalid CacheOptions configuration when Enabled is true.")
                .ValidateOnStart();
        }

        // 2. Execute fluent builder if provided
        CachingBuilder? builder = null;
        if (configure is not null)
        {
            builder = new CachingBuilder(services);
            configure(builder);
        }

        // 3. Apply fluent overrides via PostConfigure (runs after config binding)
        if (builder is not null)
        {
            var capturedBuilder = builder;
            services.PostConfigure<CacheOptions>(options => capturedBuilder.ApplyTo(options));
        }

        // 4. Apply zero-config defaults: if no config and no fluent Enabled setting, default to enabled
        if (configuration is null && builder?.Enabled is null)
        {
            services.PostConfigure<CacheOptions>(options =>
            {
                options.Enabled = true;
            });
        }

        // 5. Resolve effective options for service registration (config + builder merged)
        CacheOptions effectiveOptions = new();
        if (configuration is not null)
        {
            configuration.GetSection(CacheConfigurationKeys.CacheOptions).Bind(effectiveOptions);
        }
        builder?.ApplyTo(effectiveOptions);
        if (configuration is null && builder?.Enabled is null)
        {
            effectiveOptions.Enabled = true;
        }
        // 6. Telemetry: v2 uses static CacheInstruments (Meter/ActivitySource).
        //    No DI registration needed; consumers wire OTel pipeline to subscribe.

        // 7. Register cache infrastructure.
        // When Enabled is false, the cache is OFF at every level:
        //   - No backend (no MemoryCache, no Redis multiplexer, no HybridCache).
        //   - No serializer, no resilience pipeline, no TLS validator.
        //   - No options validation (CacheOptionsValidator short-circuits on Enabled=false).
        // Health checks are registered later (step 10) when WithHealthChecks() was used; the
        // check returns healthy when caching is disabled without probing backends.
        // RoutingCacheService is still registered so injected ICacheService consumers resolve;
        // every operation short-circuits to the factory (GetOrCreateAsync) or no-ops.
        if (effectiveOptions.Enabled)
        {
            switch (effectiveOptions.Mode)
            {
                case CacheMode.InMemory:
                    AddMemoryCacheWithOptions(services, effectiveOptions);
                    services.TryAddSingleton<InMemoryCacheService>();
                    break;

                case CacheMode.Redis:
                    if (string.IsNullOrWhiteSpace(effectiveOptions.RedisConnectionString) && builder?.RedisConfigurationAction is null)
                        throw new InvalidOperationException("CacheOptions.Mode is Redis but RedisConnectionString is not set.");
                    AddMemoryCacheWithOptions(services, effectiveOptions);
                    services.TryAddSingleton<InMemoryCacheService>();
                    ConfigureRedisCache(services, effectiveOptions, builder?.RedisConfigurationAction);
                    EnsureCacheSerializerOptions(services);
                    TryAddConnectionMultiplexer(services, effectiveOptions, builder?.RedisConfigurationAction);
                    services.TryAddSingleton<RedisCacheService>();
                    break;

                case CacheMode.Hybrid:
                    if (string.IsNullOrWhiteSpace(effectiveOptions.RedisConnectionString) && builder?.RedisConfigurationAction is null)
                        throw new InvalidOperationException("CacheOptions.Mode is Hybrid but RedisConnectionString is not set.");
                    AddMemoryCacheWithOptions(services, effectiveOptions);
                    services.TryAddSingleton<InMemoryCacheService>();
                    ConfigureHybridCache(services, effectiveOptions, builder?.RedisConfigurationAction);
                    EnsureCacheSerializerOptions(services);
                    TryAddConnectionMultiplexer(services, effectiveOptions, builder?.RedisConfigurationAction);
                    services.TryAddSingleton<RedisCacheService>();
                    services.TryAddSingleton<HybridCacheService>();
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported CacheOptions.Mode: {effectiveOptions.Mode}");
            }

            // 7b. TLS certificate validator (Redis/Hybrid only, only when enabled).
            if (effectiveOptions.Mode is CacheMode.Redis or CacheMode.Hybrid)
            {
                services.TryAddSingleton(sp =>
                {
                    var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
                    var logger = sp.GetRequiredService<ILogger<RedisCertificateValidator>>();
                    var validator = new RedisCertificateValidator(logger, opts.StrictRedisCertificateValidation);
                    RedisCertificateValidation.Configure(validator);
                    return validator;
                });
            }

            // 8. Default ICacheSerializer (consumers may swap via WithSerializer<T>).
            services.TryAddSingleton<Serialization.ICacheSerializer>(_ => new Serialization.JsonCacheSerializer());

            // 9. Default Polly resilience pipeline registry (timeout + circuit breaker + retry).
            services.TryAddSingleton<Polly.Registry.ResiliencePipelineRegistry<string>>(sp =>
            {
                var opts = sp.GetService<IOptions<Resilience.CacheResilienceOptions>>()?.Value
                           ?? new Resilience.CacheResilienceOptions();
                return Resilience.CacheResiliencePipelineBuilder.BuildDefaultRegistry(
                    timeout: opts.Timeout,
                    failureRatio: opts.FailureRatio,
                    minimumThroughput: opts.MinimumThroughput,
                    samplingDuration: opts.SamplingDuration,
                    breakDuration: opts.BreakDuration,
                    retryCount: opts.RetryCount,
                    enableRedisConcurrencyLimiter: opts.EnableRedisConcurrencyLimiter,
                    redisConcurrencyPermitLimit: opts.RedisConcurrencyPermitLimit,
                    redisConcurrencyQueueLimit: opts.RedisConcurrencyQueueLimit);
            });

        }

        // 10. Health checks (when opted in via builder). Registered regardless of Enabled so
        // the health-check endpoint stays wired. The check itself returns Healthy when caching
        // is disabled by configuration — no backend probe is attempted.
        if (builder?.RegisterHealthChecks == true)
        {
            services.AddHealthChecks()
                .AddCachingHealthChecks(
                    name: builder.HealthCheckName,
                    splitLivenessReadiness: builder.HealthCheckSplit);
        }

        // 11. Key factory (override by registering ICacheKeyFactory before AddCaching, or add another registration after).
        services.TryAddSingleton<ICacheKeyFactory, DefaultCacheKeyFactory>();

        // 12. Always register routing infrastructure so RoutingCacheService can be constructed.
        // These are stateless and zero-cost when the cache short-circuits.
        services.TryAddSingleton<Internal.StripedLockManager>(sp =>
            new Internal.StripedLockManager(sp.GetRequiredService<IOptions<CacheOptions>>().Value.StripeLockCount));
        services.TryAddSingleton<Internal.StaleEntryTracker>();
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            return new Internal.StaleRefreshThrottle(opts.StaleRefreshConcurrency);
        });

        // 13. Always register RoutingCacheService as ICacheService (TryAdd for idempotency).
        // When Enabled is false, RoutingCacheService short-circuits every call.
        services.TryAddSingleton<ICacheService, RoutingCacheService>();

        return services;
    }

    private static void AddMemoryCacheWithOptions(IServiceCollection services, CacheOptions options)
    {
        if (options.MemorySizeLimitMb.HasValue)
        {
            var limit = options.MemorySizeLimitMb.Value;
            services.AddMemoryCache(memory =>
            {
                memory.SizeLimit = limit * 1024L * 1024L;
            });
        }
        else
        {
            services.AddMemoryCache();
        }
    }

    private static void EnsureCacheSerializerOptions(IServiceCollection services)
    {
        services.AddOptions<CacheSerializerOptions>()
            .Configure(static options =>
            {
                // Align with JsonCacheSerializer defaults so ValidateDataAnnotations ([Required]) passes
                // when the host does not call Configure<CacheSerializerOptions>.
                options.JsonSerializerOptions ??= new JsonSerializerOptions(JsonSerializerDefaults.Web);
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    private static void TryAddConnectionMultiplexer(
        IServiceCollection services,
        CacheOptions options,
        Action<ConfigurationOptions>? redisConfigAction)
    {
        // Register the rotator that reloads the multiplexer when the connection string changes.
        services.TryAddSingleton<RedisConnectionRotator>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<CacheOptions>>();
            var logger = sp.GetRequiredService<ILogger<RedisConnectionRotator>>();
            return new RedisConnectionRotator(monitor, conn =>
            {
                var conf = ConfigurationOptions.Parse(conn, ignoreUnknown: true);
                conf.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(conf);
            }, logger);
        });
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RedisConnectionRotator>());

        services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        {
            // Ensure the TLS validator is configured before the multiplexer connects.
            _ = sp.GetService<RedisCertificateValidator>();

            // Use the rotator's current multiplexer when available (normal hosted-app path).
            var rotator = sp.GetRequiredService<RedisConnectionRotator>();
            if (rotator.Current is IConnectionMultiplexer mux) return mux;

            // Fallback: rotator not yet started (pure DI without IHost) or redisConfigAction used.
            ConfigurationOptions conf;
            if (redisConfigAction is not null)
            {
                conf = new ConfigurationOptions();
                redisConfigAction(conf);
            }
            else
            {
                conf = ConfigurationOptions.Parse(options.RedisConnectionString ?? "localhost:6379", ignoreUnknown: true);
            }
            conf.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(conf);
        });
    }

    private static bool ValidateCacheOptions(CacheOptions options)
    {
        if (!options.Enabled)
        {
            return true;
        }

        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(options, context, results, validateAllProperties: true);
    }

    private static void ConfigureRedisCache(IServiceCollection services, CacheOptions options, Action<ConfigurationOptions>? redisConfigAction)
    {
        services.AddStackExchangeRedisCache(redis =>
        {
            if (redisConfigAction is not null)
            {
                var configOptions = new ConfigurationOptions();
                redisConfigAction(configOptions);
                redis.ConfigurationOptions = configOptions;
            }
            else
            {
                redis.ConfigurationOptions = ConfigurationOptions.Parse(options.RedisConnectionString!, ignoreUnknown: true);
            }

            // RedisInstanceName removed in v2; KeyPrefix is applied at the routing layer instead.
            redis.ConfigurationOptions.CertificateValidation += RedisCertificateValidation.ValidateServerCertificate;
        });
    }

    private static void ConfigureHybridCache(IServiceCollection services, CacheOptions options, Action<ConfigurationOptions>? redisConfigAction)
    {
        services.AddHybridCache(hybrid =>
        {
            TimeSpan? defaultExpiration = options.GetDefaultExpiration();
            TimeSpan? defaultLocalExpiration = options.GetDefaultLocalExpiration();
            hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = defaultExpiration ?? TimeSpan.FromMinutes(DefaultExpirationMinutes),
                LocalCacheExpiration = defaultLocalExpiration ?? defaultExpiration ?? TimeSpan.FromMinutes(DefaultLocalExpirationMinutes)
            };
            if (options.MaximumPayloadBytes > 0)
                hybrid.MaximumPayloadBytes = options.MaximumPayloadBytes;
            if (options.MaximumKeyLength > 0)
                hybrid.MaximumKeyLength = options.MaximumKeyLength;
        });

        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString) || redisConfigAction is not null)
        {
            services.AddStackExchangeRedisCache(redis =>
            {
                if (redisConfigAction is not null)
                {
                    var configOptions = new ConfigurationOptions();
                    redisConfigAction(configOptions);
                    redis.ConfigurationOptions = configOptions;
                }
                else
                {
                    redis.ConfigurationOptions = ConfigurationOptions.Parse(options.RedisConnectionString!, ignoreUnknown: true);
                }

                // RedisInstanceName removed in v2; KeyPrefix is applied at the routing layer instead.
                redis.ConfigurationOptions.CertificateValidation += RedisCertificateValidation.ValidateServerCertificate;
            });
        }
    }
}
