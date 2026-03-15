using Caching.NET.Abstractions;
using Caching.NET.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Services;

/// <summary>
/// Internal routing cache service that can delegate calls to different concrete
/// cache implementations based on the application-level <see cref="CacheOptions.Mode"/>
/// and optional per-call <see cref="CacheCallOptions"/> overrides.
/// </summary>
internal sealed class RoutingCacheService : ICacheService, IRoutingCacheService
{
    private readonly CacheOptions _options;
    private readonly ILogger<RoutingCacheService> _logger;
    private readonly Abstractions.ICacheTelemetry _telemetry;
    private readonly InMemoryCacheService? _inMemory;
    private readonly RedisCacheService? _redis;
    private readonly HybridCacheService? _hybrid;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);

    public RoutingCacheService(
        IOptions<CacheOptions> options,
        ILogger<RoutingCacheService> logger,
        Abstractions.ICacheTelemetry telemetry,
        InMemoryCacheService? inMemory = null,
        RedisCacheService? redis = null,
        HybridCacheService? hybrid = null)
    {
        _options = options?.Value ?? new CacheOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _inMemory = inMemory;
        _redis = redis;
        _hybrid = hybrid;
    }

    /// <inheritdoc />
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
        => GetOrCreateAsync(
            key,
            factory,
            callOptions: null,
            expiration,
            localExpiration,
            cancellationToken);

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
        if (callOptions?.BypassCache == true)
        {
            var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
            try
            {
                if (ct.CanBeCanceled && _options.GetFactoryTimeout() is { } timeout)
                {
                    _telemetry.OnFactoryTimeout(key, "Routing", timeout);
                }
                return await factory(ct).ConfigureAwait(false);
            }
            finally
            {
                cts?.Dispose();
            }
        }

        var service = ResolveService(callOptions?.OverrideMode);

        // Optional per-key concurrency coalescing for non-Hybrid modes (and available for Hybrid as well).
        if (callOptions?.CoalesceConcurrent == true)
        {
            var semaphore = _keyLocks.GetOrAdd(key, _ => new System.Threading.SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (callOptions?.ForceRefresh == true)
                {
                    var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
                    try
                    {
                        T value = await factory(ct).ConfigureAwait(false);
                        await service.SetAsync(key, value, expiration, localExpiration, ct).ConfigureAwait(false);
                        _telemetry.OnCacheSet(key, "Routing");
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
                    return await service.GetOrCreateAsync(key, factory, expiration, localExpiration, innerCt).ConfigureAwait(false);
                }
                finally
                {
                    innerCts?.Dispose();
                }
            }
            finally
            {
                semaphore.Release();

                // Best-effort cleanup of per-key locks to avoid unbounded growth when using
                // high-cardinality keys. When the semaphore is fully released (no other waiters),
                // attempt to remove it from the dictionary. Races are harmless: if another caller
                // acquired the lock concurrently, either CurrentCount will not be 1 or the entry
                // will be re-added on demand.
                if (semaphore.CurrentCount == 1)
                {
                    _keyLocks.TryRemove(key, out _);
                }
            }
        }

        if (callOptions?.ForceRefresh == true)
        {
            var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
            try
            {
                T value = await factory(ct).ConfigureAwait(false);
                await service.SetAsync(key, value, expiration, localExpiration, ct).ConfigureAwait(false);
                _telemetry.OnCacheSet(key, "Routing");
                return value;
            }
            finally
            {
                cts?.Dispose();
            }
        }

        var innerCtNoLock = ApplyFactoryTimeout(cancellationToken, out var innerCtsNoLock);
        try
        {
            return await service.GetOrCreateAsync(key, factory, expiration, localExpiration, innerCtNoLock).ConfigureAwait(false);
        }
        finally
        {
            innerCtsNoLock?.Dispose();
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
        => SetAsync(
            key,
            value,
            callOptions: null,
            expiration,
            localExpiration,
            cancellationToken);

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
        if (callOptions?.BypassCache == true)
            return Task.CompletedTask;
        var service = ResolveService(callOptions?.OverrideMode);
        return service.SetAsync(key, value, expiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var service = ResolveService(overrideMode: null);
        return service.RemoveAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var service = ResolveService(overrideMode: null);
        return service.RemoveAsync(keys, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var service = ResolveService(overrideMode: null);
        return service.RemoveByTagAsync(tag, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var service = ResolveService(overrideMode: null);
        return service.RemoveByTagAsync(tags, cancellationToken);
    }

    private ICacheService ResolveService(CacheMode? overrideMode)
    {
        var mode = overrideMode ?? _options.Mode;

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
        // Fall back to the application-level mode, or a sensible default when misconfigured.
        if (_options.Mode == CacheMode.InMemory && _inMemory is not null)
        {
            return _inMemory;
        }

        if (_options.Mode == CacheMode.Redis && _redis is not null)
        {
            return _redis;
        }

        if (_options.Mode == CacheMode.Hybrid && _hybrid is not null)
        {
            return _hybrid;
        }

        if (_inMemory is not null)
        {
            return _inMemory;
        }

        if (_redis is not null)
        {
            return _redis;
        }

        if (_hybrid is not null)
        {
            return _hybrid;
        }

        var mode = _options.Mode;
        throw new InvalidOperationException(
            $"No underlying cache service is available for RoutingCacheService. Configured mode: {mode}. " +
            "Ensure AddCaching was called with valid configuration and that the configured mode's dependencies (e.g., Redis for Redis mode) are registered.");
    }

    private CancellationToken ApplyFactoryTimeout(CancellationToken cancellationToken, out CancellationTokenSource? cts)
    {
        var timeout = _options.GetFactoryTimeout();
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

