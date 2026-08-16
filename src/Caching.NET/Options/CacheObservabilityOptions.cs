using Microsoft.Extensions.Logging;

namespace Caching.NET.Options;

/// <summary>
/// Tracing, metrics and logging settings. All instrumentation is published under
/// Caching.NET-owned names — see <c>Caching.NET.Telemetry.CacheTelemetry</c>.
/// </summary>
public sealed class CacheObservabilityOptions
{
    /// <summary>
    /// Emit Caching.NET activities. Spans are only created when an OpenTelemetry listener is
    /// attached to the Caching.NET activity source, so leaving this on costs nothing when nothing
    /// is listening. Default <c>true</c>.
    /// </summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>Emit Caching.NET metrics. Default <c>true</c>.</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// When a single layer probe produces a span. Default
    /// <see cref="CacheLayerTracing.WhenParented"/>, which keeps request-path probes traced and drops
    /// the single-span root traces that backplane and background work would otherwise produce — a
    /// span that has already ended does not count as a parent. Gates spans only: every layer metric
    /// is recorded either way.
    /// </summary>
    public CacheLayerTracing LayerTracing { get; set; } = CacheLayerTracing.WhenParented;

    /// <summary>
    /// Include <c>cache.name</c> as a metric dimension. Turn off when an application registers a
    /// large or unbounded number of named caches. Default <c>true</c>.
    /// </summary>
    public bool IncludeCacheNameDimension { get; set; } = true;

    /// <summary>
    /// Log a one-line startup summary describing the resolved cache topology. Never includes the
    /// Redis endpoint, credentials or connection string. Default <c>true</c>.
    /// </summary>
    public bool LogStartupSummary { get; set; } = true;

    /// <summary>Level for distributed-cache (Redis) errors. Default <see cref="LogLevel.Warning"/>.</summary>
    public LogLevel DistributedCacheErrorLogLevel { get; set; } = LogLevel.Warning;

    /// <summary>Level for backplane errors. Default <see cref="LogLevel.Warning"/>.</summary>
    public LogLevel BackplaneErrorLogLevel { get; set; } = LogLevel.Warning;

    /// <summary>Level for serialization and deserialization errors. Default <see cref="LogLevel.Warning"/>.</summary>
    public LogLevel SerializationErrorLogLevel { get; set; } = LogLevel.Warning;

    /// <summary>Level recorded when a stale value is served by fail-safe. Default <see cref="LogLevel.Warning"/>.</summary>
    public LogLevel FailSafeActivationLogLevel { get; set; } = LogLevel.Warning;

    /// <summary>Level for factory exceptions. Default <see cref="LogLevel.Warning"/>.</summary>
    public LogLevel FactoryErrorLogLevel { get; set; } = LogLevel.Warning;

    /// <summary>
    /// Level for synthetic timeouts (a soft or hard timeout firing). Default
    /// <see cref="LogLevel.Debug"/> — these are expected under load and would otherwise flood logs.
    /// </summary>
    public LogLevel SyntheticTimeoutLogLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Level at which the internal cache engine's per-operation log lines are written. Default
    /// <see cref="LogLevel.Debug"/>. Set to <see cref="LogLevel.Information"/> to keep the engine's
    /// native verbosity, or <see cref="LogLevel.None"/> to drop those lines entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine logs every cache call — the verb, the resolved entry options and the outcome — at
    /// <see cref="LogLevel.Information"/>, which is the level a production application normally runs
    /// at. Measured on this package: <b>2.04 log lines per <c>GetOrSet</c></b>, each carrying a full
    /// options dump. A service doing a few thousand cache reads a second therefore pays for a few
    /// thousand log lines a second describing cache hits, which is a logging bill and an ingestion
    /// problem rather than a diagnostic. Caching.NET rewrites those lines to this level so the
    /// default deployment is quiet and the detail is one <c>Caching.NET</c>-category level change
    /// away.
    /// </para>
    /// <para>
    /// Only lines the engine emits at exactly <see cref="LogLevel.Information"/> are affected;
    /// warnings and errors are never downgraded. Because the engine reports nothing else at
    /// <see cref="LogLevel.Information"/>, any of the error-level properties on this type that is set
    /// to <see cref="LogLevel.Information"/> would be caught by the same rewrite — so
    /// <c>CachingOptionsValidator</c> rejects that combination at startup rather than letting a
    /// deliberately raised error level be silently lowered again.
    /// </para>
    /// </remarks>
    public LogLevel EngineOperationLogLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Whether per-layer duration is recorded on <c>caching.net.layer.duration</c>. Default
    /// <c>true</c>. This gates the duration histogram only — no counter, including
    /// <c>caching.net.hits</c>, <c>caching.net.misses</c> and <c>caching.net.operations</c>, is
    /// affected by this flag.
    /// </summary>
    public bool EnableLayerMetrics { get; set; } = true;
}
