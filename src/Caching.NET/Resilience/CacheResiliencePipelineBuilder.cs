using System.IO;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;
using StackExchange.Redis;

namespace Caching.NET.Resilience;

/// <summary>
/// Constructs the default Caching.NET Polly pipeline registry: separate read/write/delete
/// pipelines so write-path failures don't trip read-path breakers. Internal — Polly types are not part of the public API.
/// </summary>
internal static class CacheResiliencePipelineBuilder
{
    /// <summary>Build a registry with one pipeline per ResiliencePipelineNames entry, using the supplied knobs.</summary>
    public static ResiliencePipelineRegistry<string> BuildDefaultRegistry(
        TimeSpan? timeout = null,
        double failureRatio = 0.5,
        int minimumThroughput = 20,
        TimeSpan? samplingDuration = null,
        TimeSpan? breakDuration = null,
        int retryCount = 2,
        bool enableRedisConcurrencyLimiter = false,
        int redisConcurrencyPermitLimit = 256,
        int redisConcurrencyQueueLimit = 0,
        ILoggerFactory? loggerFactory = null)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        foreach (var name in new[] { ResiliencePipelineNames.RedisRead, ResiliencePipelineNames.RedisWrite, ResiliencePipelineNames.RedisDelete })
        {
            var pipelineName = name; // capture per-iteration for lambdas
            var logger = loggerFactory?.CreateLogger(nameof(CacheResiliencePipelineBuilder));
            registry.TryAddBuilder(pipelineName, (builder, _) =>
            {
                if (enableRedisConcurrencyLimiter && redisConcurrencyPermitLimit > 0)
                {
                    builder.AddConcurrencyLimiter(new ConcurrencyLimiterOptions
                    {
                        PermitLimit = redisConcurrencyPermitLimit,
                        QueueLimit = redisConcurrencyQueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
                }

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
                        Delay = TimeSpan.FromMilliseconds(50),
                        MaxDelay = TimeSpan.FromSeconds(1),
                        ShouldHandle = static args => ValueTask.FromResult(IsTransient(args.Outcome.Exception))
                    });
                }
            });
        }
        return registry;
    }

    private static bool IsTransient(Exception? ex) => IsTransient(ex, depth: 0);

    private static bool IsTransient(Exception? ex, int depth)
    {
        if (ex is null || depth > 8) return false;
        if (ex is OperationCanceledException) return false;

        if (ex is RedisConnectionException or RedisTimeoutException or TimeoutRejectedException or TimeoutException)
            return true;

        if (ex is SocketException or IOException)
            return true;

        if (ex is RedisServerException rse && IsTransientRedisServerMessage(rse.Message))
            return true;

        return ex.InnerException is not null && IsTransient(ex.InnerException, depth + 1);
    }

    private static bool IsTransientRedisServerMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("LOADING", StringComparison.OrdinalIgnoreCase)
               || message.Contains("READONLY", StringComparison.OrdinalIgnoreCase);
    }
}
