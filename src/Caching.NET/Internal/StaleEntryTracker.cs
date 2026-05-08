using System.Collections.Concurrent;
using System.Threading;

namespace Caching.NET.Internal;

internal readonly record struct StaleMetadata(long AbsExpiresAtUtcTicks, long StaleUntilUtcTicks);

/// <summary>
/// In-process registry of stale-while-revalidate metadata. NOT distributed:
/// each app instance maintains its own view, which is acceptable for v2.0
/// because background refresh is per-instance work. A distributed registry
/// is deferred beyond v2.0.0.
/// </summary>
internal sealed class StaleEntryTracker
{
    private const int SweepEveryNRegistrations = 256;
    private const int HardEntryLimit = 50_000;
    private const int TargetEntryCountAfterTrim = 40_000;

    private readonly ConcurrentDictionary<string, StaleMetadata> _entries = new();
    private int _registerCount;

    public void Register(string prefixedKey, TimeSpan absoluteExpiration, TimeSpan staleFor)
    {
        var now = DateTime.UtcNow.Ticks;
        var meta = new StaleMetadata(
            AbsExpiresAtUtcTicks: now + absoluteExpiration.Ticks,
            StaleUntilUtcTicks: now + absoluteExpiration.Ticks + staleFor.Ticks);
        _entries[prefixedKey] = meta;

        var registrations = Interlocked.Increment(ref _registerCount);
        if ((registrations % SweepEveryNRegistrations) == 0 || _entries.Count >= HardEntryLimit)
            Prune(now);
    }

    public bool TryGet(string prefixedKey, out StaleMetadata meta) =>
        _entries.TryGetValue(prefixedKey, out meta);

    public void Forget(string prefixedKey) => _entries.TryRemove(prefixedKey, out _);

    private void Prune(long nowTicks)
    {
        foreach (var entry in _entries)
        {
            if (entry.Value.StaleUntilUtcTicks <= nowTicks)
                _entries.TryRemove(entry.Key, out _);
        }

        var overflow = _entries.Count - HardEntryLimit;
        if (overflow <= 0) return;

        var trimTarget = Math.Max(overflow, _entries.Count - TargetEntryCountAfterTrim);
        var trimmed = 0;
        foreach (var entry in _entries)
        {
            if (_entries.TryRemove(entry.Key, out _))
                trimmed++;
            if (trimmed >= trimTarget)
                break;
        }
    }
}
