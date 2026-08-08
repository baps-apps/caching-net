using System.Collections.Concurrent;
using System.Globalization;

namespace Caching.NET.Keys;

/// <summary>
/// Canonical cache-key builder. Produces keys of the form
/// <c>{TypeName}:{id}[:{segment}]…</c>. The application, environment and tenant prefixes
/// configured on <c>CachingOptions</c> are applied by the cache itself and must not be
/// repeated here.
/// </summary>
/// <remarks>
/// The type segment is the type's simple name, with generic arguments appended
/// (<c>List_Int32</c>) so that two closed generics over the same open type cannot share a key.
/// Two distinct types with the same simple name in different namespaces still produce the same
/// segment — add a variant segment when an application caches both under the same identifier.
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var key = CacheKey.For<Product>(sku).WithVariant("v2").Build();   // "Product:ABC-1:v2"
/// var product = await cache.GetOrSetAsync(key, ct => LoadAsync(sku, ct), token: ct);
/// ]]></code>
/// </example>
public static class CacheKey
{
    // Bounded by the set of closed types an application actually caches, so it cannot grow with
    // traffic. Keeps the generic-name work off the per-call path.
    private static readonly ConcurrentDictionary<Type, string> s_typeSegments = new();

    /// <summary>Begins a key for type <typeparamref name="T"/> with the given identifier.</summary>
    /// <typeparam name="T">The cached entity type; its name becomes the first key segment.</typeparam>
    /// <param name="id">Entity identifier. Formatted with the invariant culture so that every host produces the same key.</param>
    public static CacheKeyBuilder For<T>(object id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var idText = id is IFormattable formattable
            ? formattable.ToString(format: null, formatProvider: CultureInfo.InvariantCulture)
            : id.ToString() ?? string.Empty;
        return new CacheKeyBuilder(TypeSegment(typeof(T)), idText);
    }

    internal static string TypeSegment(Type type) => s_typeSegments.GetOrAdd(type, BuildTypeSegment);

    private static string BuildTypeSegment(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        // "List`1" alone would make List<int> and List<string> the same key, so the closed
        // arguments are part of the segment. '`' is dropped because it is not a key-safe character.
        var name = type.Name;
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        if (arity >= 0)
        {
            name = name[..arity];
        }

        var arguments = type.GetGenericArguments();
        var segments = new string[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            segments[i] = BuildTypeSegment(arguments[i]);
        }

        return string.Concat(name, "_", string.Join('_', segments));
    }
}
