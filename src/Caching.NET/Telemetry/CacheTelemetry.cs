using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caching.NET.Telemetry;

/// <summary>
/// Caching.NET-owned OpenTelemetry instrumentation. The instrumentation names below are the
/// consumer contract; the internal cache engine is never named in telemetry configuration.
/// </summary>
/// <example>
/// Recommended wiring — branded metrics only, full span detail:
/// <code><![CDATA[
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceNames))
///     .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterName));
///
/// // Add the engine's per-layer instruments only if the breakdown is worth the overlap:
/// //   .WithMetrics(m => m.AddMeter(CacheTelemetry.EngineMeterNames))
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

    /// <summary>
    /// The engine-level activity sources that produce operation, memory-level, distributed-level and
    /// backplane spans. Register these <b>in addition to</b> <see cref="ActivitySourceName"/> when
    /// low-level span detail is wanted; the spans arrive under engine source names, which is the one
    /// place the engine is visible to an operator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engine spans record the raw physical cache key</b> in the <c>fusioncache.operation.key</c>
    /// attribute, prefix included. The engine offers no switch to suppress it. Caching.NET's own
    /// spans never carry a key, so registering <see cref="ActivitySourceName"/> alone keeps keys out
    /// of the tracing backend entirely.
    /// </para>
    /// <para>
    /// Register these only when cache keys are safe to export — that is, when no key embeds a user
    /// id, tenant id, email, token or other identifier — or when the collector strips the attribute
    /// (for example an OpenTelemetry processor that drops <c>fusioncache.operation.key</c>).
    /// </para>
    /// <para>
    /// The names are published here so application code never types an engine name itself. Spans do
    /// not overlap — each layer emits its own — so registering these alongside the Caching.NET
    /// source does not duplicate anything.
    /// </para>
    /// </remarks>
    public static readonly string[] EngineActivitySourceNames =
    [
        EngineTelemetryNames.ActivitySource,
        EngineTelemetryNames.ActivitySourceMemoryLevel,
        EngineTelemetryNames.ActivitySourceDistributedLevel,
        EngineTelemetryNames.ActivitySourceBackplane
    ];

    /// <summary>
    /// The Caching.NET source plus every engine-level source. Gives the most detailed trace, and
    /// <b>exports the raw physical cache key</b> — see <see cref="EngineActivitySourceNames"/>
    /// before using it.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ActivitySourceName"/> on its own unless the extra span detail is needed and
    /// cache keys are safe to export: the engine sources in this array attach the full physical key
    /// to every operation span as <c>fusioncache.operation.key</c>, and the engine has no option to
    /// turn that off.
    /// </remarks>
    public static readonly string[] ActivitySourceNames =
    [
        ActivitySourceName,
        EngineTelemetryNames.ActivitySource,
        EngineTelemetryNames.ActivitySourceMemoryLevel,
        EngineTelemetryNames.ActivitySourceDistributedLevel,
        EngineTelemetryNames.ActivitySourceBackplane
    ];

    /// <summary>
    /// Name of the engine span attribute that carries the raw physical cache key. Published so an
    /// application can drop or redact it in an OpenTelemetry processor when it registers
    /// <see cref="EngineActivitySourceNames"/>.
    /// </summary>
    public const string EngineKeyAttributeName = "fusioncache.operation.key";

    /// <summary>
    /// The engine-level meters. These carry per-layer detail Caching.NET does not re-emit
    /// (<c>fusioncache.memory.*</c>, <c>fusioncache.distributed.*</c>, <c>fusioncache.backplane.*</c>).
    /// </summary>
    /// <remarks>
    /// <b>These overlap the Caching.NET meter.</b> One cache hit increments both
    /// <c>caching.net.hits</c> and <c>fusioncache.cache.hit</c>, so a dashboard must never sum the
    /// two families. Register these only when the per-layer breakdown is worth that care; otherwise
    /// register <see cref="MeterName"/> alone.
    /// </remarks>
    public static readonly string[] EngineMeterNames =
    [
        EngineTelemetryNames.Meter,
        EngineTelemetryNames.MeterMemoryLevel,
        EngineTelemetryNames.MeterDistributedLevel,
        EngineTelemetryNames.MeterBackplane
    ];

    /// <summary>
    /// The Caching.NET meter plus every engine-level meter.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="MeterName"/> on its own: the branded meter already reports hits, misses,
    /// errors, factory executions, fail-safe, invalidations and payload sizes. This array adds the
    /// engine's per-layer instruments, which <b>double-count the same operations</b> under different
    /// names — see <see cref="EngineMeterNames"/>.
    /// </remarks>
    public static readonly string[] MeterNames =
    [
        MeterName,
        EngineTelemetryNames.Meter,
        EngineTelemetryNames.MeterMemoryLevel,
        EngineTelemetryNames.MeterDistributedLevel,
        EngineTelemetryNames.MeterBackplane
    ];

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
        Meter.CreateCounter<long>("caching.net.invalidations", "{operation}", "Entry removals, tag invalidations and clears.");

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
