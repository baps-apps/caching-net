using System.Buffers.Binary;

namespace Caching.NET.Serialization;

/// <summary>
/// Wire-level wrapper for cached payloads. Layout: 4-byte magic "CN20" + 1-byte FormatId
/// + 8-byte little-endian schema hash + 4-byte little-endian payload length + payload bytes.
/// Used by Redis-backed services so schema drift, format swaps, and corrupt entries surface
/// as misses rather than runtime exceptions.
/// </summary>
public static class PayloadEnvelope
{
    /// <summary>ASCII "CN20" — magic prefix identifying a Caching.NET v2 envelope.</summary>
    public static ReadOnlySpan<byte> Magic => "CN20"u8;

    /// <summary>Fixed header size in bytes for built-in (non-custom) format ids.</summary>
    public const int HeaderSize = 17;

    /// <summary>FormatId for the built-in JSON serializer.</summary>
    public const byte FormatIdJson = 0x01;

    /// <summary>FormatId for the built-in MessagePack serializer.</summary>
    public const byte FormatIdMsgPack = 0x02;

    /// <summary>Allocate a wire-format byte[] containing the envelope and payload.</summary>
    /// <param name="payload">Serialized payload bytes (empty allowed).</param>
    /// <param name="formatId">Caller's serializer FormatId byte.</param>
    /// <param name="schemaHash">64-bit type-stable hash (typically <see cref="Caching.NET.Internal.StableTypeHash.Compute{T}"/>).</param>
    public static byte[] Write(ReadOnlySpan<byte> payload, byte formatId, ulong schemaHash)
    {
        var wire = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(wire);
        wire[4] = formatId;
        BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(5, 8), schemaHash);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(13, 4), (uint)payload.Length);
        if (!payload.IsEmpty)
            payload.CopyTo(wire.AsSpan(HeaderSize));
        return wire;
    }

    /// <summary>
    /// Validate magic + format + schema and return the inner payload.
    /// On any mismatch returns a non-Ok result and an empty span — callers MUST treat that as a miss.
    /// </summary>
    public static PayloadEnvelopeReadResult TryRead(
        ReadOnlySpan<byte> wire,
        byte expectedFormatId,
        ulong expectedSchemaHash,
        out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (wire.Length < HeaderSize) return PayloadEnvelopeReadResult.EnvelopeInvalid;
        if (!wire[..4].SequenceEqual(Magic)) return PayloadEnvelopeReadResult.EnvelopeInvalid;

        var len = BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(13, 4));
        if (len > (uint)(wire.Length - HeaderSize)) return PayloadEnvelopeReadResult.EnvelopeInvalid;

        var format = wire[4];
        if (format != expectedFormatId) return PayloadEnvelopeReadResult.FormatDrift;

        var schema = BinaryPrimitives.ReadUInt64LittleEndian(wire.Slice(5, 8));
        if (schema != expectedSchemaHash) return PayloadEnvelopeReadResult.SchemaDrift;

        payload = wire.Slice(HeaderSize, (int)len);
        return PayloadEnvelopeReadResult.Ok;
    }
}
