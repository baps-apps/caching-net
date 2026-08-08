using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.NullObjects;
using ZiggyCreatures.Caching.Fusion.Serialization;
using ZiggyCreatures.Caching.Fusion.Serialization.NeueccMessagePack;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;
using MicrosoftOptions = Microsoft.Extensions.Options.Options;

namespace Caching.NET.Internal;

/// <summary>
/// Builds a fully-wired cache instance from <see cref="CachingOptions"/>. This is the single
/// place where Caching.NET configuration is translated into engine configuration; nothing above
/// this class, and nothing in a consuming application, touches the engine's own setup surface.
/// </summary>
internal static class CacheEngineFactory
{
    public static CacheInstance Create(IServiceProvider serviceProvider, string cacheName)
    {
        var options = serviceProvider.GetRequiredService<IOptionsMonitor<CachingOptions>>().Get(cacheName);
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(CacheLogCategories.Root);
        var telemetry = new CacheTelemetryContext(options);
        var guard = new CacheGuard(options, telemetry, logger);

        if (!options.Enabled)
        {
            CacheLogMessages.CachingDisabled(logger, cacheName);
            var disabled = new NullFusionCache(MicrosoftOptions.Create(new FusionCacheOptions { CacheName = cacheName }));
            return new CacheInstance(cacheName, disabled, guard, telemetry);
        }

        var engineOptions = MapEngineOptions(options, guard);
        var memoryCache = CreateMemoryCache(options);
        var redactKeys = !options.Security.AllowRawKeysInLogs;
        var engineLogger = new CachingCategoryLogger<FusionCache>(loggerFactory, redactKeys: redactKeys);

        var cache = new FusionCache(MicrosoftOptions.Create(engineOptions), memoryCache, engineLogger);

        RedisConnectionProvider? redisConnection = null;
        RedisCache? distributedCache = null;

        if (options.UsesDistributedLayer)
        {
            redisConnection = new RedisConnectionProvider(
                cacheName,
                options.Redis,
                loggerFactory.CreateLogger(CacheLogCategories.Redis),
                telemetry);

            distributedCache = new RedisCache(MicrosoftOptions.Create(new RedisCacheOptions
            {
                ConnectionMultiplexerFactory = redisConnection.GetConnectionAsync,
                InstanceName = options.Redis.InstancePrefix
            }));

            var serializer = new InstrumentedCacheSerializer(
                CreateWireSerializer(options.Serialization),
                options.Serialization,
                cacheName,
                telemetry,
                logger);

            cache.SetupDistributedCache(distributedCache, serializer);

            if (options.Backplane.Enabled)
            {
                var backplaneOptions = new RedisBackplaneOptions
                {
                    ConnectionMultiplexerFactory = redisConnection.GetConnectionAsync
                };

                var backplane = new RedisBackplane(
                    MicrosoftOptions.Create(backplaneOptions),
                    new CachingCategoryLogger<RedisBackplane>(loggerFactory, CacheLogCategories.Backplane, redactKeys));

                // Wrapped so backplane failures reach caching.net.backplane.errors: the engine's
                // event hub reports circuit-breaker transitions but not failures, which left the
                // counter reading zero through a whole outage.
                cache.SetupBackplane(InstrumentedBackplane.Wrap(backplane, telemetry));
            }
        }

        var eventBridge = CacheEventBridge.Attach(cache, telemetry);

        if (options.Observability.LogStartupSummary)
        {
            LogStartupSummary(logger, options);
        }

        WarnIfSerializationFailuresReachTheCaller(logger, options);
        WarnIfHybridHasNoBackplane(logger, options);

        return new CacheInstance(cacheName, cache, guard, telemetry, eventBridge, cache, distributedCache, memoryCache, redisConnection);
    }

    internal static FusionCacheOptions MapEngineOptions(CachingOptions options, CacheGuard guard)
    {
        var prefix = options.BuildKeyPrefix();
        var resilience = options.Resilience;
        var observability = options.Observability;

        var engineOptions = new FusionCacheOptions
        {
            CacheName = options.CacheName,
            CacheKeyPrefix = prefix.Length == 0 ? string.Empty : prefix + CachingDefaults.KeyPrefixSeparator,
            DefaultEntryOptions = MapEntryOptions(options),

            // Enforces the configured key-length limit at the engine boundary, so every call that
            // does not carry explicit per-entry options is guarded without a wrapper.
            DefaultEntryOptionsProvider = new KeyGuardEntryOptionsProvider(guard),

            DistributedCacheCircuitBreakerDuration = resilience.DistributedCircuitBreakerDuration,
            BackplaneCircuitBreakerDuration = resilience.BackplaneCircuitBreakerDuration,

            EnableAutoRecovery = resilience.AutoRecoveryEnabled,
            AutoRecoveryDelay = resilience.AutoRecoveryDelay,
            AutoRecoveryMaxItems = resilience.AutoRecoveryMaxItems,
            AutoRecoveryMaxRetryCount = resilience.AutoRecoveryMaxRetryCount,

            ReThrowOriginalExceptions = resilience.ThrowOriginalExceptions,

            // Tags and keys are frequently tenant- or user-scoped: keep them out of telemetry
            // unless the application opts in.
            IncludeTagsInLogs = options.Security.AllowTagsInTelemetry,
            IncludeTagsInTraces = options.Security.AllowTagsInTelemetry,
            IncludeTagsInMetrics = options.Security.AllowTagsInTelemetry,

            DistributedCacheErrorsLogLevel = observability.DistributedCacheErrorLogLevel,
            DistributedCacheSyntheticTimeoutsLogLevel = observability.SyntheticTimeoutLogLevel,
            BackplaneErrorsLogLevel = observability.BackplaneErrorLogLevel,
            BackplaneSyntheticTimeoutsLogLevel = observability.SyntheticTimeoutLogLevel,
            SerializationErrorsLogLevel = observability.SerializationErrorLogLevel,
            FailSafeActivationLogLevel = observability.FailSafeActivationLogLevel,
            FactoryErrorsLogLevel = observability.FactoryErrorLogLevel,
            FactorySyntheticTimeoutsLogLevel = observability.SyntheticTimeoutLogLevel,

            WaitForInitialBackplaneSubscribe = options.Backplane.WaitForInitialSubscribe
        };

        if (!string.IsNullOrWhiteSpace(options.Backplane.ChannelPrefix))
        {
            engineOptions.BackplaneChannelPrefix = options.Backplane.ChannelPrefix;
        }
        else if (prefix.Length > 0)
        {
            engineOptions.BackplaneChannelPrefix = prefix;
        }

        return engineOptions;
    }

    internal static FusionCacheEntryOptions MapEntryOptions(CachingOptions options)
    {
        var entry = options.Entry;
        var resilience = options.Resilience;

        var entryOptions = new FusionCacheEntryOptions
        {
            Duration = options.DefaultExpiration,
            DistributedCacheDuration = entry.DistributedExpiration,
            MemoryCacheDuration = entry.LocalExpiration,
            EagerRefreshThreshold = entry.EagerRefreshThreshold,
            JitterMaxDuration = entry.JitterMaxDuration,
            Priority = entry.Priority,
            Size = entry.Size,
            EnableAutoClone = entry.EnableAutoClone,

            IsFailSafeEnabled = resilience.FailSafeEnabled,
            FailSafeMaxDuration = resilience.FailSafeMaxDuration,
            FailSafeThrottleDuration = resilience.FailSafeThrottleDuration,

            FactorySoftTimeout = resilience.FactorySoftTimeout,
            FactoryHardTimeout = resilience.FactoryHardTimeout,
            AllowTimedOutFactoryBackgroundCompletion = resilience.AllowTimedOutFactoryBackgroundCompletion,

            DistributedCacheSoftTimeout = resilience.DistributedSoftTimeout,
            DistributedCacheHardTimeout = resilience.DistributedHardTimeout,
            AllowBackgroundDistributedCacheOperations = resilience.AllowBackgroundDistributedOperations,
            AllowBackgroundBackplaneOperations = resilience.AllowBackgroundBackplaneOperations,

            ReThrowDistributedCacheExceptions = resilience.ThrowOnDistributedCacheErrors,
            ReThrowSerializationExceptions = resilience.ThrowOnSerializationErrors,
            ReThrowBackplaneExceptions = resilience.ThrowOnBackplaneErrors
        };

        // Redis mode: Redis is authoritative. Entry reads and writes bypass the memory layer so no
        // instance can serve a value Redis has not confirmed. The memory *locker* is untouched, so
        // in-process stampede protection still coalesces concurrent factory calls.
        if (options.Mode == CacheMode.Redis)
        {
            entryOptions.SetSkipMemoryCache(true);
        }

        // In-memory mode: there is no distributed layer to talk to.
        if (options.Mode == CacheMode.InMemory)
        {
            entryOptions.SetSkipDistributedCache(true, skipBackplaneNotifications: true);
        }

        return entryOptions;
    }

    private static MemoryCache CreateMemoryCache(CachingOptions options)
    {
        var memoryOptions = new MemoryCacheOptions();
        if (options.Entry.MemorySizeLimitMegabytes is { } limitMb)
        {
            memoryOptions.SizeLimit = limitMb * 1024L * 1024L;
        }

        return new MemoryCache(memoryOptions);
    }

    private static IFusionCacheSerializer CreateWireSerializer(CacheSerializationOptions serialization)
        => serialization.Format switch
        {
            CacheSerializerFormat.MessagePack => new FusionCacheNeueccMessagePackSerializer(),
            _ => new FusionCacheSystemTextJsonSerializer(serialization.JsonSerializerOptions)
        };

    private static void LogStartupSummary(ILogger logger, CachingOptions options)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        CacheLogMessages.StartupSummary(
            logger,
            options.CacheName,
            options.Mode.ToString(),
            OnOff(options.UsesMemoryLayer),
            OnOff(options.UsesDistributedLayer),
            OnOff(options.Backplane.Enabled),
            OnOff(options.Resilience.FailSafeEnabled),
            options.UsesDistributedLayer ? options.Serialization.Format.ToString() : "None",
            OnOff(options.UsesDistributedLayer && options.Serialization.Compression.Enabled),
            OnOff(options.Observability.EnableTracing),
            OnOff(options.Observability.EnableMetrics));
    }

    /// <summary>
    /// Warns about the one option combination whose failure mode contradicts its own setting.
    /// </summary>
    /// <remarks>
    /// With background distributed operations disabled the serializer runs on the caller's path, and
    /// the engine's foreground distributed write does not honour
    /// <c>ReThrowSerializationExceptions: false</c> — a serialization failure propagates to the
    /// caller regardless. That includes Caching.NET's own payload-size guard, so an entry over
    /// <c>MaximumPayloadBytes</c> becomes a failed request rather than an uncached value. Caching.NET
    /// cannot intercept it without wrapping every cache call, so the operator is told at startup
    /// instead of discovering it from a production stack trace.
    /// </remarks>
    private static void WarnIfSerializationFailuresReachTheCaller(ILogger logger, CachingOptions options)
    {
        if (!options.UsesDistributedLayer
            || options.Resilience.AllowBackgroundDistributedOperations
            || options.Resilience.ThrowOnSerializationErrors)
        {
            return;
        }

        CacheLogMessages.ForegroundSerializationFailuresSurface(
            logger,
            options.CacheName,
            options.Serialization.MaximumPayloadBytes);
    }

    /// <summary>
    /// Warns about the topology whose failure mode is invisible until it is running on more than one
    /// replica.
    /// </summary>
    /// <remarks>
    /// Hybrid without a backplane is a legitimate choice — a single-replica deployment, or one that
    /// deliberately accepts a bounded stale window in exchange for no pub/sub traffic — so it is not
    /// rejected. It is warned about because the failure mode is silent: every instance keeps serving
    /// its own L1 copy of a value another instance has already changed, and nothing in a
    /// single-instance test or a single-pod staging environment shows it. <c>UseHybrid(...)</c>
    /// enables the backplane by default; a cache bound from configuration does not, which is the
    /// path this catches.
    /// </remarks>
    private static void WarnIfHybridHasNoBackplane(ILogger logger, CachingOptions options)
    {
        if (options.Mode != CacheMode.Hybrid || options.Backplane.Enabled)
        {
            return;
        }

        CacheLogMessages.HybridWithoutBackplane(
            logger,
            options.CacheName,
            options.Entry.LocalExpiration ?? options.DefaultExpiration);
    }

    private static string OnOff(bool value) => value ? "Enabled" : "Disabled";
}
