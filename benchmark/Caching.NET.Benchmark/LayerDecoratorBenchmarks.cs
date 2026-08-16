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
/// <remarks>
/// The <see cref="System.Diagnostics.ActivityListener"/> rows carry the same split as
/// <see cref="TelemetryOverheadBenchmarks"/> — probe under a caller's span versus probe with no
/// ambient parent — and this is the cleanest place to read it, since a probe here is not buried under
/// engine and adapter work.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class LayerDecoratorBenchmarks
{
    private MemoryCache _raw = null!;
    private IMemoryCache _noListener = null!;
    private IMemoryCache _withListener = null!;
    private IMemoryCache _traced = null!;
    private IMemoryCache _tracedAlways = null!;
    private MeterListener? _meterListener;
    private bool _probeResult;

    [GlobalSetup]
    public void Setup()
    {
        _raw = new MemoryCache(new MemoryCacheOptions());
        _raw.Set("hit", CacheHostFactory.Payload.Sample(1));

        _noListener = InstrumentedMemoryCache.Wrap(new MemoryCache(new MemoryCacheOptions()), BuildTelemetry());
        _noListener.Set("hit", CacheHostFactory.Payload.Sample(1));

        _withListener = InstrumentedMemoryCache.Wrap(new MemoryCache(new MemoryCacheOptions()), BuildTelemetry());
        _withListener.Set("hit", CacheHostFactory.Payload.Sample(1));

        _traced = InstrumentedMemoryCache.Wrap(new MemoryCache(new MemoryCacheOptions()), BuildTelemetry());
        _traced.Set("hit", CacheHostFactory.Payload.Sample(1));

        _tracedAlways = InstrumentedMemoryCache.Wrap(
            new MemoryCache(new MemoryCacheOptions()),
            BuildTelemetry(CacheLayerTracing.Always));
        _tracedAlways.Set("hit", CacheHostFactory.Payload.Sample(1));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _meterListener?.Dispose();
        TracingScope.Reset();
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

    /// <remarks>
    /// <see cref="TracingScope.Detached"/> rather than nothing at all: this row is the baseline the
    /// two <see cref="System.Diagnostics.ActivityListener"/> rows below are read against, so it has to pay the same
    /// ambient-context write they do. Otherwise the difference between the rows would include one
    /// arm's bookkeeping instead of being the span cost alone.
    /// </remarks>
    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, no trace listener")]
    public bool InstrumentedNoTraceListenerHit()
    {
        TracingScope.Detached();
        return _traced.TryGetValue("hit", out _);
    }

    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, ActivityListener attached, no parent span")]
    public bool InstrumentedTracedParentlessHit()
    {
        TracingScope.Parentless();
        return _traced.TryGetValue("hit", out _);
    }

    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, ActivityListener attached, under parent span")]
    public bool InstrumentedTracedParentedHit()
    {
        TracingScope.Parented();
        return _traced.TryGetValue("hit", out _);
    }

    /// <remarks>
    /// <para>
    /// The pair of rows that decides whether the default is worth shipping. Engine background work
    /// does not run with a blank ambient context — it runs with whatever flowed in from the request
    /// that scheduled it, which has usually finished — so this, not the parentless row, is the shape
    /// of the probes the default exists to suppress.
    /// </para>
    /// <para>
    /// Read the two against each other rather than against anything else in the table: both pay the
    /// same <see cref="ExecutionContext"/> restore (see <see cref="TracingScope"/>), so the difference
    /// between them is the span and nothing else. The <c>WhenParented</c> row should sit on the
    /// parentless number; if it sits on the parented one, the gate is not firing on the case it was
    /// written for.
    /// </para>
    /// </remarks>
    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, ActivityListener attached, ended parent span")]
    public bool InstrumentedTracedStaleParentHit()
    {
        TracingScope.RunUnderStaleParent(static state => ((LayerDecoratorBenchmarks)state!).ProbeTraced(), this);
        return _probeResult;
    }

    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, ActivityListener attached, ended parent span, LayerTracing=Always")]
    public bool InstrumentedTracedStaleParentAlwaysHit()
    {
        TracingScope.RunUnderStaleParent(static state => ((LayerDecoratorBenchmarks)state!).ProbeTracedAlways(), this);
        return _probeResult;
    }

    // Instance methods reached through a static callback and an object state, so neither row pays a
    // closure allocation that the rows it is compared against do not.
    private void ProbeTraced() => _probeResult = _traced.TryGetValue("hit", out _);

    private void ProbeTracedAlways() => _probeResult = _tracedAlways.TryGetValue("hit", out _);

    /// <remarks>
    /// <see cref="CacheLayerTracing.Always"/> is exactly the pre-3.1 behaviour, so this row and the
    /// parentless row above are the before and after of the same probe, measured in one run against
    /// one baseline rather than compared across two sessions on a machine whose absolute numbers
    /// swing (see docs/BENCHMARKS.md).
    /// </remarks>
    [Benchmark(Description = "InstrumentedMemoryCache.TryGetValue, ActivityListener attached, no parent span, LayerTracing=Always")]
    public bool InstrumentedTracedParentlessAlwaysHit()
    {
        TracingScope.Parentless();
        return _tracedAlways.TryGetValue("hit", out _);
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

    private static CacheTelemetryContext BuildTelemetry(
        CacheLayerTracing layerTracing = CacheLayerTracing.WhenParented)
        => new(new CachingOptions
        {
            CacheName = "bench-layer-decorator",
            ApplicationPrefix = "bench",
            Observability = { LayerTracing = layerTracing }
        });
}
