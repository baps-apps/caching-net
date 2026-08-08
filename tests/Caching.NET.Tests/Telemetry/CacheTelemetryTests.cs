using System.Diagnostics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

[Collection(MetricsCollection.Name)]
public class CacheTelemetryTests
{
    private static readonly string[] AllowedDimensions =
    [
        CacheTelemetryAttributes.System,
        CacheTelemetryAttributes.Mode,
        CacheTelemetryAttributes.Name,
        CacheTelemetryAttributes.Operation,
        CacheTelemetryAttributes.Result,
        CacheTelemetryAttributes.Layer,
        CacheTelemetryAttributes.ErrorType,
        CacheTelemetryAttributes.BackgroundOperation
    ];

    [Fact]
    public void InstrumentationNames_AreCachingBranded()
    {
        Assert.Equal("Caching.NET", CacheTelemetry.ActivitySourceName);
        Assert.Equal("Caching.NET", CacheTelemetry.MeterName);
        Assert.Equal("caching.net", CacheTelemetry.SystemName);
        Assert.Equal("Caching.NET", CacheTelemetry.ActivitySourceNames[0]);
        Assert.Equal("Caching.NET", CacheTelemetry.MeterNames[0]);
    }

    [Fact]
    public async Task CacheOperations_RecordCachingMetrics()
    {
        const string cacheName = "op-metrics";
        using var collector = new MetricCollector();
        using var host = TestHost.BuildNamed(cacheName, cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests"));

        // Through the adapter (not EngineCache()): caching.net.operations, caching.net.factory.executions
        // and caching.net.invalidations are now produced only by FusionCacheService, never by a raw
        // engine call that bypasses it. The cache is named and every total below is scoped to it, so
        // no other cache in the process can satisfy these counters on this one's behalf.
        var cache = host.NamedCache(cacheName);

        await cache.GetOrSetAsync<int>("telemetry:1", async (_, _) => 1);
        await cache.GetOrDefaultAsync<int>("telemetry:1");
        await cache.GetOrDefaultAsync<int>("telemetry:absent");
        await cache.RemoveAsync("telemetry:1");

        Assert.True(
            await collector.WaitForAsync(c =>
                c.Total("caching.net.hits", cacheName) >= 1
                && c.Total("caching.net.misses", cacheName) >= 1
                && c.Total("caching.net.factory.executions", cacheName) >= 1
                && c.Total("caching.net.invalidations", cacheName) >= 1
                && c.Total("caching.net.operations", cacheName) >= 1),
            "expected hit, miss, factory, invalidation and operation counters to be recorded");
    }

    [Fact]
    public async Task MetricDimensions_StayLowCardinalityAndCarryNoKeys()
    {
        const string cacheName = "dimension-probe";
        using var collector = new MetricCollector();
        using var host = TestHost.BuildNamed(cacheName, cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests"));
        var cache = host.NamedCache(cacheName);

        for (var i = 0; i < 20; i++)
        {
            var captured = i;
            await cache.GetOrSetAsync<int>($"user:{captured}:profile", async (_, _) => captured);
        }

        Assert.True(await collector.WaitForAsync(c => c.Own(cacheName).Count > 0), "no measurements were recorded");

        // Scoped to this cache: the 20 high-cardinality keys written above are this test's, so the
        // absence assertion has to look at this cache's own measurements to mean anything.
        var own = collector.Own(cacheName);

        var keys = collector.AllTagKeys(own);
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(key, AllowedDimensions));

        var values = collector.AllTagValues(own);
        Assert.DoesNotContain(values, v => v.Contains("user:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisablingMetrics_SuppressesEveryMeasurement()
    {
        using var collector = new MetricCollector();
        using var host = TestHost.BuildNamed("metrics-off", cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithTelemetry(tracing: false, metrics: false));

        var cache = host.NamedCache("metrics-off");
        await cache.GetOrSetAsync<int>("no-metrics", async (_, _) => 1);
        await cache.GetOrDefaultAsync<int>("no-metrics");
        await Task.Delay(200);

        // Other caches in the process may still be recording, so assert on this cache only.
        Assert.DoesNotContain(
            collector.Measurements,
            m => Equals(m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name), "metrics-off"));
    }

    [Fact]
    public void CacheNameDimension_CanBeDroppedForHighCacheCounts()
    {
        var options = new Options.CachingOptions
        {
            CacheName = "default",
            ApplicationPrefix = "tests"
        };
        options.Observability.IncludeCacheNameDimension = false;

        using var collector = new MetricCollector();
        var context = new CacheTelemetryContext(options);
        context.RecordMiss("unique-drop-name-probe");

        // This recorder is invoked directly, so no wait is needed.
        var probed = collector
            .Measurements
            .Where(m => Equals(m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Operation), "unique-drop-name-probe"))
            .ToArray();

        Assert.NotEmpty(probed);
        Assert.All(probed, m => Assert.False(m.Tags.ContainsKey(CacheTelemetryAttributes.Name)));
    }

    [Fact]
    public void NoListener_MeansNoActivityIsAllocated()
    {
        var options = new Options.CachingOptions { CacheName = "default", ApplicationPrefix = "tests" };
        var context = new CacheTelemetryContext(options);

        // Nothing is listening to the Caching.NET source in this test, so no span is created.
        Assert.Null(context.StartActivity("cache.get"));
        Assert.False(context.ShouldTrace);
    }

    [Fact]
    public void WithAListener_SpansCarryOnlySafeAttributes()
    {
        var options = new Options.CachingOptions { CacheName = "default", ApplicationPrefix = "tests" };
        var context = new CacheTelemetryContext(options);

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CacheTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = context.StartActivity("cache.get");

        Assert.NotNull(activity);
        Assert.Equal("cache.get", activity!.OperationName);
        Assert.Equal("caching.net", activity.GetTagItem(CacheTelemetryAttributes.System));
        Assert.Equal("InMemory", activity.GetTagItem(CacheTelemetryAttributes.Mode));
        Assert.Equal("default", activity.GetTagItem(CacheTelemetryAttributes.Name));
        Assert.Null(activity.GetTagItem(CacheTelemetryAttributes.KeyFingerprint));
    }

    [Fact]
    public void TracingDisabled_MeansNoSpanEvenWithAListener()
    {
        var options = new Options.CachingOptions { CacheName = "default", ApplicationPrefix = "tests" };
        options.Observability.EnableTracing = false;
        var context = new CacheTelemetryContext(options);

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CacheTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        Assert.Null(context.StartActivity("cache.get"));
    }
}
