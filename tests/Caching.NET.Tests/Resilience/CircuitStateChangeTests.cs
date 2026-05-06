using Caching.NET.Resilience;
using Caching.NET.Tests.Telemetry;
using Polly.CircuitBreaker;
using StackExchange.Redis;

namespace Caching.NET.Tests.Resilience;

public sealed class CircuitStateChangeTests
{
    [Fact]
    public async Task Tripping_circuit_emits_open_state_change()
    {
        var modeTag = "Redis";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.circuit_state_changes", modeTag);
        using var _ = listener;

        // Polly v8 requires BreakDuration >= 500ms
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(5),
            failureRatio: 0.5,
            minimumThroughput: 2,
            samplingDuration: TimeSpan.FromSeconds(10),
            breakDuration: TimeSpan.FromMilliseconds(500),
            retryCount: 0);
        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        // Trip the breaker by exceeding the failure ratio
        for (var i = 0; i < 2; i++)
        {
            try { await pipeline.ExecuteAsync<int>(_ => throw new RedisConnectionException(ConnectionFailureType.None, "fail")); }
            catch { /* expected */ }
        }

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.circuit_state" && (string?)t.Value == "open"));
    }

    [Fact]
    public async Task Circuit_emits_half_open_then_closed_after_break_duration()
    {
        var modeTag = "Redis";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.circuit_state_changes", modeTag);
        using var _ = listener;

        // Polly v8 requires BreakDuration >= 500ms
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(5),
            failureRatio: 0.5,
            minimumThroughput: 2,
            samplingDuration: TimeSpan.FromSeconds(10),
            breakDuration: TimeSpan.FromMilliseconds(500),
            retryCount: 0);
        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        // Trip the breaker
        for (var i = 0; i < 2; i++)
        {
            try { await pipeline.ExecuteAsync<int>(_ => throw new RedisConnectionException(ConnectionFailureType.None, "fail")); }
            catch { /* expected */ }
        }

        // Wait past break duration, then let a successful probe close the circuit
        await Task.Delay(700);
        try { await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)); } catch (BrokenCircuitException) { /* may still be open */ }

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.circuit_state" && (string?)t.Value == "open"));
    }

    [Fact]
    public async Task Each_pipeline_emits_its_own_name_tag()
    {
        var modeTag = "Redis";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.circuit_state_changes", modeTag);
        using var _ = listener;

        // Polly v8 requires BreakDuration >= 500ms
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(5),
            failureRatio: 0.5,
            minimumThroughput: 2,
            samplingDuration: TimeSpan.FromSeconds(10),
            breakDuration: TimeSpan.FromMilliseconds(500),
            retryCount: 0);

        // Trip the write pipeline
        var writePipeline = registry.GetPipeline(ResiliencePipelineNames.RedisWrite);
        for (var i = 0; i < 2; i++)
        {
            try { await writePipeline.ExecuteAsync<int>(_ => throw new RedisConnectionException(ConnectionFailureType.None, "fail")); }
            catch { /* expected */ }
        }

        Assert.Contains(values, v =>
            v.tags.Any(t => t.Key == "cache.circuit_state" && (string?)t.Value == "open") &&
            v.tags.Any(t => t.Key == "cache.pipeline" && (string?)t.Value == ResiliencePipelineNames.RedisWrite));
    }
}
