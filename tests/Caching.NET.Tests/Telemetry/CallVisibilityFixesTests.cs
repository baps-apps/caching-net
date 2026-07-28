using System.Diagnostics;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Regression coverage for the call-visibility defects found auditing the 2.3.0 telemetry work:
/// value-type reads mis-reporting <c>served_from</c>, factory timeouts masquerading as caller
/// cancellation, caller-factory faults tagged as cache errors, background stale refreshes parented
/// onto the request span that triggered them, <c>refresh</c> carrying a constant-valued
/// <c>served_from</c>, and argument validation no longer throwing synchronously.
/// </summary>
public class CallVisibilityFixesTests
{
    private static ICacheService BuildCache(Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "fix",
            ["CacheOptions:IncludeKeyHashInTraces"] = "true",
        };
        configure?.Invoke(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        return services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    private static string HashOf(string key) =>
        Caching.NET.Internal.StableStringHash.Compute64(key).ToString("x16");

    private static Activity SpanFor(ActivityListenerHelpers.TraceScope scope, string operation) =>
        Assert.Single(scope.Snapshot(), a => a.TraceId == scope.TraceId && a.Tag("cache.operation") == operation);

    // --- served_from for value-type payloads -------------------------------------------------

    [Fact]
    public async Task Value_type_miss_reports_served_from_none()
    {
        var cache = BuildCache();
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        var value = await cache.GetAsync<int>($"missing:{Guid.NewGuid():N}");

        Assert.Equal(0, value);
        Assert.Equal("none", SpanFor(scope, "get").Tag("cache.served_from"));
    }

    [Fact]
    public async Task Value_type_hit_reports_served_from_cache()
    {
        var cache = BuildCache();
        var key = $"int:{Guid.NewGuid():N}";
        await cache.SetAsync(key, 42);

        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();
        var value = await cache.GetAsync<int>(key);

        Assert.Equal(42, value);
        Assert.Equal("cache", SpanFor(scope, "get").Tag("cache.served_from"));
    }

    [Fact]
    public async Task Value_type_default_payload_still_reports_served_from_cache()
    {
        var cache = BuildCache();
        var key = $"zero:{Guid.NewGuid():N}";
        await cache.SetAsync(key, 0);

        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();
        var value = await cache.GetAsync<int>(key);

        Assert.Equal(0, value);
        Assert.Equal("cache", SpanFor(scope, "get").Tag("cache.served_from"));
    }

    [Fact]
    public async Task Value_type_batch_read_counts_hits_and_misses()
    {
        var cache = BuildCache();
        var hitKey = $"int:{Guid.NewGuid():N}";
        var missKey = $"int:{Guid.NewGuid():N}";
        await cache.SetAsync(hitKey, 7);

        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();
        var result = await cache.GetManyAsync<int>(new[] { hitKey, missKey });

        Assert.Equal(7, result[hitKey]);
        var span = SpanFor(scope, "get_many");
        Assert.Equal(1, span.GetTagItem("cache.hit_count"));
        Assert.Equal(1, span.GetTagItem("cache.miss_count"));
        Assert.Equal("mixed", span.Tag("cache.served_from"));
    }

    // --- factory timeout vs caller cancellation ----------------------------------------------

    [Fact]
    public async Task Factory_timeout_is_tagged_Timeout_and_marks_the_span_error()
    {
        var cache = BuildCache(s => s["CacheOptions:FactoryTimeout"] = "00:00:00.200");
        var key = $"slow:{Guid.NewGuid():N}";
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetOrCreateAsync(key, async ct => { await Task.Delay(5000, ct); return "v"; }));

        var span = SpanFor(scope, "get_or_create");
        Assert.Equal("Timeout", span.Tag("cache.error_kind"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Caller_cancellation_is_tagged_Canceled_and_is_not_an_error()
    {
        var cache = BuildCache();
        var key = $"cancel:{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetOrCreateAsync(key, async ct =>
            {
                await cts.CancelAsync();
                await Task.Delay(5000, ct);
                return "v";
            }, cancellationToken: cts.Token));

        var span = SpanFor(scope, "get_or_create");
        Assert.Equal("Canceled", span.Tag("cache.error_kind"));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    // --- caller factory faults are not cache errors ------------------------------------------

    [Fact]
    public async Task Factory_exception_marks_the_span_error_without_a_cache_error_kind()
    {
        var cache = BuildCache();
        var key = $"boom:{Guid.NewGuid():N}";
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync<string>(key, _ => throw new InvalidOperationException("source down")));

        var span = SpanFor(scope, "get_or_create");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.Tag("cache.error_kind"));
        Assert.Equal(true, span.GetTagItem("cache.factory_failed"));
    }

    // --- background stale refresh is linked, not parented ------------------------------------

    [Fact]
    public async Task Background_stale_refresh_links_to_its_trigger_instead_of_parenting_onto_it()
    {
        var cache = BuildCache();
        var key = $"stale:{Guid.NewGuid():N}";
        var callOptions = new CacheCallOptions { AllowStaleFor = TimeSpan.FromMinutes(5) };

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v1"), callOptions, TimeSpan.FromMilliseconds(200));
        await Task.Delay(400);

        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;
        var refreshed = new TaskCompletionSource();

        using (var trigger = new ActivitySource("test-trigger").StartActivity("trigger"))
        {
            await cache.GetOrCreateAsync(key, async ct =>
            {
                await Task.Delay(20, ct);
                refreshed.TrySetResult();
                return "v2";
            }, callOptions, TimeSpan.FromMinutes(1));
        }

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        var staleSpan = Assert.Single(activities.Snapshot(), a =>
            a.Tag("cache.operation") == "stale_refresh" && a.Tag("cache.key_hash") == HashOf(key));
        var triggerSpan = Assert.Single(activities.Snapshot(), a =>
            a.Tag("cache.operation") == "get_or_create" && a.Tag("cache.key_hash") == HashOf(key));

        Assert.NotEqual(triggerSpan.TraceId, staleSpan.TraceId);
        Assert.Contains(staleSpan.Links, l => l.Context.SpanId == triggerSpan.SpanId);
    }

    // --- refresh carries no served_from ------------------------------------------------------

    [Fact]
    public async Task Refresh_emits_no_served_from_tag()
    {
        var cache = BuildCache();
        var key = $"refresh:{Guid.NewGuid():N}";
        using var scope = ActivityListenerHelpers.CaptureWithTraceScope();

        await cache.RefreshAsync(key, _ => Task.FromResult("v"));

        var span = SpanFor(scope, "refresh");
        Assert.Null(span.Tag("cache.served_from"));
        Assert.NotNull(span.Tag("cache.factory_ms"));
    }

    // --- argument validation throws synchronously again --------------------------------------

    [Fact]
    public void Argument_validation_throws_synchronously()
    {
        var cache = BuildCache();

        // Each lambda discards the returned Task rather than returning it, so xUnit binds the
        // Action overload — which is the point: the throw must happen on the calling thread,
        // before a Task ever exists, exactly as a fire-and-forget caller would see it.
        Assert.ThrowsAny<ArgumentException>(() => { _ = cache.SetAsync(null!, "v"); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = cache.GetAsync<string>(" "); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = cache.ExistsAsync(""); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = cache.GetOrCreateAsync(null!, _ => Task.FromResult("v")); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = cache.RefreshAsync(" ", _ => Task.FromResult("v")); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = cache.GetManyAsync<string>(null!); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = cache.SetManyAsync<string>(null!); });
    }
}
