// src/Caching.NET/Internal/StableTypeHash.cs
namespace Caching.NET.Internal;

/// <summary>
/// Deterministic 64-bit hash of a CLR type's assembly-qualified name.
/// Used by the payload envelope to detect schema drift across deploys.
/// </summary>
internal static class StableTypeHash
{
    public static ulong Compute<T>() => Cache<T>.Value;

    private static class Cache<T>
    {
        public static readonly ulong Value = StableStringHash.Compute64(typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name);
    }
}
