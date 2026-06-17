namespace Caching.NET.Abstractions;

/// <summary>
/// Cache service interface for storing and retrieving cached data.
/// This is the single abstraction consumer applications depend on; it can be backed by in-memory, Redis, or hybrid caching.
/// </summary>
/// <remarks>
/// <para><strong>API stability (enterprise):</strong> This interface is the stable contract for Caching.NET. New capabilities are added via
/// extension methods (e.g. <c>Caching.NET.Extensions.CacheServiceCallExtensions</c>), per-call options (<c>CacheCallOptions</c>), and configuration
/// rather than new interface members, to minimize breaking changes. When evolving the library, prefer decorators and options over changing this interface.</para>
/// <para>All operations return <see cref="System.Threading.Tasks.Task"/> / <see cref="System.Threading.Tasks.Task{TResult}"/>. The
/// <c>ValueTask</c> migration was evaluated and reverted before v2.0.0 ship: the breaking-change cost across consumers, mocks, and decorators
/// outweighed the marginal alloc savings on synchronous in-memory hits in mixed Hybrid/Redis production workloads.</para>
/// </remarks>
/// <example>
/// Constructor injection in a controller or service:
/// <code><![CDATA[
/// public class OrderService(ICacheService cache)
/// {
///     public Task<Order> GetAsync(int id, CancellationToken ct) =>
///         cache.GetOrCreateAsync(
///             $"Order:{id}",
///             token => LoadOrderFromDbAsync(id, token),
///             expiration: TimeSpan.FromMinutes(5),
///             cancellationToken: ct);
/// }
/// ]]></code>
/// </example>
public interface ICacheService
{
    /// <summary>
    /// Gets a value from the cache, or creates it by running the <paramref name="factory"/> when it does not exist.
    /// Implementations may provide stampede protection (for example the Hybrid implementation).
    /// </summary>
    /// <typeparam name="T">The non-nullable type of the value being cached.</typeparam>
    /// <param name="key">A non-empty cache key that uniquely identifies the value.</param>
    /// <param name="factory">
    /// Asynchronous factory that is invoked when the value is not found in the cache.
    /// It is responsible for loading or computing the value (for example from a database or external service).
    /// </param>
    /// <param name="expiration">
    /// Optional absolute expiration for the value. When <c>null</c>, the implementation falls back to its configured default.
    /// For hybrid caching this controls the overall (distributed) expiration.
    /// </param>
    /// <param name="localExpiration">
    /// Optional absolute expiration for the local in-memory copy when a hybrid cache is used.
    /// This parameter is only meaningful for hybrid implementations; other implementations ignore it.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the factory call or underlying cache operations.</param>
    /// <example>
    /// <code><![CDATA[
    /// // Cache an order for 5 minutes; factory runs only on miss, concurrent callers coalesce.
    /// var order = await cache.GetOrCreateAsync(
    ///     $"Order:{orderId}",
    ///     ct => _db.LoadOrderAsync(orderId, ct),
    ///     expiration: TimeSpan.FromMinutes(5),
    ///     cancellationToken: ct);
    ///
    /// // Hybrid: shorter L1 TTL (per-instance), longer L2 TTL (shared Redis).
    /// var product = await cache.GetOrCreateAsync(
    ///     $"Product:{sku}",
    ///     ct => _catalog.GetAsync(sku, ct),
    ///     expiration: TimeSpan.FromMinutes(5),
    ///     localExpiration: TimeSpan.FromSeconds(30),
    ///     cancellationToken: ct);
    /// ]]></code>
    /// </example>
    Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Sets a value in the cache with optional expiration.
    /// </summary>
    /// <typeparam name="T">The non-nullable type of the value being cached.</typeparam>
    /// <param name="key">A non-empty cache key that uniquely identifies the value.</param>
    /// <param name="value">The value to store in the cache.</param>
    /// <param name="expiration">
    /// Optional absolute expiration for the value. When <c>null</c>, the implementation falls back to its configured default.
    /// For hybrid caching this controls the overall (distributed) expiration.
    /// </param>
    /// <param name="localExpiration">
    /// Optional absolute expiration for the local in-memory copy when a hybrid cache is used.
    /// This parameter is only meaningful for hybrid implementations; other implementations ignore it.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operation.</param>
    /// <example>
    /// <code><![CDATA[
    /// // After a write-through, prime the cache so the next read is a hit.
    /// await _db.SaveOrderAsync(order, ct);
    /// await cache.SetAsync(
    ///     $"Order:{order.Id}",
    ///     order,
    ///     expiration: TimeSpan.FromMinutes(5),
    ///     cancellationToken: ct);
    /// ]]></code>
    /// </example>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Removes a value from the cache by key.
    /// Does nothing when the key is <c>null</c>, empty, or whitespace-only.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operation.</param>
    /// <example>
    /// <code><![CDATA[
    /// // Invalidate after a destructive write.
    /// await _db.DeleteOrderAsync(orderId, ct);
    /// await cache.RemoveAsync($"Order:{orderId}", ct);
    /// ]]></code>
    /// </example>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries associated with a tag.
    /// Implementations that do not support tags must treat this as a no-op.
    /// </summary>
    /// <param name="tag">The tag identifying a group of cache entries.</param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operation.</param>
    /// <example>
    /// <code><![CDATA[
    /// // Hybrid mode only. Tag entries on write, invalidate the whole group on update.
    /// using Caching.NET.Extensions;
    /// using Caching.NET.Options;
    ///
    /// var opts = new CacheCallOptions { Tags = new[] { $"category:{categoryId}" } };
    /// await cache.SetAsync($"Product:{sku}", product, opts, TimeSpan.FromMinutes(5), cancellationToken: ct);
    ///
    /// // Later, on category edit:
    /// await cache.RemoveByTagAsync($"category:{categoryId}", ct);
    /// ]]></code>
    /// </example>
    Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries associated with multiple tags.
    /// Implementations that do not support tags must treat this as a no-op.
    /// </summary>
    /// <param name="tags">The collection of tags identifying groups of cache entries.</param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operations.</param>
    /// <example>
    /// <code><![CDATA[
    /// // Bulk invalidation across multiple tag groups.
    /// await cache.RemoveByTagAsync(new[] { $"user:{userId}", $"tenant:{tenantId}" }, ct);
    /// ]]></code>
    /// </example>
    Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a value from the cache without invoking a factory. Returns <c>default(T)</c>
    /// when the key is absent. Implementations must not throw on miss.
    /// </summary>
    /// <example>
    /// <code><![CDATA[
    /// // Cheap probe; no factory ever runs.
    /// var cached = await cache.GetAsync<Order>($"Order:{orderId}", ct);
    /// if (cached is null)
    /// {
    ///     // miss — caller decides whether to load
    /// }
    /// ]]></code>
    /// </example>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Runtime-typed counterpart to <see cref="GetAsync{T}"/>. Reads a value from the cache without invoking
    /// a factory, deserializing into <paramref name="type"/>. Returns <c>null</c> on miss, envelope-invalid,
    /// format drift, or schema drift — implementations must not throw on miss. The schema-hash and format
    /// validation are identical to the generic path for the same CLR type, so values written via
    /// <see cref="SetAsync{T}"/> are readable here and vice-versa.
    /// </summary>
    /// <param name="key">A non-empty cache key that uniquely identifies the value.</param>
    /// <param name="type">The runtime type to deserialize the cached value into. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operation.</param>
    /// <remarks>
    /// Prefer the generic <see cref="GetAsync{T}"/> when the type is known at compile time. This overload exists
    /// for callers that only have a runtime <see cref="System.Type"/> (e.g. a settings cache keyed by type). The
    /// default interface method reflects onto <see cref="GetAsync{T}"/> so existing custom implementations keep
    /// working; the built-in services override it with an efficient path.
    /// </remarks>
    /// <example>
    /// <code><![CDATA[
    /// // Type only known at runtime (e.g. resolved from DI by settings type).
    /// object? cached = await cache.GetAsync(BuildKey(type), type, ct);
    /// if (cached is null)
    /// {
    ///     // miss — caller decides whether to load
    /// }
    /// ]]></code>
    /// </example>
    Task<object?> GetAsync(string key, Type type, CancellationToken cancellationToken = default)
        => Caching.NET.Internal.RuntimeTypedCacheInvoker.GetAsync(this, key, type, cancellationToken);

    /// <summary>
    /// Returns <c>true</c> when the cache contains an entry for <paramref name="key"/>.
    /// Implementations should use the cheapest existence check available
    /// (e.g. Redis EXISTS; IMemoryCache TryGetValue).
    /// </summary>
    /// <example>
    /// <code><![CDATA[
    /// // Idempotency check — only enqueue work for keys not already in flight.
    /// if (!await cache.ExistsAsync($"Job:{jobId}", ct))
    /// {
    ///     await EnqueueAsync(jobId, ct);
    /// }
    /// ]]></code>
    /// </example>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Always invokes <paramref name="factory"/> and writes the result into the cache,
    /// overwriting any existing entry. Use to refresh stale data without removing the
    /// key first.
    /// </summary>
    /// <example>
    /// <code><![CDATA[
    /// // Scheduled background refresh of a hot key — no read-then-miss race.
    /// await cache.RefreshAsync(
    ///     "Leaderboard:Top10",
    ///     ct => ComputeTop10Async(ct),
    ///     expiration: TimeSpan.FromMinutes(1),
    ///     cancellationToken: ct);
    /// ]]></code>
    /// </example>
    Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Reads multiple values from the cache. The returned dictionary contains an entry
    /// for every key in <paramref name="keys"/> — missing keys map to <c>default(T)</c>.
    /// </summary>
    /// <example>
    /// <code><![CDATA[
    /// // Batch lookup; load only the misses from the source.
    /// var keys = orderIds.Select(id => $"Order:{id}").ToArray();
    /// var hits = await cache.GetManyAsync<Order>(keys, ct);
    ///
    /// var missing = orderIds.Where(id => hits[$"Order:{id}"] is null).ToList();
    /// if (missing.Count > 0)
    /// {
    ///     var loaded = await _db.LoadOrdersAsync(missing, ct);
    ///     await cache.SetManyAsync(
    ///         loaded.ToDictionary(o => $"Order:{o.Id}"),
    ///         expiration: TimeSpan.FromMinutes(5),
    ///         cancellationToken: ct);
    /// }
    /// ]]></code>
    /// </example>
    Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Writes multiple values to the cache. All entries share the same expiration arguments.
    /// </summary>
    /// <example>
    /// <code><![CDATA[
    /// // Warm the cache after a bulk DB read.
    /// var items = products.ToDictionary(p => $"Product:{p.Sku}");
    /// await cache.SetManyAsync(items, expiration: TimeSpan.FromMinutes(10), cancellationToken: ct);
    /// ]]></code>
    /// </example>
    Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Removes multiple keys. <c>null</c>/empty/whitespace keys are skipped.
    /// </summary>
    /// <example>
    /// <code><![CDATA[
    /// // Invalidate a batch after a multi-row write.
    /// await cache.RemoveManyAsync(orderIds.Select(id => $"Order:{id}"), ct);
    /// ]]></code>
    /// </example>
    Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);
}
