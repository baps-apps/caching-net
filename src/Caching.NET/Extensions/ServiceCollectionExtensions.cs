using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
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

        return builder.AddCheck<Caching.NET.Health.CachingHealthCheck>(
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
            builder = new CachingBuilder();
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

        // 6. Register telemetry
        if (builder?.RegisterOpenTelemetry == true)
        {
            services.TryAddSingleton<ICacheTelemetry, Caching.NET.Telemetry.OpenTelemetryCacheTelemetry>();
        }
        else
        {
            services.TryAddSingleton<ICacheTelemetry, Caching.NET.Telemetry.NoopCacheTelemetry>();
        }

        // 7. Register cache infrastructure based on effective mode
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
                services.TryAddSingleton<RedisCacheService>();
                break;

            case CacheMode.Hybrid:
                AddMemoryCacheWithOptions(services, effectiveOptions);
                services.TryAddSingleton<InMemoryCacheService>();
                ConfigureHybridCache(services, effectiveOptions, builder?.RedisConfigurationAction);
                if (!string.IsNullOrWhiteSpace(effectiveOptions.RedisConnectionString) || builder?.RedisConfigurationAction is not null)
                {
                    EnsureCacheSerializerOptions(services);
                    services.TryAddSingleton<RedisCacheService>();
                }
                services.TryAddSingleton<HybridCacheService>();
                break;

            default:
                throw new InvalidOperationException($"Unsupported CacheOptions.Mode: {effectiveOptions.Mode}");
        }

        // 8. Always register RoutingCacheService as ICacheService
        services.AddSingleton<ICacheService, RoutingCacheService>();

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

            if (!string.IsNullOrWhiteSpace(options.RedisInstanceName))
                redis.InstanceName = options.RedisInstanceName;
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
            if (options.MaximumPayloadBytes.HasValue)
                hybrid.MaximumPayloadBytes = options.MaximumPayloadBytes.Value;
            if (options.MaximumKeyLength.HasValue)
                hybrid.MaximumKeyLength = options.MaximumKeyLength.Value;
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

                if (!string.IsNullOrWhiteSpace(options.RedisInstanceName))
                    redis.InstanceName = options.RedisInstanceName;
                var strict = options.StrictRedisCertificateValidation;
                redis.ConfigurationOptions.CertificateValidation += (sender, certificate, chain, errors) =>
                    RedisCertificateValidation.ValidateServerCertificate(sender, certificate, chain, errors, strict);
            });
        }
    }
}
