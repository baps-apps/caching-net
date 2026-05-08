using System.Buffers;
using Caching.NET.Internal;
using Caching.NET.Serialization;
using Xunit;

namespace Caching.NET.Tests.Serialization;

public class PayloadEnvelopeTests
{
    private const byte FormatJson = 0x01;
    private const byte FormatMsgPack = 0x02;

    [Fact]
    public void Write_emits_magic_format_hash_length_payload()
    {
        ReadOnlySpan<byte> payload = stackalloc byte[] { 1, 2, 3, 4 };
        var schema = StableTypeHash.Compute<int>();

        byte[] wire = PayloadEnvelope.Write(payload, FormatJson, schema);

        Assert.Equal((byte)'C', wire[0]);
        Assert.Equal((byte)'N', wire[1]);
        Assert.Equal((byte)'2', wire[2]);
        Assert.Equal((byte)'0', wire[3]);
        Assert.Equal(FormatJson, wire[4]);
        Assert.Equal(schema, BitConverter.ToUInt64(wire, 5));
        Assert.Equal((uint)payload.Length, BitConverter.ToUInt32(wire, 13));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, wire[17..^4]);
        Assert.Equal(System.IO.Hashing.XxHash32.HashToUInt32(payload), BitConverter.ToUInt32(wire, wire.Length - 4));
    }

    [Fact]
    public void Roundtrip_returns_payload_for_matching_format_and_schema()
    {
        ReadOnlySpan<byte> payload = stackalloc byte[] { 9, 8, 7 };
        var schema = StableTypeHash.Compute<string>();
        byte[] wire = PayloadEnvelope.Write(payload, FormatJson, schema);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, schema, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.Ok, result);
        Assert.Equal(new byte[] { 9, 8, 7 }, decoded.ToArray());
    }

    [Fact]
    public void TryRead_with_bad_magic_returns_EnvelopeInvalid()
    {
        var wire = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        var result = PayloadEnvelope.TryRead(wire, 0x01, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_short_buffer_returns_EnvelopeInvalid()
    {
        var wire = new byte[] { (byte)'C', (byte)'N', (byte)'2', (byte)'0' }; // header only, no rest

        var result = PayloadEnvelope.TryRead(wire, 0x01, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_format_mismatch_returns_FormatDrift()
    {
        byte[] wire = PayloadEnvelope.Write(new byte[] { 1 }, FormatJson, schemaHash: 7UL);

        var result = PayloadEnvelope.TryRead(wire, FormatMsgPack, expectedSchemaHash: 7UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.FormatDrift, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_schema_mismatch_returns_SchemaDrift()
    {
        byte[] wire = PayloadEnvelope.Write(new byte[] { 1 }, FormatJson, schemaHash: 7UL);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, expectedSchemaHash: 99UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.SchemaDrift, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_payload_length_overflow_returns_EnvelopeInvalid()
    {
        // header claims 1 GiB but buffer has 17+0+4 bytes
        var wire = new byte[21];
        "CN20"u8.CopyTo(wire);
        wire[4] = FormatJson;
        BitConverter.GetBytes(0UL).CopyTo(wire, 5);
        BitConverter.GetBytes(1_073_741_824u).CopyTo(wire, 13);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_trailing_bytes_returns_EnvelopeInvalid()
    {
        byte[] wire = PayloadEnvelope.Write(new byte[] { 1, 2, 3 }, FormatJson, schemaHash: 0UL);
        var withTrailing = new byte[wire.Length + 1];
        Buffer.BlockCopy(wire, 0, withTrailing, 0, wire.Length);
        withTrailing[^1] = 0xEE;

        var result = PayloadEnvelope.TryRead(withTrailing, FormatJson, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_zero_length_payload_returns_Ok_empty()
    {
        byte[] wire = PayloadEnvelope.Write(ReadOnlySpan<byte>.Empty, FormatJson, schemaHash: 0UL);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.Ok, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_checksum_mismatch_returns_EnvelopeInvalid()
    {
        byte[] wire = PayloadEnvelope.Write(new byte[] { 1, 2, 3 }, FormatJson, schemaHash: 0UL);
        wire[^1] ^= 0x01; // flip checksum byte

        var result = PayloadEnvelope.TryRead(wire, FormatJson, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void Write_IBufferWriter_matches_byte_array_overload()
    {
        ReadOnlySpan<byte> payload = stackalloc byte[] { 9, 9, 9 };
        var schema = StableTypeHash.Compute<double>();
        var writer = new ArrayBufferWriter<byte>();
        PayloadEnvelope.Write(payload, FormatJson, schema, writer);
        byte[] expected = PayloadEnvelope.Write(payload, FormatJson, schema);
        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }
}
