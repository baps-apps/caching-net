using System.Diagnostics;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Memory;

namespace Caching.NET.Internal;

/// <summary>
/// Records in-process memory layer probes as Caching.NET spans and metrics.
/// </summary>
/// <remarks>
/// This is how per-layer detail is published under Caching.NET's own names. The engine's own
/// memory-level activity source and meter are never registered, so nothing here duplicates them.
/// Nothing is cached, evicted or reordered: every member forwards to the inner cache. This class does
/// <b>not</b> feed <c>caching.net.hits</c>/<c>caching.net.misses</c>: those are logical-read counters
/// owned exclusively by <see cref="FusionCacheService"/>, and the engine probes this layer more than
/// once per logical read (tag/clear-marker lookups, lock double-checks), which would inflate them by
/// a call-mix-dependent factor. This decorator's own per-probe hit/miss signal is
/// <c>caching.net.layer.duration</c>'s <c>cache.result</c> dimension instead.
/// </remarks>
/// <remarks>
/// This is on the hottest path in the library — every FusionCache operation probes L1 several times
/// — so the duration timestamp pair is taken only when it can possibly be recorded:
/// <see cref="_layerMetricsConfigured"/> is the per-instance, constructor-time answer to "is layer
/// metrics even configured on for this cache", and <see cref="CacheTelemetry.LayerDuration"/>'s own
/// <c>Enabled</c> is the live, per-call answer to "is anything actually listening right now". Both
/// must be true before <see cref="Stopwatch.GetTimestamp"/> runs at all. <see cref="CacheTelemetryContext.RecordLayer"/>
/// re-checks the same three conditions itself, so this is purely a cost skip, never a second source
/// of truth for whether a measurement is recorded.
/// </remarks>
internal sealed class InstrumentedMemoryCache : IMemoryCache
{
    private readonly IMemoryCache _inner;
    private readonly CacheTelemetryContext _telemetry;

    // Config-level answer, fixed for the cache's lifetime: cheaper to hoist once than to re-read
    // two properties off _telemetry on every probe.
    private readonly bool _layerMetricsConfigured;

    private InstrumentedMemoryCache(IMemoryCache inner, CacheTelemetryContext telemetry)
    {
        _inner = inner;
        _telemetry = telemetry;
        _layerMetricsConfigured = telemetry.MetricsEnabled && telemetry.LayerMetricsEnabled;
    }

    /// <summary>
    /// Wraps <paramref name="memoryCache"/>, or returns it unchanged when neither metrics nor
    /// tracing is enabled, so a cache with telemetry off pays nothing at all.
    /// </summary>
    public static IMemoryCache Wrap(IMemoryCache memoryCache, CacheTelemetryContext telemetry)
        => telemetry.MetricsEnabled || telemetry.TracingEnabled
            ? new InstrumentedMemoryCache(memoryCache, telemetry)
            : memoryCache;

    public ICacheEntry CreateEntry(object key)
    {
        using var activity = _telemetry.StartActivity("cache.memory.set");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        var entry = _inner.CreateEntry(key);

        if (recordDuration)
        {
            _telemetry.RecordLayer(CacheLayers.Memory, "set", CacheResults.Set, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return entry;
    }

    public bool TryGetValue(object key, out object? value)
    {
        using var activity = _telemetry.StartActivity("cache.memory.get");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        var found = _inner.TryGetValue(key, out value);
        var result = found ? CacheResults.Hit : CacheResults.Miss;

        activity?.SetTag(CacheTelemetryAttributes.Result, result);

        if (recordDuration)
        {
            _telemetry.RecordLayer(CacheLayers.Memory, "get", result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return found;
    }

    public void Remove(object key)
    {
        using var activity = _telemetry.StartActivity("cache.memory.remove");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        _inner.Remove(key);

        if (recordDuration)
        {
            _telemetry.RecordLayer(CacheLayers.Memory, "remove", CacheResults.Removed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>Forwards directly: statistics are a point-in-time snapshot, not a per-call probe.</summary>
    public MemoryCacheStatistics? GetCurrentStatistics() => _inner.GetCurrentStatistics();

    // Ownership is unchanged: CacheInstance disposes the inner MemoryCache directly, so this
    // decorator must not dispose it a second time.
    public void Dispose()
    {
    }

    private bool ShouldRecordDuration() => _layerMetricsConfigured && CacheTelemetry.LayerDuration.Enabled;
}
