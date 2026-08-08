using System.Text;
using Caching.NET.Internal;
using Caching.NET.Options;

namespace Caching.NET.Tests.Internal;

public class PayloadCodecTests
{
    private static CacheCompressionOptions Off() => new() { Enabled = false };

    private static CacheCompressionOptions On(int threshold = 0, int maxDecompressed = 16 * 1024 * 1024)
        => new() { Enabled = true, ThresholdBytes = threshold, MaximumDecompressedBytes = maxDecompressed };

    [Fact]
    public void UncompressedPayload_RoundTrips()
    {
        var payload = Encoding.UTF8.GetBytes("{\"id\":1}");

        var framed = PayloadCodec.Encode(payload, Off());

        Assert.Equal(payload.Length + 1, framed.Length);
        Assert.Equal(payload, PayloadCodec.Decode(framed, Off()));
    }

    [Fact]
    public void CompressiblePayload_RoundTripsAndShrinks()
    {
        var payload = Encoding.UTF8.GetBytes(new string('a', 64 * 1024));

        var framed = PayloadCodec.Encode(payload, On());

        Assert.True(framed.Length < payload.Length);
        Assert.Equal(payload, PayloadCodec.Decode(framed, On()));
    }

    [Fact]
    public void PayloadBelowThreshold_IsNotCompressed()
    {
        var payload = Encoding.UTF8.GetBytes(new string('a', 100));

        var framed = PayloadCodec.Encode(payload, On(threshold: 1024));

        Assert.Equal(payload.Length + 1, framed.Length);
        Assert.Equal(payload, PayloadCodec.Decode(framed, On(threshold: 1024)));
    }

    [Fact]
    public void IncompressiblePayload_StaysUncompressed()
    {
        var payload = new byte[8192];
        System.Security.Cryptography.RandomNumberGenerator.Fill(payload);

        var framed = PayloadCodec.Encode(payload, On());

        Assert.Equal(payload.Length + 1, framed.Length);
        Assert.Equal(payload, PayloadCodec.Decode(framed, On()));
    }

    [Fact]
    public void UncompressedPayload_IsReadableAfterCompressionIsEnabled()
    {
        var payload = Encoding.UTF8.GetBytes("written before compression was turned on");

        var framed = PayloadCodec.Encode(payload, Off());

        Assert.Equal(payload, PayloadCodec.Decode(framed, On()));
    }

    [Fact]
    public void CompressedPayload_IsReadableAfterCompressionIsDisabled()
    {
        var payload = Encoding.UTF8.GetBytes(new string('b', 64 * 1024));

        var framed = PayloadCodec.Encode(payload, On());

        Assert.Equal(payload, PayloadCodec.Decode(framed, Off()));
    }

    [Fact]
    public void EmptyPayload_IsRejected()
        => Assert.Throws<PayloadCodec.CorruptPayloadException>(() => PayloadCodec.Decode([], Off()));

    [Fact]
    public void UnknownFormatHeader_IsRejected()
    {
        byte[] poisoned = [0x7f, 1, 2, 3];

        var ex = Assert.Throws<PayloadCodec.CorruptPayloadException>(() => PayloadCodec.Decode(poisoned, Off()));

        Assert.Contains("unknown payload format", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptCompressedPayload_IsRejected()
    {
        // Header says Brotli, body is not: exactly what a poisoned or partially overwritten Redis
        // value looks like.
        byte[] poisoned = [0x01, 0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x22];

        Assert.Throws<PayloadCodec.CorruptPayloadException>(() => PayloadCodec.Decode(poisoned, On()));
    }

    [Fact]
    public void DecompressionBomb_IsStoppedAtTheConfiguredCeiling()
    {
        // 8 MiB of zeroes compresses to a few KiB: exactly the shape of a decompression bomb.
        var payload = new byte[8 * 1024 * 1024];
        var framed = PayloadCodec.Encode(payload, On());
        Assert.True(framed.Length < 64 * 1024);

        var ex = Assert.Throws<PayloadCodec.CorruptPayloadException>(
            () => PayloadCodec.Decode(framed, On(maxDecompressed: 64 * 1024)));

        Assert.Contains("exceeds", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroLengthValue_RoundTrips()
    {
        var framed = PayloadCodec.Encode([], Off());
        Assert.Equal([], PayloadCodec.Decode(framed, Off()));
    }
}
