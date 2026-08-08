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
            _inner.Subscribe(options);
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
            await _inner.SubscribeAsync(options).ConfigureAwait(false);
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
        using var activity = _telemetry.StartActivity("cache.backplane.publish");
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
        using var activity = _telemetry.StartActivity("cache.backplane.publish");
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
