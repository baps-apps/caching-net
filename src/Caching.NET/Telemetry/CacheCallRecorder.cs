using System.Diagnostics;
using Caching.NET.Internal;
using Caching.NET.Options;

namespace Caching.NET.Telemetry;

/// <summary>
/// One telemetry record for one call into <see cref="Services.RoutingCacheService"/>: starts a span,
/// times the call end to end, times the caller's factory when one runs, and on dispose emits
/// <c>cache.operation.duration</c> plus (when a factory ran) <c>cache.factory.duration</c>.
/// <para>
/// Not thread-safe by design: one instance belongs to one logical call, whose factory invocations are
/// sequential. Concurrent calls each get their own recorder, so no ambient state is involved.
/// </para>
/// </summary>
internal sealed class CacheCallRecorder : IDisposable
{
    internal const string ServedFromCache = "cache";
    internal const string ServedFromSource = "source";
    internal const string ServedFromMixed = "mixed";
    internal const string ServedFromNone = "none";

    private readonly string _operation;
    private readonly bool _readShaped;
    private readonly long _startTimestamp;
    private readonly Activity? _activity;

    private string _mode;
    private long _factoryTicks;
    private bool _factoryRan;
    private bool _servedFromCache;
    private bool _batch;
    private int _hits;
    private int _misses;
    private bool _coalesced;
    private string? _missReason;
    private string? _errorKind;
    private bool _errorThrownToCaller;
    private bool _factoryFailed;
    private Exception? _factoryException;
    private bool _disposed;

    private CacheCallRecorder(string mode, string operation, Activity? activity)
    {
        _mode = mode;
        _operation = operation;
        _readShaped = IsReadShaped(operation);
        _activity = activity;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Starts a record, or returns <c>null</c> when nothing is listening — no <see cref="ActivityListener"/>
    /// on the source and no <see cref="System.Diagnostics.Metrics.MeterListener"/> on the duration
    /// histograms. A null return means the caller allocates nothing at all for telemetry, which keeps
    /// the hot read path allocation-free for consumers who never wired an OTel pipeline.
    /// <paramref name="rawKey"/> is used only to derive a hashed key tag when
    /// <see cref="CacheOptions.IncludeKeyHashInTraces"/> is set; the raw value never reaches the span.
    /// <paramref name="link"/> attaches a causal link instead of a parent — used by background work that
    /// outlives the call that triggered it.
    /// </summary>
    public static CacheCallRecorder? Start(
        string mode, CacheOptions options, string operation, string? rawKey = null, ActivityContext? link = null)
    {
        var activity = link is { } linked
            // A root span carrying a link: the background refresh outlives the request that scheduled
            // it, so parenting it there would attach a long-running child to an already-ended span.
            ? CacheInstruments.Activity.StartActivity(
                SpanName(operation), ActivityKind.Internal, default(ActivityContext),
                links: new[] { new ActivityLink(linked) })
            : CacheInstruments.Activity.StartActivity(SpanName(operation), ActivityKind.Internal);

        if (activity is null && !CacheInstruments.OperationDuration.Enabled && !CacheInstruments.FactoryDuration.Enabled)
            return null;

        if (activity is not null && options.IncludeKeyHashInTraces && !string.IsNullOrEmpty(rawKey))
            activity.SetTag("cache.key_hash", StableStringHash.Compute64(rawKey).ToString("x16"));
        return new CacheCallRecorder(mode, operation, activity);
    }

    // `operation` is a compile-time literal at every call site, so the span names below are
    // precomputed constants: no interpolation allocation on the hot path when StartActivity is
    // about to return null anyway (no listener registered). The fallback branch keeps an unknown
    // operation working (byte-identical to the old $"cache {operation}") at the cost of an alloc,
    // which only happens for a value outside the closed set below.
    private static string SpanName(string operation) => operation switch
    {
        "get" => "cache get",
        "set" => "cache set",
        "remove" => "cache remove",
        "get_or_create" => "cache get_or_create",
        "exists" => "cache exists",
        "refresh" => "cache refresh",
        "get_many" => "cache get_many",
        "set_many" => "cache set_many",
        "remove_many" => "cache remove_many",
        "remove_by_tag" => "cache remove_by_tag",
        "clear" => "cache clear",
        "stale_refresh" => "cache stale_refresh",
        _ => "cache " + operation,
    };

    // Write-shaped operations serve nothing, so they carry no served_from tag at all rather than a
    // meaningless value that would split Prometheus series for no benefit. `refresh` counts as
    // write-shaped despite reading: it always runs the factory and writes the result, so a
    // served_from tag would be the constant "source" — a label that splits series and says nothing.
    private static bool IsReadShaped(string operation) => operation is
        "get" or "get_many" or "get_or_create" or "exists" or "stale_refresh";

    /// <summary>Replaces the mode tag once routing has resolved which backend handles the call.</summary>
    public void SetMode(string resolvedMode) => _mode = resolvedMode;

    /// <summary>
    /// Wraps the caller's factory so each invocation is timed. Elapsed time accumulates, because some
    /// paths invoke the factory more than once per call (a failed read that falls open to the factory).
    /// Exceptions propagate unchanged, their elapsed time is still counted, and the exception instance is
    /// remembered so the routing layer can tell a caller-side source failure from a cache-side one.
    /// </summary>
    public Func<CancellationToken, Task<T>> WrapFactory<T>(Func<CancellationToken, Task<T>> factory)
        => async ct =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await factory(ct);
            }
            catch (Exception ex)
            {
                _factoryException = ex;
                throw;
            }
            finally
            {
                _factoryTicks += Stopwatch.GetTimestamp() - started;
                _factoryRan = true;
            }
        };

    /// <summary>True when <paramref name="ex"/> is the exception this call's factory threw.</summary>
    public bool IsFactoryException(Exception ex) => ReferenceEquals(_factoryException, ex);

    public void MarkServedFromCache() => _servedFromCache = true;

    public void MarkNotFound() => _servedFromCache = false;

    /// <summary>Records presence for a single-key read without the caller re-deriving it from the value.</summary>
    public void MarkFound(bool found) => _servedFromCache = found;

    public void MarkBatch(int hits, int misses)
    {
        _batch = true;
        _hits = hits;
        _misses = misses;
    }

    /// <summary>Marks that this call waited on a stripe lock another call held.</summary>
    public void MarkCoalesced() => _coalesced = true;

    public void MarkMissReason(string reason) => _missReason = reason;

    /// <summary>
    /// Records a backend error. <paramref name="thrownToCaller"/> false means the failure was swallowed
    /// (fail-open) and the span keeps an unset status — a Redis blip must not paint a successful
    /// consumer request as failed.
    /// </summary>
    public void MarkError(string errorKind, bool thrownToCaller)
    {
        _errorKind = errorKind;
        _errorThrownToCaller = thrownToCaller;
    }

    /// <summary>
    /// Records that the caller's own factory threw. The call failed, so the span is marked
    /// <see cref="ActivityStatusCode.Error"/>, but no <c>cache.error_kind</c> is emitted: the cache did
    /// not fail, the source did, and conflating the two makes cache-error dashboards read backwards.
    /// </summary>
    public void MarkFactoryFault() => _factoryFailed = true;

    private string? ResolveServedFrom()
    {
        if (!_readShaped) return null;
        if (_factoryRan) return ServedFromSource;
        if (_batch)
        {
            if (_hits > 0 && _misses > 0) return ServedFromMixed;
            return _hits > 0 ? ServedFromCache : ServedFromNone;
        }
        return _servedFromCache ? ServedFromCache : ServedFromNone;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var totalMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var servedFrom = ResolveServedFrom();
        CacheInstruments.RecordDuration(_mode, _operation, totalMs, servedFrom);

        double? factoryMs = null;
        if (_factoryRan)
        {
            factoryMs = _factoryTicks * 1000.0 / Stopwatch.Frequency;
            CacheInstruments.RecordFactoryDuration(_mode, _operation, factoryMs.Value);
        }

        if (_activity is null) return;

        _activity.SetTag("cache.mode", _mode);
        _activity.SetTag("cache.operation", _operation);
        if (servedFrom is not null) _activity.SetTag("cache.served_from", servedFrom);
        if (factoryMs is { } f) _activity.SetTag("cache.factory_ms", Math.Round(f, 3));
        if (_missReason is not null) _activity.SetTag("cache.miss_reason", _missReason);
        if (_batch)
        {
            _activity.SetTag("cache.hit_count", _hits);
            _activity.SetTag("cache.miss_count", _misses);
        }
        if (_coalesced) _activity.SetTag("cache.coalesced", true);
        if (_factoryFailed)
        {
            _activity.SetTag("cache.factory_failed", true);
            _activity.SetStatus(ActivityStatusCode.Error, "factory failed");
        }
        if (_errorKind is not null)
        {
            _activity.SetTag("cache.error_kind", _errorKind);
            // Cancellation is a caller decision, not a fault.
            if (_errorThrownToCaller && _errorKind is not "Canceled")
                _activity.SetStatus(ActivityStatusCode.Error, _errorKind);
        }

        _activity.Dispose();
    }
}
