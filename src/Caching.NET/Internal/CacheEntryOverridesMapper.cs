using Caching.NET.Options;
using Microsoft.Extensions.Caching.Memory;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Internal;

/// <summary>
/// Translates <see cref="CacheEntryOverrides"/> onto the engine's per-call entry options.
/// </summary>
/// <remarks>
/// The result always starts from <see cref="IFusionCache.CreateEntryOptions"/>, which duplicates the
/// cache's configured defaults including the cache-mode skip flags. Only non-null overrides are then
/// applied. This is what makes overrides additive: there is no code path that hands the engine an
/// options object built from scratch, so no call can escape the mode's guarantees.
/// </remarks>
internal static class CacheEntryOverridesMapper
{
    /// <summary>
    /// Returns <c>null</c> when no overrides were supplied, so the engine uses its configured
    /// defaults directly.
    /// </summary>
    public static FusionCacheEntryOptions? Resolve(
        CacheEntryOverrides? overrides, IFusionCache inner, JitterPolicy jitter)
        => overrides is null ? null : Apply(overrides, inner.CreateEntryOptions(), jitter);

    /// <summary>Applies non-null overrides onto an existing engine options instance, in place.</summary>
    /// <param name="overrides">The overrides to apply.</param>
    /// <param name="options">The engine options instance to mutate.</param>
    /// <param name="jitter">The cache's configured jitter policy, for recomputing a shortened entry.</param>
    public static FusionCacheEntryOptions Apply(
        CacheEntryOverrides overrides, FusionCacheEntryOptions options, JitterPolicy jitter)
    {
        if (overrides.LocalExpiration is { } localExpiration)
        {
            options.MemoryCacheDuration = localExpiration;
        }

        if (overrides.DistributedExpiration is { } distributedExpiration)
        {
            options.DistributedCacheDuration = distributedExpiration;
        }

        if (overrides.EagerRefreshThreshold is { } eagerRefresh)
        {
            options.EagerRefreshThreshold = eagerRefresh;
        }

        if (overrides.FailSafe is { } failSafe)
        {
            options.IsFailSafeEnabled = failSafe;
        }

        if (overrides.FailSafeMaxDuration is { } failSafeMax)
        {
            options.FailSafeMaxDuration = failSafeMax;
        }

        if (overrides.FailSafeThrottleDuration is { } failSafeThrottle)
        {
            options.FailSafeThrottleDuration = failSafeThrottle;
        }

        if (overrides.FactorySoftTimeout is { } factorySoft)
        {
            options.FactorySoftTimeout = factorySoft;
        }

        if (overrides.FactoryHardTimeout is { } factoryHard)
        {
            options.FactoryHardTimeout = factoryHard;
        }

        if (overrides.DistributedSoftTimeout is { } distributedSoft)
        {
            options.DistributedCacheSoftTimeout = distributedSoft;
        }

        if (overrides.DistributedHardTimeout is { } distributedHard)
        {
            options.DistributedCacheHardTimeout = distributedHard;
        }

        if (overrides.AllowBackgroundDistributedOperations is { } backgroundDistributed)
        {
            options.AllowBackgroundDistributedCacheOperations = backgroundDistributed;
        }

        if (overrides.AllowBackgroundBackplaneOperations is { } backgroundBackplane)
        {
            options.AllowBackgroundBackplaneOperations = backgroundBackplane;
        }

        if (overrides.EnableAutoClone is { } autoClone)
        {
            options.EnableAutoClone = autoClone;
        }

        if (overrides.Priority is { } priority)
        {
            options.Priority = MapPriority(priority);
        }

        if (overrides.Size is { } size)
        {
            options.Size = size;
        }

        if (overrides.SkipBackplaneNotification is { } skipBackplane)
        {
            options.SkipBackplaneNotifications = skipBackplane;
        }

        ApplyJitter(overrides, options, jitter);

        return options;
    }

    /// <summary>
    /// Sets the entry's jitter window last, once every duration override has landed.
    /// </summary>
    /// <remarks>
    /// Order matters: jitter is proportional to the entry's lifetime, so it can only be computed
    /// after <see cref="CacheEntryOverrides.LocalExpiration"/> and
    /// <see cref="CacheEntryOverrides.DistributedExpiration"/> have been applied. Without this, a call
    /// that shortened an entry to 300 ms kept the jitter window sized for the cache's configured
    /// default — the exact mismatch proportional jitter exists to remove, reintroduced one layer up.
    /// An explicit <see cref="CacheEntryOverrides.JitterMaxDuration"/> is still honoured literally:
    /// asking for a flat window for one call is a deliberate instruction, not a duration to scale.
    /// </remarks>
    private static void ApplyJitter(
        CacheEntryOverrides overrides, FusionCacheEntryOptions options, JitterPolicy jitter)
    {
        if (overrides.JitterMaxDuration is { } explicitJitter)
        {
            options.JitterMaxDuration = explicitJitter;
            return;
        }

        var policy = overrides.JitterFraction is { } fraction ? jitter.WithFraction(fraction) : jitter;
        options.JitterMaxDuration = policy.For(JitterPolicy.ShortestDuration(options));
    }

    /// <summary>Maps the Caching.NET priority onto the memory layer's own enum.</summary>
    public static CacheItemPriority MapPriority(CacheEntryPriority priority) => priority switch
    {
        CacheEntryPriority.Low => CacheItemPriority.Low,
        CacheEntryPriority.High => CacheItemPriority.High,
        CacheEntryPriority.NeverRemove => CacheItemPriority.NeverRemove,
        _ => CacheItemPriority.Normal
    };
}
