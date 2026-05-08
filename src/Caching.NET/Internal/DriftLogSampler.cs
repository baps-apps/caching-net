using System.Collections.Concurrent;

namespace Caching.NET.Internal;

/// <summary>
/// Reduces high-volume schema/envelope drift log noise: at most one log per
/// (drift kind, key fingerprint) per sampling window.
/// </summary>
internal static class DriftLogSampler
{
    private const int MaxEntries = 4096;
    private static readonly ConcurrentDictionary<string, long> s_lastLogTicks = new();
    private static readonly long s_intervalMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    /// <summary>Returns true if a log line should be emitted for this drift event.</summary>
    public static bool ShouldLog(string driftKind, string cacheKey)
    {
        var fp = $"{driftKind}\0{StableStringHash.Compute64(cacheKey):x16}";
        var now = Environment.TickCount64;
        while (true)
        {
            if (s_lastLogTicks.TryGetValue(fp, out var prev))
            {
                if (now - prev < s_intervalMs)
                    return false;
                if (s_lastLogTicks.TryUpdate(fp, now, prev))
                    return true;
                continue;
            }

            if (s_lastLogTicks.TryAdd(fp, now))
            {
                TrimIfNeeded(now);
                return true;
            }
        }
    }

    private static void TrimIfNeeded(long now)
    {
        if (s_lastLogTicks.Count <= MaxEntries)
            return;

        var cutoff = now - (2 * s_intervalMs);
        foreach (var kvp in s_lastLogTicks)
        {
            if (kvp.Value <= cutoff)
                s_lastLogTicks.TryRemove(kvp.Key, out _);

            if (s_lastLogTicks.Count <= MaxEntries)
                return;
        }
    }
}
