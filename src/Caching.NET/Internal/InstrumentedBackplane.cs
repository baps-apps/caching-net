using System.Diagnostics;
using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace Caching.NET.Internal;

/// <summary>
/// Counts backplane failures on <c>caching.net.backplane.errors</c>, then lets the exception
/// continue to the engine untouched.
/// </summary>
/// <remarks>
/// <para>
/// The engine's event hub exposes backplane <i>circuit-breaker transitions</i>, publishes and
/// receives — but no failure event. Deriving the error counter from circuit-breaker transitions
/// alone left it reading zero through an entire Redis outage: measured at 296 failed publishes over
/// 30&#160;seconds with no measurement recorded, which makes it useless as the thing an operator
/// alerts on. Six methods of pass-through here is the smallest place that can see a backplane
/// failure at all.
/// </para>
/// <para>
/// This decorates <see cref="IFusionCacheBackplane"/> — an infrastructure port with six members —
/// not the cache operation contract. Nothing is swallowed, retried or reordered: the engine keeps
/// full control of circuit breaking, auto-recovery and error log levels, and behaviour is identical
/// with the counter switched off.
/// </para>
/// </remarks>
internal sealed class InstrumentedBackplane : IFusionCacheBackplane
{
    private readonly IFusionCacheBackplane _inner;
    private readonly CacheTelemetryContext _telemetry;

    private InstrumentedBackplane(IFusionCacheBackplane inner, CacheTelemetryContext telemetry)
    {
        _inner = inner;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Wraps <paramref name="backplane"/>, or returns it unchanged when neither metrics nor tracing
    /// is enabled, so a cache with telemetry disabled pays nothing at all.
    /// </summary>
    public static IFusionCacheBackplane Wrap(IFusionCacheBackplane backplane, CacheTelemetryContext telemetry)
        => telemetry.MetricsEnabled || telemetry.TracingEnabled
            ? new InstrumentedBackplane(backplane, telemetry)
            : backplane;

    public void Subscribe(BackplaneSubscriptionOptions options)
    {
        try
        {
            _inner.Subscribe(Instrument(options));
        }
        catch (Exception ex)
        {
            Record("subscribe", ex);
            throw;
        }
    }

    public async ValueTask SubscribeAsync(BackplaneSubscriptionOptions options)
    {
        try
        {
            await _inner.SubscribeAsync(Instrument(options)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Record("subscribe", ex);
            throw;
        }
    }

    public void Unsubscribe()
    {
        try
        {
            _inner.Unsubscribe();
        }
        catch (Exception ex)
        {
            Record("unsubscribe", ex);
            throw;
        }
    }

    public async ValueTask UnsubscribeAsync()
    {
        try
        {
            await _inner.UnsubscribeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Record("unsubscribe", ex);
            throw;
        }
    }

    public void Publish(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
    {
        using var activity = _telemetry.StartLayerActivity("cache.backplane.publish");
        activity?.SetTag(CacheTelemetryAttributes.BackgroundOperation, true);

        try
        {
            _inner.Publish(message, options, token);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            Record("publish", ex);
            throw;
        }
    }

    public async ValueTask PublishAsync(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
    {
        using var activity = _telemetry.StartLayerActivity("cache.backplane.publish");
        activity?.SetTag(CacheTelemetryAttributes.BackgroundOperation, true);

        try
        {
            await _inner.PublishAsync(message, options, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            Record("publish", ex);
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the subscription with the engine's incoming-message handlers wrapped in a
    /// <c>cache.backplane.receive</c> span, so the local invalidation work a remote message causes
    /// has a parent instead of scattering into one root trace per evicted entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The span is a <i>local root</i>, not a remote link. The message's causal parent is a span in
    /// the publishing process, and the engine's wire format — source id, timestamp, action, cache key
    /// — has no field for trace context, so there is nothing to continue a trace from. Tagging it as
    /// a background operation says so rather than implying it belongs to a request.
    /// </para>
    /// <para>
    /// Root by construction rather than by luck: the subscriber callback runs on a thread whose
    /// ambient context flowed in from wherever the subscription was established, so
    /// <see cref="Activity.Current"/> there can hold a long-finished request span. Inheriting it would
    /// attach every invalidation this instance receives, forever, to one arbitrary request.
    /// <see cref="Telemetry.CacheTelemetryContext.StartRootActivity"/> is what prevents that.
    /// </para>
    /// <para>
    /// Every property on <see cref="BackplaneSubscriptionOptions"/> is read-only, so instrumenting it
    /// means constructing a new one. That makes this the one place in the file that depends on the
    /// engine's constructor shape rather than only on its interface: a property added by a future
    /// engine version would be dropped silently here, so
    /// <c>InstrumentedBackplaneReceiveTests.Subscription_HasNoPropertyTheRebuildCouldDrop</c> asserts
    /// the type's property set against the one this rebuild copies.
    /// </para>
    /// <para>
    /// With tracing off, the original instance is handed through untouched — no rebuild, no wrapper
    /// delegate, nothing for the engine to call through on a path that can never produce a span.
    /// </para>
    /// </remarks>
    private BackplaneSubscriptionOptions Instrument(BackplaneSubscriptionOptions options)
    {
        if (!_telemetry.TracingEnabled)
        {
            return options;
        }

        var incoming = options.IncomingMessageHandler;
        var incomingAsync = options.IncomingMessageHandlerAsync;

        return new BackplaneSubscriptionOptions(
            options.CacheName,
            options.CacheInstanceId,
            options.ChannelName,
            options.ConnectHandler,
            incoming is null ? null : message => Receive(incoming, message),
            options.ConnectHandlerAsync,
            incomingAsync is null ? null : message => ReceiveAsync(incomingAsync, message));
    }

    private void Receive(Action<BackplaneMessage> incoming, BackplaneMessage message)
    {
        var activity = StartReceiveActivity();
        if (activity is null)
        {
            incoming(message);
            return;
        }

        try
        {
            incoming(message);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            throw;
        }
        finally
        {
            activity.Dispose();
        }
    }

    /// <summary>
    /// Deliberately not an <c>async</c> method. Tracing can be enabled with no listener attached — the
    /// steady state of any process that has not been asked for traces — and on that path this returns
    /// the engine's own <see cref="ValueTask"/> straight through, so delivery costs a call and nothing
    /// else. An <c>async</c> wrapper would build a state machine per received message whether or not a
    /// span was ever created.
    /// </summary>
    private ValueTask ReceiveAsync(Func<BackplaneMessage, ValueTask> incomingAsync, BackplaneMessage message)
    {
        var activity = StartReceiveActivity();
        if (activity is null)
        {
            return incomingAsync(message);
        }

        // Invoked inside its own try: a handler that throws synchronously would otherwise escape
        // before the await, leaving the span started and ambient on a pooled thread forever.
        ValueTask pending;
        try
        {
            pending = incomingAsync(message);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            activity.Dispose();
            throw;
        }

        return AwaitReceive(activity, pending);
    }

    private static async ValueTask AwaitReceive(Activity activity, ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            throw;
        }
        finally
        {
            activity.Dispose();
        }
    }

    private Activity? StartReceiveActivity()
    {
        var activity = _telemetry.StartRootActivity("cache.backplane.receive");
        activity?.SetTag(CacheTelemetryAttributes.BackgroundOperation, true);
        return activity;
    }

    // A cancelled operation is a caller or shutdown decision, not a backplane fault, and counting it
    // as one would make every deployment look like an outage.
    private void Record(string operation, Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return;
        }

        _telemetry.RecordError(CacheLayers.Backplane, exception.GetType().Name);
        _telemetry.RecordBackgroundOperation($"backplane_{operation}", succeeded: false);
    }

    private static void MarkError(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(CacheTelemetryAttributes.ErrorType, ex.GetType().Name);
    }
}
