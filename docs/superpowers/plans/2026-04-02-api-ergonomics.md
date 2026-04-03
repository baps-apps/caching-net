# API Ergonomics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace conditional NoOpCacheService registration with an always-registered RoutingCacheService, add a fluent CachingBuilder API with zero-config defaults, and update all docs/samples.

**Architecture:** A new `CachingBuilder` class collects configuration and registers services. All `AddCaching` overloads delegate to a shared `BuildServices` method that always registers `RoutingCacheService` as `ICacheService`. `RoutingCacheService` uses `IOptionsMonitor<CacheOptions>` to hot-reload the `Enabled` flag, short-circuiting to factory/no-op when disabled.

**Tech Stack:** .NET 10, Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Options, StackExchange.Redis, xUnit, Moq

---

## File Map

### New Files
- `src/Caching.NET/CachingBuilder.cs` — fluent builder class
- `tests/Caching.NET.Tests/Builder/CachingBuilderTests.cs` — builder registration + precedence tests

### Modified Files
- `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs` — new overloads, refactor internals to use builder
- `src/Caching.NET/Services/RoutingCacheService.cs` — `IOptionsMonitor`, disabled-state short-circuit
- `tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs` — add disabled-state tests
- `tests/Caching.NET.Tests/Services/RoutingCacheServiceConcurrencyTests.cs` — add disabled-state test
- `tests/Caching.NET.Tests/Validation/CacheRegistrationValidationTests.cs` — update disabled assertion
- `tests/Caching.NET.Tests/Services/BoundaryTests.cs` — add `AddLogging()` to disabled-config tests (RoutingCacheService needs ILogger)
- `samples/Caching.NET.Sample/Program.cs` — show all registration patterns
- `CLAUDE.md` — update architecture docs

### Deleted Files
- `src/Caching.NET/Services/NoOpCacheService.cs`
- `tests/Caching.NET.Tests/Services/NoOpCacheServiceTests.cs`

---

## Task 1: Create CachingBuilder Class

**Files:**
- Create: `src/Caching.NET/CachingBuilder.cs`

- [ ] **Step 1: Create the CachingBuilder class with all fluent methods**

```csharp
// src/Caching.NET/CachingBuilder.cs
using Caching.NET.Options;
using StackExchange.Redis;

namespace Caching.NET;

/// <summary>
/// Fluent builder for configuring Caching.NET services.
/// Returned internally by <c>AddCaching</c> overloads; each method returns <c>this</c> for chaining.
/// </summary>
public sealed class CachingBuilder
{
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

    /// <summary>Sets the Redis instance name used for key prefixing.</summary>
    public CachingBuilder WithInstanceName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        InstanceName = name;
        return this;
    }

    /// <summary>Registers <see cref="Telemetry.OpenTelemetryCacheTelemetry"/> as the telemetry provider.</summary>
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
            options.RedisInstanceName = InstanceName;
        if (DefaultExpiration.HasValue)
            options.DefaultExpiration = DefaultExpiration.Value.ToString();
        if (DefaultLocalExpiration.HasValue)
            options.DefaultLocalExpiration = DefaultLocalExpiration.Value.ToString();
        if (MaximumPayloadBytes.HasValue)
            options.MaximumPayloadBytes = MaximumPayloadBytes.Value;
        if (MaximumKeyLength.HasValue)
            options.MaximumKeyLength = MaximumKeyLength.Value;
        if (MemorySizeLimitMb.HasValue)
            options.MemorySizeLimitMb = MemorySizeLimitMb.Value;
        if (FactoryTimeout.HasValue)
            options.FactoryTimeout = FactoryTimeout.Value.ToString();
        if (StrictCertificateValidation)
            options.StrictRedisCertificateValidation = true;
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/Caching.NET/Caching.NET.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Caching.NET/CachingBuilder.cs
git commit -m "feat: add CachingBuilder fluent API class"
```

---

## Task 2: Refactor ServiceCollectionExtensions to Use CachingBuilder

**Files:**
- Modify: `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Replace the existing AddCaching method and add new overloads**

Replace the entire contents of `ServiceCollectionExtensions.cs` with:

```csharp
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
            // No config section — register options with defaults (Enabled=true is applied below via PostConfigure)
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
                // Only set Enabled=true as default when no config file and no explicit Disable() call
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

        // 7. Register cache infrastructure based on effective mode (even when disabled,
        //    so that flipping Enabled=true at runtime via hot-reload has services available)
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
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/Caching.NET/Caching.NET.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Caching.NET/Extensions/ServiceCollectionExtensions.cs
git commit -m "refactor: ServiceCollectionExtensions uses CachingBuilder, adds new overloads"
```

---

## Task 3: Update RoutingCacheService for IOptionsMonitor and Disabled State

**Files:**
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`

- [ ] **Step 1: Update RoutingCacheService to use IOptionsMonitor and add disabled short-circuit**

Replace the constructor and add the disabled-state check. The full updated file:

In `RoutingCacheService.cs`, make these changes:

1. Change the using/field from `IOptions<CacheOptions>` to `IOptionsMonitor<CacheOptions>`:

Replace:
```csharp
    private readonly CacheOptions _options;
```
With:
```csharp
    private readonly IOptionsMonitor<CacheOptions> _optionsMonitor;
    private readonly CacheOptions _startupOptions;
```

2. Update the constructor:

Replace:
```csharp
    public RoutingCacheService(
        IOptions<CacheOptions> options,
        ILogger<RoutingCacheService> logger,
        Abstractions.ICacheTelemetry telemetry,
        InMemoryCacheService? inMemory = null,
        RedisCacheService? redis = null,
        HybridCacheService? hybrid = null)
    {
        _options = options?.Value ?? new CacheOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _inMemory = inMemory;
        _redis = redis;
        _hybrid = hybrid;
    }
```
With:
```csharp
    public RoutingCacheService(
        IOptionsMonitor<CacheOptions> optionsMonitor,
        ILogger<RoutingCacheService> logger,
        Abstractions.ICacheTelemetry telemetry,
        InMemoryCacheService? inMemory = null,
        RedisCacheService? redis = null,
        HybridCacheService? hybrid = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _startupOptions = optionsMonitor.CurrentValue;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _inMemory = inMemory;
        _redis = redis;
        _hybrid = hybrid;
    }
```

3. Add a helper property and update references:

Add after the constructor:
```csharp
    private bool IsDisabled => !_optionsMonitor.CurrentValue.Enabled;
```

4. In the `GetOrCreateAsync` overload with `CacheCallOptions`, add the disabled check as the first thing (before the bypass check):

At the top of `GetOrCreateAsync(... CacheCallOptions? callOptions ...)`, add:
```csharp
        if (IsDisabled)
        {
            return await factory(cancellationToken).ConfigureAwait(false);
        }
```

5. In the `SetAsync` overload with `CacheCallOptions`, add the disabled check as the first thing:

At the top of `SetAsync(... CacheCallOptions? callOptions ...)`, add:
```csharp
        if (IsDisabled)
            return Task.CompletedTask;
```

6. In `RemoveAsync(string key, ...)`, add:
```csharp
        if (IsDisabled)
            return Task.CompletedTask;
```

7. In `RemoveAsync(IEnumerable<string> keys, ...)`, add:
```csharp
        if (IsDisabled)
            return Task.CompletedTask;
```

8. In `RemoveByTagAsync(string tag, ...)`, add:
```csharp
        if (IsDisabled)
            return Task.CompletedTask;
```

9. In `RemoveByTagAsync(IEnumerable<string> tags, ...)`, add:
```csharp
        if (IsDisabled)
            return Task.CompletedTask;
```

10. Update all references from `_options` to `_startupOptions` (for Mode, FactoryTimeout, etc. — startup-only settings). The `ResolveService` and `ApplyFactoryTimeout` methods use `_startupOptions`:

Replace `_options.Mode` with `_startupOptions.Mode` in `ResolveService` and `ResolveDefaultService`.
Replace `_options.GetFactoryTimeout()` with `_startupOptions.GetFactoryTimeout()` in `ApplyFactoryTimeout` and the `GetOrCreateAsync` telemetry call.

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/Caching.NET/Caching.NET.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Caching.NET/Services/RoutingCacheService.cs
git commit -m "feat: RoutingCacheService uses IOptionsMonitor for hot-reloadable Enabled flag"
```

---

## Task 4: Delete NoOpCacheService

**Files:**
- Delete: `src/Caching.NET/Services/NoOpCacheService.cs`
- Delete: `tests/Caching.NET.Tests/Services/NoOpCacheServiceTests.cs`

- [ ] **Step 1: Delete both files**

```bash
git rm src/Caching.NET/Services/NoOpCacheService.cs
git rm tests/Caching.NET.Tests/Services/NoOpCacheServiceTests.cs
```

- [ ] **Step 2: Verify the solution compiles (no lingering references)**

Run: `dotnet build`
Expected: Build succeeded. If any file still references `NoOpCacheService`, fix it.

- [ ] **Step 3: Commit**

```bash
git commit -m "refactor: delete NoOpCacheService — RoutingCacheService handles disabled state"
```

---

## Task 5: Fix Existing Tests for New Registration Behavior

**Files:**
- Modify: `tests/Caching.NET.Tests/Validation/CacheRegistrationValidationTests.cs`
- Modify: `tests/Caching.NET.Tests/Services/BoundaryTests.cs`

- [ ] **Step 1: Update CacheRegistrationValidationTests — disabled test no longer asserts NoOpCacheService**

Replace the `ValidateCacheRegistration_WhenCachingDisabled_ResolvesNoOp` test:

```csharp
    [Fact]
    public void ValidateCacheRegistration_WhenCachingDisabled_StillResolvesICacheService()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        provider.ValidateCacheRegistration();
        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);
    }
```

- [ ] **Step 2: Update BoundaryTests — add AddLogging() to disabled-config tests**

In `GetOrCreateAsync_WhitespaceKey_Throws`, the config has `Enabled=false`. Now `RoutingCacheService` is registered (which needs `ILogger`). Add `services.AddLogging();` after `new ServiceCollection();`:

```csharp
    [Fact]
    public async Task GetOrCreateAsync_WhitespaceKey_Throws()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Abstractions.ICacheService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.GetOrCreateAsync("   ", _ => Task.FromResult(1)));
    }
```

Note: The whitespace key check now goes through `RoutingCacheService`. When disabled, it calls the factory directly. But the factory in the test never runs because the key validation in `RoutingCacheService.GetOrCreateAsync` delegates to the underlying service. Since disabled mode short-circuits before delegation, we need `RoutingCacheService` to also validate the key when disabled. Add key validation to the disabled path in `RoutingCacheService.GetOrCreateAsync`:

In `src/Caching.NET/Services/RoutingCacheService.cs`, update the disabled check in `GetOrCreateAsync(... CacheCallOptions? callOptions ...)`:

```csharp
        if (IsDisabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
            return await factory(cancellationToken).ConfigureAwait(false);
        }
```

- [ ] **Step 3: Run all existing tests**

Run: `dotnet test`
Expected: All tests pass (NoOpCacheServiceTests will not exist, which is fine).

- [ ] **Step 4: Commit**

```bash
git add tests/Caching.NET.Tests/Validation/CacheRegistrationValidationTests.cs tests/Caching.NET.Tests/Services/BoundaryTests.cs src/Caching.NET/Services/RoutingCacheService.cs
git commit -m "fix: update existing tests for RoutingCacheService-always registration"
```

---

## Task 6: Add RoutingCacheService Disabled-State Tests

**Files:**
- Modify: `tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs`
- Modify: `tests/Caching.NET.Tests/Services/RoutingCacheServiceConcurrencyTests.cs`

- [ ] **Step 1: Write failing tests for disabled state in RoutingCacheServiceTests**

Add these tests to the bottom of `RoutingCacheServiceTests`:

```csharp
    [Fact]
    public async Task WhenDisabled_GetOrCreateAsync_AlwaysRunsFactory()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var first = await cache.GetOrCreateAsync("disabled:1", _ => Task.FromResult("a"));
        var second = await cache.GetOrCreateAsync("disabled:1", _ => Task.FromResult("b"));

        Assert.Equal("a", first);
        Assert.Equal("b", second);
    }

    [Fact]
    public async Task WhenDisabled_SetAndRemove_AreNoOps()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        // These should not throw
        await cache.SetAsync("disabled:set", "value");
        await cache.RemoveAsync("disabled:rem");
        await cache.RemoveAsync(new[] { "disabled:rem1", "disabled:rem2" });
        await cache.RemoveByTagAsync("tag");
        await cache.RemoveByTagAsync(new[] { "tag1", "tag2" });
    }
```

- [ ] **Step 2: Write failing test for disabled state in RoutingCacheServiceConcurrencyTests**

Add this test:

```csharp
    [Fact]
    public async Task WhenDisabled_CoalesceConcurrent_SkipsCoalescing()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var callOptions = new CacheCallOptions { CoalesceConcurrent = true };
        var counter = 0;

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => cache.GetOrCreateAsync(
                "disabled:coalesce",
                ct =>
                {
                    Interlocked.Increment(ref counter);
                    return Task.FromResult("value");
                },
                callOptions,
                cancellationToken: CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        // When disabled, every call runs the factory — no coalescing
        Assert.Equal(5, counter);
    }
```

- [ ] **Step 3: Run the new tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~WhenDisabled"`
Expected: All 3 new tests PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs tests/Caching.NET.Tests/Services/RoutingCacheServiceConcurrencyTests.cs
git commit -m "test: add RoutingCacheService disabled-state tests"
```

---

## Task 7: Add CachingBuilder Tests

**Files:**
- Create: `tests/Caching.NET.Tests/Builder/CachingBuilderTests.cs`

- [ ] **Step 1: Write builder registration and precedence tests**

```csharp
// tests/Caching.NET.Tests/Builder/CachingBuilderTests.cs
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Builder;

public class CachingBuilderTests
{
    [Fact]
    public void AddCaching_ZeroConfig_RegistersICacheService_InMemoryEnabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(CacheMode.InMemory, options.Mode);
    }

    [Fact]
    public void AddCaching_WithConfiguration_RegistersICacheService()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);
    }

    [Fact]
    public void AddCaching_FluentCodeFirst_RegistersICacheService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory());
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(CacheMode.InMemory, options.Mode);
    }

    [Fact]
    public void AddCaching_FluentDisable_StillRegistersICacheService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.Disable());
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.False(options.Enabled);
    }

    [Fact]
    public void AddCaching_FluentOverridesConfig_FluentWins()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, cache => cache.UseHybrid());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(CacheMode.Hybrid, options.Mode);
    }

    [Fact]
    public void AddCaching_FluentWithOpenTelemetry_RegistersOtelTelemetry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithOpenTelemetry());
        using var provider = services.BuildServiceProvider();

        var telemetry = provider.GetRequiredService<ICacheTelemetry>();
        Assert.IsType<Caching.NET.Telemetry.OpenTelemetryCacheTelemetry>(telemetry);
    }

    [Fact]
    public void AddCaching_FluentWithExpiration_SetsOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithDefaultExpiration(TimeSpan.FromMinutes(20))
            .WithDefaultLocalExpiration(TimeSpan.FromMinutes(8))
            .WithMaximumPayloadBytes(5_000_000)
            .WithMaximumKeyLength(512)
            .WithMemorySizeLimit(256)
            .WithFactoryTimeout(TimeSpan.FromSeconds(30))
            .WithInstanceName("test:"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(20), options.GetDefaultExpiration());
        Assert.Equal(TimeSpan.FromMinutes(8), options.GetDefaultLocalExpiration());
        Assert.Equal(5_000_000, options.MaximumPayloadBytes);
        Assert.Equal(512, options.MaximumKeyLength);
        Assert.Equal(256, options.MemorySizeLimitMb);
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetFactoryTimeout());
        Assert.Equal("test:", options.RedisInstanceName);
    }

    [Fact]
    public void AddCaching_UseRedis_WithoutConnectionString_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddCaching(cache => cache.UseRedis("")));
    }

    [Fact]
    public async Task AddCaching_HotReload_DisabledAtRuntime_ShortCircuitsToFactory()
    {
        var configData = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        // Cache a value while enabled
        var first = await cache.GetOrCreateAsync("hotreload:1", _ => Task.FromResult("cached"));
        Assert.Equal("cached", first);

        // Verify it's cached
        var second = await cache.GetOrCreateAsync("hotreload:1", _ => Task.FromResult("other"));
        Assert.Equal("cached", second);

        // Disable via config reload
        configuration["CacheOptions:Enabled"] = "false";
        configuration.Reload();

        // Now factory should run every time
        var third = await cache.GetOrCreateAsync("hotreload:1", _ => Task.FromResult("fresh"));
        Assert.Equal("fresh", third);
    }
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test --filter "FullyQualifiedName~CachingBuilderTests"`
Expected: All tests PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Caching.NET.Tests/Builder/CachingBuilderTests.cs
git commit -m "test: add CachingBuilder registration, precedence, and hot-reload tests"
```

---

## Task 8: Run Full Test Suite

- [ ] **Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass. Zero failures.

- [ ] **Step 2: If any test fails, fix it and re-run**

- [ ] **Step 3: Commit any fixes**

```bash
git add -A
git commit -m "fix: resolve test failures from builder migration"
```

---

## Task 9: Update Sample Project

**Files:**
- Modify: `samples/Caching.NET.Sample/Program.cs`

- [ ] **Step 1: Update Program.cs to show all registration patterns**

Replace the entire contents of `Program.cs`:

```csharp
using Caching.NET.Extensions;

namespace Caching.NET.Sample;

/// <summary>
/// Entry point for the Caching.NET sample web application.
/// Demonstrates all supported registration patterns for Caching.NET.
/// </summary>
public class Program
{
    /// <summary>Builds and runs the sample web host.</summary>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();

        // ────────────────────────────────────────────────────────────
        // Caching.NET Registration — pick ONE of the examples below.
        // ────────────────────────────────────────────────────────────

        // Example 1: Zero-config (InMemory, enabled, sensible defaults)
        //   No appsettings section needed. Great for prototyping or simple apps.
        //
        // builder.Services.AddCaching();

        // Example 2: Config-file driven
        //   Reads CacheOptions from appsettings.json. Existing approach — fully supported.
        //
        // builder.Services.AddCaching(builder.Configuration);

        // Example 3: Fluent code-first
        //   Programmatic configuration with IntelliSense. No config file needed.
        //
        // builder.Services.AddCaching(cache => cache
        //     .UseHybrid("localhost:6379")
        //     .WithDefaultExpiration(TimeSpan.FromMinutes(15))
        //     .WithDefaultLocalExpiration(TimeSpan.FromMinutes(5))
        //     .WithInstanceName("sampleapp:")
        //     .WithOpenTelemetry()
        //     .WithHealthChecks());

        // Example 4: Config-file + fluent overrides (recommended for production)
        //   Base configuration from appsettings.json, with fluent additions.
        //   Fluent settings override config-file values on conflict.
        builder.Services.AddCaching(builder.Configuration, cache => cache
            .WithOpenTelemetry()
            .WithHealthChecks());

        // Example 5: Explicitly disabled (for testing/staging)
        //   ICacheService still resolves — all calls pass through to factories.
        //
        // builder.Services.AddCaching(cache => cache.Disable());

        builder.Services.AddControllers();

        var app = builder.Build();

        // Validate cache registration at startup (fails fast on DI misconfiguration)
        app.Services.ValidateCacheRegistration();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.MapHealthChecks("/health");
        app.MapControllers();

        app.Run();
    }
}
```

- [ ] **Step 2: Verify sample builds**

Run: `dotnet build samples/Caching.NET.Sample/Caching.NET.Sample.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add samples/Caching.NET.Sample/Program.cs
git commit -m "docs: update sample project with all registration patterns"
```

---

## Task 10: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the Architecture section in CLAUDE.md**

Replace the `## Architecture` section (from `## Architecture` up to but not including `## Publishing`) with:

```markdown
## Architecture

### DI Registration & Builder API

`ServiceCollectionExtensions` provides four `AddCaching` overloads:
- `AddCaching()` — zero-config: InMemory, enabled, 10-minute default expiration
- `AddCaching(IConfiguration)` — reads `CacheOptions` from config section
- `AddCaching(Action<CachingBuilder>)` — fluent code-first configuration
- `AddCaching(IConfiguration, Action<CachingBuilder>)` — config base + fluent overrides (fluent wins on conflict)

All overloads delegate to a shared `AddCachingCore` that:
1. Binds config (if provided), then applies fluent overrides via `PostConfigure`
2. Registers cache infrastructure based on the resolved `Mode`
3. **Always** registers `RoutingCacheService` as the `ICacheService` singleton

There is no `NoOpCacheService`. When `Enabled=false`, `RoutingCacheService` short-circuits: `GetOrCreateAsync` runs the factory directly, all other operations return `Task.CompletedTask`.

### Hot-Reloadable Enabled Flag

`RoutingCacheService` reads `Enabled` from `IOptionsMonitor<CacheOptions>.CurrentValue` on every call. Flipping `Enabled` in appsettings takes effect immediately without restart. Mode and connection strings are startup-only (read from `IOptions<CacheOptions>` at construction time).

### Service Resolution Flow

**`RoutingCacheService`** is the central dispatcher registered as `ICacheService`. It resolves to the correct concrete service (`InMemoryCacheService`, `RedisCacheService`, or `HybridCacheService`) based on the configured mode. It also handles per-call overrides via `CacheCallOptions` (mode override, bypass, force refresh, concurrency coalescing) through the internal `IRoutingCacheService` interface.

### CachingBuilder

Fluent API for configuring cache mode (`UseInMemory()`, `UseRedis(conn)`, `UseHybrid()`), expiration, payload limits, telemetry (`WithOpenTelemetry()`), health checks (`WithHealthChecks()`), and explicit disable (`Disable()`). Each method returns the builder for chaining.

### Extension Methods for Per-Call Options

`CacheServiceCallExtensions` provides overloads that accept `CacheCallOptions`. These cast `ICacheService` to `IRoutingCacheService` internally — new per-call features go here, not on `ICacheService`.

### API Stability Contract

`ICacheService` is the stable public interface. New capabilities are added via:
1. Extension methods (`CacheServiceCallExtensions`)
2. Per-call options (`CacheCallOptions`)
3. Configuration (`CacheOptions`)
4. Builder methods (`CachingBuilder`)

Avoid adding new members to `ICacheService` directly.

### Telemetry

`ICacheTelemetry` abstraction with `NoopCacheTelemetry` (default) and `OpenTelemetryCacheTelemetry` (opt-in via `WithOpenTelemetry()`). Meter/ActivitySource name: `Caching.NET.Cache`.
```

- [ ] **Step 2: Verify CLAUDE.md renders correctly**

Read the file and verify formatting.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md architecture section for builder API and no NoOpCacheService"
```

---

## Task 11: Version Bump

**Files:**
- Modify: `src/Caching.NET/Caching.NET.csproj`

- [ ] **Step 1: Bump version to 2.0.0**

In `Caching.NET.csproj`, change:
```xml
<Version>1.0.0</Version>
```
To:
```xml
<Version>2.0.0</Version>
```

- [ ] **Step 2: Final full build + test**

Run: `dotnet build && dotnet test`
Expected: Build succeeded. All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/Caching.NET/Caching.NET.csproj
git commit -m "chore: bump version to 2.0.0 for breaking NoOpCacheService removal"
```

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Create CachingBuilder class | `CachingBuilder.cs` (new) |
| 2 | Refactor ServiceCollectionExtensions | `ServiceCollectionExtensions.cs` |
| 3 | Update RoutingCacheService (IOptionsMonitor + disabled) | `RoutingCacheService.cs` |
| 4 | Delete NoOpCacheService | `NoOpCacheService.cs`, `NoOpCacheServiceTests.cs` |
| 5 | Fix existing tests | `CacheRegistrationValidationTests.cs`, `BoundaryTests.cs` |
| 6 | Add disabled-state tests | `RoutingCacheServiceTests.cs`, `RoutingCacheServiceConcurrencyTests.cs` |
| 7 | Add CachingBuilder tests | `CachingBuilderTests.cs` (new) |
| 8 | Run full test suite | — |
| 9 | Update sample project | `Program.cs` |
| 10 | Update CLAUDE.md | `CLAUDE.md` |
| 11 | Version bump + final validation | `Caching.NET.csproj` |
