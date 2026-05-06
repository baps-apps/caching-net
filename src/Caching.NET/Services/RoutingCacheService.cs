using Caching.NET.Abstractions;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Services;

/// <summary>
/// Internal routing cache service that delegates to the configured concrete cache mode
/// and applies <see cref="CacheOptions.KeyPrefix"/> to every key. Stampede coalescing is
/// implemented via a fixed <see cref="StripedLockManager"/> (no per-key allocation, no leak).
/// </summary>
internal sealed class RoutingCacheService : ICacheService, IRoutingCacheService
{
    private const string Mode = "Routing";
    private readonly IOptionsMonitor<CacheOptions> _optionsMonitor;
    private readonly CacheOptions _startupOptions;
    private readonly ILogger<RoutingCacheService> _logger;
    private readonly StripedLockManager _lockManager;
    private readonly InMemoryCacheService? _inMemory;
    private readonly RedisCacheService? _redis;
    private readonly HybridCacheService? _hybrid;
    private readonly string _keyPrefix;

    public RoutingCacheService(
        IOptionsMonitor<CacheOptions> optionsMonitor,
        ILogger<RoutingCacheService> logger,
        StripedLockManager lockManager,
        InMemoryCacheService? inMemory = null,
        RedisCacheService? redis = null,
        HybridCacheService? hybrid = null)
    {
        _optionsMonitor = optionsMonitor;
        _startupOptions = optionsMonitor.CurrentValue;
        _logger = logger;
        _lockManager = lockManager;
        _inMemory = inMemory;
        _redis = redis;
        _hybrid = hybrid;
        _keyPrefix = string.IsNullOrEmpty(_startupOptions.KeyPrefix) ? string.Empty : _startupOptions.KeyPrefix + ":";
    }

    private bool IsDisabled => !_optionsMonitor.CurrentValue.Enabled;

    private string PrependPrefix(string key) => _keyPrefix.Length == 0 ? key : _keyPrefix + key;

    private TimeSpan? ApplyJitter(TimeSpan? expiration, double? perCallPercentage)
    {
        if (expiration is not { } ttl) return expiration;
        var pct = perCallPercentage ?? _optionsMonitor.CurrentValue.TtlJitterPercentage;
        return TtlJitter.Apply(ttl, pct);
    }

    private Task SetWithExpirationAsync<T>(
        ICacheService service, string prefixedKey, T value,
        TimeSpan? expiration, TimeSpan? sliding, TimeSpan? localExpiration,
        CancellationToken ct) where T : notnull
    {
        if (sliding is null) return service.SetAsync(prefixedKey, value, expiration, localExpiration, ct);
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
        // Hybrid does not support sliding — drop silently.
        return service.SetAsync(prefixedKey, value, expiration, localExpiration, ct);
    }

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
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));

        if (IsDisabled)
        {
            CacheInstruments.RecordMiss(Mode, "get_or_create", "Disabled");
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        var prefixed = PrependPrefix(key);

        if ((callOptions?.BypassCache ?? false))
        {
            CacheInstruments.RecordMiss(Mode, "get_or_create", "Bypass");
            var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
            try
            {
                return await factory(ct).ConfigureAwait(false);
            }
            finally
            {
                cts?.Dispose();
            }
        }

        var service = ResolveService(callOptions?.Mode);

        if ((callOptions?.CoalesceConcurrent ?? true))
        {
            var semaphore = _lockManager.GetLock(prefixed);
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if ((callOptions?.ForceRefresh ?? false))
                {
                    var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
                    try
                    {
                        T value = await factory(ct).ConfigureAwait(false);
                        var jitteredExpiration = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage);
                        await SetWithExpirationAsync(service, prefixed, value, jitteredExpiration, callOptions?.SlidingExpiration, localExpiration, ct).ConfigureAwait(false);
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
                    var jitteredExp = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage);
                    return await service.GetOrCreateAsync(prefixed, factory, jitteredExp, localExpiration, innerCt).ConfigureAwait(false);
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
                T value = await factory(ct).ConfigureAwait(false);
                var jitteredExpiration = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage);
                await SetWithExpirationAsync(service, prefixed, value, jitteredExpiration, callOptions?.SlidingExpiration, localExpiration, ct).ConfigureAwait(false);
                CacheInstruments.RecordSet(Mode);
                return value;
            }
            finally
            {
                cts?.Dispose();
            }
        }

        var noLockCt = ApplyFactoryTimeout(cancellationToken, out var noLockCts);
        try
        {
            var jitteredExp = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage);
            return await service.GetOrCreateAsync(prefixed, factory, jitteredExp, localExpiration, noLockCt).ConfigureAwait(false);
        }
        finally
        {
            noLockCts?.Dispose();
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
        if (IsDisabled) return Task.CompletedTask;
        if ((callOptions?.BypassCache ?? false)) return Task.CompletedTask;
        var service = ResolveService(callOptions?.Mode);
        var jitteredExpiration = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage);
        return SetWithExpirationAsync(service, PrependPrefix(key), value, jitteredExpiration, callOptions?.SlidingExpiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (IsDisabled) return Task.CompletedTask;
        return ResolveService(modeOverride: null).RemoveAsync(PrependPrefix(key), cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (IsDisabled) return Task.CompletedTask;
        return ResolveService(modeOverride: null).RemoveByTagAsync(tag, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (IsDisabled) return Task.CompletedTask;
        return ResolveService(modeOverride: null).RemoveByTagAsync(tags, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (IsDisabled)
        {
            CacheInstruments.RecordMiss(Mode, "get", "Disabled");
            return Task.FromResult<T?>(default);
        }
        return ResolveService(modeOverride: null).GetAsync<T>(PrependPrefix(key), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (IsDisabled) return Task.FromResult(false);
        return ResolveService(modeOverride: null).ExistsAsync(PrependPrefix(key), cancellationToken);
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
        if (IsDisabled) return Task.CompletedTask;
        return ResolveService(modeOverride: null)
            .RefreshAsync(PrependPrefix(key), factory, expiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (IsDisabled)
            return new Dictionary<string, T?>();

        var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (keyList.Length == 0) return new Dictionary<string, T?>();

        var prefixed = new string[keyList.Length];
        for (int i = 0; i < keyList.Length; i++) prefixed[i] = PrependPrefix(keyList[i]);

        var inner = await ResolveService(modeOverride: null)
            .GetManyAsync<T>(prefixed, cancellationToken).ConfigureAwait(false);

        var dict = new Dictionary<string, T?>(keyList.Length);
        for (int i = 0; i < keyList.Length; i++)
            dict[keyList[i]] = inner.TryGetValue(prefixed[i], out var v) ? v : default;
        return dict;
    }

    /// <inheritdoc />
    public Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        if (IsDisabled) return Task.CompletedTask;
        var prefixed = new Dictionary<string, T>(items.Count);
        foreach (var kvp in items) prefixed[PrependPrefix(kvp.Key)] = kvp.Value;
        var jitteredExpiration = ApplyJitter(expiration, null);
        return ResolveService(modeOverride: null)
            .SetManyAsync(prefixed, jitteredExpiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (IsDisabled || keys is null) return Task.CompletedTask;
        var prefixed = new List<string>();
        foreach (var k in keys) if (!string.IsNullOrWhiteSpace(k)) prefixed.Add(PrependPrefix(k));
        return ResolveService(modeOverride: null).RemoveManyAsync(prefixed, cancellationToken);
    }

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
}
