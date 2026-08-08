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
}
