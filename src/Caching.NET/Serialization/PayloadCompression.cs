using System.Buffers;
using System.IO.Compression;
namespace Caching.NET.Serialization;

internal static class PayloadCompression
{
    internal const byte CompressionBit = 0x80;

    internal static bool IsCompressed(byte formatId) => (formatId & CompressionBit) != 0;

    internal static byte BaseFormatId(byte formatId) => (byte)(formatId & ~CompressionBit);

    internal static byte WithCompression(byte formatId) => (byte)(formatId | CompressionBit);

    internal static bool ShouldCompress(int payloadLength, bool enabled, int thresholdBytes) =>
        enabled && payloadLength >= thresholdBytes;

    internal static byte[] CompressBrotli(ReadOnlySpan<byte> payload)
    {
        var maxCompressedLength = BrotliEncoder.GetMaxCompressedLength(payload.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxCompressedLength);
        try
        {
            if (!BrotliEncoder.TryCompress(payload, rented, out var bytesWritten, quality: 1, window: 22))
                throw new InvalidDataException("Brotli compression failed.");

            return rented[..bytesWritten].ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static byte[] DecompressBrotli(ReadOnlySpan<byte> payload)
        => DecompressBrotli(payload, 1_048_576);

    internal static byte[] DecompressBrotli(ReadOnlySpan<byte> payload, long maxDecompressedBytes)
    {
        if (maxDecompressedBytes <= 0)
            throw new InvalidOperationException("Max decompressed payload bytes must be positive.");

        if (payload.IsEmpty)
            return [];

        if (maxDecompressedBytes > int.MaxValue)
            throw new InvalidOperationException($"Max decompressed payload bytes cannot exceed {int.MaxValue}.");

        var limit = (int)maxDecompressedBytes;
        byte[] rented = ArrayPool<byte>.Shared.Rent(limit);
        try
        {
            if (!BrotliDecoder.TryDecompress(payload, rented, out var bytesWritten))
                throw new InvalidDataException($"Decompressed payload exceeded limit of {maxDecompressedBytes} bytes.");
            return rented[..bytesWritten].ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
