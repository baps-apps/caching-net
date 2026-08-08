using System.Diagnostics;
using Caching.NET.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Telemetry;

// The Caching.NET activity source is process-wide: any concurrently running test that exercises a
// real ICacheService verb would add its own "cache.*" spans to this recorder. Every assertion below
// filters recorded activities down to this class's own named cache (see OwnSpans), so contamination
// from another test class cannot produce a false positive regardless of what runs alongside it.
// This class still shares the caching-net-metrics collection with the other Telemetry tests that
// observe the same process-wide activity source, purely so span assertions here never interleave
// with the ActivityListener-registration tests in CacheTelemetryTests.
[Collection(MetricsCollection.Name)]
public class OperationSpanTests
{
    private const string CacheName = "op-spans";

    private static ServiceProvider BuildHost() => TestHost.BuildNamed(CacheName, cache => cache
        .UseInMemory()
        .WithApplicationPrefix("tests"));

    /// <summary>
    /// Every span this class's own cache produced. <see cref="Telemetry.CacheTelemetryContext"/>
    /// unconditionally tags every span with <c>cache.name</c>, so filtering on it is enough to
    /// ignore spans any other concurrently running test's cache emitted on the same process-wide
    /// activity source.
    /// </summary>
    private static Activity[] OwnSpans(SpanRecorder recorder) => recorder.Activities
        .Where(a => Equals(a.GetTagItem(CacheTelemetryAttributes.Name), CacheName))
        .ToArray();

    [Fact]
    public async Task GetOrSet_EmitsABrandedOperationSpan()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = BuildHost();

        await host.NamedCache(CacheName).GetOrSetAsync<int>("Order:42", (_, _) => Task.FromResult(1));

        var span = Assert.Single(OwnSpans(recorder), a => a.OperationName == "cache.get_or_set");
        Assert.Equal(CacheTelemetry.SystemName, span.GetTagItem(CacheTelemetryAttributes.System));
        Assert.Equal("InMemory", span.GetTagItem(CacheTelemetryAttributes.Mode));
        Assert.Equal("miss", span.GetTagItem(CacheTelemetryAttributes.Result));
        Assert.Equal(true, span.GetTagItem(CacheTelemetryAttributes.FactoryExecuted));
    }

    [Fact]
    public async Task WarmRead_EmitsItsOwnSpanWithNoResultTag()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = BuildHost();
        var cache = host.NamedCache(CacheName);

        await cache.SetAsync("k", 1);
        await cache.GetOrDefaultAsync<int>("k");

        var span = Assert.Single(OwnSpans(recorder), a => a.OperationName == "cache.get_or_default");

        // GetOrDefaultAsync keeps delegating straight to the engine and carries no cache.result tag:
        // deriving hit/miss would mean substituting a TryGet-then-return implementation, which risks
        // different stale/fail-safe semantics for one tag. The outcome is visible on the per-layer
        // child spans a future decorator emits instead.
        Assert.Null(span.GetTagItem(CacheTelemetryAttributes.Result));
    }

    [Fact]
    public async Task EveryVerbEmitsItsOwnSpan()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = BuildHost();
        var cache = host.NamedCache(CacheName);

        await cache.SetAsync("k", 1, tags: ["t"]);
        await cache.TryGetAsync<int>("k");
        await cache.ExpireAsync("k");
        await cache.RemoveAsync("k");
        await cache.RemoveByTagAsync("t");
        await cache.ClearAsync();

        var names = OwnSpans(recorder).Select(a => a.OperationName).ToArray();
        Assert.Contains("cache.set", names);
        Assert.Contains("cache.try_get", names);
        Assert.Contains("cache.expire", names);
        Assert.Contains("cache.remove", names);
        Assert.Contains("cache.remove_by_tag", names);
        Assert.Contains("cache.clear", names);
    }

    [Fact]
    public async Task GetOrSet_SpanDurationCoversTheFactoryNotJustStartingIt()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = BuildHost();

        await host.NamedCache(CacheName).GetOrSetAsync<int>("Order:slow", async (_, _) =>
        {
            await Task.Delay(50);
            return 1;
        });

        var span = Assert.Single(OwnSpans(recorder), a => a.OperationName == "cache.get_or_set");

        // If the span were disposed as soon as the awaitable was created — rather than after the
        // whole operation, factory included, completed — its recorded duration would be a fraction
        // of a millisecond instead of covering the 50 ms delay.
        Assert.True(
            span.Duration >= TimeSpan.FromMilliseconds(40),
            $"expected the recorded span duration to cover the factory's delay, was {span.Duration}");
    }

    [Fact]
    public async Task ThrowingFactory_MarksTheSpanFailedWithNoSuccessResultTag()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = BuildHost();
        var cache = host.NamedCache(CacheName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrSetAsync<int>(
                "Order:boom",
                (_, _) => throw new InvalidOperationException("boom")).AsTask());

        var span = Assert.Single(OwnSpans(recorder), a => a.OperationName == "cache.get_or_set");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem(CacheTelemetryAttributes.ErrorType));
        Assert.NotEqual(CacheResults.Hit, span.GetTagItem(CacheTelemetryAttributes.Result));
        Assert.NotEqual(CacheResults.Miss, span.GetTagItem(CacheTelemetryAttributes.Result));
    }

    [Fact]
    public async Task Set_WhenTheWriteThrows_MarksTheSpanFailedAndNeverTagsSuccess()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);

        // A disposed engine throws from inside the awaited write, after the span has started — a
        // deterministic stand-in for the same failure shape as a serialization error or a Redis
        // error surfaced by ThrowOnDistributedCacheErrors, without needing a real distributed layer.
        // Deliberately NOT a pre-cancelled token: caller cancellation is not an error (see
        // Set_WhenTheCallerCancels_... below), so using one here would assert the opposite of the
        // contract this type actually implements.
        var host = BuildHost();
        var cache = host.NamedCache(CacheName);
        host.Dispose();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            () => cache.SetAsync("Order:broken", 1).AsTask());

        var span = Assert.Single(OwnSpans(recorder), a => a.OperationName == "cache.set");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(ObjectDisposedException), span.GetTagItem(CacheTelemetryAttributes.ErrorType));
        Assert.NotEqual(CacheResults.Set, span.GetTagItem(CacheTelemetryAttributes.Result));
    }

    /// <summary>
    /// A cancellation the caller asked for is not a cache fault. In ASP.NET Core the ambient token is
    /// <c>HttpContext.RequestAborted</c>, so every client that navigates away mid-request cancels the
    /// cache calls on that request; marking those spans failed made ordinary user behaviour
    /// indistinguishable from a Redis outage on an error-rate dashboard.
    /// </summary>
    [Fact]
    public async Task Set_WhenTheCallerCancels_TagsCanceledAndDoesNotMarkTheSpanFailed()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = BuildHost();
        var cache = host.NamedCache(CacheName);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.SetAsync("Order:cancelled", 1, token: cts.Token).AsTask());

        var span = Assert.Single(OwnSpans(recorder), a => a.OperationName == "cache.set");
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(CacheResults.Canceled, span.GetTagItem(CacheTelemetryAttributes.Result));
        Assert.Null(span.GetTagItem(CacheTelemetryAttributes.ErrorType));
    }

    [Fact]
    public async Task GetOrSet_WhenTheCallerCancelsDuringTheFactory_TagsCanceledOnBothSpans()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = BuildHost();
        var cache = host.NamedCache(CacheName);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrSetAsync<int>(
                "Order:cancelled-factory",
                async ct =>
                {
                    await cts.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                    return 1;
                },
                token: cts.Token).AsTask());

        foreach (var operationName in new[] { "cache.get_or_set", "cache.factory" })
        {
            var span = await WaitForSpanAsync(recorder, operationName);
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Equal(CacheResults.Canceled, span.GetTagItem(CacheTelemetryAttributes.Result));
            Assert.Null(span.GetTagItem(CacheTelemetryAttributes.ErrorType));
        }
    }

    /// <summary>
    /// Polls for a span rather than reading the recorder once. When the caller's token is cancelled
    /// the engine abandons the factory and throws to the caller without waiting for the factory's
    /// continuation, so <c>cache.factory</c> can stop fractionally after <c>cache.get_or_set</c> has
    /// already returned. Reading immediately observed <c>cache.get_or_set</c> but not
    /// <c>cache.factory</c> reproducibly.
    /// </summary>
    private static async Task<Activity> WaitForSpanAsync(SpanRecorder recorder, string operationName)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var match = OwnSpans(recorder).Where(a => a.OperationName == operationName).ToArray();
            if (match.Length > 0)
            {
                return Assert.Single(match);
            }

            await Task.Delay(20);
        }

        Assert.Fail(
            $"no '{operationName}' span was recorded within 2s. Recorded: "
            + string.Join(", ", OwnSpans(recorder).Select(a => a.OperationName)));
        throw new InvalidOperationException("unreachable");
    }
}
