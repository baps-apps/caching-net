using System.Diagnostics;
using Caching.NET.Options;

namespace Caching.NET.Telemetry;

/// <summary>
/// Per-cache-instance telemetry recorder. Holds the pre-resolved low-cardinality dimensions for a
/// single cache so the hot path never re-derives or re-allocates them.
/// </summary>
internal sealed class CacheTelemetryContext
{
    private readonly KeyValuePair<string, object?> _systemTag;
    private readonly KeyValuePair<string, object?> _modeTag;
    private readonly KeyValuePair<string, object?>? _nameTag;

    // Both gates are folded into plain booleans at construction so a layer probe costs one field
    // read rather than an enum comparison plus a second flag. Layer probes are the highest-frequency
    // call into this type — several per logical operation — so the check in front of them is the one
    // place where a branch that could have been precomputed is worth precomputing.
    private readonly bool _layerSpansEnabled;
    private readonly bool _layerSpansRequireParent;

    public CacheTelemetryContext(CachingOptions options)
    {
        CacheName = options.CacheName;
        Mode = options.Mode.ToString();
        MetricsEnabled = options.Observability.EnableMetrics;
        TracingEnabled = options.Observability.EnableTracing;
        LayerMetricsEnabled = options.Observability.EnableLayerMetrics;
        LayerTracing = options.Observability.LayerTracing;
        AllowRawKeysInTelemetry = options.Security.AllowRawKeysInTelemetry;

        _layerSpansEnabled = TracingEnabled && LayerTracing != CacheLayerTracing.Never;
        _layerSpansRequireParent = LayerTracing == CacheLayerTracing.WhenParented;

        _systemTag = new KeyValuePair<string, object?>(CacheTelemetryAttributes.System, CacheTelemetry.SystemName);
        _modeTag = new KeyValuePair<string, object?>(CacheTelemetryAttributes.Mode, Mode);
        _nameTag = options.Observability.IncludeCacheNameDimension
            ? new KeyValuePair<string, object?>(CacheTelemetryAttributes.Name, CacheName)
            : null;
    }

    public string CacheName { get; }

    public string Mode { get; }

    public bool MetricsEnabled { get; }

    public bool TracingEnabled { get; }

    public bool LayerMetricsEnabled { get; }

    public CacheLayerTracing LayerTracing { get; }

    public bool AllowRawKeysInTelemetry { get; }

    /// <summary>True when an OpenTelemetry listener is attached and tracing is enabled.</summary>
    public bool ShouldTrace => TracingEnabled && CacheTelemetry.Activity.HasListeners();

    /// <summary>
    /// Records one logical read served from a cached value. Owned exclusively by
    /// <see cref="Internal.FusionCacheService"/>, one call per logical <c>GetOrSet*</c>/
    /// <c>GetOrDefault*</c>/<c>TryGet*</c> invocation — never per physical layer probe. The layer
    /// decorators used to feed this from <c>IMemoryCache</c>/<c>IDistributedCache</c> probes, but the
    /// engine probes a layer more than once per logical read (tag/clear-marker lookups, lock
    /// double-checks), which inflated this counter by a call-mix-dependent factor and broke the
    /// documented <c>hits / (hits + misses)</c> ratio in <c>docs/TELEMETRY.md</c>. There is
    /// deliberately no <c>cache.layer</c> dimension, matching <see cref="RecordOperation"/>: per-layer
    /// truth lives on <c>caching.net.layer.duration</c> instead, which is already probe-scoped by
    /// construction and carries its own <c>cache.result=hit|miss</c> dimension.
    /// </summary>
    public void RecordHit(string operation)
    {
        if (!MetricsEnabled || !CacheTelemetry.Hits.Enabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        CacheTelemetry.Hits.Add(1, tags);
    }

    /// <summary>
    /// Records one logical read that found no usable value. See <see cref="RecordHit"/> for why this
    /// is logical-operation-scoped rather than per-layer-probe-scoped.
    /// </summary>
    public void RecordMiss(string operation)
    {
        if (!MetricsEnabled || !CacheTelemetry.Misses.Enabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        CacheTelemetry.Misses.Add(1, tags);
    }

    public void RecordSet(string operation)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        tags.Add(CacheTelemetryAttributes.Result, CacheResults.Set);
        CacheTelemetry.Operations.Add(1, tags);
    }

    /// <summary>
    /// Records the outcome of a logical cache operation. Owned exclusively by
    /// <see cref="Internal.FusionCacheService"/>: it carries no <c>cache.layer</c> dimension because
    /// the operation is not attributable to one layer — per-layer truth lives on
    /// <c>caching.net.layer.duration</c> instead.
    /// </summary>
    public void RecordOperation(string operation, string result)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        tags.Add(CacheTelemetryAttributes.Result, result);
        CacheTelemetry.Operations.Add(1, tags);
    }

    /// <summary>
    /// Records one caller-requested invalidation (<c>Remove*</c>, <c>Expire*</c>,
    /// <c>RemoveByTag*</c>, <c>Clear*</c>). Owned exclusively by
    /// <see cref="Internal.FusionCacheService"/>, one record per logical call. It deliberately does
    /// <b>not</b> also write <c>caching.net.operations</c>: that counter is the adapter's, and the
    /// adapter records it separately on the same call. It used to be written from here, which meant
    /// <see cref="Internal.CacheEventBridge"/>'s eviction handler — routed through this method —
    /// booked an extra "operation" for every removal, overwrite and expiry the engine evicted, so a
    /// workload of 5 sets and 5 removes reported 15 operations instead of 10. Engine-side evictions
    /// now go to <see cref="RecordEviction"/> and touch neither of the adapter's counters.
    /// </summary>
    public void RecordInvalidation(string operation)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        CacheTelemetry.Invalidations.Add(1, tags);
    }

    /// <summary>
    /// Records one entry dropped from the in-process memory layer — expired, evicted under the size
    /// limit, replaced, or removed. Owned exclusively by <see cref="Internal.CacheEventBridge"/>,
    /// which is the only component that can observe it; it has its own instrument precisely because
    /// it is not a caller-requested operation and must not inflate the counters that are.
    /// </summary>
    public void RecordEviction()
    {
        if (!MetricsEnabled)
        {
            return;
        }

        CacheTelemetry.Evictions.Add(1, BaseTags());
    }

    public void RecordFactoryExecution(bool succeeded, bool background)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Layer, CacheLayers.Factory);
        tags.Add(CacheTelemetryAttributes.Result, succeeded ? CacheResults.Hit : CacheResults.Error);
        tags.Add(CacheTelemetryAttributes.BackgroundOperation, background);
        CacheTelemetry.FactoryExecutions.Add(1, tags);
    }

    public void RecordFailSafeServed()
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Result, CacheResults.Stale);
        CacheTelemetry.FailSafeServed.Add(1, tags);
    }

    public void RecordBackgroundOperation(string operation, bool succeeded)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        tags.Add(CacheTelemetryAttributes.Result, succeeded ? CacheResults.Set : CacheResults.Error);
        CacheTelemetry.BackgroundOperations.Add(1, tags);
    }

    public void RecordError(string layer, string errorType)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Layer, layer);
        tags.Add(CacheTelemetryAttributes.ErrorType, errorType);
        CacheTelemetry.Errors.Add(1, tags);

        switch (layer)
        {
            case CacheLayers.Redis:
                CacheTelemetry.RedisErrors.Add(1, tags);
                break;
            case CacheLayers.Backplane:
                CacheTelemetry.BackplaneErrors.Add(1, tags);
                break;
            default:
                break;
        }
    }

    public void RecordGuardViolation(string violation)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, violation);
        CacheTelemetry.GuardViolations.Add(1, tags);
    }

    public void RecordTlsValidation(string result)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Result, result);
        CacheTelemetry.TlsValidations.Add(1, tags);
    }

    public void RecordSerialization(string operation, double milliseconds, long payloadBytes)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        CacheTelemetry.SerializationDuration.Record(milliseconds, tags);
        CacheTelemetry.PayloadSize.Record(payloadBytes, tags);
    }

    /// <summary>Records the duration and outcome of one layer's part of an operation.</summary>
    public void RecordLayer(string layer, string operation, string result, double milliseconds)
    {
        if (!MetricsEnabled || !LayerMetricsEnabled || !CacheTelemetry.LayerDuration.Enabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Layer, layer);
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        tags.Add(CacheTelemetryAttributes.Result, result);
        CacheTelemetry.LayerDuration.Record(milliseconds, tags);
    }

    /// <summary>
    /// Starts a Caching.NET span, or returns <c>null</c> when tracing is off or no listener is
    /// attached. Callers must not build attribute values before checking the result.
    /// </summary>
    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        if (!TracingEnabled || !CacheTelemetry.Activity.HasListeners())
        {
            return null;
        }

        return Brand(CacheTelemetry.Activity.StartActivity(name, kind));
    }

    /// <summary>
    /// Starts a span with no parent, whatever <see cref="Activity.Current"/> happens to hold. For work
    /// that is genuinely the start of something — a received backplane message — rather than a step
    /// inside a caller's operation.
    /// </summary>
    /// <remarks>
    /// The explicit <c>default</c> <see cref="ActivityContext"/> is the point of this overload, not an
    /// omission. Ambient context flows with the <see cref="ExecutionContext"/>, so a thread that runs
    /// engine callbacks can be holding the span of whichever request happened to trigger the
    /// subscription — long since finished. Inheriting it would file backplane work under an unrelated
    /// request and leave a child span starting after its parent ended.
    /// </remarks>
    public Activity? StartRootActivity(string name)
    {
        if (!TracingEnabled || !CacheTelemetry.Activity.HasListeners())
        {
            return null;
        }

        return Brand(CacheTelemetry.Activity.StartActivity(name, ActivityKind.Internal, parentContext: default));
    }

    /// <summary>
    /// Starts a span for one physical layer probe, subject to <see cref="LayerTracing"/>. Returns
    /// <c>null</c> when the probe should not be traced, so callers must not build attribute values
    /// before checking the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="StartActivity"/> so the rule is expressed by which method a caller
    /// reaches for, rather than by inspecting span names after the fact. The four infrastructure
    /// decorators call this one; <see cref="Internal.FusionCacheService"/> calls
    /// <see cref="StartActivity"/> and the backplane receive path calls
    /// <see cref="StartRootActivity"/>, and neither is gated — those are the spans a gated probe
    /// would otherwise have had as a parent.
    /// </para>
    /// <para>
    /// The parent check deliberately sits after the enabled and listener checks: a process with no
    /// listener attached must not pay an <see cref="Activity.Current"/> read it cannot use.
    /// </para>
    /// <para>
    /// A <i>stopped</i> ambient span counts as no parent. Ambient context flows with the
    /// <see cref="ExecutionContext"/>, so work the engine schedules onto its own threads — a write
    /// following a background factory, an eviction on a subscriber thread — often still holds the span
    /// of the request that triggered it, already ended. Treating that as a parent is what the naive
    /// null check gets wrong: it would keep emitting exactly the probes this setting exists to drop,
    /// and file each one as a child that starts after its parent finished. The test for it is
    /// <c>LayerSpanParentingTests.StoppedAmbientSpan_CountsAsNoParent</c>.
    /// </para>
    /// </remarks>
    public Activity? StartLayerActivity(string name)
    {
        if (!_layerSpansEnabled || !CacheTelemetry.Activity.HasListeners())
        {
            return null;
        }

        if (_layerSpansRequireParent && Activity.Current is not { IsStopped: false })
        {
            return null;
        }

        return Brand(CacheTelemetry.Activity.StartActivity(name, ActivityKind.Internal));
    }

    /// <summary>Applies the three per-cache span tags. Shared by every span-starting method.</summary>
    private Activity? Brand(Activity? activity)
    {
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CacheTelemetryAttributes.System, CacheTelemetry.SystemName);
        activity.SetTag(CacheTelemetryAttributes.Mode, Mode);
        activity.SetTag(CacheTelemetryAttributes.Name, CacheName);
        return activity;
    }

    /// <summary>
    /// Starts an operation span carrying the standard tags plus a key identifier. Returns
    /// <c>null</c> when tracing is off or no listener is attached, so callers must not build
    /// attribute values before checking the result.
    /// </summary>
    public Activity? StartOperation(string name, string key)
    {
        var activity = StartActivity(name);
        if (activity is null)
        {
            return null;
        }

        if (AllowRawKeysInTelemetry)
        {
            activity.SetTag(CacheTelemetryAttributes.Key, key);
        }
        else
        {
            activity.SetTag(CacheTelemetryAttributes.KeyFingerprint, Internal.KeyFingerprint.Compute(key));
        }

        return activity;
    }

    private TagList BaseTags()
    {
        var tags = default(TagList);
        tags.Add(_systemTag);
        tags.Add(_modeTag);
        if (_nameTag is { } nameTag)
        {
            tags.Add(nameTag);
        }

        return tags;
    }
}
