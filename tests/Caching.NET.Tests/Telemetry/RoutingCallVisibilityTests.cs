using System.Diagnostics;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Telemetry;

public class RoutingCallVisibilityTests
{
    // Key-hash tagging is enabled so each assertion can be pinned to its own call via
    // cache.key_hash — the ActivitySource is process-wide and other tests emit spans too.
    private static ICacheService BuildCache(string mode, bool enabled = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = enabled ? "true" : "false",
            ["CacheOptions:Mode"] = mode,
            ["CacheOptions:KeyPrefix"] = "vis",
            ["CacheOptions:IncludeKeyHashInTraces"] = "true",
        };
        if (mode == "Hybrid") settings["CacheOptions:RedisConnectionString"] = "localhost:6379";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        return services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    private static Activity SpanFor(List<Activity> activities, string operation, string keyHash) =>
        activities.Snapshot().Single(a => a.Tag("cache.operation") == operation && a.Tag("cache.key_hash") == keyHash);

    private static string HashOf(string key) =>
        Caching.NET.Internal.StableStringHash.Compute64(key).ToString("x16");

    [Fact]
    public async Task Miss_then_hit_reports_source_then_cache()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, async ct => { await Task.Delay(30, ct); return "v"; });
        var missSpan = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", missSpan.Tag("cache.served_from"));
        Assert.Equal("InMemory", missSpan.Tag("cache.mode"));
        Assert.True(double.Parse(missSpan.Tag("cache.factory_ms")!) >= 20);

        activities.ClearCaptured();
        await cache.GetOrCreateAsync(key, _ => Task.FromResult("other"));
        var hitSpan = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("cache", hitSpan.Tag("cache.served_from"));
        Assert.Null(hitSpan.Tag("cache.factory_ms"));
    }

    [Fact]
    public async Task Miss_emits_one_total_and_one_factory_metric_sample()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (totals, totalListener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", "InMemory");
        var (factories, factoryListener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", "InMemory");
        using var _ = totalListener;
        using var __ = factoryListener;

        await cache.GetOrCreateAsync(key, async ct => { await Task.Delay(25, ct); return "v"; });
        totalListener.Dispose();
        factoryListener.Dispose();

        Assert.Contains(totals, t =>
            t.tags.Any(x => x.Key == "cache.operation" && (string?)x.Value == "get_or_create") &&
            t.tags.Any(x => x.Key == "cache.served_from" && (string?)x.Value == "source"));
        Assert.Contains(factories, f =>
            f.tags.Any(x => x.Key == "cache.operation" && (string?)x.Value == "get_or_create") && f.value >= 20);
    }

    [Fact]
    public async Task Disabled_cache_still_reports_the_call_and_times_the_source()
    {
        var cache = BuildCache("InMemory", enabled: false);
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, async ct => { await Task.Delay(25, ct); return "v"; });

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("Routing", span.Tag("cache.mode"));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.Equal("Disabled", span.Tag("cache.miss_reason"));
        Assert.True(double.Parse(span.Tag("cache.factory_ms")!) >= 20);
    }

    [Fact]
    public async Task Bypass_reports_source_with_bypass_reason()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v"), new CacheCallOptions { BypassCache = true });

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.Equal("Bypass", span.Tag("cache.miss_reason"));
    }

    [Fact]
    public async Task Force_refresh_reports_source()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "old");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("new"), new CacheCallOptions { ForceRefresh = true });

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.NotNull(span.Tag("cache.factory_ms"));
    }

    [Fact]
    public async Task Rejected_key_reports_source_with_key_rejected_reason()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "vis",
            ["CacheOptions:IncludeKeyHashInTraces"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, b => b.WithKeyValidator(_ => false));
        var cache = services.BuildServiceProvider().GetRequiredService<ICacheService>();
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v"));

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.Equal("KeyRejected", span.Tag("cache.miss_reason"));
    }

    [Fact]
    public async Task Coalesced_waiter_is_tagged_and_reports_cache()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var gate = new TaskCompletionSource();
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        var winner = cache.GetOrCreateAsync(key, async _ =>
        {
            await gate.Task;
            return "v";
        });
        // Give the winner time to take the stripe lock before the waiter arrives.
        await Task.Delay(100);
        var waiter = cache.GetOrCreateAsync(key, _ => Task.FromResult("unused"));
        gate.SetResult();
        await Task.WhenAll(winner, waiter);

        var spans = activities.Snapshot().Where(a =>
            a.Tag("cache.operation") == "get_or_create" && a.Tag("cache.key_hash") == HashOf(key)).ToList();
        Assert.Equal(2, spans.Count);
        var coalesced = Assert.Single(spans, s => s.Tag("cache.coalesced") == "True");
        Assert.Equal("cache", coalesced.Tag("cache.served_from"));
        Assert.Null(coalesced.Tag("cache.factory_ms"));
    }

    [Fact]
    public async Task Factory_exception_marks_span_error_and_still_records()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync<string>(key, _ => throw new InvalidOperationException("boom")));

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        // The source failed, not the cache: cache.error_kind stays absent so cache-error dashboards
        // don't count a flaky upstream as a cache incident. See CallVisibilityFixesTests.
        Assert.Null(span.Tag("cache.error_kind"));
        Assert.Equal(true, span.GetTagItem("cache.factory_failed"));
    }

    // Regression for a review finding: a call canceled while queued behind another call's stripe
    // lock never reaches the factory and never gets a real value — it must not be mislabeled
    // "served_from=cache" just because the happy path defaults that way for a hit.
    [Fact]
    public async Task Cancelled_while_coalesced_does_not_report_served_from_cache()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var gate = new TaskCompletionSource();
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        var winner = cache.GetOrCreateAsync(key, async _ =>
        {
            await gate.Task;
            return "v";
        });
        // Give the winner time to take the stripe lock before the waiter arrives.
        await Task.Delay(100);
        using var waiterCts = new CancellationTokenSource();
        var waiter = cache.GetOrCreateAsync(key, _ => Task.FromResult("unused"), cancellationToken: waiterCts.Token);
        // Cancel while the waiter is still queued on the stripe lock, before the winner finishes.
        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        gate.SetResult();
        await winner;

        var canceledSpan = Assert.Single(activities.Snapshot(), a =>
            a.Tag("cache.operation") == "get_or_create" &&
            a.Tag("cache.key_hash") == HashOf(key) &&
            a.Tag("cache.error_kind") == "Canceled");
        Assert.NotEqual("cache", canceledSpan.Tag("cache.served_from"));
        Assert.Null(canceledSpan.Tag("cache.factory_ms"));
    }

    [Fact]
    public async Task Get_hit_and_miss_report_cache_and_none()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var missingKey = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetAsync<string>(key);
        await cache.GetAsync<string>(missingKey);

        Assert.Equal("cache", SpanFor(activities, "get", HashOf(key)).Tag("cache.served_from"));
        Assert.Equal("none", SpanFor(activities, "get", HashOf(missingKey)).Tag("cache.served_from"));
    }

    [Fact]
    public async Task Exists_reports_cache_when_present_and_none_when_absent()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var missingKey = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        Assert.True(await cache.ExistsAsync(key));
        Assert.False(await cache.ExistsAsync(missingKey));

        Assert.Equal("cache", SpanFor(activities, "exists", HashOf(key)).Tag("cache.served_from"));
        Assert.Equal("none", SpanFor(activities, "exists", HashOf(missingKey)).Tag("cache.served_from"));
    }

    [Fact]
    // refresh always runs the factory and writes the result, so a served_from tag could only ever
    // read "source" — a constant label that splits series and carries no information. It is
    // write-shaped for tagging purposes; the factory timing is what makes the span useful.
    public async Task Refresh_times_the_factory_and_omits_served_from()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.RefreshAsync(key, async ct => { await Task.Delay(25, ct); return "v"; });

        var span = SpanFor(activities, "refresh", HashOf(key));
        Assert.Null(span.Tag("cache.served_from"));
        Assert.True(double.Parse(span.Tag("cache.factory_ms")!) >= 20);
    }

    [Fact]
    public async Task Write_operations_emit_a_span_without_served_from()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.SetAsync(key, "v");
        await cache.RemoveAsync(key);

        var setSpan = SpanFor(activities, "set", HashOf(key));
        Assert.Null(setSpan.Tag("cache.served_from"));
        Assert.Equal("InMemory", setSpan.Tag("cache.mode"));
        Assert.Null(SpanFor(activities, "remove", HashOf(key)).Tag("cache.served_from"));
    }

    [Fact]
    public async Task Clear_emits_a_span()
    {
        var cache = BuildCache("InMemory");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await ((Caching.NET.Services.IRoutingCacheService)cache).ClearAsync();

        Assert.Contains(activities.Snapshot(), a => a.Tag("cache.operation") == "clear");
    }

    [Fact]
    public async Task RemoveAsync_with_blank_key_still_emits_a_record()
    {
        var cache = BuildCache("InMemory");
        // Blank/null keys carry no cache.key_hash, so pin on TraceId (like the batch tests below)
        // rather than a global "remove" count: other test classes call RemoveAsync too, and xUnit
        // parallelizes across collections, so an unpinned count is flaky under concurrency.
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await cache.RemoveAsync("");
        await cache.RemoveAsync("  ");
        await cache.RemoveAsync(null!);

        var removeSpans = scope.Snapshot()
            .Where(a => a.TraceId == scope.TraceId && a.Tag("cache.operation") == "remove")
            .ToList();
        Assert.Equal(3, removeSpans.Count);
    }

    [Fact]
    public async Task Get_many_with_some_hits_reports_mixed_with_counts()
    {
        var cache = BuildCache("InMemory");
        var hitKey1 = $"k:{Guid.NewGuid():N}";
        var hitKey2 = $"k:{Guid.NewGuid():N}";
        var missKey = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(hitKey1, "v1");
        await cache.SetAsync(hitKey2, "v2");
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await cache.GetManyAsync<string>(new[] { hitKey1, hitKey2, missKey });

        var span = Assert.Single(scope.Snapshot(), a =>
            a.TraceId == scope.TraceId &&
            a.Tag("cache.operation") == "get_many");
        Assert.Equal("mixed", span.Tag("cache.served_from"));
        Assert.Equal("2", span.Tag("cache.hit_count"));
        Assert.Equal("1", span.Tag("cache.miss_count"));
    }

    [Fact]
    public async Task Get_many_all_hits_reports_cache()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v");
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await cache.GetManyAsync<string>(new[] { key });

        var span = Assert.Single(scope.Snapshot(), a =>
            a.TraceId == scope.TraceId &&
            a.Tag("cache.operation") == "get_many");
        Assert.Equal("cache", span.Tag("cache.served_from"));
    }

    [Fact]
    public async Task Get_many_all_misses_reports_none()
    {
        var cache = BuildCache("InMemory");
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await cache.GetManyAsync<string>(new[] { $"k:{Guid.NewGuid():N}", $"k:{Guid.NewGuid():N}" });

        var span = Assert.Single(scope.Snapshot(), a =>
            a.TraceId == scope.TraceId &&
            a.Tag("cache.operation") == "get_many");
        Assert.Equal("none", span.Tag("cache.served_from"));
        Assert.Equal("0", span.Tag("cache.hit_count"));
        Assert.Equal("2", span.Tag("cache.miss_count"));
    }

    [Fact]
    public async Task Batch_writes_emit_spans_without_served_from()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await cache.SetManyAsync(new Dictionary<string, string> { [key] = "v" });
        await cache.RemoveManyAsync(new[] { key });

        var setMany = Assert.Single(scope.Snapshot(), a =>
            a.TraceId == scope.TraceId &&
            a.Tag("cache.operation") == "set_many");
        Assert.Null(setMany.Tag("cache.served_from"));
        Assert.Contains(scope.Snapshot(), a =>
            a.TraceId == scope.TraceId &&
            a.Tag("cache.operation") == "remove_many");
    }

    [Fact]
    public async Task RemoveManyAsync_with_null_keys_still_emits_a_record()
    {
        var cache = BuildCache("InMemory");
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await cache.RemoveManyAsync(null!);

        var span = Assert.Single(scope.Snapshot(), a =>
            a.TraceId == scope.TraceId &&
            a.Tag("cache.operation") == "remove_many");
        Assert.NotNull(span);
    }

    // Background stale refreshes run on their own Task.Run after the triggering call has already
    // returned, and they deliberately start a *root* span linked back to the trigger rather than a
    // child of it (see ScheduleBackgroundRefresh), so they no longer share the test's TraceId.
    // StaleWhileRevalidateTests also triggers background refreshes and can run concurrently, so the
    // stale_refresh span is pinned by cache.key_hash — unique to this test's key — instead.
    [Fact]
    public async Task Background_stale_refresh_records_its_own_span()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var callOptions = new CacheCallOptions { AllowStaleFor = TimeSpan.FromMinutes(5) };

        // Seed with a 200ms TTL plus the stale window, then wait for it to go stale.
        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v1"), callOptions, TimeSpan.FromMilliseconds(200));
        await Task.Delay(400);

        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();
        var refreshed = new TaskCompletionSource();

        // Stale read: returns the stale value immediately and schedules the background refresh.
        var served = await cache.GetOrCreateAsync(key, async ct =>
        {
            await Task.Delay(20, ct);
            refreshed.TrySetResult();
            return "v2";
        }, callOptions, TimeSpan.FromMinutes(1));
        Assert.Equal("v1", served);

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Let the refresh task finish its write and dispose its recorder.
        await Task.Delay(200);

        var refreshSpan = Assert.Single(scope.Snapshot(), a =>
            a.Tag("cache.operation") == "stale_refresh" && a.Tag("cache.key_hash") == HashOf(key));
        Assert.NotEqual(scope.TraceId, refreshSpan.TraceId);
        Assert.Equal("source", refreshSpan.Tag("cache.served_from"));
        Assert.NotNull(refreshSpan.Tag("cache.factory_ms"));
        Assert.NotEqual(ActivityStatusCode.Error, refreshSpan.Status);

        var staleSpan = Assert.Single(scope.Snapshot(), a =>
            a.TraceId == scope.TraceId &&
            a.Tag("cache.operation") == "get_or_create" && a.Tag("cache.key_hash") == HashOf(key));
        Assert.Equal("cache", staleSpan.Tag("cache.served_from"));
    }
}
