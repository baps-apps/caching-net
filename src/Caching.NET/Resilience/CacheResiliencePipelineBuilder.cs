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
        int retryCount = 2)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        foreach (var name in new[] { ResiliencePipelineNames.RedisRead, ResiliencePipelineNames.RedisWrite, ResiliencePipelineNames.RedisDelete })
        {
            registry.TryAddBuilder(name, (builder, _) =>
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
                        ShouldHandle = static args => ValueTask.FromResult(IsTransient(args.Outcome.Exception))
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
