namespace Caching.NET.Keys;

/// <summary>
/// Produces <see cref="CacheKeyBuilder"/> instances for type-safe cache keys. Register a custom
/// implementation when you need tenant, region, or other segments injected for every key
/// (e.g. wrap <see cref="CacheKeyBuilder.WithSegment(string)"/>). The default
/// <see cref="DefaultCacheKeyFactory"/> mirrors <see cref="CacheKey.For{T}(object)"/>.
/// </summary>
public interface ICacheKeyFactory
{
    /// <summary>Same contract as <see cref="CacheKey.For{T}(object)"/> — begins a key for <typeparamref name="T"/>.</summary>
    CacheKeyBuilder For<T>(object id);
}
