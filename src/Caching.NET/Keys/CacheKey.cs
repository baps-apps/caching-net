using System.Globalization;

namespace Caching.NET.Keys;

/// <summary>
/// Canonical key-builder factory. Produces keys of the form
/// <c>{TypeName}:{id}[:{variant}][:{segment}]…</c>. Does NOT prepend the
/// configured <c>KeyPrefix</c> — the routing layer adds that.
/// For DI and testable/tenant-specific key building, use <see cref="ICacheKeyFactory"/> (registered by <c>AddCaching</c>).
/// </summary>
public static class CacheKey
{
    /// <summary>Begin a key for type <typeparamref name="T"/> with the given id.</summary>
    public static CacheKeyBuilder For<T>(object id)
    {
        ArgumentNullException.ThrowIfNull(id);
        // Format id with the invariant culture so locale-sensitive types (DateTime, decimal,
        // double, …) produce the same wire key on every host.
        var idString = id is IFormattable f
            ? f.ToString(format: null, formatProvider: CultureInfo.InvariantCulture)
            : id.ToString() ?? string.Empty;
        return new CacheKeyBuilder(typeof(T).Name, idString);
    }
}
