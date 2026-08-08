using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Memory;

namespace Caching.NET.Benchmark;

/// <summary>
/// Isolates what <see cref="InstrumentedMemoryCache"/> costs over a raw <see cref="MemoryCache"/>
/// probe, independent of everything above it (FusionCache, <c>ICacheService</c>, the factory
/// pipeline). <see cref="InMemoryBenchmarks"/> and <see cref="TelemetryOverheadBenchmarks"/> both
/// measure end-to-end <c>GetOrSetAsync</c>/<c>GetOrDefaultAsync</c> calls, where a probe-level
/// decorator regression of a few nanoseconds is buried under ~100-300 ns of engine and adapter
/// overhead. This class exists because that regression happened once already: the decorator briefly
/// took two <see cref="System.Diagnostics.Stopwatch"/> timestamps and built a
/// <see cref="System.Diagnostics.TagList"/> on every probe even with nothing listening (measured at
/// the time as 16.4 ns raw vs 52.4 ns decorated, a ~3.2x hit-path regression), a fix hoisted a
/// per-instance config check plus a live <see cref="CacheTelemetry.LayerDuration"/>.Enabled check
/// (bringing it to 15.6 ns vs 17.5 ns, ~1.12x), and a later reviewer reverted the fix outright with
/// the unit suite staying byte-identical — nothing caught the loss because no benchmark measured the
/// probe in isolation. This is that benchmark.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class LayerDecoratorBenchmarks
{
    private MemoryCache _raw = null!;
    private IMemoryCache _noListener = null!;
    private IMemoryCache _withListener = null!;
    private MeterListener? _meterListener;

    [GlobalSetup]
    public void Setup()
    {
        _raw = new MemoryCache(new MemoryCacheOptions());
        _raw.Set("hit", CacheHostFactory.Payload.Sample(1));

        _noListener = InstrumentedMemoryCache.Wrap(new MemoryCache(new MemoryCacheOptions()), BuildTelemetry());
        _noListener.Set("hit", CacheHostFactory.Payload.Sample(1));

        _withListener = InstrumentedMemoryCache.Wrap(new MemoryCache(new MemoryCacheOptions()), BuildTelemetry());
        _withListener.Set("hit", CacheHostFactory.Payload.Sample(1));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _meterListener?.Dispose();
        _raw.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Raw MemoryCache.TryGetValue")]
    public bool RawHit() => _raw.TryGetValue("hit", out _);

    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, no listener")]
    public bool InstrumentedNoListenerHit() => _noListener.TryGetValue("hit", out _);

    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, MeterListener attached")]
    public bool InstrumentedWithListenerHit()
    {
        EnsureMeterListener();
        return _withListener.TryGetValue("hit", out _);
    }

    // Attached lazily, on the first invocation of the one benchmark method that wants it. BenchmarkDotNet
    // runs each [Benchmark] method of this class as its own out-of-process invocation, so this never
    // leaks into RawHit's or InstrumentedNoListenerHit's process — but it must not run from
    // [GlobalSetup], which executes unconditionally in every one of those processes regardless of
    // which single benchmark method that process is about to time.
    private void EnsureMeterListener()
    {
        if (_meterListener is not null)
        {
            return;
        }

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CacheTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
        listener.Start();
        _meterListener = listener;
    }

    private static CacheTelemetryContext BuildTelemetry()
        => new(new CachingOptions { CacheName = "bench-layer-decorator", ApplicationPrefix = "bench" });
}
