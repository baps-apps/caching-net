using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Fail-safe accounting. The engine raises both <c>FailSafeActivate</c> and a stale <c>Hit</c> for
/// one activation; counting the stale hit as a fail-safe activation reported every outage at twice
/// its real size, and dropped the read out of the hit/operation counters entirely.
/// </summary>
[Collection(MetricsCollection.Name)]
public class FailSafeMetricTests
{
    [Fact]
    public async Task OneFailSafeActivation_IncrementsTheFailSafeCounterExactlyOnce()
    {
        using var collector = new MetricCollector();
        using var host = TestHost.BuildNamed("fail-safe-count", cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithDefaultExpiration(TimeSpan.FromMinutes(5))
            .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromHours(1), throttleDuration: TimeSpan.FromMinutes(5)));

        var cache = host.NamedCache("fail-safe-count");
        await cache.SetAsync("Order:1", "fresh");
        await cache.ExpireAsync("Order:1");

        var served = await cache.GetOrSetAsync<string>("Order:1", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("source is down");
        });

        Assert.Equal("fresh", served);
        Assert.True(
            await collector.WaitForAsync(c => Count(c, "caching.net.fail_safe.served", "fail-safe-count") >= 1),
            "fail-safe activation was never recorded");

        // Give any second (incorrect) increment time to arrive on the engine's event pump before
        // asserting that exactly one was recorded.
        await Task.Delay(500);
        Assert.Equal(1, Count(collector, "caching.net.fail_safe.served", "fail-safe-count"));
    }

    [Fact]
    public async Task StaleRead_IsStillCountedAsAHitWithAStaleResult()
    {
        using var collector = new MetricCollector();
        using var host = TestHost.BuildNamed("fail-safe-result", cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithDefaultExpiration(TimeSpan.FromMinutes(5))
            .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromHours(1), throttleDuration: TimeSpan.FromMinutes(5)));

        var cache = host.NamedCache("fail-safe-result");
        await cache.SetAsync("Order:2", "fresh");
        await cache.ExpireAsync("Order:2");

        await cache.GetOrSetAsync<string>("Order:2", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("source is down");
        });

        Assert.True(
            await collector.WaitForAsync(c => c
                .For("caching.net.operations")
                .Any(m => Named(m, "fail-safe-result")
                    && Equals(m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Result), CacheResults.Stale))),
            "a stale read was not reported as cache.result=stale");
    }

    [Fact]
    public void MetricsDisabled_MeansTheEngineEventHubIsNeverSubscribed()
    {
        var options = new CachingOptions { CacheName = "no-metrics", ApplicationPrefix = "tests" };
        options.Observability.EnableMetrics = false;

        using var host = TestHost.BuildInMemory();

        // Subscribing makes the engine build event args and queue a dispatch per operation, which is
        // pure cost when nothing records. Attach must decline instead of installing no-op handlers.
        Assert.Null(CacheEventBridge.Attach(host.Cache(), new CacheTelemetryContext(options)));
    }

    private static bool Named(MetricCollector.Measurement measurement, string cacheName)
        => Equals(measurement.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name), cacheName);

    // Filtered by cache name: a MeterListener observes the whole process, so another cache's
    // measurements must not be able to satisfy or break this assertion.
    private static long Count(MetricCollector collector, string instrument, string cacheName)
        => collector.For(instrument).Where(m => Named(m, cacheName)).Sum(m => (long)m.Value);
}
