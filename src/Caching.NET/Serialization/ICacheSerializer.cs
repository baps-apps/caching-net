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
}
