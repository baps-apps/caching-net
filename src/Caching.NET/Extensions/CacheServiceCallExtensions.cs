using Caching.NET.Abstractions;
using Caching.NET.Options;
using Caching.NET.Services;

namespace Caching.NET.Extensions;

/// <summary>
/// Extension overloads for <see cref="ICacheService"/> that accept per-call options.
/// These are optional convenience methods; existing callers can continue using
/// the core interface methods without modification.
/// </summary>
public static class CacheServiceCallExtensions
{
    /// <summary>
    /// Gets or creates a cache value with optional per-call options such as a cache mode override.
    /// When the underlying <paramref name="cache"/> does not support per-call options, they are silently ignored
    /// and the core <see cref="ICacheService.GetOrCreateAsync{T}"/> overload is used as a safe fallback.
    /// </summary>
    /// <typeparam name="T">The non-nullable type of the value being cached.</typeparam>
    /// <param name="cache">The cache service to call.</param>
    /// <param name="key">A non-empty cache key that uniquely identifies the value.</param>
    /// <param name="factory">
    /// Asynchronous factory invoked when the value is not found in the cache.
    /// Responsible for loading or computing the value (e.g. from a database or external service).
    /// </param>
    /// <param name="callOptions">
    /// Optional per-call options (e.g. <see cref="CacheCallOptions.Mode"/>, <see cref="CacheCallOptions.ForceRefresh"/>,
    /// <see cref="CacheCallOptions.BypassCache"/>, <see cref="CacheCallOptions.CoalesceConcurrent"/>).
    /// Pass <c>null</c> to use the application-level defaults.
    /// </param>
    /// <param name="expiration">Optional absolute expiration. Falls back to the configured default when <c>null</c>.</param>
    /// <param name="localExpiration">
    /// Optional local (in-memory) expiration for Hybrid mode. Ignored by non-Hybrid implementations.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the factory or underlying cache operation.</param>
    /// <returns>The cached value if present; otherwise the value produced by <paramref name="factory"/>.</returns>
    public static Task<T> GetOrCreateAsync<T>(
        this ICacheService cache,
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        if (cache is IRoutingCacheService routing)
        {
            return routing.GetOrCreateAsync(
                key,
                factory,
                callOptions,
                expiration,
                localExpiration,
                cancellationToken);
        }

        // Safe fallback: ignore per-call options when the underlying cache does not support them.
        return cache.GetOrCreateAsync(
            key,
            factory,
            expiration,
            localExpiration,
            cancellationToken);
    }

    /// <summary>
    /// Sets a value in the cache with optional per-call options such as a cache mode override.
    /// When the underlying <paramref name="cache"/> does not support per-call options, they are silently ignored
    /// and the core <see cref="ICacheService.SetAsync{T}"/> overload is used as a safe fallback.
    /// </summary>
    /// <typeparam name="T">The non-nullable type of the value being cached.</typeparam>
    /// <param name="cache">The cache service to call.</param>
    /// <param name="key">A non-empty cache key that uniquely identifies the value.</param>
    /// <param name="value">The value to store in the cache.</param>
    /// <param name="callOptions">
    /// Optional per-call options (e.g. <see cref="CacheCallOptions.Mode"/>, <see cref="CacheCallOptions.BypassCache"/>).
    /// Pass <c>null</c> to use the application-level defaults.
    /// </param>
    /// <param name="expiration">Optional absolute expiration. Falls back to the configured default when <c>null</c>.</param>
    /// <param name="localExpiration">
    /// Optional local (in-memory) expiration for Hybrid mode. Ignored by non-Hybrid implementations.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the underlying cache operation.</param>
    public static Task SetAsync<T>(
        this ICacheService cache,
        string key,
        T value,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        if (cache is IRoutingCacheService routing)
        {
            return routing.SetAsync(
                key,
                value,
                callOptions,
                expiration,
                localExpiration,
                cancellationToken);
        }

        // Safe fallback: ignore per-call options when the underlying cache does not support them.
        return cache.SetAsync(
            key,
            value,
            expiration,
            localExpiration,
            cancellationToken);
    }
}

