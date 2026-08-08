using Caching.NET.Internal;
using Caching.NET.Options;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class PayloadCodecProperties
{
    private static CacheCompressionOptions Compression(bool enabled, int threshold = 0)
        => new()
        {
            Enabled = enabled,
            ThresholdBytes = threshold,
            MaximumDecompressedBytes = 64 * 1024 * 1024
        };

    [Property(MaxTest = 300)]
    public bool EncodeThenDecode_IsIdentity_WithoutCompression(byte[] payload)
    {
        var options = Compression(enabled: false);
        return PayloadCodec.Decode(PayloadCodec.Encode(payload, options), options).SequenceEqual(payload);
    }

    [Property(MaxTest = 300)]
    public bool EncodeThenDecode_IsIdentity_WithCompression(byte[] payload)
    {
        var options = Compression(enabled: true);
        return PayloadCodec.Decode(PayloadCodec.Encode(payload, options), options).SequenceEqual(payload);
    }

    [Property(MaxTest = 200)]
    public bool EncodedPayload_IsReadableUnderEitherCompressionSetting(byte[] payload)
    {
        var written = PayloadCodec.Encode(payload, Compression(enabled: true));
        return PayloadCodec.Decode(written, Compression(enabled: false)).SequenceEqual(payload);
    }

    [Property(MaxTest = 200)]
    public bool EncodedPayload_AlwaysCarriesAKnownFormatHeader(byte[] payload)
    {
        var written = PayloadCodec.Encode(payload, Compression(enabled: true));
        return written.Length >= 1 && written[0] <= 0x01;
    }

    [Property(MaxTest = 300)]
    public bool UnknownFormatHeader_IsAlwaysRejected(byte[] body, byte header)
    {
        // Only 0x00 and 0x01 are valid formats; anything else must be refused, never guessed at.
        if (header <= 0x01)
        {
            return true;
        }

        byte[] framed = [header, .. body];
        try
        {
            PayloadCodec.Decode(framed, Compression(enabled: true));
            return false;
        }
        catch (PayloadCodec.CorruptPayloadException)
        {
            return true;
        }
    }

    [Property(MaxTest = 100)]
    public bool DecompressionCeiling_IsNeverExceeded(byte[] seed)
    {
        if (seed.Length == 0)
        {
            return true;
        }

        // Build a highly compressible payload much larger than the ceiling we will read it back with.
        var payload = new byte[512 * 1024];
        Array.Fill(payload, seed[0]);

        var written = PayloadCodec.Encode(payload, Compression(enabled: true));

        try
        {
            PayloadCodec.Decode(written, new CacheCompressionOptions
            {
                Enabled = true,
                MaximumDecompressedBytes = 4096
            });
            return false;
        }
        catch (PayloadCodec.CorruptPayloadException)
        {
            return true;
        }
    }
}
