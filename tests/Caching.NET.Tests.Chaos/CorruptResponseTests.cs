using Caching.NET.Serialization;
using Xunit;

namespace Caching.NET.Tests.Chaos;

public class CorruptResponseTests
{
    [Fact]
    public void Random_bytes_never_throw_on_envelope_decode()
    {
        var rng = new Random(42);
        var buf = new byte[256];

        for (int i = 0; i < 1000; i++)
        {
            rng.NextBytes(buf);
            var status = PayloadEnvelope.TryRead(buf, PayloadEnvelope.FormatIdJson, 0UL, out _);
            Assert.True(
                status is PayloadEnvelopeReadResult.EnvelopeInvalid
                       or PayloadEnvelopeReadResult.FormatDrift
                       or PayloadEnvelopeReadResult.SchemaDrift
                       or PayloadEnvelopeReadResult.Ok,
                $"Unexpected status {status} on iteration {i}");
        }
    }

    [Fact]
    public void Short_buffers_never_throw_on_envelope_decode()
    {
        for (int len = 0; len < PayloadEnvelope.HeaderSize + 4; len++)
        {
            var buf = new byte[len];
            var status = PayloadEnvelope.TryRead(buf, PayloadEnvelope.FormatIdJson, 0UL, out _);
            Assert.True(
                status is PayloadEnvelopeReadResult.EnvelopeInvalid
                       or PayloadEnvelopeReadResult.FormatDrift
                       or PayloadEnvelopeReadResult.SchemaDrift
                       or PayloadEnvelopeReadResult.Ok);
        }
    }
}
