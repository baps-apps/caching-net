using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Distributed;
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
/// When <c>null</c>, <c>GetOrCreateAsync</c> always falls back to executing the factory directly.
/// </param>
/// <param name="options">Bound <see cref="CacheOptions"/> that control expiration defaults and enabled state.</param>
/// <param name="logger">Logger for recording operational warnings and errors.</param>
/// <param name="distributedCache">
/// Optional distributed cache backend used for lightweight existence checks without full deserialization.
/// </param>
internal sealed class HybridCacheService(
    HybridCache? cache,
    IOptions<CacheOptions> options,
    ILogger<HybridCacheService> logger,
    IDistributedCache? distributedCache = null) : Abstractions.ICacheService
{
    private const string Mode = "Hybrid";
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultLocalExpiration = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
        => GetOrCreateAsync(key, factory, expiration, localExpiration, tags: null, cancellationToken);

    /// <summary>
    /// Tag-aware <see cref="GetOrCreateAsync{T}(string, Func{CancellationToken, Task{T}}, TimeSpan?, TimeSpan?, CancellationToken)"/>.
    /// Associates <paramref name="tags"/> with the cached entry so it can later be evicted via
    /// <see cref="RemoveByTagAsync(string, CancellationToken)"/>. Tags are a Hybrid-only capability;
    /// <see cref="Services.RoutingCacheService"/> routes here only when the caller supplied tags.
    /// </summary>
    internal async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration,
        TimeSpan? localExpiration,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));

        if (!options.Value.Enabled || cache == null)
        {
            logger.HybridCacheDisabled(FormatKey(key));
            CacheInstruments.RecordMiss(Mode, "get_or_create", "Disabled");
            return await factory(cancellationToken);
        }

        try
        {
            var entryOptions = BuildEntryOptions(expiration, localExpiration);
            var factoryRan = false;
            async ValueTask<T> wrapper(CancellationToken ct)
            {
                factoryRan = true;
                return await factory(ct);
            }
            var value = await cache.GetOrCreateAsync(key, wrapper, entryOptions, NormalizeTags(tags), cancellationToken);
            if (factoryRan)
            {
                CacheInstruments.RecordMiss(Mode, "get_or_create", "NotFound");
                // HybridCache always stores the factory result; evict it when null so it is not cached.
                if (value is null)
                    await cache.RemoveAsync(key, cancellationToken);
            }
            else
            {
                CacheInstruments.RecordHit(Mode, "get_or_create");
            }
            return value!;
        }
        catch (Exception ex)
        {
            logger.HybridGetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "get_or_create", ClassifyError(ex));
            return await factory(cancellationToken);
        }
    }

    private static string ClassifyError(Exception ex) => ex switch
    {
        TimeoutException => "Timeout",
        OperationCanceledException => "Cancelled",
        _ => "Unknown",
    };

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
        => SetAsync(key, value, expiration, localExpiration, tags: null, cancellationToken);

    /// <summary>
    /// Tag-aware <see cref="SetAsync{T}(string, T, TimeSpan?, TimeSpan?, CancellationToken)"/>.
    /// Associates <paramref name="tags"/> with the cached entry so it can later be evicted via
    /// <see cref="RemoveByTagAsync(string, CancellationToken)"/>. Tags are a Hybrid-only capability;
    /// <see cref="Services.RoutingCacheService"/> routes here only when the caller supplied tags.
    /// </summary>
    internal async Task SetAsync<T>(string key, T value, TimeSpan? expiration, TimeSpan? localExpiration, IReadOnlyList<string>? tags, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            var entryOptions = BuildEntryOptions(expiration, localExpiration);
            await cache.SetAsync(key, value, entryOptions, NormalizeTags(tags), cancellationToken);
            CacheInstruments.RecordSet(Mode);
        }
        catch (Exception ex)
        {
            logger.HybridSetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "set", ClassifyError(ex));
        }
    }

    /// <summary>
    /// Converts caller-supplied tags into the form expected by <see cref="HybridCache"/>:
    /// <c>null</c> when there are no tags, otherwise the same sequence. Returning <c>null</c>
    /// for an empty list avoids associating an entry with a zero-length tag set.
    /// </summary>
    private static IEnumerable<string>? NormalizeTags(IReadOnlyList<string>? tags)
        => tags is { Count: > 0 } ? tags : null;

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            await cache.RemoveAsync(key, cancellationToken);
            CacheInstruments.RecordRemove(Mode);
        }
        catch (Exception ex)
        {
            logger.HybridRemoveFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "remove", ClassifyError(ex));
        }
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            await cache.RemoveByTagAsync(tag, cancellationToken);
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
            await RemoveByTagAsync(tag, cancellationToken);
    }

    /// <summary>
    /// Invalidates every <see cref="HybridCache"/> entry via the reserved wildcard tag <c>"*"</c>.
    /// This is a <em>logical</em> invalidation: entries remain in L1/L2 until they expire naturally,
    /// but are treated as misses on the next read. There is no physical flush of the backing stores.
    /// <para>
    /// App scope: although the wildcard is a tag (not a key), the marker is persisted to L2 through the
    /// Redis adapter whose <c>InstanceName</c> is set to <c>KeyPrefix</c> (see ConfigureHybridCache). The
    /// marker is therefore namespaced per app, so apps sharing one Redis database do not invalidate each
    /// other's entries — provided each app uses a unique <c>KeyPrefix</c>.
    /// </para>
    /// </summary>
    internal async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            await cache.RemoveByTagAsync("*", cancellationToken);
            CacheInstruments.RecordRemove(Mode, "clear");
        }
        catch (Exception ex)
        {
            logger.HybridClearFailed(ex);
            CacheInstruments.RecordError(Mode, "clear", ClassifyError(ex));
        }
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
            if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
            {
                HybridValueBox<T>? boxed = await cache.GetOrCreateAsync(
                    key,
                    static _ => ValueTask.FromResult<HybridValueBox<T>?>(null),
                    options: null,
                    tags: null,
                    cancellationToken);
                if (boxed is null)
                {
                    CacheInstruments.RecordMiss(Mode, "get", "NotFound");
                    return default;
                }

                CacheInstruments.RecordHit(Mode, "get");
                return boxed.Value;
            }

            T value = await cache.GetOrCreateAsync(
                key,
                static _ => ValueTask.FromResult(default(T)!),
                options: null,
                tags: null,
                cancellationToken);
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
    public Task<object?> GetAsync(string key, Type type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        ArgumentNullException.ThrowIfNull(type);
        if (!options.Value.Enabled || cache == null)
        {
            CacheInstruments.RecordMiss(Mode, "get", "Disabled");
            return Task.FromResult<object?>(null);
        }
        // HybridCache only exposes a generic GetOrCreateAsync<T>, so route the runtime-typed read
        // through the generic GetAsync<T> implementation above. This guarantees exact parity (L1 box
        // assignability + L2 envelope/schema validation) with the compile-time path for the same type.
        return RuntimeTypedCacheInvoker.GetAsync(this, key, type, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled || cache == null) return false;
        if (string.IsNullOrWhiteSpace(key)) return false;

        if (distributedCache is not null)
        {
            try
            {
                var raw = await distributedCache.GetAsync(key, cancellationToken);
                if (raw is not null) return true;
            }
            catch (Exception ex)
            {
                CacheInstruments.RecordError(Mode, "exists", ClassifyError(ex));
            }
        }

        var v = await GetAsync<object>(key, cancellationToken);
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
            T value = await factory(cancellationToken);
            await cache.SetAsync(key, value, entry, tags: null, cancellationToken);
            CacheInstruments.RecordSet(Mode);
        }
        catch (Exception ex)
        {
            logger.HybridSetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "refresh", ClassifyError(ex));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        var dict = new Dictionary<string, T?>();
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            dict[k] = await GetAsync<T>(k, cancellationToken);
        }
        return dict;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var kvp in items)
            await SetAsync(kvp.Key, kvp.Value, expiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys is null) return;
        foreach (var k in keys)
            if (!string.IsNullOrWhiteSpace(k))
                await RemoveAsync(k, cancellationToken);
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

    private sealed record HybridValueBox<T>(T Value);
}
