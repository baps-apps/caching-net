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
