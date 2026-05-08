namespace Caching.NET.Resilience;

/// <summary>
/// Tunable options for Caching.NET Redis resilience (per-op timeout, circuit breaker, retry, optional concurrency cap).
/// This type is library-owned and does not expose Polly types — Polly is an implementation detail behind <see cref="CachingBuilder.WithResilience"/>.
/// </summary>
public sealed class CacheResilienceOptions
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

    /// <summary>
    /// When true, adds an outermost Polly concurrency limiter to each Redis resilience pipeline
    /// so a brownout cannot queue unbounded threads (default false).
    /// </summary>
    public bool EnableRedisConcurrencyLimiter { get; set; }

    /// <summary>Maximum concurrent executions per pipeline when <see cref="EnableRedisConcurrencyLimiter"/> is true (default 256).</summary>
    public int RedisConcurrencyPermitLimit { get; set; } = 256;

    /// <summary>Queued executions when the permit pool is saturated (default 0 — fail fast when full).</summary>
    public int RedisConcurrencyQueueLimit { get; set; }
}
