namespace Caching.NET;

/// <summary>
/// Resolves Caching.NET cache instances by name.
/// </summary>
/// <remarks>
/// <para>
/// Prefer constructor injection when the cache is known at compile time — inject
/// <see cref="ICacheService"/> for the default cache, or apply
/// <c>[FromKeyedServices("name")]</c> for a named one. Use this provider when the cache is chosen
/// at run time, or when a component needs several caches.
/// </para>
/// <para>
/// Every instance returned here is a singleton created and owned by Caching.NET's registration;
/// the provider resolves, it never constructs, and it holds no mutable state.
/// </para>
/// </remarks>
/// <example>
/// <code><![CDATA[
/// public sealed class ProductService(ICacheProvider caches)
/// {
///     private readonly ICacheService _hot = caches.GetCache("short-lived");
///     private readonly ICacheService _main = caches.Default;
/// }
/// ]]></code>
/// </example>
public interface ICacheProvider
{
    /// <summary>
    /// The cache registered by the unnamed <c>AddCaching</c> overloads.
    /// </summary>
    /// <exception cref="InvalidOperationException">No default cache was registered.</exception>
    ICacheService Default { get; }

    /// <summary>Names of every cache registered through Caching.NET, in registration order.</summary>
    IReadOnlyList<string> CacheNames { get; }

    /// <summary>Resolves a cache by name.</summary>
    /// <param name="cacheName">The configured <c>CacheName</c>.</param>
    /// <exception cref="InvalidOperationException">No cache is registered under that name.</exception>
    ICacheService GetCache(string cacheName);

    /// <summary>Resolves a cache by name, returning <c>null</c> when it is not registered.</summary>
    /// <param name="cacheName">The configured <c>CacheName</c>.</param>
    ICacheService? GetCacheOrNull(string cacheName);

    /// <summary>Resolves the key and tag guard for a named cache.</summary>
    /// <param name="cacheName">The configured <c>CacheName</c>.</param>
    /// <exception cref="InvalidOperationException">No cache is registered under that name.</exception>
    ICacheGuard GetGuard(string cacheName);
}
