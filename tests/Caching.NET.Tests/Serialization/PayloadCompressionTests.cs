using System.IO.Compression;
using Caching.NET.Serialization;

namespace Caching.NET.Tests.Serialization;

public sealed class PayloadCompressionTests
{
    [Fact]
    public void CompressionBitHelpers_RoundTrip()
    {
        const byte format = PayloadEnvelope.FormatIdJson;
        var compressed = PayloadCompression.WithCompression(format);
        Assert.True(PayloadCompression.IsCompressed(compressed));
        Assert.Equal(format, PayloadCompression.BaseFormatId(compressed));
    }

    [Fact]
    public void ShouldCompress_RespectsEnabledAndThreshold()
    {
        Assert.False(PayloadCompression.ShouldCompress(1024, enabled: false, thresholdBytes: 512));
        Assert.False(PayloadCompression.ShouldCompress(511, enabled: true, thresholdBytes: 512));
        Assert.True(PayloadCompression.ShouldCompress(512, enabled: true, thresholdBytes: 512));
    }

    [Fact]
    public void CompressThenDecompress_RoundTrips()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(new string('x', 10_000));
        var compressed = PayloadCompression.CompressBrotli(payload);
        var decompressed = PayloadCompression.DecompressBrotli(compressed, maxDecompressedBytes: 20_000);
        Assert.Equal(payload, decompressed);
    }

    [Fact]
    public void DecompressBrotli_ThrowsWhenOutputExceedsLimit()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(new string('x', 10_000));
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                brotli.Write(payload);
            }

            compressed = output.ToArray();
        }

        Assert.Throws<InvalidDataException>(() => PayloadCompression.DecompressBrotli(compressed, maxDecompressedBytes: 100));
    }
}
