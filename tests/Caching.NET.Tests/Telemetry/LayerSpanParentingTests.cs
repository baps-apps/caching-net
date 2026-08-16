using System.Diagnostics;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Memory;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Whether a layer probe produces a span depends on whether it runs under one.
/// </summary>
/// <remarks>
/// <para>
/// A probe issued from a cache verb is always parented, because Caching.NET's own operation span
/// (<c>cache.get_or_set</c> and friends) is above it — that holds whether or not the application has
/// a request span of its own, which is why the caller path is unaffected by this setting and is
/// asserted here to stay that way.
/// </para>
/// <para>
/// The probes that arrive with no parent are the ones the engine issues on its own: evicting an
/// entry on a backplane subscriber thread, writing an entry after a background factory completes.
/// Those became one root trace per probe — a sub-millisecond span, alone, with nothing saying what
/// caused it. Those are what <see cref="CacheLayerTracing.WhenParented"/> drops, and the decorator is
/// where that can be tested honestly, since routing through a cache verb would supply the very
/// parent whose absence is the subject.
/// </para>
/// </remarks>
[Collection(MetricsCollection.Name)]
public class LayerSpanParentingTests
{
    private const string CallerSourceName = "Caching.NET.Tests.Caller";

    private static readonly ActivitySource CallerSource = new(CallerSourceName);

    private static IMemoryCache Probe(string cacheName, CacheLayerTracing layerTracing)
        => InstrumentedMemoryCache.Wrap(
            new MemoryCache(new MemoryCacheOptions()),
            new CacheTelemetryContext(new CachingOptions
            {
                CacheName = cacheName,
                ApplicationPrefix = "tests",
                Observability = { LayerTracing = layerTracing }
            }));

    private static Activity[] LayerSpans(SpanRecorder recorder, string cacheName) => recorder.Activities
        .Where(a => Equals(a.GetTagItem(CacheTelemetryAttributes.Name), cacheName))
        .Where(a => a.OperationName.StartsWith("cache.memory.", StringComparison.Ordinal))
        .ToArray();

    /// <summary>
    /// Cleared rather than assumed empty: xUnit may leave whatever ran before on this thread as the
    /// ambient activity, which would silently turn a parentless case into a parented one.
    /// </summary>
    private static void ProbeWithNoParent(IMemoryCache cache)
    {
        Activity.Current = null;
        cache.TryGetValue("Order:42", out _);
    }

    private static void ProbeUnderParent(IMemoryCache cache)
    {
        using var caller = CallerSource.StartActivity("caller.request");
        cache.TryGetValue("Order:42", out _);
    }

    [Fact]
    public void Default_ParentlessProbe_EmitsNoSpan()
    {
        const string CacheName = "layer-span-default-parentless";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);

        ProbeWithNoParent(Probe(CacheName, CacheLayerTracing.WhenParented));

        Assert.Empty(LayerSpans(recorder, CacheName));
    }

    [Fact]
    public void Default_ParentedProbe_EmitsASpanUnderTheCaller()
    {
        const string CacheName = "layer-span-default-parented";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);

        ProbeUnderParent(Probe(CacheName, CacheLayerTracing.WhenParented));

        var span = Assert.Single(LayerSpans(recorder, CacheName));
        Assert.Equal("cache.memory.get", span.OperationName);
        Assert.NotNull(span.ParentId);
    }

    /// <summary>
    /// The case a plain <c>Activity.Current is null</c> check gets wrong, and the reason the default is
    /// worth anything at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probes this setting exists to suppress are issued from work the engine schedules onto its
    /// own threads — a write after a background factory, an eviction on a subscriber thread. That work
    /// does not start from a blank context: ambient state flows with the <see cref="ExecutionContext"/>
    /// captured when the work was scheduled, so <see cref="Activity.Current"/> there is usually the
    /// span of the request that triggered it — which finished long before the probe ran.
    /// </para>
    /// <para>
    /// Counting that as a parent would suppress almost nothing while attaching each probe to an
    /// unrelated request as a child that starts after its parent ended. So a stopped ambient span
    /// counts as no parent.
    /// </para>
    /// <para>
    /// The setup has to go through a captured <see cref="ExecutionContext"/>, and that is not
    /// ceremony. <c>Activity.Current = someStoppedActivity</c> does not reproduce this: the property
    /// setter rejects a finished activity and leaves <c>Current</c> null, so a test written that way
    /// passes without ever exercising the branch it names — it asserts the parentless case twice.
    /// Context <i>restore</i> applies no such check, which is precisely why a finished span reaches
    /// background work at all. <c>Task.Run</c> flows it identically; this is the same mechanism with
    /// the scheduling removed.
    /// </para>
    /// </remarks>
    [Fact]
    public void EndedAmbientSpan_CountsAsNoParent()
    {
        const string CacheName = "layer-span-ended-parent";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);
        var cache = Probe(CacheName, CacheLayerTracing.WhenParented);

        RunUnderEndedSpan(() =>
        {
            Assert.True(Activity.Current?.IsStopped, "the ambient span must be present and ended");
            cache.TryGetValue("Order:42", out _);
        });

        Assert.Empty(LayerSpans(recorder, CacheName));
    }

    /// <summary>
    /// The counterpart: the same ended ambient span under <see cref="CacheLayerTracing.Always"/> does
    /// emit. Without this, the test above could keep passing for a reason that has nothing to do with
    /// parenting — a probe that stopped emitting spans entirely would satisfy it.
    /// </summary>
    [Fact]
    public void EndedAmbientSpan_UnderAlways_StillEmits()
    {
        const string CacheName = "layer-span-ended-parent-always";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);
        var cache = Probe(CacheName, CacheLayerTracing.Always);

        RunUnderEndedSpan(() => cache.TryGetValue("Order:42", out _));

        Assert.Single(LayerSpans(recorder, CacheName));
    }

    /// <summary>
    /// Captures the ambient context while a span is live, ends the span, then runs <paramref name="probe"/>
    /// on that restored context — the shape every engine-scheduled callback arrives in.
    /// </summary>
    private static void RunUnderEndedSpan(Action probe)
    {
        Activity.Current = null;

        var caller = CallerSource.StartActivity("caller.request");
        Assert.NotNull(caller);

        var captured = ExecutionContext.Capture();
        Assert.NotNull(captured);

        caller.Stop();
        Activity.Current = null;

        ExecutionContext.Run(captured, _ => probe(), null);
    }

    [Fact]
    public void Always_ParentlessProbe_EmitsASpan()
    {
        const string CacheName = "layer-span-always";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);

        ProbeWithNoParent(Probe(CacheName, CacheLayerTracing.Always));

        Assert.Single(LayerSpans(recorder, CacheName));
    }

    [Fact]
    public void Never_ParentedProbe_EmitsNoSpan()
    {
        const string CacheName = "layer-span-never";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);

        ProbeUnderParent(Probe(CacheName, CacheLayerTracing.Never));

        Assert.Empty(LayerSpans(recorder, CacheName));
    }

    /// <summary>
    /// The reason suppression is a reduction in noise rather than in measurement: the probe whose
    /// span was dropped is still timed on <c>caching.net.layer.duration</c>.
    /// </summary>
    [Fact]
    public void SuppressedProbe_StillRecordsLayerDuration()
    {
        const string CacheName = "layer-span-metrics-intact";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);
        using var metrics = new MetricCollector("caching.net.layer.duration");

        ProbeWithNoParent(Probe(CacheName, CacheLayerTracing.WhenParented));

        Assert.Empty(LayerSpans(recorder, CacheName));
        Assert.Contains(metrics.Own(CacheName), m => m.Instrument == "caching.net.layer.duration");
    }

    /// <summary>
    /// The caller path is untouched by the default. A cache verb starts its own operation span, so
    /// every probe underneath it is parented — with or without an application request span above.
    /// This is the assertion that would fail if the gate were ever widened to operation spans.
    /// </summary>
    [Fact]
    public async Task Default_CacheVerbWithNoRequestSpan_StillTracesItsLayerProbes()
    {
        const string CacheName = "layer-span-caller-path";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, CallerSourceName);
        using var host = TestHost.BuildNamed(CacheName, cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests"));

        Activity.Current = null;
        await host.NamedCache(CacheName).GetOrSetAsync<int>("Order:42", (_, _) => Task.FromResult(1));

        Assert.NotEmpty(LayerSpans(recorder, CacheName));
        Assert.All(LayerSpans(recorder, CacheName), span => Assert.NotNull(span.ParentId));
    }
}
