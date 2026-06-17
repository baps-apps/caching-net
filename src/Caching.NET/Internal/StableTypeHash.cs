using System.Collections.Concurrent;
using System.Reflection;
using SchemaAttribute = Caching.NET.CacheSchemaAttribute;

namespace Caching.NET.Internal;

/// <summary>
/// Deterministic 64-bit hash of a CLR type identity for the payload envelope.
/// Uses <see cref="Type.FullName"/> (not assembly-qualified name) so patch/package version bumps do not
/// change the hash; opt into <see cref="T:Caching.NET.CacheSchemaAttribute"/> for intentional invalidation.
/// </summary>
internal static class StableTypeHash
{
    private static readonly ConcurrentDictionary<Type, ulong> RuntimeCache = new();

    /// <summary>Compute the stable schema hash for the compile-time type <typeparamref name="T"/>.</summary>
    public static ulong Compute<T>() => Cache<T>.Value;

    /// <summary>
    /// Runtime-typed counterpart to <see cref="Compute{T}"/>. Returns the identical hash that
    /// <see cref="Compute{T}"/> produces when <paramref name="type"/> equals <c>typeof(T)</c>, so the
    /// generic and non-generic cache read paths never diverge.
    /// </summary>
    public static ulong Compute(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return RuntimeCache.GetOrAdd(type, static t => ComputeFor(t));
    }

    private static class Cache<T>
    {
        public static readonly ulong Value = ComputeFor(typeof(T));
    }

    private static ulong ComputeFor(Type type)
    {
        var identity = type.FullName ?? type.Name;
        var attr = type.GetCustomAttribute<SchemaAttribute>(inherit: false);
        var input = attr is null
            ? identity
            : string.Concat(identity, "\u001F", attr.Version);
        return StableStringHash.Compute64(input);
    }
}
