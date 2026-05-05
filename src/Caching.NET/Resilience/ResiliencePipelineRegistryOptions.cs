namespace Caching.NET.Resilience;

/// <summary>
/// Knobs for the default Caching.NET Polly pipeline (timeout + circuit breaker + retry).
/// </summary>
public sealed class ResiliencePipelineRegistryOptions
{
    /// <summary>Per-op timeout (default 2 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Failure ratio threshold for circuit breaker (default 0.5).</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Minimum number of operations in sampling window before breaker can trip (default 20).</summary>
    public int MinimumThroughput { get; set; } = 20;

    /// <summary>Sampling window for failure-ratio calculation (default 30 s).</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the breaker stays open before half-open (default 15 s).</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Number of retry attempts on transient failures (default 2).</summary>
    public int RetryCount { get; set; } = 2;
}
