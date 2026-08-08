namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// A <see cref="System.Diagnostics.Metrics.MeterListener"/> observes the whole process, so tests
/// that assert on the absence of measurements must not run alongside tests that produce them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsCollection
{
    public const string Name = "caching-net-metrics";
}
