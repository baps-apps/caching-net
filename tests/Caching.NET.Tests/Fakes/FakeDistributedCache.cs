using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;

namespace Caching.NET.Tests.Fakes;

/// <summary>
/// In-memory fake for <see cref="IDistributedCache"/> for unit testing without Redis.
/// </summary>
public sealed class FakeDistributedCache : IDistributedCache
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    /// <inheritdoc />
    public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        await Task.Yield();
        return Get(key);
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        _store[key] = value;
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        await Task.Yield();
        Set(key, value, options);
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        _store.TryRemove(key, out _);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        await Task.Yield();
        Remove(key);
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
        // No-op for in-memory fake; sliding expiration not simulated.
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        await Task.Yield();
        Refresh(key);
    }

    /// <summary>Clears all entries from the in-memory store.</summary>
    public void Clear() => _store.Clear();
}
