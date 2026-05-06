using Caching.NET.Serialization;
using FsCheck;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class PayloadEnvelopeProperties
{
    [Property]
    public bool Random_bytes_never_throw_on_TryRead(byte[] wire)
    {
        if (wire is null) return true;
        try
        {
            PayloadEnvelope.TryRead(wire, PayloadEnvelope.FormatIdJson, 0UL, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Property]
    public bool Write_then_TryRead_returns_Ok_for_any_payload(byte[] payload, ulong schemaHash)
    {
        if (payload is null) return true;
        var wire = PayloadEnvelope.Write(payload, PayloadEnvelope.FormatIdJson, schemaHash);
        var result = PayloadEnvelope.TryRead(wire, PayloadEnvelope.FormatIdJson, schemaHash, out var recovered);
        return result == PayloadEnvelopeReadResult.Ok && recovered.SequenceEqual(payload);
    }

    [Property]
    public bool TryRead_detects_format_drift(byte[] payload, ulong schemaHash)
    {
        if (payload is null) return true;
        var wire = PayloadEnvelope.Write(payload, PayloadEnvelope.FormatIdJson, schemaHash);
        // Read back expecting MsgPack — should report FormatDrift
        var result = PayloadEnvelope.TryRead(wire, PayloadEnvelope.FormatIdMsgPack, schemaHash, out _);
        return result == PayloadEnvelopeReadResult.FormatDrift;
    }
}
