using System.Diagnostics;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Distributed;

namespace Caching.NET.Internal;

/// <summary>
/// Records distributed (Redis) layer probes as Caching.NET spans and metrics.
/// </summary>
/// <remarks>
/// This is how per-layer detail is published under Caching.NET's own names. The engine's own
/// distributed-level activity source and meter are never registered, so nothing here duplicates
/// them. Nothing is cached, retried or reordered: every member forwards to the inner cache.
/// <see cref="IDistributedCache"/> is not <see cref="IDisposable"/>, so there is no disposal
/// concern here — unlike <see cref="InstrumentedMemoryCache"/>. This class does <b>not</b> feed
/// <c>caching.net.hits</c>/<c>caching.net.misses</c>: those are logical-read counters owned
/// exclusively by <see cref="FusionCacheService"/>, and the engine probes this layer more than once
/// per logical read, which would inflate them by a call-mix-dependent factor. This decorator's own
/// per-probe hit/miss signal is <c>caching.net.layer.duration</c>'s <c>cache.result</c> dimension.
/// </remarks>
/// <remarks>
/// Every verb marks the span and records <see cref="CacheResults.Error"/> on the way out of a
/// failure, mirroring <see cref="InstrumentedBackplane"/>'s <c>Publish</c> handling. This class is
/// the sole emitter of <c>cache.redis.*</c> spans and the <c>redis</c> layer of
/// <c>caching.net.layer.duration</c>: without the catch, a distributed-cache exception (most
/// commonly a soft/hard timeout — the slowest L2 operations, and the ones most likely to throw)
/// would leave an open span with no error tag and skip the duration recording entirely, biasing the
/// remaining measurements toward the operations that happened to succeed.
/// </remarks>
/// <remarks>
/// Each verb takes the duration timestamp pair only when <see cref="_layerMetricsConfigured"/> (the
/// per-instance, constructor-time config check) and <see cref="CacheTelemetry.LayerDuration"/>'s own
/// live <c>Enabled</c> both say the measurement can possibly be recorded — the same skip
/// <see cref="InstrumentedMemoryCache"/> applies on its own hotter path.
/// <see cref="CacheTelemetryContext.RecordLayer"/> re-checks the same conditions itself, so this is
/// purely a cost skip, never a second source of truth.
/// </remarks>
internal sealed class InstrumentedDistributedCache : IDistributedCache
{
    private readonly IDistributedCache _inner;
    private readonly CacheTelemetryContext _telemetry;
    private readonly bool _layerMetricsConfigured;

    private InstrumentedDistributedCache(IDistributedCache inner, CacheTelemetryContext telemetry)
    {
        _inner = inner;
        _telemetry = telemetry;
        _layerMetricsConfigured = telemetry.MetricsEnabled && telemetry.LayerMetricsEnabled;
    }

    /// <summary>
    /// Wraps <paramref name="distributedCache"/>, or returns it unchanged when neither metrics nor
    /// tracing is enabled, so a cache with telemetry off pays nothing at all.
    /// </summary>
    public static IDistributedCache Wrap(IDistributedCache distributedCache, CacheTelemetryContext telemetry)
        => telemetry.MetricsEnabled || telemetry.TracingEnabled
            ? new InstrumentedDistributedCache(distributedCache, telemetry)
            : distributedCache;

    public byte[]? Get(string key)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.get");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            var value = _inner.Get(key);
            var result = value is null ? CacheResults.Miss : CacheResults.Hit;

            activity?.SetTag(CacheTelemetryAttributes.Result, result);

            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "get", result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            return value;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "get", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.get");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            var value = await _inner.GetAsync(key, token).ConfigureAwait(false);
            var result = value is null ? CacheResults.Miss : CacheResults.Hit;

            activity?.SetTag(CacheTelemetryAttributes.Result, result);

            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "get", result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            return value;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "get", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.set");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            _inner.Set(key, value, options);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "set", CacheResults.Set, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "set", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.set");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            await _inner.SetAsync(key, value, options, token).ConfigureAwait(false);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "set", CacheResults.Set, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "set", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    public void Refresh(string key)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.refresh");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            _inner.Refresh(key);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "refresh", CacheResults.Hit, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "refresh", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.refresh");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            await _inner.RefreshAsync(key, token).ConfigureAwait(false);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "refresh", CacheResults.Hit, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "refresh", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    public void Remove(string key)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.remove");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            _inner.Remove(key);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "remove", CacheResults.Removed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "remove", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        using var activity = _telemetry.StartLayerActivity("cache.redis.remove");
        var recordDuration = ShouldRecordDuration();
        var started = recordDuration ? Stopwatch.GetTimestamp() : default;

        try
        {
            await _inner.RemoveAsync(key, token).ConfigureAwait(false);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "remove", CacheResults.Removed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            if (recordDuration)
            {
                _telemetry.RecordLayer(CacheLayers.Redis, "remove", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    private bool ShouldRecordDuration() => _layerMetricsConfigured && CacheTelemetry.LayerDuration.Enabled;

    private static void MarkError(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(CacheTelemetryAttributes.ErrorType, ex.GetType().Name);
    }
}
