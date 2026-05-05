using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

public sealed class CacheInstrumentsTests
{
    [Fact]
    public void RecordHit_EmitsCounterWithModeAndOperationTags()
    {
        // Use a unique mode tag to avoid cross-test bleed from other tests that also emit
        // cache.hits via the shared static Meter.
        var uniqueMode = $"TestMode-{Guid.NewGuid():N}";
        using var listener = MeterListenerHelpers.ForCounterWithTags("cache.hits", out var observed);

        CacheInstruments.RecordHit(uniqueMode, "get_or_create");
        listener.Dispose();

        var match = observed.FirstOrDefault(o => (string?)o.tags.GetValueOrDefault("cache.mode") == uniqueMode);
        Assert.NotEqual(default, match);
        Assert.Equal(1, match.value);
        Assert.Equal("get_or_create", match.tags["cache.operation"]);
    }

    [Fact]
    public void RecordMiss_IncludesMissReasonTag()
    {
        using var listener = MeterListenerHelpers.ForCounterWithTags("cache.misses", out var observed);

        CacheInstruments.RecordMiss("InMemory", "get_or_create", "NotFound");

        listener.Dispose();

        var (_, tags) = Assert.Single(observed);
        Assert.Equal("NotFound", tags["cache.miss_reason"]);
    }
}
