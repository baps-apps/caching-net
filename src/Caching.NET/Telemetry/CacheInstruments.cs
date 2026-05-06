using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caching.NET.Telemetry;

/// <summary>
/// Static OTel instruments for Caching.NET. Replaces v1's <c>ICacheTelemetry</c> interface.
/// Subscribe via <c>builder.Services.AddOpenTelemetry().WithMetrics(b =&gt; b.AddMeter(CacheInstruments.MeterName))</c>.
/// </summary>
public static class CacheInstruments
{
    /// <summary>Meter name. Use to wire OTel pipelines.</summary>
    public const string MeterName = "Caching.NET";

    /// <summary>ActivitySource name. Use to wire OTel pipelines.</summary>
    public const string ActivitySourceName = "Caching.NET";

    /// <summary>Library version reported on instruments.</summary>
    public const string Version = "2.0.0";

    internal static readonly Meter Meter = new(MeterName, Version);

    /// <summary>The shared ActivitySource for cache operations.</summary>
    public static readonly ActivitySource Activity = new(ActivitySourceName, Version);

    internal static readonly Counter<long> Hits =
        Meter.CreateCounter<long>("cache.hits", unit: "{op}", description: "Cache hits.");
    internal static readonly Counter<long> Misses =
        Meter.CreateCounter<long>("cache.misses", unit: "{op}", description: "Cache misses.");
    internal static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("cache.errors", unit: "{op}", description: "Cache backend errors.");
    internal static readonly Counter<long> Sets =
        Meter.CreateCounter<long>("cache.sets", unit: "{op}", description: "Cache writes.");
    internal static readonly Counter<long> Removes =
        Meter.CreateCounter<long>("cache.removes", unit: "{op}", description: "Cache removals.");

    internal static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("cache.operation.duration", unit: "ms", description: "Cache operation duration.");

    internal static readonly Counter<long> Evictions =
        Meter.CreateCounter<long>("cache.evictions", unit: "{entry}", description: "Cache entry evictions.");
    internal static readonly Counter<long> StaleServed =
        Meter.CreateCounter<long>("cache.stale_served", unit: "{op}", description: "Stale entries served while a background refresh ran.");
    internal static readonly Counter<long> CircuitStateChanges =
        Meter.CreateCounter<long>("cache.circuit_state_changes", unit: "{event}", description: "Polly circuit-breaker state transitions.");
    internal static readonly Counter<long> SchemaDrift =
        Meter.CreateCounter<long>("cache.schema_drift", unit: "{event}", description: "Envelope/format/schema drift events on read.");

    internal static readonly Histogram<long> PayloadBytes =
        Meter.CreateHistogram<long>("cache.payload.bytes", unit: "By", description: "Serialized payload size in bytes.");

    internal static readonly UpDownCounter<long> StaleRefreshInFlight =
        Meter.CreateUpDownCounter<long>("cache.stale_refresh.in_flight", unit: "{task}", description: "Background stale-refresh tasks in flight.");

    internal static readonly Counter<long> TlsValidations =
        Meter.CreateCounter<long>("cache.tls.validation", unit: "{event}", description: "Redis TLS certificate validation outcomes.");

    /// <summary>Record a cache hit.</summary>
    public static void RecordHit(string mode, string operation)
        => Hits.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Record a cache miss with a reason tag.</summary>
    public static void RecordMiss(string mode, string operation, string reason = "NotFound")
        => Misses.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation),
            new KeyValuePair<string, object?>("cache.miss_reason", reason));

    /// <summary>Record an error from a cache backend operation.</summary>
    public static void RecordError(string mode, string operation, string errorKind)
        => Errors.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation),
            new KeyValuePair<string, object?>("cache.error_kind", errorKind));

    /// <summary>Record a cache write.</summary>
    public static void RecordSet(string mode, string operation = "set")
        => Sets.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Record a cache removal.</summary>
    public static void RecordRemove(string mode, string operation = "remove")
        => Removes.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Record a cache operation duration in milliseconds.</summary>
    public static void RecordDuration(string mode, string operation, double milliseconds)
        => OperationDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Record a cache entry eviction with the reason.</summary>
    public static void RecordEviction(string mode, string evictionReason)
        => Evictions.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.eviction_reason", evictionReason));

    /// <summary>Record a stale entry served while a background refresh was in progress.</summary>
    public static void RecordStaleServed(string mode, string operation)
        => StaleServed.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Record a Polly circuit-breaker state transition.</summary>
    public static void RecordCircuitStateChange(string mode, string pipeline, string circuitState)
        => CircuitStateChanges.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.pipeline", pipeline),
            new KeyValuePair<string, object?>("cache.circuit_state", circuitState));

    /// <summary>Record an envelope/format/schema drift event on read.</summary>
    public static void RecordSchemaDrift(string mode, string driftKind)
        => SchemaDrift.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.drift_kind", driftKind));

    /// <summary>Record the serialized payload size in bytes.</summary>
    public static void RecordPayloadBytes(string mode, string operation, long bytes)
        => PayloadBytes.Record(bytes,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Increment or decrement the count of background stale-refresh tasks in flight.</summary>
    public static void AddStaleRefreshInFlight(string mode, long delta)
        => StaleRefreshInFlight.Add(delta,
            new KeyValuePair<string, object?>("cache.mode", mode));

    /// <summary>Record a Redis TLS validation outcome.</summary>
    public static void RecordTlsValidation(string mode, string result)
        => TlsValidations.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.tls_result", result));

}
