using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Caching.NET.Options;
using Caching.NET.Internal;
using Caching.NET.Telemetry;

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
/// <param name="logger">Logger for recording operational warnings and errors.</param>
internal sealed class HybridCacheService(
    HybridCache? cache,
    IOptions<CacheOptions> options,
    ILogger<HybridCacheService> logger) : Abstractions.ICacheService
{
    private const string Mode = "Hybrid";
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
            logger.HybridCacheDisabled(FormatKey(key));
            CacheInstruments.RecordMiss(Mode, "get_or_create", "Disabled");
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var entryOptions = BuildEntryOptions(expiration, localExpiration);
            var factoryRan = false;
            async ValueTask<T> wrapper(CancellationToken ct)
            {
                factoryRan = true;
                return await factory(ct).ConfigureAwait(false);
            }
            var value = await cache.GetOrCreateAsync(key, wrapper, entryOptions, tags: null, cancellationToken).ConfigureAwait(false);
            if (factoryRan)
                CacheInstruments.RecordMiss(Mode, "get_or_create", "NotFound");
            else
                CacheInstruments.RecordHit(Mode, "get_or_create");
            return value;
        }
        catch (Exception ex)
        {
            logger.HybridGetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "get_or_create", ClassifyError(ex));
            return await factory(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ClassifyError(Exception ex) => ex switch
    {
        TimeoutException => "Timeout",
        OperationCanceledException => "Cancelled",
        _ => "Unknown",
    };

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            var entryOptions = BuildEntryOptions(expiration, localExpiration);
            await cache.SetAsync(key, value, entryOptions, tags: null, cancellationToken).ConfigureAwait(false);
            CacheInstruments.RecordSet(Mode);
        }
        catch (Exception ex)
        {
            logger.HybridSetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "set", ClassifyError(ex));
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
            CacheInstruments.RecordRemove(Mode);
        }
        catch (Exception ex)
        {
            logger.HybridRemoveFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "remove", ClassifyError(ex));
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
            CacheInstruments.RecordRemove(Mode, "remove_by_tag");
        }
        catch (Exception ex)
        {
            logger.HybridTagRemoveFailed(tag, ex);
            CacheInstruments.RecordError(Mode, "remove_by_tag", ClassifyError(ex));
        }
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (tags == null) return;
        foreach (var tag in tags)
            await RemoveByTagAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (!options.Value.Enabled || cache == null)
        {
            CacheInstruments.RecordMiss(Mode, "get", "Disabled");
            return default;
        }
        try
        {
            T value = await cache.GetOrCreateAsync<T>(
                key,
                static _ => ValueTask.FromResult(default(T)!),
                options: null,
                tags: null,
                cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                CacheInstruments.RecordMiss(Mode, "get", "NotFound");
                return default;
            }
            CacheInstruments.RecordHit(Mode, "get");
            return value;
        }
        catch (Exception ex)
        {
            CacheInstruments.RecordError(Mode, "get", ClassifyError(ex));
            return default;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var v = await GetAsync<object>(key, cancellationToken).ConfigureAwait(false);
        return v != null;
    }

    /// <inheritdoc />
    public async Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            var entry = BuildEntryOptions(expiration, localExpiration);
            T value = await factory(cancellationToken).ConfigureAwait(false);
            await cache.SetAsync(key, value, entry, tags: null, cancellationToken).ConfigureAwait(false);
            CacheInstruments.RecordSet(Mode);
        }
        catch (Exception ex)
        {
            logger.HybridSetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "refresh", ClassifyError(ex));
        }
    }

    private string FormatKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        if (options.Value.IncludeRawKeyInLogs)
            return key.Length <= 64 ? key : key[..64] + "...";
        return StableStringHash.Compute64(key).ToString("x16");
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
