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
    public static ulong Compute<T>() => Cache<T>.Value;

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
