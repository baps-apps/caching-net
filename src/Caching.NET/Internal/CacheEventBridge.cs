using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Events;

namespace Caching.NET.Internal;

/// <summary>
/// Translates the cache engine's internal event stream into Caching.NET-branded metrics.
/// </summary>
/// <remarks>
/// <para>
/// This bridges the events no other Caching.NET component can observe correctly: fail-safe,
/// eager-refresh and background-factory outcomes, engine-level eviction, and the distributed/backplane
/// health signals — plus foreground <c>factory.executions</c>/factory errors, which live here rather
/// than on <see cref="FusionCacheService"/> for a specific reason (see below). Hits, misses,
/// operations and foreground invalidations are owned by <see cref="FusionCacheService"/>; per-layer
/// duration is owned by the layer decorators (<see cref="InstrumentedMemoryCache"/>,
/// <see cref="InstrumentedDistributedCache"/>). Each signal has exactly one producer — recording the
/// same outcome from two places double-counts it inside one meter. Handlers run on the engine's
/// background event pump, so they never add latency to a cache call, and they only receive cache keys
/// and outcome flags — no cached values ever reach telemetry.
/// </para>
/// <para>
/// <b>Why factory accounting lives here and not on the adapter's own factory wrapper:</b> the engine
/// reuses the exact <c>Func</c> delegate a caller passes to <c>GetOrSetAsync</c>/<c>GetOrSet</c> for
/// background completions too — eager refresh and timed-out-factory background completion invoke the
/// very same closure, on a background thread, after the foreground call has already returned. A
/// factory wrapper inside <see cref="FusionCacheService"/> cannot tell which invocation it is
/// (<c>FusionCacheFactoryExecutionContext{T}</c> exposes no such flag — <c>HasStaleValue</c> is not a
/// discriminator, since a foreground fail-safe-eligible refresh has one too), so recording there
/// unconditionally double-counts every eager-refresh cycle: measured over 60 cycles, 120 real factory
/// invocations produced <c>background=False → 120, background=True → 60</c> (180 records for 120
/// executions) when both the wrapper and this bridge recorded. The engine itself does not have that
/// ambiguity — it knows which of its own code paths triggered the call — so
/// <see cref="OnFactorySuccess"/>/<see cref="OnFactoryError"/> and
/// <see cref="OnBackgroundFactorySuccess"/>/<see cref="OnBackgroundFactoryError"/> are the sole,
/// correctly-tagged producers of <c>caching.net.factory.executions</c> and the <c>factory</c> layer of
/// <c>caching.net.errors</c>. <see cref="FusionCacheService"/> keeps timing the delegate via
/// <c>RecordLayer(CacheLayers.Factory, …)</c> — it is the only component that can — and keeps its own
/// <c>cache.factory</c> span, but records neither counter.
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

    private void OnFactorySuccess(object? sender, FusionCacheEntryEventArgs e)
    {
        if (IsEngineInternalTagKey(e.Key))
        {
            return;
        }

        _telemetry.RecordFactoryExecution(succeeded: true, background: false);
    }

    private void OnFactoryError(object? sender, FusionCacheEntryEventArgs e)
    {
        if (IsEngineInternalTagKey(e.Key))
        {
            return;
        }

        _telemetry.RecordFactoryExecution(succeeded: false, background: false);
        _telemetry.RecordError(CacheLayers.Factory, "FactoryError");
    }

    // These three carry the same engine-internal-key filter as the four factory handlers, for the
    // same reason: the engine drives its own tag bookkeeping through internal GetOrSet<long> calls,
    // and those can time out, activate fail-safe or eager-refresh exactly like an application
    // factory. Counting them tells an operator the application's factory is in trouble when nothing
    // of the application's ran.
    private void OnFactorySyntheticTimeout(object? sender, FusionCacheEntryEventArgs e)
    {
        if (IsEngineInternalTagKey(e.Key))
        {
            return;
        }

        _telemetry.RecordError(CacheLayers.Factory, nameof(SyntheticTimeoutException));
    }

    private void OnFailSafeActivate(object? sender, FusionCacheEntryEventArgs e)
    {
        if (IsEngineInternalTagKey(e.Key))
        {
            return;
        }

        _telemetry.RecordFailSafeServed();
    }

    private void OnEagerRefresh(object? sender, FusionCacheEntryEventArgs e)
    {
        if (IsEngineInternalTagKey(e.Key))
        {
            return;
        }

        _telemetry.RecordBackgroundOperation("eager_refresh", succeeded: true);
    }

    private void OnBackgroundFactorySuccess(object? sender, FusionCacheEntryEventArgs e)
    {
        if (IsEngineInternalTagKey(e.Key))
        {
            return;
        }

        _telemetry.RecordFactoryExecution(succeeded: true, background: true);
    }

    private void OnBackgroundFactoryError(object? sender, FusionCacheEntryEventArgs e)
    {
        if (IsEngineInternalTagKey(e.Key))
        {
            return;
        }

        _telemetry.RecordFactoryExecution(succeeded: false, background: true);
        _telemetry.RecordError(CacheLayers.Factory, "BackgroundFactoryError");
    }

    // Its own instrument, not caching.net.invalidations and emphatically not the adapter-owned
    // caching.net.operations: MemoryCache raises an eviction for Removed and Replaced as well as
    // expiry, so counting one here on a counter the adapter also writes books the same logical
    // action twice (measured: 5 sets + 5 removes reported 15 operations and 10 invalidations for
    // 10 operations and 5 invalidations).
    private void OnEviction(object? sender, FusionCacheEntryEvictionEventArgs e)
        => _telemetry.RecordEviction();

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

    /// <summary>
    /// Whether <paramref name="key"/> belongs to the engine's own tag bookkeeping rather than to an
    /// application cache entry. FusionCache maintains tag state (per-tag clear markers, and the
    /// process-wide "clear all"/"expire all" markers) through its own internal
    /// <c>GetOrSetAsync&lt;long&gt;</c> calls, keyed under this prefix — real factory executions, just
    /// the engine's, not the application's. The four factory handlers below must not count them:
    /// measured on a cache that never runs an application factory (only <c>SetAsync</c> +
    /// <c>GetOrDefaultAsync</c>), <c>caching.net.factory.executions{background=false}</c> read 2
    /// before this filter — a false positive for an operator alerting on "any factory execution means
    /// the cache is missing." A substring check, not <c>StartsWith</c>: the engine prepends the
    /// configured <c>CacheKeyPrefix</c> (Caching.NET's application/cache-name prefix) ahead of its own
    /// tag prefix, so the raw key observed here is <c>{prefix}__fc:t:!</c>, not <c>__fc:t:!</c> —
    /// confirmed by logging the raw key on this exact scenario before settling on <c>Contains</c>.
    /// References <c>FusionCacheInternalStrings.DefaultTagCacheKeyPrefix</c> (the engine's own
    /// constant, `"__fc:t:"` as of FusionCache 2.6.0) rather than a private literal:
    /// <see cref="CacheEngineFactory"/> never touches <c>InternalStrings</c>, so this is always the
    /// live value the engine itself uses, not a copy that can drift out of sync with it.
    /// </summary>
    private static bool IsEngineInternalTagKey(string key)
        => key.Contains(FusionCacheInternalStrings.DefaultTagCacheKeyPrefix, StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var events = _events;

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
