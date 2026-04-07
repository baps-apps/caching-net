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
/// <param name="cache">The <see cref="IMemoryCache"/> instance to use for in-process caching.</param>
/// <param name="options">Bound <see cref="CacheOptions"/> that control expiration defaults.</param>
/// <param name="telemetry">Telemetry sink for recording cache hits, misses, and errors.</param>
/// <param name="logger">Logger for recording operational warnings and errors.</param>
public sealed class InMemoryCacheService(
    IMemoryCache cache,
    IOptions<CacheOptions> options,
    Abstractions.ICacheTelemetry telemetry,
    ILogger<InMemoryCacheService> logger) : Abstractions.ICacheService
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));

        if (cache.TryGetValue(key, out T? cached))
        {
            telemetry.OnCacheHit(key, "InMemory");
            return cached!;
        }

        T value = await factory(cancellationToken).ConfigureAwait(false);
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? DefaultExpiration;
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan });
        telemetry.OnCacheMiss(key, "InMemory");
        telemetry.OnCacheSet(key, "InMemory");
        return value;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? DefaultExpiration;
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan });
        telemetry.OnCacheSet(key, "InMemory");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;
        cache.Remove(key);
        telemetry.OnCacheRemove(key, "InMemory");
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
        logger.LogDebug(Caching.NET.Internal.CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in InMemory mode; no-op for tag {Tag}. Use Hybrid mode for tag support.", tag);
        telemetry.OnCacheRemoveByTag(tag, "InMemory");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        logger.LogDebug(Caching.NET.Internal.CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in InMemory mode; no-op. Use Hybrid mode for tag support.");
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                telemetry.OnCacheRemoveByTag(tag, "InMemory");
            }
        }
        return Task.CompletedTask;
    }
}
