using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

public sealed class CacheInstrumentsTests
{
    [Fact]
    public void RecordHit_EmitsCounterWithModeAndOperationTags()
    {
        using var listener = MeterListenerHelpers.ForCounterWithTags("cache.hits", out var observed);

        CacheInstruments.RecordHit("Redis", "get_or_create");

        listener.RecordObservableInstruments();
        listener.Dispose();

        var (value, tags) = Assert.Single(observed);
        Assert.Equal(1, value);
        Assert.Equal("Redis", tags["cache.mode"]);
        Assert.Equal("get_or_create", tags["cache.operation"]);
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
