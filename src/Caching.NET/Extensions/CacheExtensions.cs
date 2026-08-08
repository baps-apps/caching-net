using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Extensions;

/// <summary>
/// Caching.NET additions to the cache API. Each method here does something the underlying
/// operation contract does not already do — batching, existence probing, forced refresh. Nothing
/// in this class renames or re-exposes an existing cache method.
/// </summary>
public static class CacheExtensions
{
    /// <summary>
    /// Reads several keys concurrently. The result contains an entry for every requested key;
    /// missing keys map to <c>default</c>.
    /// </summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="keys">Keys to read. Duplicates are collapsed.</param>
    /// <param name="options">Optional per-entry options.</param>
    /// <param name="token">Cancellation token.</param>
    /// <example>
    /// <code><![CDATA[
    /// var hits = await cache.GetManyAsync<Order>(ids.Select(id => $"Order:{id}"), token: ct);
    /// var missing = ids.Where(id => hits[$"Order:{id}"] is null).ToList();
    /// ]]></code>
    /// </example>
    public static async Task<IReadOnlyDictionary<string, TValue?>> GetManyAsync<TValue>(
        this IFusionCache cache,
        IEnumerable<string> keys,
        FusionCacheEntryOptions? options = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(keys);

        var distinct = keys.Distinct(StringComparer.Ordinal).ToArray();
        var results = new TValue?[distinct.Length];

        // Started directly rather than through Task.Run: the reads are already asynchronous, so a
        // thread-pool hop per key would only add scheduling latency and allocations, and would push
        // large batches into thread-pool starvation on a busy server.
        var reads = new Task[distinct.Length];
        for (var i = 0; i < distinct.Length; i++)
        {
            reads[i] = ReadAsync(i);
        }

        await Task.WhenAll(reads).ConfigureAwait(false);

        async Task ReadAsync(int index)
            => results[index] = await cache
                .GetOrDefaultAsync<TValue>(distinct[index], default!, options, token)
                .ConfigureAwait(false);

        var map = new Dictionary<string, TValue?>(distinct.Length, StringComparer.Ordinal);
        for (var i = 0; i < distinct.Length; i++)
        {
            map[distinct[i]] = results[i];
        }

        return map;
    }

    /// <summary>Writes several entries concurrently, all sharing the same options and tags.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="items">Key/value pairs to write.</param>
    /// <param name="options">Optional per-entry options.</param>
    /// <param name="tags">Optional tags applied to every entry.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task SetManyAsync<TValue>(
        this IFusionCache cache,
        IReadOnlyDictionary<string, TValue> items,
        FusionCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        var materializedTags = tags?.ToArray();
        var writes = new List<Task>(items.Count);
        foreach (var (key, value) in items)
        {
            writes.Add(cache.SetAsync(key, value, options, materializedTags, token).AsTask());
        }

        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    /// <summary>Removes several keys concurrently. <c>null</c>, empty and whitespace keys are skipped.</summary>
    /// <param name="cache">The cache.</param>
    /// <param name="keys">Keys to remove.</param>
    /// <param name="options">Optional per-entry options.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task RemoveManyAsync(
        this IFusionCache cache,
        IEnumerable<string> keys,
        FusionCacheEntryOptions? options = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(keys);

        var removals = new List<Task>();
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            removals.Add(cache.RemoveAsync(key, options, token).AsTask());
        }

        if (removals.Count > 0)
        {
            await Task.WhenAll(removals).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when a usable value is cached for <paramref name="key"/>. Does not run a
    /// factory and does not extend the entry's lifetime.
    /// </summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Optional per-entry options.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<bool> ExistsAsync<TValue>(
        this IFusionCache cache,
        string key,
        FusionCacheEntryOptions? options = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var result = await cache.TryGetAsync<TValue>(key, options, token).ConfigureAwait(false);
        return result.HasValue;
    }

    /// <summary>
    /// Runs the factory unconditionally and overwrites the entry. Use to refresh a hot key on a
    /// schedule without the read-then-miss race that remove-then-read introduces.
    /// </summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the new value.</param>
    /// <param name="options">Optional per-entry options.</param>
    /// <param name="tags">Optional tags applied to the new entry.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<TValue> RefreshAsync<TValue>(
        this IFusionCache cache,
        string key,
        Func<CancellationToken, Task<TValue>> factory,
        FusionCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(factory);

        var value = await factory(token).ConfigureAwait(false);
        await cache.SetAsync(key, value, options, tags, token).ConfigureAwait(false);
        return value;
    }
}
