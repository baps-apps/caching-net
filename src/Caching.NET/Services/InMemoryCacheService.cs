using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Caching.NET.Options;

namespace Caching.NET.Services;

/// <summary>
/// <see cref="Abstractions.ICacheService"/> implementation backed by <see cref="IMemoryCache"/>.
/// Uses a single in-process cache tier; tag methods are no-op because <see cref="IMemoryCache"/> does not support tags.
/// The <c>localExpiration</c> parameter on methods is accepted to match the shared abstraction but is ignored.
/// </summary>
public sealed class InMemoryCacheService : Abstractions.ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly CacheOptions _options;
    private readonly Abstractions.ICacheTelemetry _telemetry;
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);

    private readonly ILogger<InMemoryCacheService> _logger;

    /// <param name="cache">The <see cref="IMemoryCache"/> instance to use for in-process caching.</param>
    /// <param name="options">Bound <see cref="CacheOptions"/> that control expiration defaults.</param>
    /// <param name="telemetry">Telemetry sink for recording cache hits, misses, and errors.</param>
    /// <param name="logger">Logger for recording operational warnings and errors.</param>
    public InMemoryCacheService(
        IMemoryCache cache,
        IOptions<CacheOptions> options,
        Abstractions.ICacheTelemetry telemetry,
        ILogger<InMemoryCacheService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? new CacheOptions();
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));

        if (_cache.TryGetValue(key, out T? cached))
        {
            _telemetry.OnCacheHit(key, "InMemory");
            return cached!;
        }

        T value = await factory(cancellationToken).ConfigureAwait(false);
        var expirationSpan = expiration ?? _options.GetDefaultExpiration() ?? DefaultExpiration;
        _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan });
        _telemetry.OnCacheMiss(key, "InMemory");
        _telemetry.OnCacheSet(key, "InMemory");
        return value;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        var expirationSpan = expiration ?? _options.GetDefaultExpiration() ?? DefaultExpiration;
        _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan });
        _telemetry.OnCacheSet(key, "InMemory");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;
        _cache.Remove(key);
        _telemetry.OnCacheRemove(key, "InMemory");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys == null) return;
        foreach (var key in keys)
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(Caching.NET.Internal.CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in InMemory mode; no-op for tag {Tag}. Use Hybrid mode for tag support.", tag);
        _telemetry.OnCacheRemoveByTag(tag, "InMemory");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(Caching.NET.Internal.CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in InMemory mode; no-op. Use Hybrid mode for tag support.");
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _telemetry.OnCacheRemoveByTag(tag, "InMemory");
            }
        }
        return Task.CompletedTask;
    }
}
