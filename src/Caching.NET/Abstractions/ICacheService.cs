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
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries associated with a tag.
    /// Implementations that do not support tags must treat this as a no-op.
    /// </summary>
    /// <param name="tag">The tag identifying a group of cache entries.</param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operation.</param>
    Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries associated with multiple tags.
    /// Implementations that do not support tags must treat this as a no-op.
    /// </summary>
    /// <param name="tags">The collection of tags identifying groups of cache entries.</param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operations.</param>
    Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a value from the cache without invoking a factory. Returns <c>default(T)</c>
    /// when the key is absent. Implementations must not throw on miss.
    /// </summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Returns <c>true</c> when the cache contains an entry for <paramref name="key"/>.
    /// Implementations should use the cheapest existence check available
    /// (e.g. Redis EXISTS; IMemoryCache TryGetValue).
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Always invokes <paramref name="factory"/> and writes the result into the cache,
    /// overwriting any existing entry. Use to refresh stale data without removing the
    /// key first.
    /// </summary>
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
    Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Writes multiple values to the cache. All entries share the same expiration arguments.
    /// </summary>
    Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Removes multiple keys. <c>null</c>/empty/whitespace keys are skipped.
    /// </summary>
    Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);
}
