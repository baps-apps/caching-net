using System.Diagnostics;
using System.Reflection;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Caching.NET.Tests.Telemetry;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// A received backplane message is the root of the local invalidation work it causes, so the
/// decorator gives that work a span to hang from.
/// </summary>
/// <remarks>
/// Without it, every eviction a remote invalidation triggers becomes its own parentless root trace:
/// a burst of one-span traces named <c>cache.memory.remove</c> with nothing saying what caused them.
/// The message's true causal parent lives in the publishing process and cannot be recovered — the
/// engine's wire format carries no trace context — so this span is a local anchor, not a remote
/// link, and it is marked as background work rather than pretending to be part of a request.
/// </remarks>
[Collection(MetricsCollection.Name)]
public class InstrumentedBackplaneReceiveTests
{
    // Stands in for whatever span the subscriber thread inherited from the code that set the
    // subscription up — a request, a startup activity — rather than for anything Caching.NET emits.
    private const string SubscriberSourceName = "Caching.NET.Tests.Subscriber";

    private static readonly ActivitySource SubscriberSource = new(SubscriberSourceName);

    private static readonly BackplaneMessage Message =
        BackplaneMessage.CreateForEntryRemove("source-1", "Order:42", DateTimeOffset.UtcNow.UtcTicks);

    // Published by the instance that is about to receive it back off the channel: same source id as
    // the subscription's cache instance id.
    private static readonly BackplaneMessage OwnMessage =
        BackplaneMessage.CreateForEntryRemove("instance-1", "Order:42", DateTimeOffset.UtcNow.UtcTicks);

    private static IFusionCacheBackplane Wrap(CapturingBackplane inner, string cacheName)
        => InstrumentedBackplane.Wrap(
            inner,
            new CacheTelemetryContext(new CachingOptions { CacheName = cacheName, ApplicationPrefix = "tests" }));

    private static BackplaneSubscriptionOptions Subscription(
        Action<BackplaneMessage>? incoming = null,
        Func<BackplaneMessage, ValueTask>? incomingAsync = null,
        Action<BackplaneConnectionInfo>? connect = null,
        Func<BackplaneConnectionInfo, ValueTask>? connectAsync = null)
        => new(
            cacheName: "subscribed-cache",
            cacheInstanceId: "instance-1",
            channelName: "channel-1",
            connectHandler: connect ?? (_ => { }),
            incomingMessageHandler: incoming ?? (_ => { }),
            connectHandlerAsync: connectAsync ?? (_ => ValueTask.CompletedTask),
            incomingMessageHandlerAsync: incomingAsync ?? (_ => ValueTask.CompletedTask));

    private static Activity[] OwnSpans(SpanRecorder recorder, string cacheName) => recorder.Activities
        .Where(a => Equals(a.GetTagItem(CacheTelemetryAttributes.Name), cacheName))
        .ToArray();

    [Fact]
    public void IncomingMessage_EmitsABrandedReceiveSpan()
    {
        const string CacheName = "backplane-receive-span";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();

        Wrap(inner, CacheName).Subscribe(Subscription());
        inner.Subscription!.IncomingMessageHandler!(Message);

        var span = Assert.Single(OwnSpans(recorder, CacheName), a => a.OperationName == "cache.backplane.receive");
        Assert.Equal(CacheTelemetry.SystemName, span.GetTagItem(CacheTelemetryAttributes.System));
        Assert.Equal(true, span.GetTagItem(CacheTelemetryAttributes.BackgroundOperation));
    }

    /// <summary>
    /// The span is only worth creating if the engine's own work lands inside it: this is what turns a
    /// burst of parentless <c>cache.memory.remove</c> roots into one trace.
    /// </summary>
    [Fact]
    public void EngineHandler_RunsInsideTheReceiveSpan()
    {
        const string CacheName = "backplane-receive-ambient";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();
        Activity? ambient = null;

        Wrap(inner, CacheName).Subscribe(Subscription(incoming: _ => ambient = Activity.Current));
        inner.Subscription!.IncomingMessageHandler!(Message);

        Assert.NotNull(ambient);
        Assert.Equal("cache.backplane.receive", ambient.OperationName);
    }

    [Fact]
    public async Task AsyncEngineHandler_RunsInsideTheReceiveSpan()
    {
        const string CacheName = "backplane-receive-ambient-async";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();
        Activity? ambient = null;

        await Wrap(inner, CacheName).SubscribeAsync(Subscription(incomingAsync: _ =>
        {
            ambient = Activity.Current;
            return ValueTask.CompletedTask;
        }));
        await inner.Subscription!.IncomingMessageHandlerAsync!(Message);

        Assert.NotNull(ambient);
        Assert.Equal("cache.backplane.receive", ambient.OperationName);
    }

    /// <summary>
    /// Redis pub/sub delivers a published message back to the connection that published it, and the
    /// engine drops those by source id — so a span around one would time an early return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decorator wraps the engine's handler, and the engine's
    /// <c>message.SourceId == _cache.InstanceId</c> check lives <i>inside</i> that handler, so without
    /// this the wrapper spans every write the local instance makes. In a two-replica deployment that
    /// is half of all <c>cache.backplane.receive</c> traces doing nothing.
    /// </para>
    /// <para>
    /// It also puts the span back in step with the metric: <c>caching.net.background.operations</c>
    /// with <c>cache.operation=backplane_receive</c> comes off the engine's <c>MessageReceived</c>
    /// event, which the engine raises only after the same source-id check. Two Caching.NET signals for
    /// one event must not disagree about how many there were.
    /// </para>
    /// </remarks>
    [Fact]
    public void SelfPublishedMessage_EmitsNoReceiveSpan()
    {
        const string CacheName = "backplane-receive-own";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();

        Wrap(inner, CacheName).Subscribe(Subscription());
        inner.Subscription!.IncomingMessageHandler!(OwnMessage);

        Assert.DoesNotContain(OwnSpans(recorder, CacheName), a => a.OperationName == "cache.backplane.receive");
    }

    [Fact]
    public async Task AsyncSelfPublishedMessage_EmitsNoReceiveSpan()
    {
        const string CacheName = "backplane-receive-own-async";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();

        await Wrap(inner, CacheName).SubscribeAsync(Subscription());
        await inner.Subscription!.IncomingMessageHandlerAsync!(OwnMessage);

        Assert.DoesNotContain(OwnSpans(recorder, CacheName), a => a.OperationName == "cache.backplane.receive");
    }

    /// <summary>
    /// Skipping the span must not skip the delivery: dropping the message is the engine's decision,
    /// made on the same source id plus auto-recovery state the decorator cannot see.
    /// </summary>
    [Fact]
    public void SelfPublishedMessage_StillReachesTheEngineExactlyOnce()
    {
        var inner = new CapturingBackplane();
        var received = 0;

        Wrap(inner, "backplane-receive-own-delivery").Subscribe(Subscription(incoming: _ => received++));
        inner.Subscription!.IncomingMessageHandler!(OwnMessage);

        Assert.Equal(1, received);
    }

    [Fact]
    public async Task AsyncSelfPublishedMessage_StillReachesTheEngineExactlyOnce()
    {
        var inner = new CapturingBackplane();
        var received = 0;

        await Wrap(inner, "backplane-receive-own-delivery-async").SubscribeAsync(Subscription(incomingAsync: _ =>
        {
            received++;
            return ValueTask.CompletedTask;
        }));
        await inner.Subscription!.IncomingMessageHandlerAsync!(OwnMessage);

        Assert.Equal(1, received);
    }

    [Fact]
    public void IncomingMessage_StillReachesTheEngineExactlyOnce()
    {
        var inner = new CapturingBackplane();
        var received = 0;

        Wrap(inner, "backplane-receive-delivery").Subscribe(Subscription(incoming: _ => received++));
        inner.Subscription!.IncomingMessageHandler!(Message);

        Assert.Equal(1, received);
    }

    /// <summary>
    /// A received message starts its own trace, even when the subscriber thread is carrying a
    /// finished span from whenever the subscription was set up.
    /// </summary>
    /// <remarks>
    /// Ambient context flows with the <see cref="ExecutionContext"/>. A cache is a singleton, so if it
    /// is first resolved inside a request, the subscription — and every callback the subscriber thread
    /// ever runs — inherits that request's span. Without an explicit root, one arbitrary request would
    /// collect every invalidation the instance receives for the rest of the process's life.
    /// </remarks>
    [Fact]
    public void ReceiveSpan_IsARootEvenUnderAStaleAmbientSpan()
    {
        const string CacheName = "backplane-receive-root";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName, SubscriberSourceName);
        var inner = new CapturingBackplane();

        var stale = SubscriberSource.StartActivity("startup.request");
        Assert.NotNull(stale);
        Wrap(inner, CacheName).Subscribe(Subscription());
        stale.Stop();
        Activity.Current = stale;

        inner.Subscription!.IncomingMessageHandler!(Message);
        Activity.Current = null;

        var span = Assert.Single(OwnSpans(recorder, CacheName), a => a.OperationName == "cache.backplane.receive");
        Assert.Null(span.ParentId);
        Assert.NotEqual(stale.TraceId, span.TraceId);
    }

    [Fact]
    public void HandlerThrowing_MarksTheReceiveSpanAsFailed()
    {
        const string CacheName = "backplane-receive-error";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();

        Wrap(inner, CacheName).Subscribe(Subscription(incoming: _ => throw new InvalidOperationException("boom")));

        Assert.Throws<InvalidOperationException>(() => inner.Subscription!.IncomingMessageHandler!(Message));

        var span = Assert.Single(OwnSpans(recorder, CacheName), a => a.OperationName == "cache.backplane.receive");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem(CacheTelemetryAttributes.ErrorType));
    }

    [Fact]
    public async Task AsyncHandlerThrowing_MarksTheReceiveSpanAsFailed()
    {
        const string CacheName = "backplane-receive-error-async";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();

        await Wrap(inner, CacheName).SubscribeAsync(Subscription(incomingAsync: async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await inner.Subscription!.IncomingMessageHandlerAsync!(Message));

        var span = Assert.Single(OwnSpans(recorder, CacheName), a => a.OperationName == "cache.backplane.receive");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    /// <summary>
    /// A handler that throws before its first await must not leave the span started — and therefore
    /// ambient — on a pooled subscriber thread, where it would silently adopt everything that thread
    /// ran afterwards.
    /// </summary>
    [Fact]
    public async Task AsyncHandlerThrowingSynchronously_StopsTheReceiveSpan()
    {
        const string CacheName = "backplane-receive-sync-throw";
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        var inner = new CapturingBackplane();

        await Wrap(inner, CacheName).SubscribeAsync(
            Subscription(incomingAsync: _ => throw new InvalidOperationException("boom")));

        Assert.Throws<InvalidOperationException>(() => inner.Subscription!.IncomingMessageHandlerAsync!(Message));

        Assert.Null(Activity.Current);
        var span = Assert.Single(OwnSpans(recorder, CacheName), a => a.OperationName == "cache.backplane.receive");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    /// <summary>
    /// The decorator cannot mutate a subscription — every property is read-only — so it rebuilds one,
    /// and a rebuild silently drops anything it forgets to copy. This asserts every field survives,
    /// including the connect handlers, which are passed through rather than wrapped.
    /// </summary>
    [Fact]
    public void Subscription_IsRebuiltWithEveryFieldPreserved()
    {
        var inner = new CapturingBackplane();
        Action<BackplaneConnectionInfo> connect = _ => { };
        Func<BackplaneConnectionInfo, ValueTask> connectAsync = _ => ValueTask.CompletedTask;

        Wrap(inner, "backplane-receive-fields")
            .Subscribe(Subscription(connect: connect, connectAsync: connectAsync));

        var subscription = inner.Subscription!;
        Assert.Equal("subscribed-cache", subscription.CacheName);
        Assert.Equal("instance-1", subscription.CacheInstanceId);
        Assert.Equal("channel-1", subscription.ChannelName);
        Assert.Same(connect, subscription.ConnectHandler);
        Assert.Same(connectAsync, subscription.ConnectHandlerAsync);
        Assert.NotNull(subscription.IncomingMessageHandler);
        Assert.NotNull(subscription.IncomingMessageHandlerAsync);
    }

    /// <summary>
    /// The previous test only checks the fields it already knows about, so it cannot fail for a field
    /// that does not exist yet — which is the actual risk. This one can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the rebuild carries is a constructor argument, because every property on this type
    /// is get-only. So the parameter list of the widest constructor <i>is</i> the set of things that
    /// can be lost, and a parameter added with a default value would leave the call in
    /// <c>InstrumentedBackplane.Instrument</c> compiling and silently dropping it on every
    /// subscription. Asserting the list catches that at the next package bump instead of in
    /// production, where the symptom would be a backplane that connects and never delivers.
    /// </para>
    /// <para>
    /// Properties are deliberately not the subject: <c>BackplaneSubscriptionOptions.Handler</c> is a
    /// get-only <c>[Obsolete]</c> alias with no constructor parameter behind it, so it is derived from
    /// what is copied rather than something that could go missing.
    /// </para>
    /// </remarks>
    [Fact]
    public void SubscriptionConstructor_TakesNothingTheRebuildDoesNotPass()
    {
        var actual = typeof(BackplaneSubscriptionOptions)
            .GetConstructors()
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .First()
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        string[] passedByInstrument =
        [
            "cacheName",
            "cacheInstanceId",
            "channelName",
            "connectHandler",
            "incomingMessageHandler",
            "connectHandlerAsync",
            "incomingMessageHandlerAsync"
        ];

        Assert.True(
            passedByInstrument.SequenceEqual(actual, StringComparer.Ordinal),
            $"BackplaneSubscriptionOptions' widest constructor now takes ({string.Join(", ", actual)}). "
            + "InstrumentedBackplane.Instrument rebuilds this type argument by argument, so anything "
            + "it does not pass is dropped on every subscription. Pass the new argument there, then "
            + "add it here.");
    }

    /// <summary>
    /// With tracing off the decorator has nothing to add, so the engine's own handler must be handed
    /// through untouched rather than wrapped in a delegate that will never start a span.
    /// </summary>
    [Fact]
    public void TracingDisabled_PassesTheEngineHandlerThroughUnwrapped()
    {
        var inner = new CapturingBackplane();
        var telemetry = new CacheTelemetryContext(new CachingOptions
        {
            CacheName = "backplane-receive-tracing-off",
            ApplicationPrefix = "tests",
            Observability = { EnableTracing = false }
        });
        Action<BackplaneMessage> incoming = _ => { };

        InstrumentedBackplane.Wrap(inner, telemetry).Subscribe(Subscription(incoming: incoming));

        Assert.Same(incoming, inner.Subscription!.IncomingMessageHandler);
    }

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
