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
}
