using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// Verifies that Caching.NET configuration maps onto the engine exactly as documented. These are
/// the assertions that keep the feature matrix honest.
/// </summary>
public class CacheEngineMappingTests
{
    private static CachingOptions Options(CacheMode mode)
        => new()
        {
            CacheName = "default",
            ApplicationPrefix = "orders-api",
            Mode = mode,
            Redis = { Configuration = mode == CacheMode.InMemory ? null : "localhost:6379" }
        };

    private static CacheGuard Guard(CachingOptions options)
        => new(options, new CacheTelemetryContext(options), NullLogger.Instance);

    [Fact]
    public void KeyPrefix_IsAppliedWithTheSeparator()
    {
        var options = Options(CacheMode.InMemory);
        options.EnvironmentPrefix = "prod";

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.Equal("orders-api:prod:", engine.CacheKeyPrefix);
    }

    [Fact]
    public void InMemoryMode_SkipsTheDistributedLayerEntirely()
    {
        var options = Options(CacheMode.InMemory);

        var entry = CacheEngineFactory.MapEntryOptions(options);

        Assert.True(entry.SkipDistributedCacheRead);
        Assert.True(entry.SkipDistributedCacheWrite);
        Assert.True(entry.SkipBackplaneNotifications);
        Assert.False(entry.SkipMemoryCacheRead);
        Assert.False(entry.SkipMemoryCacheWrite);
    }

    [Fact]
    public void RedisMode_BypassesTheMemoryLayerSoRedisStaysAuthoritative()
    {
        var options = Options(CacheMode.Redis);

        var entry = CacheEngineFactory.MapEntryOptions(options);

        Assert.True(entry.SkipMemoryCacheRead);
        Assert.True(entry.SkipMemoryCacheWrite);
        Assert.False(entry.SkipDistributedCacheRead);
        Assert.False(entry.SkipDistributedCacheWrite);
    }

    [Fact]
    public void HybridMode_UsesBothLayers()
    {
        var options = Options(CacheMode.Hybrid);

        var entry = CacheEngineFactory.MapEntryOptions(options);

        Assert.False(entry.SkipMemoryCacheRead);
        Assert.False(entry.SkipMemoryCacheWrite);
        Assert.False(entry.SkipDistributedCacheRead);
        Assert.False(entry.SkipDistributedCacheWrite);
    }

    [Fact]
    public void ExpirationSettings_MapOntoEntryOptions()
    {
        var options = Options(CacheMode.Hybrid);
        options.DefaultExpiration = TimeSpan.FromMinutes(7);
        options.Entry.DistributedExpiration = TimeSpan.FromHours(1);
        options.Entry.LocalExpiration = TimeSpan.FromSeconds(45);
        options.Entry.EagerRefreshThreshold = 0.8f;
        options.Entry.JitterMaxDuration = TimeSpan.FromSeconds(3);

        var entry = CacheEngineFactory.MapEntryOptions(options);

        Assert.Equal(TimeSpan.FromMinutes(7), entry.Duration);
        Assert.Equal(TimeSpan.FromHours(1), entry.DistributedCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(45), entry.MemoryCacheDuration);
        Assert.Equal(0.8f, entry.EagerRefreshThreshold);
        Assert.Equal(TimeSpan.FromSeconds(3), entry.JitterMaxDuration);
    }

    [Fact]
    public void ResilienceSettings_MapOntoEntryOptions()
    {
        var options = Options(CacheMode.Hybrid);
        options.Resilience.FailSafeEnabled = true;
        options.Resilience.FailSafeMaxDuration = TimeSpan.FromHours(4);
        options.Resilience.FailSafeThrottleDuration = TimeSpan.FromSeconds(15);
        options.Resilience.FactorySoftTimeout = TimeSpan.FromSeconds(1);
        options.Resilience.FactoryHardTimeout = TimeSpan.FromSeconds(9);
        options.Resilience.DistributedSoftTimeout = TimeSpan.FromMilliseconds(250);
        options.Resilience.DistributedHardTimeout = TimeSpan.FromSeconds(3);

        var entry = CacheEngineFactory.MapEntryOptions(options);

        Assert.True(entry.IsFailSafeEnabled);
        Assert.Equal(TimeSpan.FromHours(4), entry.FailSafeMaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), entry.FailSafeThrottleDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), entry.FactorySoftTimeout);
        Assert.Equal(TimeSpan.FromSeconds(9), entry.FactoryHardTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), entry.DistributedCacheSoftTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), entry.DistributedCacheHardTimeout);
    }

    [Fact]
    public void TagsAreKeptOutOfTelemetryUnlessOptedIn()
    {
        var options = Options(CacheMode.Hybrid);

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.False(engine.IncludeTagsInLogs);
        Assert.False(engine.IncludeTagsInTraces);
        Assert.False(engine.IncludeTagsInMetrics);
    }

    [Fact]
    public void TagTelemetry_CanBeOptedIn()
    {
        var options = Options(CacheMode.Hybrid);
        options.Security.AllowTagsInTelemetry = true;

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.True(engine.IncludeTagsInMetrics);
    }

    [Fact]
    public void BackplaneChannel_DefaultsToTheKeyPrefixSoApplicationsDoNotCrossTalk()
    {
        var options = Options(CacheMode.Hybrid);
        options.EnvironmentPrefix = "prod";

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.Equal("orders-api:prod", engine.BackplaneChannelPrefix);
    }

    [Fact]
    public void ExplicitBackplaneChannel_Wins()
    {
        var options = Options(CacheMode.Hybrid);
        options.Backplane.ChannelPrefix = "custom-channel";

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.Equal("custom-channel", engine.BackplaneChannelPrefix);
    }

    [Fact]
    public void KeyGuard_IsInstalledAsTheEngineEntryOptionsProvider()
    {
        var options = Options(CacheMode.InMemory);

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.IsType<KeyGuardEntryOptionsProvider>(engine.DefaultEntryOptionsProvider);
    }

    [Fact]
    public void AutoRecoverySettings_MapOntoEngineOptions()
    {
        var options = Options(CacheMode.Hybrid);
        options.Resilience.AutoRecoveryEnabled = true;
        options.Resilience.AutoRecoveryMaxItems = 42;
        options.Resilience.AutoRecoveryMaxRetryCount = 7;
        options.Resilience.AutoRecoveryDelay = TimeSpan.FromSeconds(9);

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.True(engine.EnableAutoRecovery);
        Assert.Equal(42, engine.AutoRecoveryMaxItems);
        Assert.Equal(7, engine.AutoRecoveryMaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(9), engine.AutoRecoveryDelay);
    }

    [Fact]
    public void CircuitBreakerDurations_MapOntoEngineOptions()
    {
        var options = Options(CacheMode.Hybrid);
        options.Resilience.DistributedCircuitBreakerDuration = TimeSpan.FromSeconds(11);
        options.Resilience.BackplaneCircuitBreakerDuration = TimeSpan.FromSeconds(13);

        var engine = CacheEngineFactory.MapEngineOptions(options, Guard(options));

        Assert.Equal(TimeSpan.FromSeconds(11), engine.DistributedCacheCircuitBreakerDuration);
        Assert.Equal(TimeSpan.FromSeconds(13), engine.BackplaneCircuitBreakerDuration);
    }
}
