using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Services;

/// <summary>
/// <see cref="Abstractions.ICacheService"/> implementation backed by <see cref="IMemoryCache"/>.
/// Tag methods are no-ops because <see cref="IMemoryCache"/> does not support tags.
/// </summary>
internal sealed class InMemoryCacheService(
    IMemoryCache cache,
    IOptions<CacheOptions> options,
    ILogger<InMemoryCacheService> logger) : Abstractions.ICacheService
{
    private const string Mode = "InMemory";
    private static readonly TimeSpan FallbackExpiration = TimeSpan.FromMinutes(10);
    private static readonly PostEvictionDelegate s_evictionCallback = OnEvicted;

    private static void OnEvicted(object key, object? value, EvictionReason reason, object? state) =>
        CacheInstruments.RecordEviction(Mode, reason.ToString());

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
            CacheInstruments.RecordHit(Mode, "get_or_create");
            return cached!;
        }

        CacheInstruments.RecordMiss(Mode, "get_or_create", "NotFound");
        T value = await factory(cancellationToken).ConfigureAwait(false);
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? FallbackExpiration;
        var entryOpts = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
        entryOpts.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration { EvictionCallback = s_evictionCallback });
        cache.Set(key, value, entryOpts);
        CacheInstruments.RecordSet(Mode);
        return value;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? FallbackExpiration;
        var entryOpts = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
        entryOpts.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration { EvictionCallback = s_evictionCallback });
        cache.Set(key, value, entryOpts);
        CacheInstruments.RecordSet(Mode);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;
        cache.Remove(key);
        CacheInstruments.RecordRemove(Mode);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        logger.TagNotSupported(tag);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        logger.TagNotSupported("(multiple tags)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (cache.TryGetValue(key, out T? cached))
        {
            CacheInstruments.RecordHit(Mode, "get");
            return Task.FromResult<T?>(cached);
        }
        CacheInstruments.RecordMiss(Mode, "get", "NotFound");
        return Task.FromResult<T?>(default);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        var present = cache.TryGetValue(key, out _);
        if (present) CacheInstruments.RecordHit(Mode, "exists");
        else CacheInstruments.RecordMiss(Mode, "exists", "NotFound");
        return Task.FromResult(present);
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
        var value = await factory(cancellationToken).ConfigureAwait(false);
        await SetAsync(key, value, expiration, localExpiration, cancellationToken).ConfigureAwait(false);
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
            dict[k] = await GetAsync<T>(k, cancellationToken).ConfigureAwait(false);
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
            await SetAsync(kvp.Key, kvp.Value, expiration, localExpiration, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys is null) return;
        foreach (var k in keys)
        {
            if (!string.IsNullOrWhiteSpace(k))
                await RemoveAsync(k, cancellationToken).ConfigureAwait(false);
        }
    }
}
