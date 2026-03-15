namespace Caching.NET.Abstractions;

/// <summary>
/// Abstraction for cache-level telemetry (metrics and traces).
/// Implementations are responsible for emitting metrics, spans, or logs to the
/// application's observability stack. The default implementation is a no-op.
/// </summary>
public interface ICacheTelemetry
{
    /// <summary>
    /// Called when a cache lookup returns a value that was already present in the cache.
    /// </summary>
    /// <param name="key">The cache key that was looked up.</param>
    /// <param name="mode">The cache mode that served the hit (e.g. "InMemory", "Redis", "Hybrid").</param>
    void OnCacheHit(string key, string mode);

    /// <summary>
    /// Called when a cache lookup finds no cached value, causing the factory to be invoked.
    /// </summary>
    /// <param name="key">The cache key that was looked up.</param>
    /// <param name="mode">The cache mode that produced the miss.</param>
    void OnCacheMiss(string key, string mode);

    /// <summary>
    /// Called when a value is successfully written to the cache.
    /// </summary>
    /// <param name="key">The cache key that was written.</param>
    /// <param name="mode">The cache mode that stored the value.</param>
    void OnCacheSet(string key, string mode);

    /// <summary>
    /// Called when a single cache entry is removed by key.
    /// </summary>
    /// <param name="key">The cache key that was removed.</param>
    /// <param name="mode">The cache mode that performed the removal.</param>
    void OnCacheRemove(string key, string mode);

    /// <summary>
    /// Called when cache entries are evicted by tag.
    /// Implementations that do not support tags may call this unconditionally or skip it.
    /// </summary>
    /// <param name="tag">The tag whose associated entries were removed.</param>
    /// <param name="mode">The cache mode that performed the tag removal.</param>
    void OnCacheRemoveByTag(string tag, string mode);

    /// <summary>
    /// Called when a cache operation (get, set, remove, serialize, etc.) fails with an exception.
    /// </summary>
    /// <param name="operation">A short label for the operation that failed (e.g. "get_or_create", "set", "remove").</param>
    /// <param name="keyOrTag">The cache key or tag that was involved in the failed operation.</param>
    /// <param name="mode">The cache mode in which the error occurred.</param>
    /// <param name="exception">The exception that was thrown.</param>
    void OnCacheError(string operation, string keyOrTag, string mode, Exception exception);

    /// <summary>
    /// Called when the factory delegate supplied to <c>GetOrCreateAsync</c> exceeds the configured
    /// <see cref="Caching.NET.Options.CacheOptions.FactoryTimeout"/> and is cancelled.
    /// </summary>
    /// <param name="key">The cache key whose factory timed out.</param>
    /// <param name="mode">The cache mode that attempted to call the factory.</param>
    /// <param name="timeout">The timeout duration that was exceeded.</param>
    void OnFactoryTimeout(string key, string mode, TimeSpan timeout);
}

