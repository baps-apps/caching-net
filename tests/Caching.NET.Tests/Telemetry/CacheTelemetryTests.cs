using System.Diagnostics;
using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;

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
        using var collector = new MetricCollector();
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.GetOrSetAsync<int>("telemetry:1", async _ => 1);
        await cache.GetOrDefaultAsync<int>("telemetry:1");
        await cache.GetOrDefaultAsync<int>("telemetry:absent");
        await cache.RemoveAsync("telemetry:1");

        Assert.True(
            await collector.WaitForAsync(c =>
                c.Total("caching.net.hits") >= 1
                && c.Total("caching.net.misses") >= 1
                && c.Total("caching.net.factory.executions") >= 1
                && c.Total("caching.net.invalidations") >= 1
                && c.Total("caching.net.operations") >= 1),
            "expected hit, miss, factory, invalidation and operation counters to be recorded");
    }

    [Fact]
    public async Task MetricDimensions_StayLowCardinalityAndCarryNoKeys()
    {
        using var collector = new MetricCollector();
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        for (var i = 0; i < 20; i++)
        {
            await cache.GetOrSetAsync<int>($"user:{i}:profile", async _ => i);
        }

        Assert.True(await collector.WaitForAsync(c => c.Measurements.Count > 0), "no measurements were recorded");

        var keys = collector.AllTagKeys();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(key, AllowedDimensions));

        var values = collector.AllTagValues();
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
        await cache.GetOrSetAsync<int>("no-metrics", async _ => 1);
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
