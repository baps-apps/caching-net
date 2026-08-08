using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Guards against the same outcome being recorded by two producers into one meter. Fix round 1 gave
/// <c>caching.net.hits</c>/<c>caching.net.misses</c> and <c>caching.net.factory.executions</c> the
/// wrong owner (see the emitter table in <c>task-11-report.md</c>'s "Fix round 2" section); this
/// class was corrected alongside the production code in round 2, not just re-pointed at new numbers.
/// </summary>
[Collection(MetricsCollection.Name)]
public class NoDoubleCountingTests
{
    // Unreachable on purpose: abortConnect=false means the multiplexer never blocks trying to
    // connect, so a Hybrid cache built against it behaves exactly like an InMemory cache for every
    // assertion here — the point is Hybrid's *mode*, not a live Redis round trip.
    private const string UnreachableRedis = "127.0.0.1:1,abortConnect=false,connectTimeout=250";

    private static readonly string[] ReadOperations = ["get_or_set", "get_or_default", "try_get"];

    [Fact]
    public async Task OneColdGetOrSet_RecordsOneMissAndOneFactoryExecution()
    {
        using var collector = new MetricCollector(
            "caching.net.misses", "caching.net.hits", "caching.net.factory.executions", "caching.net.operations");
        using var host = TestHost.BuildNamed("count-once", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await host.NamedCache("count-once").GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));

        // caching.net.factory.executions is now recorded by CacheEventBridge off the engine's
        // background event pump (see round 2's Critical 2), so wait for it before asserting totals.
        await collector.WaitForAsync(c => Total(Mine(c), "caching.net.factory.executions") >= 1);

        var mine = Mine(collector);

        // caching.net.hits/misses are logical-read counters owned by FusionCacheService (round 2's
        // Critical 1): exactly one record per call, regardless of how many times the engine probes
        // the physical memory layer underneath. A cold GetOrSetAsync is exactly one miss, matching
        // the brief's original expectation — round 1's "2" was correcting for a decorator-level,
        // per-probe implementation that round 2 removed.
        Assert.Equal(1, Total(mine, "caching.net.factory.executions"));
        Assert.Equal(1, Total(mine, "caching.net.misses"));
        Assert.Equal(0, Total(mine, "caching.net.hits"));

        static MetricCollector.Measurement[] Mine(MetricCollector c) => c.Measurements
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "count-once")
            .ToArray();

        static long Total(IEnumerable<MetricCollector.Measurement> measurements, string instrument)
            => measurements.Where(m => m.Instrument == instrument).Sum(m => (long)m.Value);
    }

    /// <summary>
    /// Task 14b's context-free <c>GetOrSetAsync</c> overload delegates to the context-taking one
    /// (see <see cref="Internal.FusionCacheService"/>) specifically so it goes through the exact same
    /// guards, spans and counters — this is the same invariant as
    /// <see cref="OneColdGetOrSet_RecordsOneMissAndOneFactoryExecution"/>, reproduced through the new
    /// overload to prove the one-producer-per-signal rule holds there too.
    /// </summary>
    [Fact]
    public async Task OneColdContextFreeGetOrSet_RecordsOneMissAndOneOperation()
    {
        using var collector = new MetricCollector(
            "caching.net.misses", "caching.net.hits", "caching.net.factory.executions", "caching.net.operations");
        using var host = TestHost.BuildNamed("count-once-context-free", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await host.NamedCache("count-once-context-free").GetOrSetAsync("k", async _ => await Task.FromResult(1));

        await collector.WaitForAsync(c => Total(Mine(c), "caching.net.factory.executions") >= 1);

        var mine = Mine(collector);
        var operations = mine
            .Where(m => m.Instrument == "caching.net.operations"
                && m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Operation) as string == "get_or_set")
            .Sum(m => (long)m.Value);

        Assert.Equal(1, Total(mine, "caching.net.misses"));
        Assert.Equal(0, Total(mine, "caching.net.hits"));
        Assert.Equal(1, operations);

        static MetricCollector.Measurement[] Mine(MetricCollector c) => c.Measurements
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "count-once-context-free")
            .ToArray();

        static long Total(IEnumerable<MetricCollector.Measurement> measurements, string instrument)
            => measurements.Where(m => m.Instrument == instrument).Sum(m => (long)m.Value);
    }

    [Fact]
    public async Task OneWarmRead_RecordsOneHit()
    {
        using var collector = new MetricCollector("caching.net.hits");
        using var host = TestHost.BuildNamed("count-hit", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await host.NamedCache("count-hit").SetAsync("k", 1);
        await host.NamedCache("count-hit").GetOrDefaultAsync<int>("k");

        var hits = collector.Measurements
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "count-hit")
            .Sum(m => m.Value);

        Assert.Equal(1, hits);
    }

    /// <summary>
    /// The invariant round 1's suite never asserted, which is exactly why the wrong producer slipped
    /// through a green bar: for any sequence of calls, every logical read (<c>get_or_set</c>,
    /// <c>get_or_default</c>, <c>try_get</c>) contributes exactly one hit-or-miss, and nothing else
    /// does. Reproduces the reviewer's probe: one write, two reads that hit, one read that misses.
    /// </summary>
    [Fact]
    public async Task MixedReadWriteSequence_HitsPlusMissesReconcileWithReadOperations()
    {
        using var collector = new MetricCollector("caching.net.hits", "caching.net.misses", "caching.net.operations");
        using var host = TestHost.BuildNamed("reconcile", c => c.UseInMemory().WithApplicationPrefix("tests"));
        var cache = host.NamedCache("reconcile");

        await cache.SetAsync("k", 1);                        // write — not a read
        await cache.GetOrDefaultAsync<int>("k");              // read: hit
        await cache.GetOrDefaultAsync<int>("absent");         // read: miss
        await cache.TryGetAsync<int>("k");                    // read: hit

        var mine = Mine(collector);
        var hits = Total(mine, "caching.net.hits");
        var misses = Total(mine, "caching.net.misses");
        var readOperations = mine
            .Where(m => m.Instrument == "caching.net.operations"
                && ReadOperations.Contains(m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Operation) as string))
            .Sum(m => (long)m.Value);

        // The true ratio for this sequence is 2 hits / 1 miss out of 3 reads (66.7%) — this is the
        // exact scenario the round-1 decorator-based implementation reported as 2 hits / 5 misses
        // (28.6%) because writes also emit internal engine-level "get" probes the decorators observed.
        Assert.Equal(2, hits);
        Assert.Equal(1, misses);
        Assert.Equal(3, readOperations);
        Assert.Equal(hits + misses, readOperations);

        static MetricCollector.Measurement[] Mine(MetricCollector c) => c.Measurements
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "reconcile")
            .ToArray();

        static long Total(IEnumerable<MetricCollector.Measurement> measurements, string instrument)
            => measurements.Where(m => m.Instrument == instrument).Sum(m => (long)m.Value);
    }

    [Fact]
    public async Task HybridHit_CarriesNoLayerTag()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildNamed("layer-hybrid", c => c
            .UseHybrid(UnreachableRedis, enableBackplane: false)
            .WithApplicationPrefix("tests"));

        var cache = host.NamedCache("layer-hybrid");
        await cache.SetAsync("hybrid-key", 1);
        await cache.GetOrSetAsync<int>("hybrid-key", (_, _) => Task.FromResult(2));

        var span = Assert.Single(
            OwnSpans(recorder, "layer-hybrid"),
            a => a.OperationName == "cache.get_or_set" && Equals(a.GetTagItem(CacheTelemetryAttributes.Result), CacheResults.Hit));
        Assert.Null(span.GetTagItem(CacheTelemetryAttributes.Layer));
    }

    [Fact]
    public async Task InMemoryHit_StillCarriesMemoryLayerTag()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildNamed("layer-memory", c => c.UseInMemory().WithApplicationPrefix("tests"));

        var cache = host.NamedCache("layer-memory");
        await cache.SetAsync("mem-key", 1);
        await cache.GetOrSetAsync<int>("mem-key", (_, _) => Task.FromResult(2));

        var span = Assert.Single(
            OwnSpans(recorder, "layer-memory"),
            a => a.OperationName == "cache.get_or_set" && Equals(a.GetTagItem(CacheTelemetryAttributes.Result), CacheResults.Hit));
        Assert.Equal(CacheLayers.Memory, span.GetTagItem(CacheTelemetryAttributes.Layer));
    }

    private static System.Diagnostics.Activity[] OwnSpans(SpanRecorder recorder, string cacheName) => recorder.Activities
        .Where(a => Equals(a.GetTagItem(CacheTelemetryAttributes.Name), cacheName))
        .ToArray();

    /// <summary>
    /// <c>CacheEventBridge.OnFactoryError</c> is (again, per round 2's Critical 2) the sole producer
    /// of <c>caching.net.errors{layer=factory,error.type=FactoryError}</c>: the adapter's own factory
    /// wrapper cannot record it without double-counting every eager-refresh cycle (see the bridge's
    /// remarks), so it only times the delegate now. With no fail-safe and no stale value to fall back
    /// to, the factory's exception propagates to the caller, so this also exercises the outer
    /// <c>catch</c> that marks the operation span failed.
    /// </summary>
    [Fact]
    public async Task FactoryFailureWithNoFailSafe_RecordsExactlyOneFactoryError()
    {
        using var collector = new MetricCollector("caching.net.errors");
        using var host = TestHost.BuildNamed("count-factory-error", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.NamedCache("count-factory-error").GetOrSetAsync<int>(
                "k",
                (_, _) => throw new InvalidOperationException("factory is down")).AsTask());

        // Dispatched off the engine's background event pump, not synchronously from the catch block.
        await collector.WaitForAsync(c => Mine(c).Length >= 1);

        var mine = Mine(collector);
        Assert.Single(mine);
        Assert.Equal("FactoryError", mine[0].Tags.GetValueOrDefault(CacheTelemetryAttributes.ErrorType));

        MetricCollector.Measurement[] Mine(MetricCollector c) => c.Measurements
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "count-factory-error")
            .ToArray();
    }

    /// <summary>
    /// Fix round 3, N1: FusionCache maintains its own tag bookkeeping (per-tag and process-wide clear
    /// markers) through internal <c>GetOrSetAsync&lt;long&gt;</c> calls keyed under
    /// <c>FusionCacheInternalStrings.DefaultTagCacheKeyPrefix</c> — real factory executions, just the
    /// engine's, not the application's. <see cref="Internal.CacheEventBridge"/>'s four factory
    /// handlers must not count them: a cache that never runs an application factory (only
    /// <c>SetAsync</c> + <c>GetOrDefaultAsync</c>, neither of which takes a factory) must record
    /// exactly zero <c>caching.net.factory.executions</c>, not the 2 it recorded before this filter
    /// (measured — see task-11-report.md's "Fix round 3" section for the exact before/after numbers).
    /// </summary>
    [Fact]
    public async Task NoApplicationFactory_RecordsZeroFactoryExecutions()
    {
        using var collector = new MetricCollector("caching.net.factory.executions");
        using var host = TestHost.BuildNamed("no-app-factory", c => c.UseInMemory().WithApplicationPrefix("tests"));
        var cache = host.NamedCache("no-app-factory");

        await cache.SetAsync("k", 1);
        await cache.GetOrDefaultAsync<int>("k");

        // No factory-executions record is ever expected here, so there is nothing to WaitForAsync on;
        // give the engine's background event pump a window to deliver anything it might (wrongly)
        // have queued, then assert the negative.
        await Task.Delay(500);

        var mine = collector.Measurements
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "no-app-factory")
            .ToArray();

        Assert.Empty(mine);
    }

    /// <summary>
    /// Fix round 3, N2: nothing in the test suite exercised <c>OnBackgroundFactorySuccess</c>,
    /// <c>OnBackgroundFactoryError</c> or <c>OnEagerRefresh</c> before this test — Critical 2's entire
    /// justification ("only the bridge can tell background from foreground") had zero automated
    /// coverage, so deleting the bridge's background subscription broke nothing. Asserts only the
    /// background bucket (stable — see N1 above for why the foreground bucket used to carry
    /// engine-internal noise) after one eager-refresh cycle: the background completion must be counted
    /// by the bridge's <c>caching.net.factory.executions{background=true}</c> and must agree with
    /// <c>caching.net.background.operations{operation=eager_refresh}</c>, since both instruments
    /// describe the same one completion from two angles.
    /// </summary>
    [Fact]
    public async Task EagerRefreshCycle_RecordsExactlyOneBackgroundFactoryExecution()
    {
        using var collector = new MetricCollector("caching.net.factory.executions", "caching.net.background.operations");
        using var host = TestHost.BuildNamed("eager-refresh-bg", c => c
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithDefaultExpiration(TimeSpan.FromSeconds(2))
            .WithJitter(TimeSpan.Zero)
            .WithEagerRefresh(0.1f));

        var cache = host.NamedCache("eager-refresh-bg");

        // First read: cold, populates the entry. Second read, after the eager-refresh threshold
        // (10% of 2s = 200ms) but before the entry actually expires: served from cache immediately,
        // and triggers exactly one background refresh cycle.
        await cache.GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));
        await Task.Delay(400);
        await cache.GetOrSetAsync<int>("k", (_, _) => Task.FromResult(2));

        await collector.WaitForAsync(
            c => BackgroundFactoryExecutions(c) >= 1 && EagerRefreshOperations(c) >= 1,
            timeoutMilliseconds: 3000);

        Assert.Equal(1, BackgroundFactoryExecutions(collector));
        Assert.Equal(1, EagerRefreshOperations(collector));
        Assert.Equal(BackgroundFactoryExecutions(collector), EagerRefreshOperations(collector));

        static long BackgroundFactoryExecutions(MetricCollector c) => c
            .For("caching.net.factory.executions")
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "eager-refresh-bg"
                && Equals(m.Tags.GetValueOrDefault(CacheTelemetryAttributes.BackgroundOperation), true))
            .Sum(m => (long)m.Value);

        static long EagerRefreshOperations(MetricCollector c) => c
            .For("caching.net.background.operations")
            .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == "eager-refresh-bg"
                && Equals(m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Operation), "eager_refresh"))
            .Sum(m => (long)m.Value);
    }
}
