using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;
using StackExchange.Redis;

namespace Caching.NET.Resilience;

/// <summary>
/// Constructs the default Caching.NET Polly pipeline registry: separate read/write/delete
/// pipelines so write-path failures don't trip read-path breakers.
/// </summary>
public static class CacheResiliencePipelineBuilder
{
    /// <summary>Build a registry with one pipeline per ResiliencePipelineNames entry, using the supplied knobs.</summary>
    public static ResiliencePipelineRegistry<string> BuildDefaultRegistry(
        TimeSpan? timeout = null,
        double failureRatio = 0.5,
        int minimumThroughput = 20,
        TimeSpan? samplingDuration = null,
        TimeSpan? breakDuration = null,
        int retryCount = 2,
        ILoggerFactory? loggerFactory = null)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        foreach (var name in new[] { ResiliencePipelineNames.RedisRead, ResiliencePipelineNames.RedisWrite, ResiliencePipelineNames.RedisDelete })
        {
            var pipelineName = name; // capture per-iteration for lambdas
            var logger = loggerFactory?.CreateLogger(nameof(CacheResiliencePipelineBuilder));
            registry.TryAddBuilder(pipelineName, (builder, _) =>
            {
                builder
                    .AddTimeout(new TimeoutStrategyOptions
                    {
                        Timeout = timeout ?? TimeSpan.FromSeconds(2)
                    })
                    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                    {
                        FailureRatio = failureRatio,
                        MinimumThroughput = minimumThroughput,
                        SamplingDuration = samplingDuration ?? TimeSpan.FromSeconds(30),
                        BreakDuration = breakDuration ?? TimeSpan.FromSeconds(15),
                        ShouldHandle = static args => ValueTask.FromResult(IsTransient(args.Outcome.Exception)),
                        OnOpened = args =>
                        {
                            CacheInstruments.RecordCircuitStateChange("Redis", pipelineName, "open");
                            logger?.LogWarning("Circuit breaker opened for pipeline {Pipeline} (break duration: {BreakDuration})", pipelineName, args.BreakDuration);
                            return default;
                        },
                        OnClosed = _ =>
                        {
                            CacheInstruments.RecordCircuitStateChange("Redis", pipelineName, "closed");
                            logger?.LogWarning("Circuit breaker closed for pipeline {Pipeline}", pipelineName);
                            return default;
                        },
                        OnHalfOpened = _ =>
                        {
                            CacheInstruments.RecordCircuitStateChange("Redis", pipelineName, "half-open");
                            logger?.LogWarning("Circuit breaker half-opened for pipeline {Pipeline}", pipelineName);
                            return default;
                        }
                    });

                if (retryCount > 0)
                {
                    builder.AddRetry(new RetryStrategyOptions
                    {
                        MaxRetryAttempts = retryCount,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        ShouldHandle = static args => ValueTask.FromResult(IsTransient(args.Outcome.Exception))
                    });
                }
            });
        }
        return registry;
    }

    private static bool IsTransient(Exception? ex) => ex switch
    {
        RedisConnectionException => true,
        RedisTimeoutException => true,
        TimeoutRejectedException => true,
        TimeoutException => true,
        _ => false
    };
}
