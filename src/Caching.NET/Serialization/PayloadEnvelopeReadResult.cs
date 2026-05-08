namespace Caching.NET.Serialization;

/// <summary>
/// Outcome of <see cref="PayloadEnvelope.TryRead"/>. All non-Ok results indicate the wire bytes are unusable
/// and the consumer should treat the read as a miss with the corresponding miss-reason tag.
/// </summary>
public enum PayloadEnvelopeReadResult
{
    /// <summary>Envelope decoded; payload is valid for the expected format and schema.</summary>
    Ok = 0,

    /// <summary>Magic bytes wrong, header truncated, or payload-length larger than buffer.</summary>
    EnvelopeInvalid = 1,

    /// <summary>FormatId in envelope differs from caller's expected format (e.g. cached as MessagePack, reading as JSON).</summary>
    FormatDrift = 2,

    /// <summary>SchemaHash differs from expected — the cached DTO type changed since the entry was written.</summary>
    SchemaDrift = 3,
}
