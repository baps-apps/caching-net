using System.Diagnostics;
using Caching.NET.Options;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

public class CacheCallRecorderTests
{
    private static CacheOptions Options(bool keyHash = false) =>
        new() { KeyPrefix = "t", IncludeKeyHashInTraces = keyHash };

    // Every test here attaches a MeterListener before starting, so Start never returns null; the
    // null case (nothing listening at all) is covered by Start_returns_null_when_nothing_is_listening.
    private static CacheCallRecorder StartRecorder(string mode, string operation, CacheOptions options, string? rawKey = null)
        => CacheCallRecorder.Start(mode, options, operation, rawKey)
           ?? throw new InvalidOperationException("recorder must be created while a listener is attached");

    [Fact]
    public void Dispose_records_exactly_one_total_sample()
    {
        var mode = $"unit-rec-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (StartRecorder(mode, "get", Options())) { }

        Assert.Single(values);
        Assert.True(values[0].value >= 0);
    }

    [Fact]
    public void Double_dispose_records_once()
    {
        var mode = $"unit-dd-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        var rec = StartRecorder(mode, "get", Options());
        rec.Dispose();
        rec.Dispose();

        Assert.Single(values);
    }

    [Fact]
    public void Read_that_found_a_value_is_served_from_cache()
    {
        var mode = $"unit-hit-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get", Options()))
            rec.MarkServedFromCache();

        Assert.Contains(values[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "cache");
    }

    [Fact]
    public void Read_that_found_nothing_is_served_from_none()
    {
        var mode = $"unit-none-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get", Options()))
            rec.MarkNotFound();

        Assert.Contains(values[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "none");
    }

    [Fact]
    public async Task Factory_that_ran_marks_source_and_records_factory_duration()
    {
        var mode = $"unit-src-{Guid.NewGuid():N}";
        var (totals, totalListener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        var (factories, factoryListener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = totalListener;
        using var __ = factoryListener;

        using (var rec = StartRecorder(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(async ct =>
            {
                await Task.Delay(25, ct);
                return "v";
            });
            Assert.Equal("v", await wrapped(CancellationToken.None));
        }

        Assert.Contains(totals[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "source");
        Assert.Single(factories);
        Assert.True(factories[0].value >= 20, $"expected >= 20ms, got {factories[0].value}");
        Assert.True(totals[0].value >= factories[0].value);
    }

    [Fact]
    public void No_factory_means_no_factory_duration_sample()
    {
        var mode = $"unit-nofac-{Guid.NewGuid():N}";
        var (factories, listener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get_or_create", Options()))
            rec.MarkServedFromCache();

        Assert.Empty(factories);
    }

    [Fact]
    public async Task Factory_invoked_twice_accumulates_into_one_sample()
    {
        var mode = $"unit-twice-{Guid.NewGuid():N}";
        var (factories, listener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(async ct => { await Task.Delay(20, ct); return "v"; });
            await wrapped(CancellationToken.None);
            await wrapped(CancellationToken.None);
        }

        Assert.Single(factories);
        Assert.True(factories[0].value >= 35, $"expected accumulated >= 35ms, got {factories[0].value}");
    }

    [Fact]
    public async Task Factory_that_throws_still_records_both_samples()
    {
        var mode = $"unit-throw-{Guid.NewGuid():N}";
        var (totals, totalListener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        var (factories, factoryListener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = totalListener;
        using var __ = factoryListener;

        using (var rec = StartRecorder(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(_ => throw new InvalidOperationException("boom"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrapped(CancellationToken.None));
        }

        Assert.Single(totals);
        Assert.Single(factories);
        Assert.Contains(totals[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "source");
    }

    [Theory]
    [InlineData(3, 2, "mixed")]
    [InlineData(3, 0, "cache")]
    [InlineData(0, 2, "none")]
    [InlineData(0, 0, "none")]
    public void Batch_outcome_resolves_served_from(int hits, int misses, string expected)
    {
        var mode = $"unit-batch-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get_many", Options()))
            rec.MarkBatch(hits, misses);

        Assert.Contains(values[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == expected);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("set_many")]
    [InlineData("remove")]
    [InlineData("remove_many")]
    [InlineData("remove_by_tag")]
    [InlineData("clear")]
    public void Write_operations_omit_served_from(string operation)
    {
        var mode = $"unit-write-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (StartRecorder(mode, operation, Options())) { }

        Assert.DoesNotContain(values[0].tags, t => t.Key == "cache.served_from");
    }

    [Fact]
    public void SetMode_overrides_the_mode_tag()
    {
        var resolved = $"unit-resolved-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", resolved);
        using var _ = listener;

        using (var rec = StartRecorder("Routing", "get", Options()))
            rec.SetMode(resolved);

        Assert.Single(values);
    }

    [Fact]
    public void Span_carries_operation_mode_and_served_from()
    {
        var mode = $"unit-span-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get_or_create", Options()))
            rec.MarkServedFromCache();

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.Equal("cache get_or_create", span.DisplayName);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal("cache", span.Tag("cache.served_from"));
    }

    [Fact]
    public async Task Span_carries_factory_ms_when_the_factory_ran()
    {
        var mode = $"unit-spanfac-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(async ct => { await Task.Delay(20, ct); return "v"; });
            await wrapped(CancellationToken.None);
        }

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.NotNull(span.Tag("cache.factory_ms"));
        Assert.True(double.Parse(span.Tag("cache.factory_ms")!) >= 15);
    }

    [Fact]
    public void Span_carries_batch_counts_and_coalesced_and_miss_reason()
    {
        var mode = $"unit-spantags-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get_many", Options()))
        {
            rec.MarkBatch(3, 2);
            rec.MarkCoalesced();
            rec.MarkMissReason("NotFound");
        }

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.Equal("3", span.Tag("cache.hit_count"));
        Assert.Equal("2", span.Tag("cache.miss_count"));
        Assert.Equal("True", span.Tag("cache.coalesced"));
        Assert.Equal("NotFound", span.Tag("cache.miss_reason"));
    }

    [Fact]
    public void Escaping_error_sets_span_status_error()
    {
        var mode = $"unit-err-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get", Options()))
            rec.MarkError("Timeout", thrownToCaller: true);

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Timeout", span.Tag("cache.error_kind"));
    }

    [Fact]
    public void Swallowed_error_keeps_span_ok_but_tags_error_kind()
    {
        var mode = $"unit-failopen-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get_or_create", Options()))
            rec.MarkError("Timeout", thrownToCaller: false);

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Timeout", span.Tag("cache.error_kind"));
    }

    [Fact]
    public void Cancellation_is_tagged_but_not_an_error()
    {
        var mode = $"unit-cancel-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = StartRecorder(mode, "get", Options()))
            rec.MarkError("Canceled", thrownToCaller: true);

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Canceled", span.Tag("cache.error_kind"));
    }

    [Fact]
    public void Key_hash_tag_only_when_opted_in()
    {
        var optedIn = $"unit-kh-on-{Guid.NewGuid():N}";
        var optedOut = $"unit-kh-off-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (StartRecorder(optedIn, "get", Options(keyHash: true), "member:42")) { }
        using (StartRecorder(optedOut, "get", Options(keyHash: false), "member:42")) { }

        var withHash = activities.Single(a => a.Tag("cache.mode") == optedIn);
        var withoutHash = activities.Single(a => a.Tag("cache.mode") == optedOut);
        Assert.Equal(16, withHash.Tag("cache.key_hash")!.Length);
        Assert.DoesNotContain("member:42", withHash.Tag("cache.key_hash")!);
        Assert.Null(withoutHash.Tag("cache.key_hash"));
    }
}
