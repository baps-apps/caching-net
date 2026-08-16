using BenchmarkDotNet.Attributes;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace Caching.NET.Benchmark;

/// <summary>
/// What it costs to deliver one incoming backplane message through <see cref="InstrumentedBackplane"/>.
/// </summary>
/// <remarks>
/// <para>
/// A received backplane message is the start of local invalidation work — the engine evicts the
/// affected memory entry on the subscriber's thread. Nothing about that path is on a caller's stack,
/// so it is not covered by any of the end-to-end suites, and it is dispatched once per message per
/// instance: an invalidation storm makes this a hot path even though ordinary traffic does not.
/// </para>
/// <para>
/// The subscription is taken through the decorator and the captured handler is invoked directly, so
/// these rows measure delivery and nothing underneath it. The handler itself only increments a
/// counter: the engine's real eviction work is measured by
/// <see cref="LayerDecoratorBenchmarks"/> and would otherwise swamp the difference this class exists
/// to show.
/// </para>
/// <para>
/// No Redis is involved, deliberately. A real backplane would add network delivery — which is not
/// what changes when the decorator changes — and would put these rows behind an environment
/// variable, where a regression goes unmeasured by default.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class BackplaneDispatchBenchmarks
{
    private BackplaneSubscriptionOptions _subscription = null!;
    private BackplaneMessage _message = null!;
    private int _handled;

    [GlobalSetup]
    public void Setup()
    {
        var stub = new CapturingBackplane();
        var backplane = InstrumentedBackplane.Wrap(
            stub,
            new CacheTelemetryContext(new CachingOptions
            {
                CacheName = "bench-backplane-dispatch",
                ApplicationPrefix = "bench"
            }),
            new BackplaneKeyDecoder(
                "bench:",
                FusionCacheInternalStrings.DefaultTagCacheKeyPrefix,
                FusionCacheInternalStrings.DefaultClearRemoveTag,
                FusionCacheInternalStrings.DefaultClearExpireTag));

        backplane.Subscribe(new BackplaneSubscriptionOptions(
            cacheName: "bench-backplane-dispatch",
            cacheInstanceId: "bench-instance",
            channelName: "bench-channel",
            connectHandler: _ => { },
            incomingMessageHandler: _ => Interlocked.Increment(ref _handled),
            connectHandlerAsync: _ => ValueTask.CompletedTask,
            incomingMessageHandlerAsync: _ =>
            {
                Interlocked.Increment(ref _handled);
                return ValueTask.CompletedTask;
            }));

        _subscription = stub.Subscription
            ?? throw new InvalidOperationException("the decorator did not pass a subscription through");

        // Prefixed, as the wire carries it: the traced row includes decoding it back and fingerprinting
        // the result, which is what a real received message costs.
        _message = BackplaneMessage.CreateForEntryRemove(
            "bench-source",
            "bench:bench-key",
            DateTimeOffset.UtcNow.UtcTicks);
    }

    [GlobalCleanup]
    public void Cleanup() => TracingScope.Reset();

    [Benchmark(Baseline = true, Description = "Incoming message dispatch, no trace listener")]
    public int Dispatch()
    {
        TracingScope.Detached();
        _subscription.IncomingMessageHandler!(_message);
        return _handled;
    }

    [Benchmark(Description = "Incoming message dispatch, trace listener attached")]
    public int DispatchTraced()
    {
        TracingScope.Parentless();
        _subscription.IncomingMessageHandler!(_message);
        return _handled;
    }

    [Benchmark(Description = "Incoming message dispatch (async), no trace listener")]
    public async Task<int> DispatchAsync()
    {
        TracingScope.Detached();
        await _subscription.IncomingMessageHandlerAsync!(_message);
        return _handled;
    }

    [Benchmark(Description = "Incoming message dispatch (async), trace listener attached")]
    public async Task<int> DispatchAsyncTraced()
    {
        TracingScope.Parentless();
        await _subscription.IncomingMessageHandlerAsync!(_message);
        return _handled;
    }

    /// <summary>
    /// Captures whatever subscription the decorator hands down, so the benchmark can invoke the
    /// handler the engine would have been given.
    /// </summary>
    private sealed class CapturingBackplane : IFusionCacheBackplane
    {
        public BackplaneSubscriptionOptions? Subscription { get; private set; }

        public void Subscribe(BackplaneSubscriptionOptions options) => Subscription = options;

        public ValueTask SubscribeAsync(BackplaneSubscriptionOptions options)
        {
            Subscription = options;
            return ValueTask.CompletedTask;
        }

        public void Unsubscribe() => Subscription = null;

        public ValueTask UnsubscribeAsync()
        {
            Subscription = null;
            return ValueTask.CompletedTask;
        }

        public void Publish(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
        {
        }

        public ValueTask PublishAsync(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
            => ValueTask.CompletedTask;
    }
}
