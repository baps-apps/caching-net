using Caching.NET.Internal;
using Caching.NET.Options;

namespace Caching.NET.Tests.Internal;

public class CacheEntryOverridesMapperTests
{
    [Fact]
    public void NullOverrides_ResolveToNull()
    {
        using var host = TestHost.BuildInMemory();
        var inner = host.EngineCache();

        Assert.Null(CacheEntryOverridesMapper.Resolve(null, inner, host.JitterPolicy()));
    }

    [Fact]
    public void EmptyOverrides_PreserveEveryDefault()
    {
        using var host = TestHost.BuildInMemory(c => c.WithDefaultExpiration(TimeSpan.FromMinutes(7)));
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(new CacheEntryOverrides(), inner, host.JitterPolicy());

        Assert.NotNull(resolved);
        Assert.Equal(TimeSpan.FromMinutes(7), resolved!.Duration);
    }

    [Fact]
    public void NullMarkerOverrides_ResolveToNull()
        => Assert.Null(CacheEntryOverridesMapper.ResolveMarker(null, MarkerDefaults(CacheMode.Hybrid)));

    /// <summary>
    /// An empty override object on <c>RemoveByTag</c> or <c>Clear</c> must leave the marker exactly as
    /// <c>null</c> would — the marker defaults, mode mapping included — not the entry defaults.
    /// </summary>
    [Theory]
    [InlineData(CacheMode.InMemory)]
    [InlineData(CacheMode.Redis)]
    [InlineData(CacheMode.Hybrid)]
    public void EmptyMarkerOverrides_KeepTheMarkerDefaults(CacheMode mode)
    {
        var defaults = MarkerDefaults(mode);

        var resolved = CacheEntryOverridesMapper.ResolveMarker(new CacheEntryOverrides(), defaults);

        Assert.NotNull(resolved);
        Assert.NotSame(defaults, resolved);
        AssertSameLifetimeAndPlacement(defaults, resolved!);
    }

    [Fact]
    public void MarkerOverrides_ApplyTheOperationalSettings()
    {
        var resolved = CacheEntryOverridesMapper.ResolveMarker(
            new CacheEntryOverrides
            {
                AllowBackgroundDistributedOperations = true,
                AllowBackgroundBackplaneOperations = true,
                SkipBackplaneNotification = true,
                DistributedSoftTimeout = TimeSpan.FromMilliseconds(56),
                DistributedHardTimeout = TimeSpan.FromMilliseconds(78)
            },
            MarkerDefaults(CacheMode.Hybrid));

        Assert.True(resolved!.AllowBackgroundDistributedCacheOperations);
        Assert.True(resolved.AllowBackgroundBackplaneOperations);
        Assert.True(resolved.SkipBackplaneNotifications);
        Assert.Equal(TimeSpan.FromMilliseconds(56), resolved.DistributedCacheSoftTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(78), resolved.DistributedCacheHardTimeout);
    }

    /// <summary>
    /// The marker's lifetime is what keeps an invalidation effective, so no per-call setting may
    /// shorten it — or move it between layers.
    /// </summary>
    [Fact]
    public void MarkerOverrides_IgnoreLifetimeSettings()
    {
        var defaults = MarkerDefaults(CacheMode.Hybrid);

        var resolved = CacheEntryOverridesMapper.ResolveMarker(
            new CacheEntryOverrides
            {
                LocalExpiration = TimeSpan.FromSeconds(1),
                DistributedExpiration = TimeSpan.FromSeconds(1),
                JitterMaxDuration = TimeSpan.FromSeconds(3),
                JitterFraction = 0.5,
                EagerRefreshThreshold = 0.5f,
                FailSafe = false,
                FailSafeMaxDuration = TimeSpan.FromSeconds(1),
                FailSafeThrottleDuration = TimeSpan.FromSeconds(1),
                FactorySoftTimeout = TimeSpan.FromMilliseconds(1),
                FactoryHardTimeout = TimeSpan.FromMilliseconds(2),
                EnableAutoClone = true,
                Priority = CacheEntryPriority.Low,
                Size = 99
            },
            defaults);

        AssertSameLifetimeAndPlacement(defaults, resolved!);
        Assert.Equal(defaults.JitterMaxDuration, resolved!.JitterMaxDuration);
        Assert.Equal(defaults.EagerRefreshThreshold, resolved.EagerRefreshThreshold);
        Assert.Equal(defaults.FailSafeThrottleDuration, resolved.FailSafeThrottleDuration);
        Assert.Equal(defaults.FactorySoftTimeout, resolved.FactorySoftTimeout);
        Assert.Equal(defaults.FactoryHardTimeout, resolved.FactoryHardTimeout);
        Assert.Equal(defaults.EnableAutoClone, resolved.EnableAutoClone);
        Assert.Equal(defaults.Priority, resolved.Priority);
        Assert.Equal(defaults.Size, resolved.Size);
    }

    /// <summary>
    /// A marker ignores an override silently unless <c>IgnoredMarkerOverrides</c> names it, so a new
    /// <see cref="CacheEntryOverrides"/> property must land in exactly one place: applied by
    /// <c>ResolveMarker</c>, or reported as ignored.
    /// </summary>
    [Fact]
    public void MarkerOverrides_EveryPropertyIsAppliedOrReportedIgnored()
    {
        string[] applied =
        [
            nameof(CacheEntryOverrides.AllowBackgroundDistributedOperations),
            nameof(CacheEntryOverrides.AllowBackgroundBackplaneOperations),
            nameof(CacheEntryOverrides.SkipBackplaneNotification),
            nameof(CacheEntryOverrides.DistributedSoftTimeout),
            nameof(CacheEntryOverrides.DistributedHardTimeout)
        ];

        foreach (var property in typeof(CacheEntryOverrides).GetProperties())
        {
            var overrides = new CacheEntryOverrides();
            property.SetValue(overrides, SampleValue(property.PropertyType));

            var ignored = CacheEntryOverridesMapper.IgnoredMarkerOverrides(overrides);

            if (applied.Contains(property.Name))
            {
                Assert.Equal(string.Empty, ignored);
            }
            else
            {
                Assert.Equal(property.Name, ignored);
            }
        }
    }

    private static object SampleValue(Type type) => Nullable.GetUnderlyingType(type) switch
    {
        { } t when t == typeof(TimeSpan) => TimeSpan.FromSeconds(1),
        { } t when t == typeof(bool) => true,
        { } t when t == typeof(double) => 0.5d,
        { } t when t == typeof(float) => 0.5f,
        { } t when t == typeof(long) => 1L,
        { } t when t == typeof(CacheEntryPriority) => CacheEntryPriority.High,
        _ => throw new InvalidOperationException($"No sample value for {type}; extend SampleValue.")
    };

    private static ZiggyCreatures.Caching.Fusion.FusionCacheEntryOptions MarkerDefaults(CacheMode mode)
        => CacheEngineFactory.MapEngineOptions(new CachingOptions
        {
            CacheName = "default",
            ApplicationPrefix = "tests",
            Mode = mode,
            Redis = { Configuration = mode == CacheMode.InMemory ? null : "localhost:6379" }
        }).TagsDefaultEntryOptions;

    private static void AssertSameLifetimeAndPlacement(
        ZiggyCreatures.Caching.Fusion.FusionCacheEntryOptions expected,
        ZiggyCreatures.Caching.Fusion.FusionCacheEntryOptions actual)
    {
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.MemoryCacheDuration, actual.MemoryCacheDuration);
        Assert.Equal(expected.DistributedCacheDuration, actual.DistributedCacheDuration);
        Assert.Equal(expected.IsFailSafeEnabled, actual.IsFailSafeEnabled);
        Assert.Equal(expected.FailSafeMaxDuration, actual.FailSafeMaxDuration);
        Assert.Equal(expected.DistributedCacheFailSafeMaxDuration, actual.DistributedCacheFailSafeMaxDuration);
        Assert.Equal(expected.SkipMemoryCacheRead, actual.SkipMemoryCacheRead);
        Assert.Equal(expected.SkipMemoryCacheWrite, actual.SkipMemoryCacheWrite);
        Assert.Equal(expected.SkipDistributedCacheRead, actual.SkipDistributedCacheRead);
        Assert.Equal(expected.SkipDistributedCacheWrite, actual.SkipDistributedCacheWrite);
    }

    [Fact]
    public void InMemoryMode_PreservesSkipDistributedCache_WhenOverridesSupplied()
    {
        using var host = TestHost.BuildInMemory();
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides { DistributedExpiration = TimeSpan.FromMinutes(1) },
            inner,
            host.JitterPolicy());

        Assert.True(resolved!.SkipDistributedCacheRead);
        Assert.True(resolved.SkipDistributedCacheWrite);
    }

    [Fact]
    public void RedisMode_PreservesSkipMemoryCache_WhenOverridesSupplied()
    {
        using var host = TestHost.Build(c => c
            .UseRedis("localhost:6379,abortConnect=false")
            .WithApplicationPrefix("tests"));
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides { LocalExpiration = TimeSpan.FromMinutes(1) },
            inner,
            host.JitterPolicy());

        Assert.True(resolved!.SkipMemoryCacheRead);
        Assert.True(resolved.SkipMemoryCacheWrite);
    }

    [Fact]
    public void EveryOverride_IsApplied()
    {
        using var host = TestHost.BuildInMemory();
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides
            {
                LocalExpiration = TimeSpan.FromSeconds(11),
                DistributedExpiration = TimeSpan.FromSeconds(22),
                JitterMaxDuration = TimeSpan.FromSeconds(3),
                EagerRefreshThreshold = 0.75f,
                FailSafe = true,
                FailSafeMaxDuration = TimeSpan.FromMinutes(5),
                FailSafeThrottleDuration = TimeSpan.FromSeconds(9),
                FactorySoftTimeout = TimeSpan.FromMilliseconds(120),
                FactoryHardTimeout = TimeSpan.FromMilliseconds(340),
                DistributedSoftTimeout = TimeSpan.FromMilliseconds(56),
                DistributedHardTimeout = TimeSpan.FromMilliseconds(78),
                AllowBackgroundDistributedOperations = false,
                AllowBackgroundBackplaneOperations = false,
                EnableAutoClone = true,
                Priority = CacheEntryPriority.NeverRemove,
                Size = 42,
                SkipBackplaneNotification = true
            },
            inner,
            host.JitterPolicy());

        Assert.NotNull(resolved);
        Assert.Equal(TimeSpan.FromSeconds(11), resolved!.MemoryCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(22), resolved.DistributedCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), resolved.JitterMaxDuration);
        Assert.Equal(0.75f, resolved.EagerRefreshThreshold);
        Assert.True(resolved.IsFailSafeEnabled);
        Assert.Equal(TimeSpan.FromMinutes(5), resolved.FailSafeMaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(9), resolved.FailSafeThrottleDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(120), resolved.FactorySoftTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(340), resolved.FactoryHardTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(56), resolved.DistributedCacheSoftTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(78), resolved.DistributedCacheHardTimeout);
        Assert.False(resolved.AllowBackgroundDistributedCacheOperations);
        Assert.False(resolved.AllowBackgroundBackplaneOperations);
        Assert.True(resolved.EnableAutoClone);
        Assert.Equal(Microsoft.Extensions.Caching.Memory.CacheItemPriority.NeverRemove, resolved.Priority);
        Assert.Equal(42, resolved.Size);
        Assert.True(resolved.SkipBackplaneNotifications);
    }
}
