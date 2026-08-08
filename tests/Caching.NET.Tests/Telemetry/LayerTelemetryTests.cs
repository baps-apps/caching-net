using System.Diagnostics;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace Caching.NET.Tests.Telemetry;

[Collection(MetricsCollection.Name)]
public class LayerTelemetryTests
{
    [Fact]
    public async Task MemoryProbes_EmitLayerSpansAndDurations()
    {
        using var spans = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var metrics = new MetricCollector("caching.net.layer.duration");
        using var host = TestHost.BuildNamed("layer-spans", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await host.NamedCache("layer-spans").GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));
        await host.NamedCache("layer-spans").GetOrDefaultAsync<int>("k");

        Assert.Contains(spans.Activities, a => a.OperationName == "cache.memory.get");
        Assert.Contains(
            metrics.Measurements,
            m => m.Tags[CacheTelemetryAttributes.Layer] as string == CacheLayers.Memory
                 && m.Tags[CacheTelemetryAttributes.Name] as string == "layer-spans");
    }

    [Fact]
    public async Task TelemetryDisabled_InstallsNoDecorators()
    {
        using var spans = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildNamed("no-telemetry", c => c
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithTelemetry(tracing: false, metrics: false));

        await host.NamedCache("no-telemetry").GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));

        // A MeterListener/ActivityListener observes the whole process (see MetricsCollection's own
        // remarks): filtering to this test's own cache name is what makes the absence assertion
        // trustworthy rather than merely convenient, per the project convention that absence
        // assertions must filter by cache name.
        Assert.DoesNotContain(
            spans.Activities,
            a => Equals(a.GetTagItem(CacheTelemetryAttributes.Name), "no-telemetry")
                 && a.OperationName.StartsWith("cache.", StringComparison.Ordinal));
    }

    // --- Wrap gate coverage --------------------------------------------------------------
    //
    // TelemetryDisabled_InstallsNoDecorators above only proves that no span leaks through when
    // telemetry is off end to end; it cannot fail on a broken Wrap gate alone, because Task 9's
    // downstream TracingEnabled/MetricsEnabled checks inside CacheTelemetryContext independently
    // no-op regardless of what Wrap decides. Wrap's own gate is the only logic Task 10 adds over
    // pure forwarding, so it needs its own direct tests against the return value's identity.

    [Fact]
    public void MemoryCacheWrap_BothTelemetryFlagsOff_ReturnsTheInnerCacheUnchanged()
    {
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var telemetry = TelemetryContext(tracing: false, metrics: false);

        Assert.Same(inner, InstrumentedMemoryCache.Wrap(inner, telemetry));
    }

    [Fact]
    public void MemoryCacheWrap_TracingOnlyEnabled_WrapsTheInnerCache()
    {
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var telemetry = TelemetryContext(tracing: true, metrics: false);

        Assert.NotSame(inner, InstrumentedMemoryCache.Wrap(inner, telemetry));
    }

    [Fact]
    public void MemoryCacheWrap_MetricsOnlyEnabled_WrapsTheInnerCache()
    {
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var telemetry = TelemetryContext(tracing: false, metrics: true);

        Assert.NotSame(inner, InstrumentedMemoryCache.Wrap(inner, telemetry));
    }

    [Fact]
    public void DistributedCacheWrap_BothTelemetryFlagsOff_ReturnsTheInnerCacheUnchanged()
    {
        var inner = new NoOpDistributedCache();
        var telemetry = TelemetryContext(tracing: false, metrics: false);

        Assert.Same(inner, InstrumentedDistributedCache.Wrap(inner, telemetry));
    }

    [Fact]
    public void DistributedCacheWrap_TracingOnlyEnabled_WrapsTheInnerCache()
    {
        var inner = new NoOpDistributedCache();
        var telemetry = TelemetryContext(tracing: true, metrics: false);

        Assert.NotSame(inner, InstrumentedDistributedCache.Wrap(inner, telemetry));
    }

    [Fact]
    public void DistributedCacheWrap_MetricsOnlyEnabled_WrapsTheInnerCache()
    {
        var inner = new NoOpDistributedCache();
        var telemetry = TelemetryContext(tracing: false, metrics: true);

        Assert.NotSame(inner, InstrumentedDistributedCache.Wrap(inner, telemetry));
    }

    // --- Distributed-cache failure attribution --------------------------------------------
    //
    // InstrumentedDistributedCache is the sole emitter of cache.redis.* spans and the redis layer
    // of caching.net.layer.duration. Without a catch, an exception (most commonly a soft/hard
    // timeout — the slowest L2 operations) unwinds through the open activity leaving it Unset with
    // no error tag, and skips RecordLayer entirely, so the duration histogram would only ever see
    // the operations that happened to succeed.

    [Fact]
    public void DistributedGet_OnFailure_MarksTheSpanAndRecordsAnErrorDuration()
    {
        using var spans = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var metrics = new MetricCollector("caching.net.layer.duration");

        var telemetry = TelemetryContext(tracing: true, metrics: true, cacheName: "redis-get-fail");
        var wrapped = InstrumentedDistributedCache.Wrap(new ThrowingDistributedCache(), telemetry);

        Assert.Throws<InvalidOperationException>(() => wrapped.Get("k"));

        var span = Assert.Single(spans.Activities, a => a.OperationName == "cache.redis.get");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem(CacheTelemetryAttributes.ErrorType));

        Assert.Contains(
            metrics.Measurements,
            m => m.Tags[CacheTelemetryAttributes.Name] as string == "redis-get-fail"
                 && m.Tags[CacheTelemetryAttributes.Layer] as string == CacheLayers.Redis
                 && m.Tags[CacheTelemetryAttributes.Result] as string == CacheResults.Error);
    }

    [Fact]
    public void DistributedSet_OnFailure_MarksTheSpanAndRecordsAnErrorDuration()
    {
        using var spans = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var metrics = new MetricCollector("caching.net.layer.duration");

        var telemetry = TelemetryContext(tracing: true, metrics: true, cacheName: "redis-set-fail");
        var wrapped = InstrumentedDistributedCache.Wrap(new ThrowingDistributedCache(), telemetry);

        Assert.Throws<InvalidOperationException>(() => wrapped.Set("k", [1], new DistributedCacheEntryOptions()));

        var span = Assert.Single(spans.Activities, a => a.OperationName == "cache.redis.set");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem(CacheTelemetryAttributes.ErrorType));

        Assert.Contains(
            metrics.Measurements,
            m => m.Tags[CacheTelemetryAttributes.Name] as string == "redis-set-fail"
                 && m.Tags[CacheTelemetryAttributes.Layer] as string == CacheLayers.Redis
                 && m.Tags[CacheTelemetryAttributes.Result] as string == CacheResults.Error);
    }

    private static CacheTelemetryContext TelemetryContext(bool tracing, bool metrics, string cacheName = "default")
    {
        var options = new CachingOptions { CacheName = cacheName, ApplicationPrefix = "tests" };
        options.Observability.EnableTracing = tracing;
        options.Observability.EnableMetrics = metrics;
        return new CacheTelemetryContext(options);
    }

    /// <summary>A minimal <see cref="IDistributedCache"/> whose members are never invoked, used only
    /// to check <c>Wrap</c>'s return-value identity.</summary>
    private sealed class NoOpDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => Task.CompletedTask;

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
        }

        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }

    /// <summary>An <see cref="IDistributedCache"/> whose every member throws, used to prove the
    /// decorator marks the span and records an error duration instead of swallowing the failure.</summary>
    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("boom");

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException("boom");

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => throw new InvalidOperationException("boom");

        public void Refresh(string key) => throw new InvalidOperationException("boom");

        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");

        public void Remove(string key) => throw new InvalidOperationException("boom");

        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");
    }
}
