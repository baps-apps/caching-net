using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Pins the accounting of engine-side evictions, which used to inflate two counters the adapter owns.
/// </summary>
/// <remarks>
/// <para>
/// <c>CacheTelemetryContext.RecordInvalidation</c> wrote both <c>caching.net.invalidations</c> and
/// <c>caching.net.operations</c>, and <c>CacheEventBridge.OnEviction</c> routed through it. The
/// in-process memory layer raises an eviction for <c>Removed</c> and <c>Replaced</c> as well as for
/// expiry, so every removal and every overwrite was booked a second time on both counters — by a
/// second producer, which CLAUDE.md's one-producer-per-signal rule exists to forbid. Measured on
/// 5 sets + 5 removes of distinct keys: <c>operations</c> 15 (expected 10) and <c>invalidations</c>
/// 10 (expected 5).
/// </para>
/// <para>
/// Evictions now have their own instrument, <c>caching.net.evictions</c>: they are engine-initiated,
/// not caller-requested, so they belong on neither of the adapter's counters — and a dedicated
/// counter keeps the signal rather than dropping it.
/// </para>
/// <para>
/// In the <c>caching-net-metrics</c> collection because a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// observes the whole process; every assertion here filters by this test's own cache name.
/// </para>
/// </remarks>
[Collection(MetricsCollection.Name)]
public class EvictionAccountingTests
{
    private const int N = 5;

    [Fact]
    public async Task SetsAndRemoves_BookOneOperationEachAndNoEvictionOnTheOperationCounter()
    {
        const string CacheName = "eviction-accounting";
        using var collector = new MetricCollector(
            "caching.net.operations", "caching.net.invalidations", "caching.net.evictions");
        using var host = TestHost.BuildNamed(CacheName, c => c.UseInMemory().WithApplicationPrefix("tests"));
        var cache = host.NamedCache(CacheName);

        for (var i = 0; i < N; i++)
        {
            await cache.SetAsync($"d{i}", i);
        }

        for (var i = 0; i < N; i++)
        {
            await cache.RemoveAsync($"d{i}");
        }

        // The eviction events are dispatched on the engine's background pump, so wait for all N to
        // land before asserting the totals — otherwise a regression could pass by arriving late.
        await collector.WaitForAsync(c => Total(c, CacheName, "caching.net.evictions") >= N);

        // N sets + N removes are 2N logical calls, and nothing else may reach this counter.
        Assert.Equal(2 * N, Total(collector, CacheName, "caching.net.operations"));

        // N caller-requested invalidations. The evictions those removals caused are not invalidations
        // the application asked for, and must not be counted a second time here.
        Assert.Equal(N, Total(collector, CacheName, "caching.net.invalidations"));

        // The signal is kept, on its own instrument.
        Assert.Equal(N, Total(collector, CacheName, "caching.net.evictions"));
    }

    [Fact]
    public async Task OverwritingOneKey_BooksOneOperationPerWriteAndNoInvalidation()
    {
        const string CacheName = "eviction-accounting-overwrite";
        using var collector = new MetricCollector(
            "caching.net.operations", "caching.net.invalidations", "caching.net.evictions");
        using var host = TestHost.BuildNamed(CacheName, c => c.UseInMemory().WithApplicationPrefix("tests"));
        var cache = host.NamedCache(CacheName);

        for (var i = 0; i < N; i++)
        {
            await cache.SetAsync("k", i);
        }

        // Each overwrite replaces the previous entry, which the memory layer reports as an eviction:
        // N - 1 of them. This is the workload that read 9 operations for 5 sets.
        await collector.WaitForAsync(c => Total(c, CacheName, "caching.net.evictions") >= N - 1);

        Assert.Equal(N, Total(collector, CacheName, "caching.net.operations"));

        // Nobody asked for an invalidation here at all.
        Assert.Equal(0, Total(collector, CacheName, "caching.net.invalidations"));
    }

    private static long Total(MetricCollector collector, string cacheName, string instrument) => collector
        .For(instrument)
        .Where(m => m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name) as string == cacheName)
        .Sum(m => (long)m.Value);
}
