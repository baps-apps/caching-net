using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace Caching.NET.Serialization;

/// <summary>
/// Wire-level wrapper for cached payloads. Layout: 4-byte magic "CN20" + 1-byte FormatId
/// + 8-byte little-endian schema hash + 4-byte little-endian payload length + payload bytes
/// + 4-byte little-endian payload checksum (XxHash32).
/// Used by Redis-backed services so schema drift, format swaps, and corrupt entries surface
/// as misses rather than runtime exceptions.
/// </summary>
public static class PayloadEnvelope
{
    private const byte FormatFlagMask = 0x7F;
    /// <summary>ASCII "CN20" — magic prefix identifying a Caching.NET v2 envelope.</summary>
    public static ReadOnlySpan<byte> Magic => "CN20"u8;

    /// <summary>Fixed header size in bytes for built-in (non-custom) format ids.</summary>
    public const int HeaderSize = 17;
    /// <summary>Fixed trailer size in bytes for payload checksum.</summary>
    public const int TrailerSize = 4;

    /// <summary>FormatId for the built-in JSON serializer.</summary>
    public const byte FormatIdJson = 0x01;

    /// <summary>FormatId for the built-in MessagePack serializer.</summary>
    public const byte FormatIdMsgPack = 0x02;

    /// <summary>FormatId for unknown or custom serializers.</summary>
    public const byte FormatIdUnknown = 0xFF;

    /// <summary>Allocate a wire-format byte[] containing the envelope and payload.</summary>
    /// <param name="payload">Serialized payload bytes (empty allowed).</param>
    /// <param name="formatId">Caller's serializer FormatId byte.</param>
    /// <param name="schemaHash">64-bit type-stable hash (typically <see cref="Caching.NET.Internal.StableTypeHash.Compute{T}"/> — based on <see cref="Type.FullName"/> and optional <see cref="CacheSchemaAttribute"/>).</param>
    public static byte[] Write(ReadOnlySpan<byte> payload, byte formatId, ulong schemaHash)
    {
        var needed = HeaderSize + payload.Length + TrailerSize;
        // Skip zero-init on the wire buffer; Write fills every byte (audit P5).
        var wire = GC.AllocateUninitializedArray<byte>(needed);
        Write(payload, formatId, schemaHash, wire);
        return wire;
    }

    /// <summary>Writes the envelope and payload to <paramref name="writer"/> without an intermediate <c>byte[]</c>.</summary>
    public static void Write(ReadOnlySpan<byte> payload, byte formatId, ulong schemaHash, IBufferWriter<byte> writer)
    {
        var needed = HeaderSize + payload.Length + TrailerSize;
        var span = writer.GetSpan(needed);
        Write(payload, formatId, schemaHash, span);
        writer.Advance(needed);
    }

    private static void Write(ReadOnlySpan<byte> payload, byte formatId, ulong schemaHash, Span<byte> wire)
    {
        Magic.CopyTo(wire);
        wire[4] = formatId;
        BinaryPrimitives.WriteUInt64LittleEndian(wire[5..], schemaHash);
        BinaryPrimitives.WriteUInt32LittleEndian(wire[13..], (uint)payload.Length);
        if (!payload.IsEmpty)
            payload.CopyTo(wire[HeaderSize..]);
        var checksumOffset = HeaderSize + payload.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(wire[checksumOffset..], XxHash32.HashToUInt32(payload));
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
        if (wire.Length < HeaderSize + TrailerSize) return PayloadEnvelopeReadResult.EnvelopeInvalid;
        if (!wire[..4].SequenceEqual(Magic)) return PayloadEnvelopeReadResult.EnvelopeInvalid;

        var len = BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(13, 4));
        if (len != (uint)(wire.Length - HeaderSize - TrailerSize)) return PayloadEnvelopeReadResult.EnvelopeInvalid;

        var format = wire[4];
        if ((format & FormatFlagMask) != (expectedFormatId & FormatFlagMask))
            return PayloadEnvelopeReadResult.FormatDrift;

        var schema = BinaryPrimitives.ReadUInt64LittleEndian(wire.Slice(5, 8));
        if (schema != expectedSchemaHash) return PayloadEnvelopeReadResult.SchemaDrift;

        var candidate = wire.Slice(HeaderSize, (int)len);
        var checksumOffset = HeaderSize + (int)len;
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(checksumOffset, TrailerSize));
        if (XxHash32.HashToUInt32(candidate) != expectedChecksum) return PayloadEnvelopeReadResult.EnvelopeInvalid;
        payload = candidate;
        return PayloadEnvelopeReadResult.Ok;
    }
}
