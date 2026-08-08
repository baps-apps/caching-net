using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Caching.NET.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Benchmark;

/// <summary>
/// What observability costs on the hot path, with and without a listener attached. Each tier is
/// measured on a pre-warmed key (hit path) and a cold key that is never populated (miss path), since
/// the two paths build different metric/trace attributes and can regress independently.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class TelemetryOverheadBenchmarks
{
    private ServiceProvider _withTelemetry = null!;
    private ServiceProvider _withoutTelemetry = null!;
    private ICacheService _instrumented = null!;
    private ICacheService _silent = null!;
    private ActivityListener? _listener;
    private int _missCounter;

    [GlobalSetup]
    public void Setup()
    {
        (_withTelemetry, _instrumented) = CacheHostFactory.Create(cache => cache
            .UseInMemory()
            .WithTelemetry(tracing: true, metrics: true), cacheName: "telemetry-on");

        (_withoutTelemetry, _silent) = CacheHostFactory.Create(cache => cache
            .UseInMemory()
            .WithTelemetry(tracing: false, metrics: false), cacheName: "telemetry-off");

        _instrumented.Set("hit", CacheHostFactory.Payload.Sample(1));
        _silent.Set("hit", CacheHostFactory.Payload.Sample(1));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _withTelemetry.Dispose();
        _withoutTelemetry.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Hit, telemetry disabled")]
    public async Task<CacheHostFactory.Payload?> TelemetryDisabled()
        => await _silent.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");

    [Benchmark(Description = "Hit, metrics enabled, no trace listener")]
    public async Task<CacheHostFactory.Payload?> MetricsEnabledNoListener()
        => await _instrumented.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");

    [Benchmark(Description = "Hit, trace listener attached")]
    public async Task<CacheHostFactory.Payload?> TracingWithListener()
    {
        EnsureListener();
        return await _instrumented.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");
    }

    [Benchmark(Description = "Miss, telemetry disabled")]
    public async Task<CacheHostFactory.Payload?> TelemetryDisabledColdKey()
        => await _silent.GetOrDefaultAsync<CacheHostFactory.Payload>($"cold-{Interlocked.Increment(ref _missCounter)}");

    [Benchmark(Description = "Miss, metrics enabled, no trace listener")]
    public async Task<CacheHostFactory.Payload?> MetricsEnabledNoListenerColdKey()
        => await _instrumented.GetOrDefaultAsync<CacheHostFactory.Payload>($"cold-{Interlocked.Increment(ref _missCounter)}");

    [Benchmark(Description = "Miss, trace listener attached")]
    public async Task<CacheHostFactory.Payload?> TracingWithListenerColdKey()
    {
        EnsureListener();
        return await _instrumented.GetOrDefaultAsync<CacheHostFactory.Payload>($"cold-{Interlocked.Increment(ref _missCounter)}");
    }

    private void EnsureListener()
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CacheTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }
}
