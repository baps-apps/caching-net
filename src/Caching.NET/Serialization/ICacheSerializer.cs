using System.Reflection;

namespace Caching.NET.Serialization;

/// <summary>
/// Pluggable cache value serializer. Implementations must be thread-safe and stateless.
/// FormatId is recorded in the PayloadEnvelope (P1) so cross-serializer drift is detected on read.
/// </summary>
public interface ICacheSerializer
{
    /// <summary>Short stable identifier (e.g., "json", "msgpack"). Recorded in payload envelope.</summary>
    string FormatId { get; }

    /// <summary>Serialize a value to bytes for cache storage.</summary>
    byte[] Serialize<T>(T value);

    /// <summary>Deserialize bytes back into a value. Returns default(T) for an empty buffer.</summary>
    T? Deserialize<T>(ReadOnlyMemory<byte> bytes);

    /// <summary>
    /// Runtime-typed counterpart to <see cref="Deserialize{T}"/>. Deserializes <paramref name="bytes"/>
    /// into <paramref name="type"/>, returning <c>null</c> for an empty buffer. The default implementation
    /// reflects onto <see cref="Deserialize{T}"/> so existing custom serializers keep working without a
    /// recompile; built-in serializers override it with a direct, allocation-light path.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Reflection fallback for custom serializers that did not override this overload; built-in serializers override it with a trim-safe path.")]
    object? Deserialize(ReadOnlyMemory<byte> bytes, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var method = GenericDeserializeDefinition.MakeGenericMethod(type);
        return method.Invoke(this, [bytes]);
    }

    private static readonly MethodInfo GenericDeserializeDefinition = typeof(ICacheSerializer)
        .GetMethods()
        .Single(m => m is { Name: nameof(Deserialize), IsGenericMethodDefinition: true }
            && m.GetParameters() is [{ ParameterType.IsGenericParameter: false }]);
}
