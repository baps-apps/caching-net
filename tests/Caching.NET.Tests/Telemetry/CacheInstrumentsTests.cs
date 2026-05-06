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

    [Fact]
    public void RecordEviction_emits_evictions_counter_with_reason_tag()
    {
        var modeTag = $"unit-evict-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.evictions", modeTag);
        using var _ = listener;

        CacheInstruments.RecordEviction(modeTag, "Capacity");

        Assert.Single(values);
        Assert.Equal(1L, values[0].value);
        Assert.Contains(values[0].tags, t => t.Key == "cache.eviction_reason" && (string?)t.Value == "Capacity");
    }

    [Fact]
    public void RecordStaleServed_emits_stale_served_counter()
    {
        var modeTag = $"unit-stale-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.stale_served", modeTag);
        using var _ = listener;

        CacheInstruments.RecordStaleServed(modeTag, "get_or_create");

        Assert.Single(values);
        Assert.Contains(values[0].tags, t => t.Key == "cache.operation" && (string?)t.Value == "get_or_create");
    }

    [Fact]
    public void RecordCircuitStateChange_emits_counter_with_state_tag()
    {
        var modeTag = $"unit-cb-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.circuit_state_changes", modeTag);
        using var _ = listener;

        CacheInstruments.RecordCircuitStateChange(modeTag, "cache.redis.read", "open");

        Assert.Single(values);
        Assert.Contains(values[0].tags, t => t.Key == "cache.circuit_state" && (string?)t.Value == "open");
    }

    [Fact]
    public void RecordSchemaDrift_emits_counter_with_kind_tag()
    {
        var modeTag = $"unit-drift-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.schema_drift", modeTag);
        using var _ = listener;

        CacheInstruments.RecordSchemaDrift(modeTag, "schema_drift");

        Assert.Single(values);
        Assert.Contains(values[0].tags, t => t.Key == "cache.drift_kind" && (string?)t.Value == "schema_drift");
    }

    [Fact]
    public void RecordPayloadBytes_emits_histogram()
    {
        var modeTag = $"unit-bytes-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.payload.bytes", modeTag);
        using var _ = listener;

        CacheInstruments.RecordPayloadBytes(modeTag, "set", 4096);

        Assert.Single(values);
        Assert.Equal(4096L, values[0].value);
        Assert.Contains(values[0].tags, t => t.Key == "cache.operation" && (string?)t.Value == "set");
    }

    [Fact]
    public void TrackStaleRefresh_emits_updowncounter_increments_and_decrements()
    {
        var modeTag = $"unit-srf-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.stale_refresh.in_flight", modeTag);
        using var _ = listener;

        CacheInstruments.AddStaleRefreshInFlight(modeTag, 1);
        CacheInstruments.AddStaleRefreshInFlight(modeTag, -1);

        Assert.Equal(2, values.Count);
        Assert.Equal(1L, values[0].value);
        Assert.Equal(-1L, values[1].value);
    }
}
