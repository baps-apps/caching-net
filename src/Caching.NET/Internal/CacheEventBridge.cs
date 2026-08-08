using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Events;

namespace Caching.NET.Internal;

/// <summary>
/// Translates the cache engine's internal event stream into Caching.NET-branded metrics.
/// </summary>
/// <remarks>
/// <para>
/// This is how Caching.NET publishes hit/miss/factory/fail-safe/error counters under its own meter
/// without wrapping a single cache method. Handlers run on the engine's background event pump, so
/// they never add latency to a cache call, and they only receive cache keys and outcome flags —
/// no cached values ever reach telemetry.
/// </para>
/// <para>
/// Subscriptions live for the lifetime of the cache instance and are released on dispose.
/// </para>
/// </remarks>
internal sealed class CacheEventBridge : IDisposable
{
    // The hub is captured at attach time rather than read from the cache on dispose: the container
    // may already have disposed the cache by the time this runs, and unsubscribing must never throw
    // during shutdown.
    private readonly FusionCacheEventsHub _events;
    private readonly CacheTelemetryContext _telemetry;
    private bool _disposed;

    private CacheEventBridge(IFusionCache cache, CacheTelemetryContext telemetry)
    {
        _events = cache.Events;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Subscribes to the engine's event hub, or returns <c>null</c> when metrics are disabled.
    /// </summary>
    /// <remarks>
    /// Every subscription makes the engine build event arguments and queue a background dispatch for
    /// each operation. With metrics off the handlers would only return immediately, so the cheaper
    /// and more honest answer is not to subscribe at all.
    /// </remarks>
    public static CacheEventBridge? Attach(IFusionCache cache, CacheTelemetryContext telemetry)
    {
        if (!telemetry.MetricsEnabled)
        {
            return null;
        }

        var bridge = new CacheEventBridge(cache, telemetry);
        bridge.Subscribe();
        return bridge;
    }

    private void Subscribe()
    {
        var events = _events;

        events.Hit += OnHit;
        events.Miss += OnMiss;
        events.Set += OnSet;
        events.Remove += OnRemove;
        events.RemoveByTag += OnRemoveByTag;
        events.Clear += OnClear;
        events.Expire += OnExpire;

        events.FactorySuccess += OnFactorySuccess;
        events.FactoryError += OnFactoryError;
        events.FactorySyntheticTimeout += OnFactorySyntheticTimeout;
        events.FailSafeActivate += OnFailSafeActivate;
        events.EagerRefresh += OnEagerRefresh;
        events.BackgroundFactorySuccess += OnBackgroundFactorySuccess;
        events.BackgroundFactoryError += OnBackgroundFactoryError;

        events.Memory.Eviction += OnEviction;
        events.Distributed.SerializationError += OnSerializationError;
        events.Distributed.DeserializationError += OnDeserializationError;
        events.Distributed.CircuitBreakerChange += OnDistributedCircuitBreakerChange;
        events.Backplane.CircuitBreakerChange += OnBackplaneCircuitBreakerChange;
        events.Backplane.MessagePublished += OnBackplaneMessagePublished;
        events.Backplane.MessageReceived += OnBackplaneMessageReceived;
    }

    private void OnHit(object? sender, FusionCacheEntryHitEventArgs e)
    {
        // The engine does not report which level answered on the common path; Hybrid resolves L1
        // first by construction, and the distributed-level meter reports L2 activity separately.
        var layer = _telemetry.Mode == nameof(Options.CacheMode.Redis) ? CacheLayers.Redis : CacheLayers.Memory;

        // A stale read is still a read served from the cache, so it belongs in the hit/operation
        // counters. The fail-safe counter is incremented by the FailSafeActivate handler alone:
        // counting it here as well double-counted every activation.
        _telemetry.RecordHit("get", layer, stale: e.IsStale);
    }

    private void OnMiss(object? sender, FusionCacheEntryEventArgs e) => _telemetry.RecordMiss("get");

    private void OnSet(object? sender, FusionCacheEntryEventArgs e) => _telemetry.RecordSet("set");

    private void OnRemove(object? sender, FusionCacheEntryEventArgs e) => _telemetry.RecordInvalidation("remove");

    private void OnExpire(object? sender, FusionCacheEntryEventArgs e) => _telemetry.RecordInvalidation("expire");

    private void OnRemoveByTag(object? sender, FusionCacheTagEventArgs e) => _telemetry.RecordInvalidation("remove_by_tag");

    private void OnClear(object? sender, EventArgs e) => _telemetry.RecordInvalidation("clear");

    private void OnFactorySuccess(object? sender, FusionCacheEntryEventArgs e)
        => _telemetry.RecordFactoryExecution(succeeded: true, background: false);

    private void OnFactoryError(object? sender, FusionCacheEntryEventArgs e)
    {
        _telemetry.RecordFactoryExecution(succeeded: false, background: false);
        _telemetry.RecordError(CacheLayers.Factory, "FactoryError");
    }

    private void OnFactorySyntheticTimeout(object? sender, FusionCacheEntryEventArgs e)
        => _telemetry.RecordError(CacheLayers.Factory, nameof(SyntheticTimeoutException));

    private void OnFailSafeActivate(object? sender, FusionCacheEntryEventArgs e) => _telemetry.RecordFailSafeServed();

    private void OnEagerRefresh(object? sender, FusionCacheEntryEventArgs e)
        => _telemetry.RecordBackgroundOperation("eager_refresh", succeeded: true);

    private void OnBackgroundFactorySuccess(object? sender, FusionCacheEntryEventArgs e)
        => _telemetry.RecordFactoryExecution(succeeded: true, background: true);

    private void OnBackgroundFactoryError(object? sender, FusionCacheEntryEventArgs e)
    {
        _telemetry.RecordFactoryExecution(succeeded: false, background: true);
        _telemetry.RecordError(CacheLayers.Factory, "BackgroundFactoryError");
    }

    private void OnEviction(object? sender, FusionCacheEntryEvictionEventArgs e)
        => _telemetry.RecordInvalidation("eviction");

    private void OnSerializationError(object? sender, FusionCacheEntryEventArgs e)
        => _telemetry.RecordError(CacheLayers.Redis, "SerializationError");

    private void OnDeserializationError(object? sender, FusionCacheEntryEventArgs e)
        => _telemetry.RecordError(CacheLayers.Redis, "DeserializationError");

    private void OnDistributedCircuitBreakerChange(object? sender, FusionCacheCircuitBreakerChangeEventArgs e)
    {
        if (!e.IsClosed)
        {
            _telemetry.RecordError(CacheLayers.Redis, "CircuitBreakerOpen");
        }
    }

    private void OnBackplaneCircuitBreakerChange(object? sender, FusionCacheCircuitBreakerChangeEventArgs e)
    {
        if (!e.IsClosed)
        {
            _telemetry.RecordError(CacheLayers.Backplane, "CircuitBreakerOpen");
        }
    }

    private void OnBackplaneMessagePublished(object? sender, FusionCacheBackplaneMessageEventArgs e)
        => _telemetry.RecordBackgroundOperation("backplane_publish", succeeded: true);

    private void OnBackplaneMessageReceived(object? sender, FusionCacheBackplaneMessageEventArgs e)
        => _telemetry.RecordBackgroundOperation("backplane_receive", succeeded: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var events = _events;

        events.Hit -= OnHit;
        events.Miss -= OnMiss;
        events.Set -= OnSet;
        events.Remove -= OnRemove;
        events.RemoveByTag -= OnRemoveByTag;
        events.Clear -= OnClear;
        events.Expire -= OnExpire;

        events.FactorySuccess -= OnFactorySuccess;
        events.FactoryError -= OnFactoryError;
        events.FactorySyntheticTimeout -= OnFactorySyntheticTimeout;
        events.FailSafeActivate -= OnFailSafeActivate;
        events.EagerRefresh -= OnEagerRefresh;
        events.BackgroundFactorySuccess -= OnBackgroundFactorySuccess;
        events.BackgroundFactoryError -= OnBackgroundFactoryError;

        events.Memory.Eviction -= OnEviction;
        events.Distributed.SerializationError -= OnSerializationError;
        events.Distributed.DeserializationError -= OnDeserializationError;
        events.Distributed.CircuitBreakerChange -= OnDistributedCircuitBreakerChange;
        events.Backplane.CircuitBreakerChange -= OnBackplaneCircuitBreakerChange;
        events.Backplane.MessagePublished -= OnBackplaneMessagePublished;
        events.Backplane.MessageReceived -= OnBackplaneMessageReceived;
    }
}
