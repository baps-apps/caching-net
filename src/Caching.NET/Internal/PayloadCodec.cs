using System.IO.Compression;
using Caching.NET.Options;

namespace Caching.NET.Internal;

/// <summary>
/// Frames every distributed-cache payload with a one-byte format header and applies optional
/// Brotli compression.
/// </summary>
/// <remarks>
/// The header is always written, whether or not compression is enabled, so that turning
/// compression on or off later does not make existing entries unreadable. A payload whose header
/// is unrecognised is rejected as corrupt rather than guessed at, which is what turns a poisoned
/// or truncated Redis value into a cache miss instead of a deserialization of attacker-controlled
/// bytes. Decompression is bounded by
/// <see cref="CacheCompressionOptions.MaximumDecompressedBytes"/>.
/// </remarks>
internal static class PayloadCodec
{
    private const byte FormatRaw = 0x00;
    private const byte FormatBrotli = 0x01;

    /// <summary>Raised when a stored payload cannot be framed or decoded.</summary>
    internal sealed class CorruptPayloadException : InvalidOperationException
    {
        public CorruptPayloadException(string reason)
            : base($"Caching.NET rejected an unreadable cache payload: {reason}.")
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public static byte[] Encode(byte[] payload, CacheCompressionOptions compression)
    {
        if (compression.Enabled && payload.Length >= compression.ThresholdBytes)
        {
            var compressed = Compress(payload);
            // Only keep the compressed form when it is actually smaller, so incompressible
            // payloads (already-compressed blobs, random ids) do not pay a size penalty.
            if (compressed.Length < payload.Length)
            {
                return Frame(FormatBrotli, compressed);
            }
        }

        return Frame(FormatRaw, payload);
    }

    public static byte[] Decode(byte[] framed, CacheCompressionOptions compression)
    {
        if (framed.Length == 0)
        {
            throw new CorruptPayloadException("empty payload");
        }

        var format = framed[0];
        var body = framed.AsSpan(1);

        return format switch
        {
            FormatRaw => body.ToArray(),
            FormatBrotli => Decompress(body, compression.MaximumDecompressedBytes),
            _ => throw new CorruptPayloadException($"unknown payload format 0x{format:x2}")
        };
    }

    private static byte[] Frame(byte format, ReadOnlySpan<byte> body)
    {
        var framed = new byte[body.Length + 1];
        framed[0] = format;
        body.CopyTo(framed.AsSpan(1));
        return framed;
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream(payload.Length / 2);
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> body, int maximumBytes)
    {
        using var input = new MemoryStream(body.ToArray(), writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        // Read with an explicit ceiling rather than CopyTo: a small compressed payload can expand
        // without bound, and an unbounded copy would let one poisoned entry exhaust process memory.
        var buffer = new byte[81920];
        int read;
        try
        {
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maximumBytes)
                {
                    throw new CorruptPayloadException(
                        $"decompressed payload exceeds the {maximumBytes}-byte limit");
                }

                output.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException && ex is not CorruptPayloadException)
        {
            // BrotliStream reports a malformed stream as InvalidDataException or
            // InvalidOperationException depending on where decoding fails.
            throw new CorruptPayloadException($"malformed compressed payload ({ex.GetType().Name})");
        }

        return output.ToArray();
    }
}
