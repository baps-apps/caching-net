using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Source-generated zero-allocation logger messages for hot-path cache operations.
/// EventId ranges match the stable IDs originally defined in CacheLogEvents:
///   1000–1099: Redis operations
///   1100–1199: Hybrid operations
///   1106–1108: Envelope/schema drift (Redis reads)
///   1200–1299: Tag/policy not-supported
/// </summary>
internal static partial class CacheLogMessages
{
    // ── Redis ────────────────────────────────────────────────────────────────

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning,
        Message = "Redis get failed for key {KeyHash}; executing factory (fail-open).")]
    public static partial void RedisGetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "Redis set failed for key {KeyHash}.")]
    public static partial void RedisSetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Redis remove failed for key {KeyHash}.")]
    public static partial void RedisRemoveFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error,
        Message = "Serialization failed for key {KeyHash}.")]
    public static partial void RedisSerializationFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "Key length ({Length}) exceeds MaximumKeyLength ({Max}); skipping cache for {Operation}.")]
    public static partial void RedisKeyTooLong(this ILogger logger, int length, int max, string operation);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Payload for key {KeyHash} exceeds MaximumPayloadBytes ({Size} bytes); not caching.")]
    public static partial void RedisPayloadTooLarge(this ILogger logger, string keyHash, int size);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
        Message = "Envelope invalid for key {KeyHash}; treating as miss.")]
    public static partial void RedisEnvelopeInvalid(this ILogger logger, string keyHash);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Warning,
        Message = "Format drift for key {KeyHash}; treating as miss.")]
    public static partial void RedisFormatDrift(this ILogger logger, string keyHash);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Warning,
        Message = "Schema drift for key {KeyHash}; treating as miss.")]
    public static partial void RedisSchemaDrift(this ILogger logger, string keyHash);

    // ── Hybrid ───────────────────────────────────────────────────────────────

    [LoggerMessage(EventId = 1100, Level = LogLevel.Error,
        Message = "Hybrid get failed for key {KeyHash}; executing factory (fail-open).")]
    public static partial void HybridGetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error,
        Message = "Hybrid set failed for key {KeyHash}.")]
    public static partial void HybridSetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error,
        Message = "Hybrid remove failed for key {KeyHash}.")]
    public static partial void HybridRemoveFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error,
        Message = "Hybrid tag-remove failed for tag {Tag}.")]
    public static partial void HybridTagRemoveFailed(this ILogger logger, string tag, Exception ex);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Debug,
        Message = "Cache disabled or unavailable — executing factory for key {KeyHash}.")]
    public static partial void HybridCacheDisabled(this ILogger logger, string keyHash);

    // ── Tag not supported ────────────────────────────────────────────────────

    [LoggerMessage(EventId = 1200, Level = LogLevel.Debug,
        Message = "RemoveByTagAsync is not supported in this mode; no-op for tag {Tag}. Use Hybrid mode for tag support.")]
    public static partial void TagNotSupported(this ILogger logger, string tag);
}
