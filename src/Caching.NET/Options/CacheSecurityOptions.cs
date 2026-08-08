namespace Caching.NET.Options;

/// <summary>
/// What Caching.NET does when a cache key or tag breaks a configured limit.
/// </summary>
public enum CacheGuardPolicy
{
    /// <summary>Do not check.</summary>
    Ignore = 0,

    /// <summary>Log a warning and record a metric, then continue.</summary>
    Warn = 1,

    /// <summary>Throw <see cref="ArgumentException"/> so the defect surfaces in test and staging.</summary>
    Throw = 2
}

/// <summary>
/// Key, tag and telemetry-redaction limits.
/// </summary>
public sealed class CacheSecurityOptions
{
    /// <summary>
    /// Maximum length of the physical cache key, prefix included. Keys are also bounded by Redis
    /// (512&#160;MiB) but an unbounded key is a memory and telemetry-cardinality hazard.
    /// Must be greater than zero. Default 512.
    /// </summary>
    public int MaximumKeyLength { get; set; } = 512;

    /// <summary>What to do when a key exceeds <see cref="MaximumKeyLength"/>. Default <see cref="CacheGuardPolicy.Throw"/>.</summary>
    public CacheGuardPolicy KeyLengthPolicy { get; set; } = CacheGuardPolicy.Throw;

    /// <summary>Maximum length of a single tag. Must be greater than zero. Default 128.</summary>
    public int MaximumTagLength { get; set; } = 128;

    /// <summary>Maximum number of tags on one entry. Must be greater than zero. Default 16.</summary>
    public int MaximumTagCount { get; set; } = 16;

    /// <summary>What to do when a tag breaks a limit. Default <see cref="CacheGuardPolicy.Throw"/>.</summary>
    public CacheGuardPolicy TagPolicy { get; set; } = CacheGuardPolicy.Throw;

    /// <summary>
    /// Include raw cache keys in log messages. Default <c>false</c>: a key can carry identifiers or
    /// other personal data, so only a non-reversible fingerprint is logged. Development only.
    /// </summary>
    public bool AllowRawKeysInLogs { get; set; }

    /// <summary>
    /// Include tag values in logs, traces and metrics. Default <c>false</c>: tags are frequently
    /// tenant- or user-scoped, which makes them both sensitive and high-cardinality.
    /// </summary>
    public bool AllowTagsInTelemetry { get; set; }
}
