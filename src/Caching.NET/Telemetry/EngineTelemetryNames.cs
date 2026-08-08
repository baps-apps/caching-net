using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Telemetry;

/// <summary>
/// Instrumentation names owned by the internal cache engine. Exposed only through the
/// Caching.NET-branded arrays on <see cref="CacheTelemetry"/> so that application code and
/// OpenTelemetry wiring never reference the engine by name.
/// </summary>
internal static class EngineTelemetryNames
{
    public const string ActivitySource = FusionCacheDiagnostics.ActivitySourceName;
    public const string ActivitySourceMemoryLevel = FusionCacheDiagnostics.ActivitySourceNameMemoryLevel;
    public const string ActivitySourceDistributedLevel = FusionCacheDiagnostics.ActivitySourceNameDistributedLevel;
    public const string ActivitySourceBackplane = FusionCacheDiagnostics.ActivitySourceNameBackplane;

    public const string Meter = FusionCacheDiagnostics.MeterName;
    public const string MeterMemoryLevel = FusionCacheDiagnostics.MeterNameMemoryLevel;
    public const string MeterDistributedLevel = FusionCacheDiagnostics.MeterNameDistributedLevel;
    public const string MeterBackplane = FusionCacheDiagnostics.MeterNameBackplane;
}
