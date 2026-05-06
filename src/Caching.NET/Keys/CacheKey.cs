namespace Caching.NET.Keys;

/// <summary>
/// Canonical key-builder factory. Produces keys of the form
/// <c>{TypeName}:{id}[:{variant}][:{segment}]…</c>. Does NOT prepend the
/// configured <c>KeyPrefix</c> — the routing layer adds that.
/// </summary>
public static class CacheKey
{
    /// <summary>Begin a key for type <typeparamref name="T"/> with the given id.</summary>
    public static CacheKeyBuilder For<T>(object id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new CacheKeyBuilder(typeof(T).Name, id.ToString() ?? string.Empty);
    }
}
