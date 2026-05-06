using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using StackExchange.Redis;
using Caching.NET.Abstractions;
using Caching.NET.Configuration;
using Caching.NET.Internal;
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
    /// Registers a lightweight health check for the Caching.NET pipeline.
    /// This complements, but does not replace, infrastructure-specific checks such as Redis health checks.
    /// </summary>
    public static IHealthChecksBuilder AddCachingHealthChecks(
        this IHealthChecksBuilder builder,
        string name = "caching-net",
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus failureStatus =
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
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

        // 5. KeyPrefix fallback: if neither config nor builder set KeyPrefix, default to the entry
        //    assembly name (sanitized). Spec §13 — production deployments should still set KeyPrefix
        //    explicitly, but this avoids forcing every test/sample to specify one and keeps validation
        //    passing for zero-config use.
        services.PostConfigure<CacheOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.KeyPrefix))
            {
                var name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
                           ?? "caching-net";
                options.KeyPrefix = SanitizeKeyPrefix(name);
            }
        });

        // 6. Resolve effective options for service registration (config + builder merged)
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
        if (string.IsNullOrWhiteSpace(effectiveOptions.KeyPrefix))
        {
            effectiveOptions.KeyPrefix = SanitizeKeyPrefix(
                System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "caching-net");
        }

        // 6. Telemetry: v2 uses static CacheInstruments (Meter/ActivitySource).
        //    No DI registration needed; consumers wire OTel pipeline to subscribe.

        // 7. Register cache infrastructure based on effective mode.
        // When disabled, only register minimal InMemory infrastructure (RoutingCacheService
        // will short-circuit anyway, but services must be resolvable if Enabled is toggled on at runtime).
        if (!effectiveOptions.Enabled)
        {
            AddMemoryCacheWithOptions(services, effectiveOptions);
            services.TryAddSingleton<InMemoryCacheService>();
        }
        else
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
                    AddMemoryCacheWithOptions(services, effectiveOptions);
                    services.TryAddSingleton<InMemoryCacheService>();
                    ConfigureHybridCache(services, effectiveOptions, builder?.RedisConfigurationAction);
                    if (!string.IsNullOrWhiteSpace(effectiveOptions.RedisConnectionString) || builder?.RedisConfigurationAction is not null)
                    {
                        EnsureCacheSerializerOptions(services);
                        TryAddConnectionMultiplexer(services, effectiveOptions, builder?.RedisConfigurationAction);
                        services.TryAddSingleton<RedisCacheService>();
                    }
                    services.TryAddSingleton<HybridCacheService>();
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported CacheOptions.Mode: {effectiveOptions.Mode}");
            }
        }

        // 8. Register the striped lock manager for stampede protection.
        services.TryAddSingleton<Internal.StripedLockManager>(sp =>
            new Internal.StripedLockManager(sp.GetRequiredService<IOptions<CacheOptions>>().Value.StripeLockCount));

        services.TryAddSingleton<Internal.StaleEntryTracker>();
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            return new Internal.StaleRefreshThrottle(opts.StaleRefreshConcurrency);
        });

        // 9. Register the default ICacheSerializer (consumers may swap via WithSerializer<T>).
        services.TryAddSingleton<Serialization.ICacheSerializer>(_ => new Serialization.JsonCacheSerializer());

        // 10. Register the default Polly resilience pipeline registry (timeout + circuit breaker + retry).
        services.TryAddSingleton<Polly.Registry.ResiliencePipelineRegistry<string>>(sp =>
        {
            var opts = sp.GetService<IOptions<Resilience.ResiliencePipelineRegistryOptions>>()?.Value
                       ?? new Resilience.ResiliencePipelineRegistryOptions();
            return Resilience.CacheResiliencePipelineBuilder.BuildDefaultRegistry(
                timeout: opts.Timeout,
                failureRatio: opts.FailureRatio,
                minimumThroughput: opts.MinimumThroughput,
                samplingDuration: opts.SamplingDuration,
                breakDuration: opts.BreakDuration,
                retryCount: opts.RetryCount);
        });

        // 11. Always register RoutingCacheService as ICacheService (TryAdd for idempotency)
        services.TryAddSingleton<ICacheService, RoutingCacheService>();

        // 9. Register health checks if requested via builder
        if (builder?.RegisterHealthChecks == true)
        {
            services.AddHealthChecks()
                .AddCachingHealthChecks(name: builder.HealthCheckName);
        }

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
        if (!services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<CacheSerializerOptions>)))
        {
            services.AddOptions<CacheSerializerOptions>()
                .Configure(_ => { })
                .ValidateDataAnnotations()
                .ValidateOnStart();
        }
    }

    private static void TryAddConnectionMultiplexer(
        IServiceCollection services,
        CacheOptions options,
        Action<ConfigurationOptions>? redisConfigAction)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            ConfigurationOptions conf;
            if (redisConfigAction is not null)
            {
                conf = new ConfigurationOptions();
                redisConfigAction(conf);
            }
            else
            {
                conf = ConfigurationOptions.Parse(options.RedisConnectionString!, ignoreUnknown: true);
            }
            conf.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(conf);
        });
    }

    private static string SanitizeKeyPrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "caching-net";
        Span<char> buffer = stackalloc char[Math.Min(raw.Length, 64)];
        var len = Math.Min(raw.Length, 64);
        for (var i = 0; i < len; i++)
        {
            var c = raw[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '.' or ':' or '-' or '_' ? c : '-';
        }
        if (buffer.Length == 0) return "caching-net";
        if (!char.IsLetterOrDigit(buffer[0])) buffer[0] = 'a';
        return new string(buffer);
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
            var strict = options.StrictRedisCertificateValidation;
            redis.ConfigurationOptions.CertificateValidation += (sender, certificate, chain, errors) =>
                RedisCertificateValidation.ValidateServerCertificate(sender, certificate, chain, errors, strict);
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
                var strict = options.StrictRedisCertificateValidation;
                redis.ConfigurationOptions.CertificateValidation += (sender, certificate, chain, errors) =>
                    RedisCertificateValidation.ValidateServerCertificate(sender, certificate, chain, errors, strict);
            });
        }
    }
}
