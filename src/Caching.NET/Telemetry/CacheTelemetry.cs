using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caching.NET.Telemetry;

/// <summary>
/// Caching.NET-owned OpenTelemetry instrumentation. These names are the consumer contract; the
/// internal cache engine is never named in telemetry configuration and emits nothing of its own,
/// because its sources are never registered.
/// </summary>
/// <example>
/// <code><![CDATA[
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceNames))
///     .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterNames));
/// ]]></code>
/// </example>
public static class CacheTelemetry
{
    /// <summary>Caching.NET activity source name.</summary>
    public const string ActivitySourceName = "Caching.NET";

    /// <summary>Caching.NET meter name.</summary>
    public const string MeterName = "Caching.NET";

    /// <summary>Value of the <c>cache.system</c> span and metric attribute.</summary>
    public const string SystemName = "caching.net";

    private static readonly string s_version = ResolveVersion();

    /// <summary>Every activity source Caching.NET emits from.</summary>
    public static readonly string[] ActivitySourceNames = [ActivitySourceName];

    /// <summary>Every meter Caching.NET emits from.</summary>
    public static readonly string[] MeterNames = [MeterName];

    /// <summary>
    /// The Caching.NET activity source. Spans are only produced when a listener is attached, so
    /// check <see cref="ActivitySource.HasListeners"/> before building attribute values.
    /// </summary>
    public static readonly ActivitySource Activity = new(ActivitySourceName, s_version);

    internal static readonly Meter Meter = new(MeterName, s_version);

    // Counters -----------------------------------------------------------------------------
    internal static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("caching.net.operations", "{operation}", "Cache operations by result.");

    internal static readonly Counter<long> Hits =
        Meter.CreateCounter<long>("caching.net.hits", "{operation}", "Cache reads served from a cached value.");

    internal static readonly Counter<long> Misses =
        Meter.CreateCounter<long>("caching.net.misses", "{operation}", "Cache reads with no usable cached value.");

    internal static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("caching.net.errors", "{error}", "Cache errors by layer.");

    internal static readonly Counter<long> FactoryExecutions =
        Meter.CreateCounter<long>("caching.net.factory.executions", "{execution}", "Factory delegate executions.");

    internal static readonly Counter<long> FailSafeServed =
        Meter.CreateCounter<long>("caching.net.fail_safe.served", "{operation}", "Stale values served because a factory failed or timed out.");

    internal static readonly Counter<long> Invalidations =
        Meter.CreateCounter<long>("caching.net.invalidations", "{operation}", "Entry removals, tag invalidations and clears requested by the application.");

    internal static readonly Counter<long> Evictions =
        Meter.CreateCounter<long>("caching.net.evictions", "{eviction}", "Entries dropped from the in-process memory layer.");

    internal static readonly Counter<long> RedisErrors =
        Meter.CreateCounter<long>("caching.net.redis.errors", "{error}", "Distributed-cache (Redis) errors.");

    internal static readonly Counter<long> BackplaneErrors =
        Meter.CreateCounter<long>("caching.net.backplane.errors", "{error}", "Backplane errors.");

    internal static readonly Counter<long> BackgroundOperations =
        Meter.CreateCounter<long>("caching.net.background.operations", "{operation}", "Background refresh, write and recovery operations.");

    internal static readonly Counter<long> GuardViolations =
        Meter.CreateCounter<long>("caching.net.guard.violations", "{violation}", "Key or tag limit violations detected by Caching.NET.");

    internal static readonly Counter<long> TlsValidations =
        Meter.CreateCounter<long>("caching.net.redis.tls.validations", "{validation}", "Redis TLS certificate validation outcomes.");

    // Histograms ---------------------------------------------------------------------------
    internal static readonly Histogram<double> SerializationDuration =
        Meter.CreateHistogram<double>("caching.net.serialization.duration", "ms", "Serialize and deserialize duration.");

    internal static readonly Histogram<long> PayloadSize =
        Meter.CreateHistogram<long>("caching.net.payload.size", "By", "Serialized payload size.");

    internal static readonly Histogram<double> LayerDuration =
        Meter.CreateHistogram<double>("caching.net.layer.duration", "ms", "Per-layer operation duration.");

    private static string ResolveVersion()
    {
        var assembly = typeof(CacheTelemetry).Assembly;
        var attributes = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false);
        if (attributes.Length > 0 && attributes[0] is System.Reflection.AssemblyInformationalVersionAttribute informational)
        {
            var value = informational.InformationalVersion;
            var plus = value.IndexOf('+');
            return plus >= 0 ? value[..plus] : value;
        }

        return assembly.GetName().Version?.ToString() ?? "3.0.0";
    }
}
