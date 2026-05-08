namespace Caching.NET.Keys;

/// <summary>
/// Default <see cref="ICacheKeyFactory"/> that delegates to <see cref="CacheKey.For{T}(object)"/>.
/// </summary>
public sealed class DefaultCacheKeyFactory : ICacheKeyFactory
{
    /// <inheritdoc />
    public CacheKeyBuilder For<T>(object id) => CacheKey.For<T>(id);
}
