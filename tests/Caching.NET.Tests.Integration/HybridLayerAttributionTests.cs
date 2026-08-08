using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Telemetry;
using Caching.NET.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Pins that a Hybrid hit served by L2 is attributed to the layer that actually answered it.
/// </summary>
/// <remarks>
/// <para>
/// The layer IS observable: <c>FusionCacheMemoryEventsHub</c> and
/// <c>FusionCacheDistributedEventsHub</c> both inherit <c>Hit</c>/<c>Miss</c>/<c>Set</c>/<c>Remove</c>
/// from <c>FusionCacheCommonEventsHub</c>, so subscribing to <c>events.Memory.Hit</c> and
/// <c>events.Distributed.Hit</c> separately tells you which level answered — confirmed empirically
/// with a two-instance Hybrid probe over a shared L2: <c>MEM Miss | DIST Hit stale=False | … | TOP
/// Hit</c>. What is <em>not</em> observable is the level on the single top-level <c>Hit</c> event
/// <c>CacheEventBridge</c> used to subscribe to (<c>FusionCacheEntryHitEventArgs</c> carries only
/// <c>Key</c> and <c>IsStale</c>) — which no longer matters for that specific subscription, since a
/// later task (one producer per telemetry signal) deleted the whole <c>Hit</c>/<c>Miss</c> bridge in
/// favour of the layer decorators, which always see the physical layer they wrap and are never
/// wrong. What remained genuinely unresolvable from the top-level event was the layer tag on
/// <c>FusionCacheService</c>'s own <c>cache.get_or_set</c> operation span: that method now reports no
/// <c>cache.layer</c> at all for a Hybrid hit (see <c>FusionCacheService.ResolveHitLayer</c>) rather
/// than guess — this test's own call path is <c>GetOrDefaultAsync</c>, which never tagged
/// <c>cache.layer</c> on its span in the first place, so it is unaffected either way.
/// </para>
/// <para>
/// Subscribing to the per-level hubs instead of the top-level one was never a drop-in fix, either:
/// the same probe that confirmed the level hubs carry the answer also showed they fire for
/// FusionCache's own internal tag/clear-marker lookups, not only for the caller's logical read — one
/// logical <c>GetOrDefaultAsync</c> produced two extra <c>MEM Miss</c> and two extra <c>DIST Miss</c>
/// alongside the one hit that mattered. The layer decorators inherit the same multiplicity for
/// <c>caching.net.hits</c>/<c>caching.net.misses</c> (they count physical probes, not logical reads),
/// which is exactly why this test asserts on <c>caching.net.layer.duration</c> instead — see below.
/// </para>
/// <para>
/// <c>caching.net.layer.duration</c> is the signal this task's own decorators give correct,
/// per-layer attribution to (via <c>cache.redis.get</c>/<c>cache.memory.get</c>), so it is what this
/// test asserts on instead of <c>caching.net.hits</c>.
/// </para>
/// </remarks>
[Collection(RedisCollection.Name)]
public class HybridLayerAttributionTests
{
    private const string CacheName = "hybrid-layer-attr";

    private readonly RedisFixture _redis;

    public HybridLayerAttributionTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private (ServiceProvider Provider, ICacheService Cache) Host(string prefix)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddCaching(CacheName, cache => cache
            .UseHybrid(_redis.ConnectionString, enableBackplane: false)
            .WithApplicationPrefix(prefix)
            .WithJitter(TimeSpan.Zero)
            .WithDefaultExpiration(TimeSpan.FromMinutes(5))
            // The write must complete before the second host's read can observe it.
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        return (provider, provider.GetRequiredKeyedService<ICacheService>(CacheName));
    }

    [Fact]
    public async Task HybridHitServedByRedis_IsAttributedToRedis()
    {
        using var layerDurations = new LayerDurationCollector(CacheName);

        var (writerProvider, cacheA) = Host("hybrid-layer-attr");
        var (readerProvider, cacheB) = Host("hybrid-layer-attr");
        using var writer = writerProvider;
        using var reader = readerProvider;

        await cacheA.SetAsync("Order:1", 42);

        // Reader's L1 has never seen this key: an L1 miss followed by an L2 hit.
        var value = await cacheB.GetOrDefaultAsync<int>("Order:1");
        Assert.Equal(42, value);

        var observed = await layerDurations.WaitForAnyAsync(
            m => m.Layer == CacheLayers.Redis && m.Operation == "get" && m.Result == CacheResults.Hit,
            TimeSpan.FromSeconds(10));

        Assert.True(
            observed,
            "no caching.net.layer.duration measurement recorded cache.layer=redis for the L2-served hit "
                + $"(collected: {string.Join(", ", layerDurations.Measurements.Select(m => $"[{m.Layer}/{m.Operation}/{m.Result}]"))})");
    }

    /// <summary>
    /// Collects <c>caching.net.layer.duration</c> measurements carrying <c>cache.name</c> equal to
    /// the given cache name, so the test can assert on the
    /// <c>cache.layer</c>/<c>cache.operation</c>/<c>cache.result</c> tags the layer decorators
    /// record.
    /// </summary>
    /// <remarks>
    /// Filtered by cache name rather than relying on <see cref="RedisCollection"/>'s sequential
    /// execution alone: a <c>MeterListener</c> observes the whole process, and the project
    /// convention (see <c>CLAUDE.md</c> and <c>OperationSpanTests</c>) is that an absence — or, as
    /// here, a specific-presence — assertion must filter by cache name rather than trust collection
    /// ordering not to change.
    /// </remarks>
    private sealed class LayerDurationCollector : IDisposable
    {
        private readonly string _cacheName;
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private readonly List<Measurement> _measurements = [];
        private readonly object _gate = new();

        public LayerDurationCollector(string cacheName)
        {
            _cacheName = cacheName;

            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CacheTelemetry.MeterName
                    && instrument.Name == "caching.net.layer.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                string? name = null;
                string? layer = null;
                string? operation = null;
                string? result = null;

                foreach (var tag in tags)
                {
                    switch (tag.Key)
                    {
                        case CacheTelemetryAttributes.Name:
                            name = tag.Value as string;
                            break;
                        case CacheTelemetryAttributes.Layer:
                            layer = tag.Value as string;
                            break;
                        case CacheTelemetryAttributes.Operation:
                            operation = tag.Value as string;
                            break;
                        case CacheTelemetryAttributes.Result:
                            result = tag.Value as string;
                            break;
                        default:
                            break;
                    }
                }

                if (!string.Equals(name, _cacheName, StringComparison.Ordinal))
                {
                    return;
                }

                lock (_gate)
                {
                    _measurements.Add(new Measurement(layer, operation, result));
                }
            });

            _listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (_gate)
                {
                    return _measurements.ToArray();
                }
            }
        }

        public async Task<bool> WaitForAnyAsync(Func<Measurement, bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Measurements.Any(predicate))
                {
                    return true;
                }

                await Task.Delay(25);
            }

            return Measurements.Any(predicate);
        }

        public void Dispose() => _listener.Dispose();

        internal sealed record Measurement(string? Layer, string? Operation, string? Result);
    }
}
