namespace Caching.NET.Keys;

/// <summary>
/// Produces <see cref="CacheKeyBuilder"/> instances. Register a custom implementation before
/// <c>AddCaching</c> when every key needs an injected segment (tenant, region, schema version).
/// The default implementation mirrors <see cref="CacheKey.For{T}(object)"/>.
/// </summary>
public interface ICacheKeyFactory
{
    /// <summary>Begins a key for <typeparamref name="T"/>, same contract as <see cref="CacheKey.For{T}(object)"/>.</summary>
    /// <typeparam name="T">The cached entity type.</typeparam>
    /// <param name="id">Entity identifier.</param>
    CacheKeyBuilder For<T>(object id);
}
