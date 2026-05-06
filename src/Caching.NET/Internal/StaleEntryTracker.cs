using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<string, StaleMetadata> _entries = new();

    public void Register(string prefixedKey, TimeSpan absoluteExpiration, TimeSpan staleFor)
    {
        var now = DateTime.UtcNow.Ticks;
        var meta = new StaleMetadata(
            AbsExpiresAtUtcTicks: now + absoluteExpiration.Ticks,
            StaleUntilUtcTicks: now + absoluteExpiration.Ticks + staleFor.Ticks);
        _entries[prefixedKey] = meta;
    }

    public bool TryGet(string prefixedKey, out StaleMetadata meta) =>
        _entries.TryGetValue(prefixedKey, out meta);

    public void Forget(string prefixedKey) => _entries.TryRemove(prefixedKey, out _);
}
