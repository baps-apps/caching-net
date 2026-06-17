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
    private static readonly PostEvictionCallbackRegistration s_evictionRegistration = new() { EvictionCallback = s_evictionCallback };

    private static void OnEvicted(object key, object? value, EvictionReason reason, object? state) =>
        CacheInstruments.RecordEviction(Mode, reason.ToString());

    /// <inheritdoc />
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        _ = localExpiration;

        if (cache.TryGetValue(key, out T? cached))
        {
            CacheInstruments.RecordHit(Mode, "get_or_create");
            return Task.FromResult(cached!);
        }

        return GetOrCreateSlowAsync(key, factory, expiration, cancellationToken);
    }

    private async Task<T> GetOrCreateSlowAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration,
        CancellationToken cancellationToken) where T : notnull
    {
        CacheInstruments.RecordMiss(Mode, "get_or_create", "NotFound");
        T value = await factory(cancellationToken);
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? FallbackExpiration;
        var entryOpts = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
        entryOpts.PostEvictionCallbacks.Add(s_evictionRegistration);
        cache.Set(key, value, entryOpts);
        CacheInstruments.RecordSet(Mode);
        return value;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        _ = cancellationToken;
        _ = localExpiration;
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? FallbackExpiration;
        var entryOpts = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
        entryOpts.PostEvictionCallbacks.Add(s_evictionRegistration);
        cache.Set(key, value, entryOpts);
        CacheInstruments.RecordSet(Mode);
        return Task.CompletedTask;
    }

    internal Task SetAsync<T>(string key, T value, MemoryCacheEntryOptions entry, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        _ = cancellationToken;
        entry.PostEvictionCallbacks.Add(s_evictionRegistration);
        cache.Set(key, value, entry);
        CacheInstruments.RecordSet(Mode);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;
        cache.Remove(key);
        CacheInstruments.RecordRemove(Mode);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        logger.TagNotSupported(tag);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        logger.TagNotSupported("(multiple tags)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        _ = cancellationToken;
        if (cache.TryGetValue(key, out T? cached))
        {
            CacheInstruments.RecordHit(Mode, "get");
            return Task.FromResult<T?>(cached);
        }
        CacheInstruments.RecordMiss(Mode, "get", "NotFound");
        return Task.FromResult<T?>(default);
    }

    /// <inheritdoc />
    public Task<object?> GetAsync(string key, Type type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        ArgumentNullException.ThrowIfNull(type);
        _ = cancellationToken;
        // Mirror the generic path's cast-miss behavior: a stored value whose runtime type is not
        // assignable to the requested type counts as a miss.
        if (cache.TryGetValue(key, out var cached) && cached is not null && type.IsInstanceOfType(cached))
        {
            CacheInstruments.RecordHit(Mode, "get");
            return Task.FromResult<object?>(cached);
        }
        CacheInstruments.RecordMiss(Mode, "get", "NotFound");
        return Task.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        _ = cancellationToken;
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
        var value = await factory(cancellationToken);
        await SetAsync(key, value, expiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        _ = cancellationToken;
        var dict = new Dictionary<string, T?>();
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            if (cache.TryGetValue(k, out T? cached))
            {
                CacheInstruments.RecordHit(Mode, "get");
                dict[k] = cached;
            }
            else
            {
                CacheInstruments.RecordMiss(Mode, "get", "NotFound");
                dict[k] = default;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, T?>>(dict);
    }

    /// <inheritdoc />
    public Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        _ = localExpiration;
        _ = cancellationToken;
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? FallbackExpiration;
        foreach (var kvp in items)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
            var entryOpts = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
            entryOpts.PostEvictionCallbacks.Add(s_evictionRegistration);
            cache.Set(kvp.Key, kvp.Value, entryOpts);
            CacheInstruments.RecordSet(Mode);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys is null) return Task.CompletedTask;
        _ = cancellationToken;
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            cache.Remove(k);
            CacheInstruments.RecordRemove(Mode);
        }
        return Task.CompletedTask;
    }
}
