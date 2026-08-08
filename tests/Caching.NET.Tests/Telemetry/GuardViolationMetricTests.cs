using Caching.NET;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// <c>caching.net.guard.violations</c> is what tells an operator that keys, tags or payloads are
/// breaking the configured limits — including under the <c>Warn</c> policy, where the operation
/// succeeds and the metric is the only durable signal. It is documented, so it needs a test that it
/// is actually emitted.
/// </summary>
[Collection(MetricsCollection.Name)]
public class GuardViolationMetricTests
{
    [Fact]
    public async Task KeyOverTheLengthLimit_IncrementsGuardViolations_UnderWarnPolicy()
    {
        using var collector = new MetricCollector();
        using var host = TestHost.BuildNamed("guard-warn", cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithMaximumKeyLength(64)
            .WithSecurity(s => s.KeyLengthPolicy = CacheGuardPolicy.Warn));

        // Warn lets the call through, so nothing throws and the metric is the whole signal.
        await host.NamedCache("guard-warn").SetAsync(new string('k', 200), 1);

        Assert.True(
            await collector.WaitForAsync(c => Count(c, "guard-warn", "key_too_long") >= 1),
            "an over-length key under the Warn policy must still be counted");
    }

    [Fact]
    public void RejectedTag_IncrementsGuardViolations()
    {
        using var collector = new MetricCollector();
        using var host = TestHost.BuildNamed("guard-tags", cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithSecurity(s =>
            {
                s.TagPolicy = CacheGuardPolicy.Warn;
                s.MaximumTagLength = 8;
            }));

        var guard = Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions
            .GetRequiredKeyedService<ICacheGuard>(host, "guard-tags");
        guard.ValidateTags(["this-tag-is-far-too-long"]);

        Assert.True(Count(collector, "guard-tags", "tag_rejected") >= 1, "a rejected tag must be counted");
    }

    [Fact]
    public async Task OversizedPayload_IncrementsGuardViolations()
    {
        using var collector = new MetricCollector();

        // No Redis is needed: the payload guard lives in the serializer, which is only reachable on
        // the distributed path, so this asserts through the serializer directly rather than
        // pretending an InMemory cache enforces a wire-format limit it never applies.
        var options = new CachingOptions
        {
            CacheName = "guard-payload",
            ApplicationPrefix = "tests",
            Serialization = { MaximumPayloadBytes = 64 }
        };

        var serializer = new InstrumentedCacheSerializer(
            new ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson.FusionCacheSystemTextJsonSerializer(),
            options.Serialization,
            options.CacheName,
            new CacheTelemetryContext(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var oversized = new string('x', 4096);
        Assert.Throws<InvalidOperationException>(() => _ = serializer.Serialize(oversized));

        Assert.True(
            await collector.WaitForAsync(c => Count(c, "guard-payload", "payload_too_large") >= 1),
            "an oversized payload must be counted as a guard violation");
    }

    private static long Count(MetricCollector collector, string cacheName, string violation)
        => collector
            .For("caching.net.guard.violations")
            .Where(m =>
                m.Tags.TryGetValue(CacheTelemetryAttributes.Name, out var name)
                && string.Equals(name?.ToString(), cacheName, StringComparison.Ordinal)
                && m.Tags.TryGetValue(CacheTelemetryAttributes.Operation, out var operation)
                && string.Equals(operation?.ToString(), violation, StringComparison.Ordinal))
            .Sum(m => (long)m.Value);
}
