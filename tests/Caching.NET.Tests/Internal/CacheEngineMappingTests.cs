using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Caching.Memory;

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

    [Fact]
    public void KeyPrefix_IsAppliedWithTheSeparator()
    {
        var options = Options(CacheMode.InMemory);
        options.EnvironmentPrefix = "prod";

        var engine = CacheEngineFactory.MapEngineOptions(options);

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

    /// <summary>
    /// The mode's layer topology must reach the engine's tag <i>marker</i> entries, not only ordinary
    /// entries — <c>RemoveByTag</c> and <c>Clear</c> are implemented as markers that reads compare
    /// themselves against, so a marker left in a layer the mode excludes hides invalidations rather
    /// than merely costing memory. See <see cref="CacheEngineFactory.MapTagsEntryOptions"/>.
    /// </summary>
    [Fact]
    public void RedisMode_BypassesTheMemoryLayerForTagMarkersToo()
    {
        var options = Options(CacheMode.Redis);

        var markers = CacheEngineFactory.MapTagsEntryOptions(
            options, CacheEngineFactory.MapEngineOptions(options).TagsDefaultEntryOptions);

        Assert.True(markers.SkipMemoryCacheRead);
        Assert.True(markers.SkipMemoryCacheWrite);

        // The marker's durable copy is what makes an invalidation survive for an instance that was
        // offline when it happened, so the distributed side keeps the engine's long default.
        Assert.False(markers.SkipDistributedCacheRead);
        Assert.False(markers.SkipDistributedCacheWrite);
    }

    [Fact]
    public void InMemoryMode_KeepsTagMarkersOutOfTheDistributedLayerToo()
    {
        var options = Options(CacheMode.InMemory);

        var markers = CacheEngineFactory.MapTagsEntryOptions(
            options, CacheEngineFactory.MapEngineOptions(options).TagsDefaultEntryOptions);

        Assert.True(markers.SkipDistributedCacheRead);
        Assert.True(markers.SkipDistributedCacheWrite);
        Assert.True(markers.SkipBackplaneNotifications);
    }

    /// <summary>
    /// Hybrid may cache a marker in memory — the backplane evicts it — but it must not outlive the
    /// local expiration that a backplane-less deployment relies on to converge.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(30)]
    public void HybridMode_BoundsTheInProcessTagMarkerByTheLocalExpiration(int? localSeconds)
    {
        var options = Options(CacheMode.Hybrid);
        options.DefaultExpiration = TimeSpan.FromMinutes(4);
        options.Entry.LocalExpiration = localSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        var markers = CacheEngineFactory.MapTagsEntryOptions(
            options, CacheEngineFactory.MapEngineOptions(options).TagsDefaultEntryOptions);

        var expected = options.Entry.LocalExpiration ?? options.DefaultExpiration;
        Assert.Equal(expected, markers.MemoryCacheDuration);

        // Ten days by default — left alone on purpose, so the invalidation itself is durable.
        Assert.True(markers.Duration >= TimeSpan.FromDays(1));
        Assert.False(markers.SkipMemoryCacheRead);
    }

    [Fact]
    public void MapEngineOptions_AppliesTheModeToTagMarkers()
    {
        // Guards the wiring, not just the mapper: MapTagsEntryOptions is only correct if
        // MapEngineOptions actually calls it.
        Assert.True(CacheEngineFactory.MapEngineOptions(Options(CacheMode.Redis))
            .TagsDefaultEntryOptions.SkipMemoryCacheRead);

        Assert.True(CacheEngineFactory.MapEngineOptions(Options(CacheMode.InMemory))
            .TagsDefaultEntryOptions.SkipDistributedCacheRead);

        var hybrid = Options(CacheMode.Hybrid);
        hybrid.Entry.LocalExpiration = TimeSpan.FromSeconds(45);
        Assert.Equal(
            TimeSpan.FromSeconds(45),
            CacheEngineFactory.MapEngineOptions(hybrid).TagsDefaultEntryOptions.MemoryCacheDuration);
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

    // Caching.NET's own CacheEntryPriority replaced the BCL CacheItemPriority on the public surface,
    // so every value now passes through a hand-written mapping. A wrong arm compiles and runs, which
    // is exactly the case CLAUDE.md's "adding a knob" rule asks a mapping assertion to catch.
    [Theory]
    [InlineData(CacheEntryPriority.Low, CacheItemPriority.Low)]
    [InlineData(CacheEntryPriority.Normal, CacheItemPriority.Normal)]
    [InlineData(CacheEntryPriority.High, CacheItemPriority.High)]
    [InlineData(CacheEntryPriority.NeverRemove, CacheItemPriority.NeverRemove)]
    public void Priority_MapsOntoEntryOptions(CacheEntryPriority configured, CacheItemPriority expected)
    {
        var options = Options(CacheMode.InMemory);
        options.Entry.Priority = configured;

        var entry = CacheEngineFactory.MapEntryOptions(options);

        Assert.Equal(expected, entry.Priority);
    }

    /// <summary>
    /// The shipped default, asserted on both sides of the mapping. It lived in
    /// <c>CachingOptionsValidatorTests</c>, which is for validation rules; a default that every
    /// unconfigured entry inherits belongs next to the mapping it feeds. Asserting the mapped value
    /// too means a default changed without the mapping being revisited cannot pass silently.
    /// </summary>
    [Fact]
    public void Priority_DefaultsToNormalAndMapsThrough()
    {
        var options = Options(CacheMode.InMemory);

        Assert.Equal(CacheEntryPriority.Normal, options.Entry.Priority);
        Assert.Equal(CacheItemPriority.Normal, CacheEngineFactory.MapEntryOptions(options).Priority);
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

        var engine = CacheEngineFactory.MapEngineOptions(options);

        Assert.False(engine.IncludeTagsInLogs);
        Assert.False(engine.IncludeTagsInTraces);
        Assert.False(engine.IncludeTagsInMetrics);
    }

    [Fact]
    public void TagTelemetry_CanBeOptedIn()
    {
        var options = Options(CacheMode.Hybrid);
        options.Security.AllowTagsInTelemetry = true;

        var engine = CacheEngineFactory.MapEngineOptions(options);

        Assert.True(engine.IncludeTagsInMetrics);
    }

    [Fact]
    public void BackplaneChannel_DefaultsToTheKeyPrefixSoApplicationsDoNotCrossTalk()
    {
        var options = Options(CacheMode.Hybrid);
        options.EnvironmentPrefix = "prod";

        var engine = CacheEngineFactory.MapEngineOptions(options);

        Assert.Equal("orders-api:prod", engine.BackplaneChannelPrefix);
    }

    [Fact]
    public void ExplicitBackplaneChannel_Wins()
    {
        var options = Options(CacheMode.Hybrid);
        options.Backplane.ChannelPrefix = "custom-channel";

        var engine = CacheEngineFactory.MapEngineOptions(options);

        Assert.Equal("custom-channel", engine.BackplaneChannelPrefix);
    }

    [Fact]
    public void AutoRecoverySettings_MapOntoEngineOptions()
    {
        var options = Options(CacheMode.Hybrid);
        options.Resilience.AutoRecoveryEnabled = true;
        options.Resilience.AutoRecoveryMaxItems = 42;
        options.Resilience.AutoRecoveryMaxRetryCount = 7;
        options.Resilience.AutoRecoveryDelay = TimeSpan.FromSeconds(9);

        var engine = CacheEngineFactory.MapEngineOptions(options);

        Assert.True(engine.EnableAutoRecovery);
        Assert.Equal(42, engine.AutoRecoveryMaxItems);
        Assert.Equal(7, engine.AutoRecoveryMaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(9), engine.AutoRecoveryDelay);
    }

    /// <summary>
    /// The configured limit reaches <see cref="MemoryCacheOptions.SizeLimit"/> unscaled. It used to be
    /// multiplied by 1024 × 1024 under the name <c>MemorySizeLimitMegabytes</c>, on the assumption
    /// that <c>SizeLimit</c> was a byte budget. It is not: it is a ceiling on the summed <c>Size</c>
    /// the entries declare, and the default per-entry size is 1 — so a "1 MB" cap was really a
    /// 1 048 576-entry cap and 200 entries of ~400 KB each (about 78 MB) all stayed resident under it.
    /// Any multiplication here invents a unit the memory layer does not use.
    /// </summary>
    [Fact]
    public void MemorySizeLimit_ReachesTheMemoryLayerUnscaled()
    {
        var options = Options(CacheMode.InMemory);
        options.Entry.MemorySizeLimit = 256;
        options.Entry.Size = 1;

        var memory = CacheEngineFactory.MapMemoryCacheOptions(options);

        Assert.Equal(256L, memory.SizeLimit);
    }

    [Fact]
    public void NoMemorySizeLimit_LeavesTheMemoryLayerUnbounded()
    {
        var memory = CacheEngineFactory.MapMemoryCacheOptions(Options(CacheMode.InMemory));

        Assert.Null(memory.SizeLimit);
    }

    [Fact]
    public void CircuitBreakerDurations_MapOntoEngineOptions()
    {
        var options = Options(CacheMode.Hybrid);
        options.Resilience.DistributedCircuitBreakerDuration = TimeSpan.FromSeconds(11);
        options.Resilience.BackplaneCircuitBreakerDuration = TimeSpan.FromSeconds(13);

        var engine = CacheEngineFactory.MapEngineOptions(options);

        Assert.Equal(TimeSpan.FromSeconds(11), engine.DistributedCacheCircuitBreakerDuration);
        Assert.Equal(TimeSpan.FromSeconds(13), engine.BackplaneCircuitBreakerDuration);
    }
}
