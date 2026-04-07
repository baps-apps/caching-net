using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Caching.NET.Options;
using Caching.NET.Internal;

namespace Caching.NET.Services;

/// <summary>
/// <see cref="Abstractions.ICacheService"/> implementation that wraps <see cref="HybridCache"/> (in-memory + optional Redis with stampede protection).
/// Honors both <c>expiration</c> (overall/distributed) and <c>localExpiration</c> (in-memory tier) when provided.
/// When caching is disabled through <see cref="CacheOptions.Enabled"/> or the underlying <see cref="HybridCache"/> is unavailable,
/// this service executes the factory directly and treats all write/remove operations as no-ops.
/// </summary>
/// <param name="cache">
/// The <see cref="HybridCache"/> instance to use, or <c>null</c> when the hybrid cache is unavailable.
/// When <c>null</c>, <see cref="GetOrCreateAsync{T}"/> always falls back to executing the factory directly.
/// </param>
/// <param name="options">Bound <see cref="CacheOptions"/> that control expiration defaults and enabled state.</param>
/// <param name="telemetry">Telemetry sink for recording cache hits, misses, and errors.</param>
/// <param name="logger">Logger for recording operational warnings and errors.</param>
public sealed class HybridCacheService(
    HybridCache? cache,
    IOptions<CacheOptions> options,
    Abstractions.ICacheTelemetry telemetry,
    ILogger<HybridCacheService> logger) : Abstractions.ICacheService
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultLocalExpiration = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));

        if (!options.Value.Enabled || cache == null)
        {
            logger.LogDebug(CacheLogEvents.HybridCacheDisabled, "Cache disabled or unavailable - executing factory for key: {Key}", TruncateKey(key));
            telemetry.OnCacheMiss(key, "Hybrid");
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var entryOptions = BuildEntryOptions(expiration, localExpiration);
            async ValueTask<T> wrapper(CancellationToken ct) => await factory(ct).ConfigureAwait(false);
            var value = await cache.GetOrCreateAsync(key, wrapper, entryOptions, tags: null, cancellationToken).ConfigureAwait(false);
            // HybridCache already coalesces concurrent requests; treat successful GetOrCreate as a hit when
            // the value was previously cached and a miss when computed. We cannot easily distinguish here,
            // so we record a generic request and leave hit/miss attribution to upstream metrics if needed.
            telemetry.OnCacheHit(key, "Hybrid");
            return value;
        }
        catch (Exception ex)
        {
            logger.LogError(CacheLogEvents.HybridGetFailed, ex, "Error getting or creating cache entry for key: {Key}; executing factory (fail-open).", TruncateKey(key));
            telemetry.OnCacheError("get_or_create", key, "Hybrid", ex);
            return await factory(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            var entryOptions = BuildEntryOptions(expiration, localExpiration);
            await cache.SetAsync(key, value, entryOptions, tags: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(CacheLogEvents.HybridSetFailed, ex, "Error setting cache entry for key: {Key}.", TruncateKey(key));
            telemetry.OnCacheError("set", key, "Hybrid", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            telemetry.OnCacheRemove(key, "Hybrid");
        }
        catch (Exception ex)
        {
            logger.LogError(CacheLogEvents.HybridRemoveFailed, ex, "Error removing cache entry for key: {Key}.", TruncateKey(key));
            telemetry.OnCacheError("remove", key, "Hybrid", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys == null) return;
        foreach (var key in keys)
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            await cache.RemoveByTagAsync(tag, cancellationToken).ConfigureAwait(false);
            telemetry.OnCacheRemoveByTag(tag, "Hybrid");
        }
        catch (Exception ex)
        {
            logger.LogError(CacheLogEvents.HybridTagRemoveFailed, ex, "Error removing cache entries for tag: {Tag}", tag);
            telemetry.OnCacheError("remove_by_tag", tag, "Hybrid", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (tags == null) return;
        foreach (var tag in tags)
            await RemoveByTagAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    private static string TruncateKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        return key.Length <= 64 ? key : key[..64] + "...";
    }

    private static HybridCacheEntryOptions? BuildEntryOptions(TimeSpan? expiration, TimeSpan? localExpiration)
    {
        if (!expiration.HasValue && !localExpiration.HasValue)
            return null;
        return new HybridCacheEntryOptions
        {
            Expiration = expiration ?? DefaultExpiration,
            LocalCacheExpiration = localExpiration ?? expiration ?? DefaultLocalExpiration
        };
    }
}
