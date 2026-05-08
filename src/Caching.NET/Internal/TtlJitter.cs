namespace Caching.NET.Internal;

/// <summary>
/// Applies symmetric jitter to a TTL window: result ∈ [ttl·(1−p), ttl·(1+p)] for p ∈ [0, 0.5].
/// Used by RoutingCacheService to spread cache-expiry storms.
/// </summary>
internal static class TtlJitter
{
    public static TimeSpan Apply(TimeSpan ttl, double percentage)
    {
        if (percentage <= 0) return ttl;
        var p = Math.Min(percentage, 0.5);
        // factor ∈ [-1, +1]
        var factor = (Random.Shared.NextDouble() * 2.0) - 1.0;
        var ticks = (long)(ttl.Ticks * (1.0 + p * factor));
        if (ticks < TimeSpan.FromMilliseconds(1).Ticks)
            ticks = TimeSpan.FromMilliseconds(1).Ticks;
        return TimeSpan.FromTicks(ticks);
    }
}
