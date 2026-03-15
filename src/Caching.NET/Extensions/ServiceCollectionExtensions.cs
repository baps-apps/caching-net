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
/// </summary>
public static class ServiceCollectionExtensions
{
    private const int DefaultExpirationMinutes = 10;
    private const int DefaultLocalExpirationMinutes = 5;

    /// <summary>
    /// Reads <see cref="Options.CacheOptions"/> from the <c>CacheOptions</c> configuration section and registers
    /// <see cref="Abstractions.ICacheService"/> based on <see cref="Options.CacheOptions.Mode"/> and <see cref="Options.CacheOptions.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// When <see cref="Options.CacheOptions.Enabled"/> is <c>false</c>, a <c>NoOpCacheService</c> is registered so consumers
    /// can keep depending on <see cref="Abstractions.ICacheService"/> without null checks.
    /// When enabled, the service registered depends on <see cref="Options.CacheOptions.Mode"/>:
    /// <list type="bullet">
    ///   <item><description><see cref="Options.CacheMode.InMemory"/> — registers <c>IMemoryCache</c> and <c>InMemoryCacheService</c>.</description></item>
    ///   <item><description><see cref="Options.CacheMode.Redis"/> — additionally registers <c>IDistributedCache</c> (StackExchange.Redis) and <c>RedisCacheService</c>.</description></item>
    ///   <item><description><see cref="Options.CacheMode.Hybrid"/> — registers both tiers plus <c>HybridCache</c> and <c>HybridCacheService</c>.</description></item>
    /// </list>
    /// A default <c>NoopCacheTelemetry</c> is registered unless the application has already registered its own <see cref="Abstractions.ICacheTelemetry"/>.
    /// </remarks>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configuration">
    /// The application configuration. Must contain a <c>CacheOptions</c> section that can be bound to <see cref="Options.CacheOptions"/>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> so that calls can be chained.</returns>
    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(CacheConfigurationKeys.CacheOptions);
        services.AddOptions<CacheOptions>()
            .Bind(section)
            .Validate(ValidateCacheOptions, "Invalid CacheOptions configuration when Enabled is true.")
            .ValidateOnStart();

        CacheOptions options = new();
        section.Bind(options);

        if (!options.Enabled)
        {
            services.AddSingleton<ICacheService, NoOpCacheService>();
            return services;
        }

        // Telemetry: register a default no-op implementation if the consumer has not provided one.
        services.TryAddSingleton<Caching.NET.Abstractions.ICacheTelemetry, Caching.NET.Telemetry.NoopCacheTelemetry>();

        switch (options.Mode)
        {
            case CacheMode.InMemory:
                AddMemoryCacheWithOptions(services, options);
                services.AddSingleton<InMemoryCacheService>();
                break;

            case CacheMode.Redis:
                if (string.IsNullOrWhiteSpace(options.RedisConnectionString))
                    throw new InvalidOperationException("CacheOptions.Mode is Redis but RedisConnectionString is not set.");
                AddMemoryCacheWithOptions(services, options);
                services.AddSingleton<InMemoryCacheService>();
                ConfigureRedisCache(services, options);
                EnsureCacheSerializerOptions(services);
                services.AddSingleton<RedisCacheService>();
                break;

            case CacheMode.Hybrid:
                AddMemoryCacheWithOptions(services, options);
                services.AddSingleton<InMemoryCacheService>();
                ConfigureHybridCache(services, options); // registers HybridCache and, when RedisConnectionString is set, AddStackExchangeRedisCache
                if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
                {
                    EnsureCacheSerializerOptions(services);
                    services.AddSingleton<RedisCacheService>();
                }
                services.AddSingleton<HybridCacheService>();
                break;

            default:
                throw new InvalidOperationException($"Unsupported CacheOptions.Mode: {options.Mode}");
        }

        services.AddSingleton<ICacheService, RoutingCacheService>();

        return services;
    }

    /// <summary>
    /// Registers a lightweight health check for the Caching.NET pipeline.
    /// This complements, but does not replace, infrastructure-specific checks such as Redis health checks.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">Optional registration name; defaults to "caching-net".</param>
    /// <param name="failureStatus">
    /// The <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus"/> to report on failure.
    /// </param>
    /// <returns>The same <see cref="IHealthChecksBuilder"/> so that calls can be chained.</returns>
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
    /// Validates that <see cref="Abstractions.ICacheService"/> can be resolved and the configured mode is available.
    /// Call this after building the service provider (e.g., in host startup) to fail fast on DI misconfiguration.
    /// Does not probe the cache backend (e.g., Redis); use health checks for connectivity.
    /// </summary>
    /// <param name="serviceProvider">The built service provider to validate.</param>
    /// <returns>The same <see cref="IServiceProvider"/> so that the call can be chained.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Abstractions.ICacheService"/> cannot be resolved.</exception>
    public static IServiceProvider ValidateCacheRegistration(this IServiceProvider serviceProvider)
    {
        _ = serviceProvider.GetRequiredService<ICacheService>();
        return serviceProvider;
    }

    private static void AddMemoryCacheWithOptions(IServiceCollection services, CacheOptions options)
    {
        if (options.MemorySizeLimitMb.HasValue)
        {
            var limit = options.MemorySizeLimitMb.Value;
            services.AddMemoryCache(memory =>
            {
                memory.SizeLimit = limit * 1024L * 1024L; // MB to bytes; SizeLimit is in units of size
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

    private static void ConfigureRedisCache(IServiceCollection services, CacheOptions options)
    {
        services.AddStackExchangeRedisCache(redis =>
        {
            redis.ConfigurationOptions = ConfigurationOptions.Parse(options.RedisConnectionString!, ignoreUnknown: true);
            if (!string.IsNullOrWhiteSpace(options.RedisInstanceName))
                redis.InstanceName = options.RedisInstanceName;
            var strict = options.StrictRedisCertificateValidation;
            redis.ConfigurationOptions.CertificateValidation += (sender, certificate, chain, errors) =>
                RedisCertificateValidation.ValidateServerCertificate(sender, certificate, chain, errors, strict);
        });
    }

    private static void ConfigureHybridCache(IServiceCollection services, CacheOptions options)
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

        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            services.AddStackExchangeRedisCache(redis =>
            {
                redis.ConfigurationOptions = ConfigurationOptions.Parse(options.RedisConnectionString, ignoreUnknown: true);
                if (!string.IsNullOrWhiteSpace(options.RedisInstanceName))
                    redis.InstanceName = options.RedisInstanceName;
                var strict = options.StrictRedisCertificateValidation;
                redis.ConfigurationOptions.CertificateValidation += (sender, certificate, chain, errors) =>
                    RedisCertificateValidation.ValidateServerCertificate(sender, certificate, chain, errors, strict);
            });
        }
    }
}
