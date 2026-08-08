using System.Text.Json;

namespace Caching.NET.Options;

/// <summary>
/// Wire format used for the distributed (Redis) layer. Only used when
/// <see cref="CachingOptions.UsesDistributedLayer"/> is true.
/// </summary>
public enum CacheSerializerFormat
{
    /// <summary>
    /// <c>System.Text.Json</c>. Human-readable, no type-name handling, safe against polymorphic
    /// deserialization attacks. Default.
    /// </summary>
    SystemTextJson = 0,

    /// <summary>
    /// MessagePack using the contractless resolver. Smaller and faster than JSON. Type names are
    /// never written to or read from the payload, so it is not vulnerable to type-confusion
    /// attacks, but it is not human-readable and both writer and reader must agree on the shape.
    /// </summary>
    MessagePack = 1
}

/// <summary>
/// Payload compression settings for the distributed layer.
/// </summary>
public sealed class CacheCompressionOptions
{
    /// <summary>Enable Brotli compression for payloads at or above <see cref="ThresholdBytes"/>. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minimum serialized size before compression is attempted. Default 16&#160;KiB.</summary>
    public int ThresholdBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// Hard ceiling on the decompressed size of an inbound payload. Decompression stops and the
    /// payload is rejected as corrupt beyond this, which bounds decompression-bomb exposure.
    /// Default 16&#160;MiB.
    /// </summary>
    public int MaximumDecompressedBytes { get; set; } = 16 * 1024 * 1024;
}

/// <summary>
/// Serialization settings for the distributed layer.
/// </summary>
public sealed class CacheSerializationOptions
{
    /// <summary>Wire format. Default <see cref="CacheSerializerFormat.SystemTextJson"/>.</summary>
    public CacheSerializerFormat Format { get; set; } = CacheSerializerFormat.SystemTextJson;

    /// <summary>
    /// Maximum serialized size of a single cache entry, in bytes. Writes above this are rejected
    /// and reads above this are treated as corrupt, which bounds both memory pressure and the
    /// blast radius of an oversized-value denial-of-service attempt. Must be greater than zero.
    /// Default 1&#160;MiB.
    /// </summary>
    public long MaximumPayloadBytes { get; set; } = 1_048_576;

    /// <summary>Compression settings.</summary>
    public CacheCompressionOptions Compression { get; set; } = new();

    /// <summary>
    /// Code-only override for the JSON serializer, used when
    /// <see cref="Format"/> is <see cref="CacheSerializerFormat.SystemTextJson"/>.
    /// Supply a source-generated <c>JsonSerializerContext</c> through
    /// <see cref="JsonSerializerOptions.TypeInfoResolver"/> for trim- and AOT-safe serialization.
    /// Not bound from configuration.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
