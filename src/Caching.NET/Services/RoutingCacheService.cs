using Caching.NET.Abstractions;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Services;

/// <summary>
/// Internal routing cache service that delegates to the configured concrete cache mode
/// and applies <see cref="CacheOptions.KeyPrefix"/> to every key. Stampede coalescing is
/// implemented via a fixed <see cref="StripedLockManager"/> (no per-key allocation, no leak).
/// </summary>
internal sealed class RoutingCacheService : ICacheService, IRoutingCacheService, IAsyncDisposable, IDisposable
{
    private const string Mode = "Routing";
    private readonly IOptionsMonitor<CacheOptions> _optionsMonitor;
    private readonly CacheOptions _startupOptions;
    private readonly ILogger<RoutingCacheService> _logger;
    private readonly StripedLockManager _lockManager;
    private readonly StaleEntryTracker _staleTracker;
    private readonly StaleRefreshThrottle _throttle;
    private readonly InMemoryCacheService? _inMemory;
    private readonly RedisCacheService? _redis;
    private readonly HybridCacheService? _hybrid;
    private readonly string _keyPrefix;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _backgroundRefreshes = new();
    private int _backgroundRefreshId;
    private int _disposed;

    public RoutingCacheService(
        IOptionsMonitor<CacheOptions> optionsMonitor,
        ILogger<RoutingCacheService> logger,
        StripedLockManager lockManager,
        StaleEntryTracker staleTracker,
        StaleRefreshThrottle throttle,
        InMemoryCacheService? inMemory = null,
        RedisCacheService? redis = null,
        HybridCacheService? hybrid = null)
    {
        _optionsMonitor = optionsMonitor;
        _startupOptions = optionsMonitor.CurrentValue;
        _logger = logger;
        _lockManager = lockManager;
        _staleTracker = staleTracker;
        _throttle = throttle;
        _inMemory = inMemory;
        _redis = redis;
        _hybrid = hybrid;
        // Hybrid delegates prefixing to the L2 adapter's InstanceName (see ConfigureHybridCache) so the
        // prefix also covers HybridCache's tag/wildcard invalidation markers, which are created *below*
        // this routing layer. Prefixing here too would double-stamp Hybrid L2 keys, so routing steps aside
        // for Hybrid. InMemory/Redis keep routing-layer prefixing (it must cover their direct-multiplexer
        // and SCAN paths, which bypass the adapter).
        _keyPrefix = (_startupOptions.Mode == CacheMode.Hybrid || string.IsNullOrEmpty(_startupOptions.KeyPrefix))
            ? string.Empty
            : _startupOptions.KeyPrefix + ":";
    }

    private string PrependPrefix(string key) => _keyPrefix.Length == 0 ? key : _keyPrefix + key;

    // Every public entry point snapshots IOptionsMonitor.CurrentValue exactly once and threads that
    // snapshot through. CurrentValue is not a field read — it goes through the options cache on every
    // call — and reading it four times per get_or_create showed up in the hot-path allocation profile.
    // Snapshotting is also the more defensible semantics: one call sees one configuration, even if a
    // reload lands mid-call.
    private bool TryPreparePrefixedKey(string userKey, string operation, CacheOptions opts, out string prefixed)
    {
        var segment = userKey;
        if (opts.KeyTransformer is { } xf)
        {
            segment = xf(segment);
            if (string.IsNullOrWhiteSpace(segment))
            {
                _logger.RoutingKeyRejectedByTransformer(operation);
                prefixed = string.Empty;
                return false;
            }
        }

        if (opts.KeyValidator is { } vf && !vf(segment))
        {
            _logger.RoutingKeyRejectedByValidator(operation);
            prefixed = string.Empty;
            return false;
        }

        prefixed = PrependPrefix(segment);
        var max = opts.MaximumKeyLength;
        if (max > 0 && prefixed.Length > max)
        {
            _logger.RedisKeyTooLong(prefixed.Length, max, operation);
            prefixed = string.Empty;
            return false;
        }

        return true;
    }

    private static TimeSpan? ApplyJitter(TimeSpan? expiration, double? perCallPercentage, CacheOptions opts)
    {
        if (expiration is not { } ttl) return expiration;
        var pct = perCallPercentage ?? opts.TtlJitterPercentage;
        return TtlJitter.Apply(ttl, pct);
    }

    private static Task SetWithExpirationAsync<T>(
        ICacheService service, string prefixedKey, T value,
        TimeSpan? expiration, TimeSpan? sliding, TimeSpan? localExpiration,
        IReadOnlyList<string>? tags,
        CancellationToken ct) where T : notnull
    {
        if (sliding is null) return SetRoutedAsync(service, prefixedKey, value, expiration, localExpiration, tags, ct);
        if (service is InMemoryCacheService inMem)
        {
            var entry = new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration,
                SlidingExpiration = sliding,
            };
            return inMem.SetAsync(prefixedKey, value, entry, ct);
        }
        if (service is RedisCacheService redis)
            return redis.SetWithSlidingAsync(prefixedKey, value, expiration, sliding, ct);
        // Hybrid does not support sliding — drop silently. Tags are still honoured.
        return SetRoutedAsync(service, prefixedKey, value, expiration, localExpiration, tags, ct);
    }

    // Routes a write to the tag-aware Hybrid overload when tags are present, otherwise the core
    // ICacheService.SetAsync. Tags are a Hybrid-only capability; other modes ignore them.
    private static Task SetRoutedAsync<T>(
        ICacheService service, string prefixedKey, T value,
        TimeSpan? expiration, TimeSpan? localExpiration,
        IReadOnlyList<string>? tags, CancellationToken ct) where T : notnull
        => tags is { Count: > 0 } && service is HybridCacheService hybrid
            ? hybrid.SetAsync(prefixedKey, value, expiration, localExpiration, tags, ct)
            : service.SetAsync(prefixedKey, value, expiration, localExpiration, ct);

    // Routes a get-or-create to the tag-aware Hybrid overload when tags are present, otherwise the
    // core ICacheService.GetOrCreateAsync. Tags are a Hybrid-only capability; other modes ignore them.
    private static Task<T> GetOrCreateRoutedAsync<T>(
        ICacheService service, string prefixedKey,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration, TimeSpan? localExpiration,
        IReadOnlyList<string>? tags, CancellationToken ct) where T : notnull
        => tags is { Count: > 0 } && service is HybridCacheService hybrid
            ? hybrid.GetOrCreateAsync(prefixedKey, factory, expiration, localExpiration, tags, ct)
            : service.GetOrCreateAsync(prefixedKey, factory, expiration, localExpiration, ct);

    /// <inheritdoc />
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
        => GetOrCreateAsync(key, factory, callOptions: null, expiration, localExpiration, cancellationToken);

    /// <inheritdoc />
    // Argument validation sits in a non-async wrapper so it still throws on the calling thread rather
    // than through the returned Task — a fire-and-forget `_ = cache.GetOrCreateAsync(badKey, f)` must
    // fail loudly at the call site, not as an unobserved task exception. The wrapper also keeps the
    // whole call to a single async state machine.
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        return GetOrCreateCoreAsync(key, factory, callOptions, expiration, localExpiration, cancellationToken);
    }

    private async Task<T> GetOrCreateCoreAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration,
        TimeSpan? localExpiration,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "get_or_create", key);
        try
        {
            var originalFactory = factory;
            if (recorder is not null) factory = recorder.WrapFactory(factory);

            if (!opts.Enabled)
            {
                CacheInstruments.RecordMiss(Mode, "get_or_create", "Disabled");
                recorder?.MarkMissReason("Disabled");
                return await factory(cancellationToken);
            }

            if (!TryPreparePrefixedKey(key, "get_or_create", opts, out var prefixed))
            {
                CacheInstruments.RecordMiss(Mode, "get_or_create", "KeyRejected");
                recorder?.MarkMissReason("KeyRejected");
                return await factory(cancellationToken);
            }

            if ((callOptions?.BypassCache ?? false))
            {
                CacheInstruments.RecordMiss(Mode, "get_or_create", "Bypass");
                recorder?.MarkMissReason("Bypass");
                var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
                try
                {
                    return await factory(ct);
                }
                finally
                {
                    cts?.Dispose();
                }
            }

            var service = ResolveService(callOptions?.Mode);
            recorder?.SetMode(ModeNameOf(service));

            // Stale-while-revalidate: if the entry is past its logical expiry but within the stale
            // window, return the stale value immediately and schedule a background refresh.
            // Not supported for Hybrid mode (Hybrid manages its own L1/L2 revalidation internally).
            var allowStaleFor = callOptions?.AllowStaleFor;
            if (allowStaleFor is { } swWindow && !IsHybridMode() && _staleTracker.TryGet(prefixed, out var staleMeta))
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                if (nowTicks > staleMeta.AbsExpiresAtUtcTicks && nowTicks <= staleMeta.StaleUntilUtcTicks)
                {
                    var stale = await service.GetAsync<T>(prefixed, cancellationToken);
                    if (stale is not null)
                    {
                        CacheInstruments.RecordStaleServed(Mode, "get_or_create");
                        recorder?.MarkServedFromCache();
                        ScheduleBackgroundRefresh(key, prefixed, originalFactory, callOptions, expiration, localExpiration);
                        return stale;
                    }
                    _staleTracker.Forget(prefixed);
                }
                else if (nowTicks > staleMeta.StaleUntilUtcTicks)
                {
                    _staleTracker.Forget(prefixed);
                }
            }

            if ((callOptions?.CoalesceConcurrent ?? true))
            {
                var semaphore = _lockManager.GetLock(prefixed);
                var waitTask = semaphore.WaitAsync(cancellationToken);
                if (!waitTask.IsCompleted) recorder?.MarkCoalesced();
                await waitTask;
                try
                {
                    if ((callOptions?.ForceRefresh ?? false))
                    {
                        var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
                        try
                        {
                            T value = await factory(ct);
                            var jitteredExpiration = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage, opts);
                            await RoutingCacheService.SetWithExpirationAsync(service, prefixed, value, jitteredExpiration, callOptions?.SlidingExpiration, localExpiration, callOptions?.Tags, ct);
                            CacheInstruments.RecordSet(Mode);
                            return value;
                        }
                        finally
                        {
                            cts?.Dispose();
                        }
                    }

                    var innerCt = ApplyFactoryTimeout(cancellationToken, out var innerCts);
                    try
                    {
                        // When AllowStaleFor is configured, extend the underlying TTL so the entry
                        // remains readable during the stale window, and register metadata so future
                        // reads can detect the stale condition without a cache miss.
                        if (allowStaleFor is { } swrTtl && !IsHybridMode())
                        {
                            var swrResult = await GetOrCreateWithStaleWindowAsync(service, prefixed, factory, callOptions, expiration, localExpiration, swrTtl, opts, innerCt);
                            recorder?.MarkServedFromCache();
                            return swrResult;
                        }

                        var jitteredExp = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage, opts);
                        var routedResult = await GetOrCreateRoutedAsync(service, prefixed, factory, jitteredExp, localExpiration, callOptions?.Tags, innerCt);
                        recorder?.MarkServedFromCache();
                        return routedResult;
                    }
                    finally
                    {
                        innerCts?.Dispose();
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }

            if ((callOptions?.ForceRefresh ?? false))
            {
                var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
                try
                {
                    T value = await factory(ct);
                    var jitteredExpiration = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage, opts);
                    await RoutingCacheService.SetWithExpirationAsync(service, prefixed, value, jitteredExpiration, callOptions?.SlidingExpiration, localExpiration, callOptions?.Tags, ct);
                    CacheInstruments.RecordSet(Mode);
                    return value;
                }
                finally
                {
                    cts?.Dispose();
                }
            }

            // No-coalesce path: when AllowStaleFor is configured, use extended TTL + register metadata.
            if (allowStaleFor is { } swrNoLock && !IsHybridMode())
            {
                var noLockCtSwr = ApplyFactoryTimeout(cancellationToken, out var noLockCtsSwr);
                try
                {
                    var swrResult = await GetOrCreateWithStaleWindowAsync(service, prefixed, factory, callOptions, expiration, localExpiration, swrNoLock, opts, noLockCtSwr);
                    recorder?.MarkServedFromCache();
                    return swrResult;
                }
                finally
                {
                    noLockCtsSwr?.Dispose();
                }
            }

            var noLockCt = ApplyFactoryTimeout(cancellationToken, out var noLockCts);
            try
            {
                var jitteredExp = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage, opts);
                var routedResult = await GetOrCreateRoutedAsync(service, prefixed, factory, jitteredExp, localExpiration, callOptions?.Tags, noLockCt);
                recorder?.MarkServedFromCache();
                return routedResult;
            }
            finally
            {
                noLockCts?.Dispose();
            }
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
        => SetAsync(key, value, callOptions: null, expiration, localExpiration, cancellationToken);

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        return SetCoreAsync(key, value, callOptions, expiration, localExpiration, cancellationToken);
    }

    private async Task SetCoreAsync<T>(
        string key,
        T value,
        CacheCallOptions? callOptions,
        TimeSpan? expiration,
        TimeSpan? localExpiration,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "set", key);
        try
        {
            if (!opts.Enabled) return;
            if ((callOptions?.BypassCache ?? false)) return;
            if (!TryPreparePrefixedKey(key, "set", opts, out var prefixed)) return;
            var service = ResolveService(callOptions?.Mode);
            recorder?.SetMode(ModeNameOf(service));
            var jitteredExpiration = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage, opts);
            await RoutingCacheService.SetWithExpirationAsync(
                service, prefixed, value, jitteredExpiration, callOptions?.SlidingExpiration,
                localExpiration, callOptions?.Tags, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "remove", key);
        try
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!opts.Enabled) return;
            if (!TryPreparePrefixedKey(key, "remove", opts, out var prefixed)) return;
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            await service.RemoveAsync(prefixed, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Clears cache entries for this application, scoped by <see cref="CacheOptions.KeyPrefix"/>.
    /// InMemory clears the process memory cache; Redis SCANs and removes <c>{KeyPrefix}:*</c>; Hybrid
    /// invalidates via the reserved wildcard tag <c>"*"</c>. The Hybrid wildcard marker is namespaced per
    /// app because KeyPrefix is applied as the Hybrid L2 <c>InstanceName</c> (see ConfigureHybridCache), so
    /// apps sharing one Redis database do not invalidate each other when each uses a unique KeyPrefix.
    /// No-op when disabled.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "clear");
        try
        {
            if (!opts.Enabled) return;
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            switch (service)
            {
                case InMemoryCacheService inMemory:
                    await inMemory.ClearAsync(cancellationToken);
                    break;
                case HybridCacheService hybrid:
                    await hybrid.ClearAsync(cancellationToken);
                    break;
                case RedisCacheService redis:
                    await redis.ClearAsync(EscapeGlob(_keyPrefix) + "*", cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    // Escapes Redis glob metacharacters so a literal key prefix is matched verbatim by SCAN MATCH.
    private static string EscapeGlob(string literal)
    {
        if (literal.Length == 0) return literal;
        var sb = new StringBuilder(literal.Length + 8);
        foreach (var c in literal)
        {
            if (c is '\\' or '*' or '?' or '[' or ']') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "remove_by_tag");
        try
        {
            if (!opts.Enabled) return;
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            await service.RemoveByTagAsync(tag, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "remove_by_tag");
        try
        {
            if (!opts.Enabled) return;
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            await service.RemoveByTagAsync(tags, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        return GetCoreAsync<T>(key, cancellationToken);
    }

    private async Task<T?> GetCoreAsync<T>(string key, CancellationToken cancellationToken) where T : notnull
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "get", key);
        try
        {
            if (!opts.Enabled)
            {
                CacheInstruments.RecordMiss(Mode, "get", "Disabled");
                recorder?.MarkMissReason("Disabled");
                return default;
            }
            if (!TryPreparePrefixedKey(key, "get", opts, out var prefixed))
            {
                CacheInstruments.RecordMiss(Mode, "get", "KeyRejected");
                recorder?.MarkMissReason("KeyRejected");
                return default;
            }
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            // Read through the presence-aware probe: `value is not null` cannot express a miss when T
            // is a value type (a missing int and a cached 0 are both 0), which would report every
            // value-type miss as served_from=cache.
            if (service is ICacheReadProbe probe)
            {
                var result = await probe.TryGetAsync<T>(prefixed, cancellationToken);
                recorder?.MarkFound(result.Found);
                return result.Value;
            }
            var value = await service.GetAsync<T>(prefixed, cancellationToken);
            recorder?.MarkFound(value is not null);
            return value;
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<object?> GetAsync(string key, Type type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        ArgumentNullException.ThrowIfNull(type);
        return GetCoreAsync(key, type, cancellationToken);
    }

    private async Task<object?> GetCoreAsync(string key, Type type, CancellationToken cancellationToken)
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "get", key);
        try
        {
            if (!opts.Enabled)
            {
                CacheInstruments.RecordMiss(Mode, "get", "Disabled");
                recorder?.MarkMissReason("Disabled");
                return null;
            }
            if (!TryPreparePrefixedKey(key, "get", opts, out var prefixed))
            {
                CacheInstruments.RecordMiss(Mode, "get", "KeyRejected");
                recorder?.MarkMissReason("KeyRejected");
                return null;
            }
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            // The runtime-typed read returns object?, where null is unambiguously "absent" even for a
            // boxed value type, so no probe is needed here.
            var value = await service.GetAsync(prefixed, type, cancellationToken);
            recorder?.MarkFound(value is not null);
            return value;
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        return ExistsCoreAsync(key, cancellationToken);
    }

    private async Task<bool> ExistsCoreAsync(string key, CancellationToken cancellationToken)
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "exists", key);
        try
        {
            if (!opts.Enabled)
            {
                recorder?.MarkMissReason("Disabled");
                return false;
            }
            if (!TryPreparePrefixedKey(key, "exists", opts, out var prefixed))
            {
                recorder?.MarkMissReason("KeyRejected");
                return false;
            }
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            var present = await service.ExistsAsync(prefixed, cancellationToken);
            recorder?.MarkFound(present);
            return present;
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        return RefreshCoreAsync(key, factory, expiration, localExpiration, cancellationToken);
    }

    private async Task RefreshCoreAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration,
        TimeSpan? localExpiration,
        CancellationToken cancellationToken) where T : notnull
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "refresh", key);
        try
        {
            if (!opts.Enabled)
            {
                recorder?.MarkMissReason("Disabled");
                return;
            }
            if (!TryPreparePrefixedKey(key, "refresh", opts, out var prefixed))
            {
                recorder?.MarkMissReason("KeyRejected");
                return;
            }
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            await service.RefreshAsync(
                prefixed,
                recorder is null ? factory : recorder.WrapFactory(factory),
                expiration, localExpiration, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        return GetManyCoreAsync<T>(keys, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, T?>> GetManyCoreAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken) where T : notnull
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "get_many");
        try
        {
            if (!opts.Enabled)
            {
                recorder?.MarkBatch(0, 0);
                recorder?.MarkMissReason("Disabled");
                return new Dictionary<string, T?>();
            }

            var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            if (keyList.Length == 0)
            {
                recorder?.MarkBatch(0, 0);
                return new Dictionary<string, T?>();
            }

            var dict = new Dictionary<string, T?>(keyList.Length);
            var okKeys = new List<string>(keyList.Length);
            var okPrefixed = new List<string>(keyList.Length);
            foreach (var k in keyList)
            {
                if (TryPreparePrefixedKey(k, "get_many", opts, out var p))
                {
                    okKeys.Add(k);
                    okPrefixed.Add(p);
                }
                else
                {
                    dict[k] = default;
                }
            }

            if (okPrefixed.Count == 0)
            {
                recorder?.MarkBatch(0, dict.Count);
                recorder?.MarkMissReason("KeyRejected");
                return dict;
            }

            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));

            // Presence has to come from the backend, not from a null check on the returned value:
            // for a value-type T every miss deserializes to default(T), which is not null, so counting
            // non-nulls would report a batch of misses as a batch of hits.
            int hits;
            if (service is ICacheReadProbe probe)
            {
                var probed = await probe.TryGetManyAsync<T>(okPrefixed, cancellationToken);
                hits = 0;
                for (int i = 0; i < okKeys.Count; i++)
                {
                    if (probed.TryGetValue(okPrefixed[i], out var p) && p.Found)
                    {
                        dict[okKeys[i]] = p.Value;
                        hits++;
                    }
                    else
                    {
                        dict[okKeys[i]] = default;
                    }
                }
            }
            else
            {
                var inner = await service.GetManyAsync<T>(okPrefixed, cancellationToken);
                for (int i = 0; i < okKeys.Count; i++)
                    dict[okKeys[i]] = inner.TryGetValue(okPrefixed[i], out var v) ? v : default;
                hits = dict.Values.Count(v => v is not null);
            }

            // A key rejected before dispatch returned nothing to the caller, so it counts as a miss.
            recorder?.MarkBatch(hits, dict.Count - hits);
            return dict;
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        return SetManyCoreAsync(items, expiration, localExpiration, cancellationToken);
    }

    private async Task SetManyCoreAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration,
        TimeSpan? localExpiration,
        CancellationToken cancellationToken) where T : notnull
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "set_many");
        try
        {
            if (!opts.Enabled || items.Count == 0) return;
            var jitteredExpiration = ApplyJitter(expiration, null, opts);
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            var prefixed = new Dictionary<string, T>(items.Count);
            foreach (var kvp in items)
            {
                if (!TryPreparePrefixedKey(kvp.Key, "set_many", opts, out var p)) continue;
                prefixed[p] = kvp.Value;
            }
            if (prefixed.Count == 0) return;
            await service.SetManyAsync(prefixed, jitteredExpiration, localExpiration, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var opts = _optionsMonitor.CurrentValue;
        using var recorder = CacheCallRecorder.Start(Mode, opts, "remove_many");
        try
        {
            if (keys is null) return;
            if (!opts.Enabled) return;
            var prefixed = new List<string>();
            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (TryPreparePrefixedKey(k, "remove_many", opts, out var p)) prefixed.Add(p);
            }
            if (prefixed.Count == 0) return;
            var service = ResolveService(modeOverride: null);
            recorder?.SetMode(ModeNameOf(service));
            await service.RemoveManyAsync(prefixed, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCallFailure(recorder, ex, cancellationToken);
            throw;
        }
    }

    private bool IsHybridMode() => _startupOptions.Mode == CacheMode.Hybrid;

    private async Task<T> GetOrCreateWithStaleWindowAsync<T>(
        ICacheService service,
        string prefixed,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration,
        TimeSpan? localExpiration,
        TimeSpan staleWindow,
        CacheOptions opts,
        CancellationToken cancellationToken) where T : notnull
    {
        var rawAbsExp = callOptions?.AbsoluteExpiration ?? expiration ?? opts.DefaultExpiration;
        var jitteredAbsExp = ApplyJitter(rawAbsExp, callOptions?.JitterPercentage, opts) ?? rawAbsExp;
        var extendedTtl = jitteredAbsExp + staleWindow;
        bool factoryRan = false;
        T result = await service.GetOrCreateAsync(
            prefixed,
            async ct =>
            {
                factoryRan = true;
                return await factory(ct);
            },
            extendedTtl,
            localExpiration,
            cancellationToken);
        if (factoryRan)
            _staleTracker.Register(prefixed, jitteredAbsExp, staleWindow);
        return result;
    }

    private void ScheduleBackgroundRefresh<T>(
        string rawKey,
        string prefixedKey,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration,
        TimeSpan? localExpiration) where T : notnull
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_throttle.TryAcquire()) return;
        var shutdownToken = _shutdown.Token;

        // The refresh outlives the call that scheduled it, so it must not be a *child* of that call's
        // span: the parent ends first, and the consumer's request trace ends up holding a long-running
        // child it never waited for. Record the trigger as a link instead, and clear Activity.Current
        // across the Task.Run so ExecutionContext capture cannot re-parent it behind our back.
        var triggerContext = Activity.Current?.Context;
        var refreshId = Interlocked.Increment(ref _backgroundRefreshId);
        var previousActivity = Activity.Current;
        Activity.Current = null;
        Task refreshTask;
        try
        {
            refreshTask = Task.Run(async () =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // Hash the same (unprefixed) key the triggering get_or_create hashed, so the two spans
            // carry a matching cache.key_hash and can be correlated — the prefix is routing-layer
            // plumbing, not part of the logical key.
            var refreshOpts = _optionsMonitor.CurrentValue;
            using var recorder = CacheCallRecorder.Start(
                Mode, refreshOpts, "stale_refresh", rawKey, triggerContext);
            CacheInstruments.AddStaleRefreshInFlight(Mode, +1);
            var lockStripe = _lockManager.GetLock(prefixedKey);
            // Bound the wait so a stuck stripe-holder cannot pin a throttle slot indefinitely.
            var lockTimeout = refreshOpts.GetFactoryTimeout() ?? TimeSpan.FromSeconds(30);
            bool lockAcquired = false;
            try
            {
                lockAcquired = await lockStripe.WaitAsync(lockTimeout, shutdownToken);
                if (!lockAcquired)
                {
                    _logger.StaleRefreshLockTimeout(prefixedKey, lockTimeout.TotalMilliseconds);
                    CacheInstruments.RecordError(Mode, "stale_refresh", "Timeout");
                    recorder?.MarkError("Timeout", thrownToCaller: false);
                    return;
                }
                var factoryCt = ApplyFactoryTimeout(shutdownToken, out var cts);
                T value;
                try
                {
                    value = await (recorder is null ? factory : recorder.WrapFactory(factory))(factoryCt);
                }
                finally
                {
                    cts?.Dispose();
                }
                var inner = ResolveService(callOptions?.Mode);
                recorder?.SetMode(ModeNameOf(inner));
                var abs = callOptions?.AbsoluteExpiration ?? expiration ?? refreshOpts.DefaultExpiration;
                var staleFor = callOptions?.AllowStaleFor ?? TimeSpan.Zero;
                var ttl = abs + staleFor;
                await inner.SetAsync(prefixedKey, value, ttl, localExpiration, shutdownToken);
                if (staleFor > TimeSpan.Zero)
                    _staleTracker.Register(prefixedKey, abs, staleFor);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Swallow expected shutdown cancellation.
                recorder?.MarkError("Canceled", thrownToCaller: false);
            }
            catch (Exception ex)
            {
                var kind = ClassifyError(ex, shutdownToken);
                _logger.StaleRefreshFailed(prefixedKey, ex);
                CacheInstruments.RecordError(Mode, "stale_refresh", kind);
                recorder?.MarkError(kind, thrownToCaller: false);
            }
            finally
            {
                if (lockAcquired) lockStripe.Release();
                _throttle.Release();
                CacheInstruments.AddStaleRefreshInFlight(Mode, -1);
                _backgroundRefreshes.TryRemove(refreshId, out _);
            }
        });
        }
        finally
        {
            Activity.Current = previousActivity;
        }
        _backgroundRefreshes[refreshId] = refreshTask;
    }

    // Tags the span for a call that failed. A caller-supplied factory throwing is the source failing,
    // not the cache: the span is marked Error so the failure is visible, but no cache.error_kind is
    // emitted, because a cache-error dashboard that counts every flaky upstream is worse than useless.
    private static void RecordCallFailure(CacheCallRecorder? recorder, Exception ex, CancellationToken callerToken)
    {
        if (recorder is null) return;
        // Cancellation still classifies normally — the distinction that matters there is caller-cancel
        // vs. our own FactoryTimeout, not who threw.
        if (ex is not OperationCanceledException && recorder.IsFactoryException(ex))
        {
            recorder.MarkFactoryFault();
            return;
        }
        recorder.MarkError(ClassifyError(ex, callerToken), thrownToCaller: true);
    }

    private static string ClassifyError(Exception ex, CancellationToken callerToken) => ex switch
    {
        // RedisTimeoutException derives from TimeoutException — listing TimeoutException covers both.
        TimeoutException => "Timeout",
        OperationCanceledException when callerToken.IsCancellationRequested => "Canceled",
        // The caller's token is still live, so the token that fired was the linked one CacheOptions
        // .FactoryTimeout cancels. Reporting that as "Canceled" hid every blown factory timeout behind
        // a status the recorder deliberately refuses to mark as an error.
        OperationCanceledException => "Timeout",
        StackExchange.Redis.RedisConnectionException => "ConnectionFailed",
        Polly.CircuitBreaker.BrokenCircuitException => "CircuitOpen",
        System.Text.Json.JsonException => "Serialization",
        MessagePack.MessagePackSerializationException => "Serialization",
        _ => "Unknown"
    };

    // The mode tag reports which backend actually handled the call. Short-circuit paths that never
    // reach a backend keep the "Routing" value, matching the existing counter behavior.
    private static string ModeNameOf(ICacheService service) => service switch
    {
        InMemoryCacheService => "InMemory",
        RedisCacheService => "Redis",
        HybridCacheService => "Hybrid",
        _ => Mode,
    };

    private ICacheService ResolveService(CacheMode? modeOverride)
    {
        var mode = modeOverride ?? _startupOptions.Mode;

        return mode switch
        {
            CacheMode.InMemory when _inMemory is not null => _inMemory,
            CacheMode.Redis when _redis is not null => _redis,
            CacheMode.Hybrid when _hybrid is not null => _hybrid,
            _ => ResolveDefaultService()
        };
    }

    private ICacheService ResolveDefaultService()
    {
        if (_startupOptions.Mode == CacheMode.InMemory && _inMemory is not null) return _inMemory;
        if (_startupOptions.Mode == CacheMode.Redis && _redis is not null) return _redis;
        if (_startupOptions.Mode == CacheMode.Hybrid && _hybrid is not null) return _hybrid;
        if (_inMemory is not null) return _inMemory;
        if (_redis is not null) return _redis;
        if (_hybrid is not null) return _hybrid;

        throw new InvalidOperationException(
            $"No underlying cache service is available for RoutingCacheService. Configured mode: {_startupOptions.Mode}. " +
            "Ensure AddCaching was called with valid configuration and that the configured mode's dependencies (e.g., Redis for Redis mode) are registered.");
    }

    private CancellationToken ApplyFactoryTimeout(CancellationToken cancellationToken, out CancellationTokenSource? cts)
    {
        var timeout = _startupOptions.GetFactoryTimeout();
        if (timeout is not { } t)
        {
            cts = null;
            return cancellationToken;
        }
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(t);
        return cts.Token;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdown.Cancel();
        Task[] inflight = _backgroundRefreshes.Values.ToArray();
        if (inflight.Length > 0)
        {
            try
            {
                await Task.WhenAll(inflight);
            }
            catch
            {
                // Individual refresh tasks self-log failures; swallow here to keep disposal safe.
            }
        }

        _shutdown.Dispose();
    }

    public void Dispose()
    {
        Task.Run(async () => await DisposeAsync()).GetAwaiter().GetResult();
    }
}

/// <summary>
/// Internal contract used by extension methods to access per-call overloads
/// without exposing them on the public <see cref="ICacheService"/> interface.
/// </summary>
internal interface IRoutingCacheService
{
    Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull;

    Task SetAsync<T>(
        string key,
        T value,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull;

    Task ClearAsync(CancellationToken cancellationToken = default);
}
