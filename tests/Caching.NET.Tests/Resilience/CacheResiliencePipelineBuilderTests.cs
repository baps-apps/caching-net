using Caching.NET.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Caching.NET.Tests.Resilience;

public sealed class CacheResiliencePipelineBuilderTests
{
    [Fact]
    public void BuildDefault_ProducesPipelineForEachOp()
    {
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry();
        Assert.NotNull(registry.GetPipeline(ResiliencePipelineNames.RedisRead));
        Assert.NotNull(registry.GetPipeline(ResiliencePipelineNames.RedisWrite));
        Assert.NotNull(registry.GetPipeline(ResiliencePipelineNames.RedisDelete));
    }

    [Fact]
    public async Task DefaultPipeline_WrapsTimeout_ThrowsTimeoutRejected()
    {
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromMilliseconds(50),
            retryCount: 0);
        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
            await pipeline.ExecuteAsync(static async ct =>
            {
                await Task.Delay(500, ct);
            }));
    }

    [Fact]
    public async Task DefaultPipeline_OpensCircuitAfterRepeatedFailures()
    {
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(1),
            failureRatio: 0.5,
            minimumThroughput: 4,
            samplingDuration: TimeSpan.FromSeconds(2),
            breakDuration: TimeSpan.FromSeconds(5),
            retryCount: 0);
        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        for (var i = 0; i < 8; i++)
        {
            try { await pipeline.ExecuteAsync(static _ => throw new TimeoutException()); }
            catch { /* expected */ }
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(static _ => ValueTask.CompletedTask));
    }
}
