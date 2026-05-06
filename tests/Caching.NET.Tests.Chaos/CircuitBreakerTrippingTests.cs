using Caching.NET.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using StackExchange.Redis;
using Xunit;

namespace Caching.NET.Tests.Chaos;

public class CircuitBreakerTrippingTests
{
    [Fact]
    public async Task Repeated_exceptions_trip_circuit_breaker()
    {
        // Low thresholds so the breaker trips in a handful of calls
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(5),
            failureRatio: 0.5,
            minimumThroughput: 3,
            samplingDuration: TimeSpan.FromSeconds(30),
            breakDuration: TimeSpan.FromSeconds(60),
            retryCount: 0,
            loggerFactory: NullLoggerFactory.Instance);

        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        for (int i = 0; i < 20; i++)
        {
            try
            {
                await pipeline.ExecuteAsync<int>(_ =>
                    throw new RedisConnectionException(ConnectionFailureType.None, "chaos-boom"));
            }
            catch (BrokenCircuitException)
            {
                // Circuit opened — success
                return;
            }
            catch (Exception ex) when (ex is not BrokenCircuitException)
            {
                // Still in closed state; keep going
            }
        }

        Assert.Fail("Circuit breaker never opened after 20 consecutive transient failures");
    }
}
