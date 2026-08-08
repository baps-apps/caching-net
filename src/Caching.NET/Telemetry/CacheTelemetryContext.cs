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

    public CacheTelemetryContext(CachingOptions options)
    {
        CacheName = options.CacheName;
        Mode = options.Mode.ToString();
        MetricsEnabled = options.Observability.EnableMetrics;
        TracingEnabled = options.Observability.EnableTracing;

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

    /// <summary>True when an OpenTelemetry listener is attached and tracing is enabled.</summary>
    public bool ShouldTrace => TracingEnabled && CacheTelemetry.Activity.HasListeners();

    /// <summary>
    /// Records a read served from a cached value. <paramref name="stale"/> marks a value returned
    /// past its logical expiration by fail-safe: it still counts as a hit, so the hit ratio stays
    /// meaningful, but it is reported with <c>cache.result=stale</c>.
    /// </summary>
    public void RecordHit(string operation, string layer, bool stale = false)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        tags.Add(CacheTelemetryAttributes.Layer, layer);
        CacheTelemetry.Hits.Add(1, tags);

        tags.Add(CacheTelemetryAttributes.Result, stale ? CacheResults.Stale : CacheResults.Hit);
        CacheTelemetry.Operations.Add(1, tags);
    }

    public void RecordMiss(string operation)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        CacheTelemetry.Misses.Add(1, tags);

        tags.Add(CacheTelemetryAttributes.Result, CacheResults.Miss);
        CacheTelemetry.Operations.Add(1, tags);
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

    public void RecordInvalidation(string operation)
    {
        if (!MetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        CacheTelemetry.Invalidations.Add(1, tags);

        tags.Add(CacheTelemetryAttributes.Result, CacheResults.Removed);
        CacheTelemetry.Operations.Add(1, tags);
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

    /// <summary>
    /// Starts a Caching.NET span, or returns <c>null</c> when tracing is off or no listener is
    /// attached. Callers must not build attribute values before checking the result.
    /// </summary>
    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        if (!TracingEnabled)
        {
            return null;
        }

        var activity = CacheTelemetry.Activity.StartActivity(name, kind);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CacheTelemetryAttributes.System, CacheTelemetry.SystemName);
        activity.SetTag(CacheTelemetryAttributes.Mode, Mode);
        activity.SetTag(CacheTelemetryAttributes.Name, CacheName);
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
