using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Source-generated zero-allocation logger messages for hot-path cache operations.
/// EventId ranges: 1000–1099 info/debug, 1100–1199 warn, 1200–1299 error.
/// </summary>
internal static partial class CacheLogMessages
{
    // ── Info/Debug (1000–1099) ────────────────────────────────────────────────

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug,
        Message = "Cache disabled or unavailable — executing factory for key {Key}.")]
    public static partial void HybridCacheDisabled(this ILogger logger, string key);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "RemoveByTagAsync is not supported in this mode; no-op for tag {Tag}. Use Hybrid mode for tag support.")]
    public static partial void TagNotSupported(this ILogger logger, string tag);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug,
        Message = "Server-side Redis {Operation} failed; falling back to fan-out.")]
    public static partial void RedisMultiplexerFailed(this ILogger logger, string operation, Exception ex);

    // ── Warning (1100–1199) ───────────────────────────────────────────────────

    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning,
        Message = "Redis get failed for key {Key}; executing factory (fail-open).")]
    public static partial void RedisGetFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Redis set failed for key {Key}.")]
    public static partial void RedisSetFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning,
        Message = "Redis remove failed for key {Key}.")]
    public static partial void RedisRemoveFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
        Message = "Key length ({Length}) exceeds MaximumKeyLength ({Max}); skipping cache for {Operation}.")]
    public static partial void RedisKeyTooLong(this ILogger logger, int length, int max, string operation);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning,
        Message = "Payload for key {Key} exceeds MaximumPayloadBytes ({Size} bytes); not caching.")]
    public static partial void RedisPayloadTooLarge(this ILogger logger, string key, int size);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning,
        Message = "Hybrid get failed for key {Key}; executing factory (fail-open).")]
    public static partial void HybridGetFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
        Message = "Envelope invalid for key {Key}; treating as miss.")]
    public static partial void RedisEnvelopeInvalid(this ILogger logger, string key);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Warning,
        Message = "Format drift for key {Key}; treating as miss.")]
    public static partial void RedisFormatDrift(this ILogger logger, string key);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Warning,
        Message = "Schema drift for key {Key}; treating as miss.")]
    public static partial void RedisSchemaDrift(this ILogger logger, string key);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Warning,
        Message = "Caching option {OptionName} changed at runtime but is startup-only. Restart required.")]
    public static partial void StartupOnlyOptionChanged(this ILogger logger, string optionName);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Warning,
        Message = "Redis set failed after factory for key {Key}; returning value without caching.")]
    public static partial void RedisSetFailedAfterFactory(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Warning,
        Message = "Background stale refresh failed for key {Key}; stale entry will expire naturally.")]
    public static partial void StaleRefreshFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1112, Level = LogLevel.Warning,
        Message = "Background stale refresh skipped for key {Key}: could not acquire stripe lock within {TimeoutMs}ms.")]
    public static partial void StaleRefreshLockTimeout(this ILogger logger, string key, double timeoutMs);

    // ── Error (1200–1299) ─────────────────────────────────────────────────────

    [LoggerMessage(EventId = 1200, Level = LogLevel.Error,
        Message = "Serialization failed for key {Key}.")]
    public static partial void RedisSerializationFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Error,
        Message = "Hybrid set failed for key {Key}.")]
    public static partial void HybridSetFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error,
        Message = "Hybrid remove failed for key {Key}.")]
    public static partial void HybridRemoveFailed(this ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Error,
        Message = "Hybrid tag-remove failed for tag {Tag}.")]
    public static partial void HybridTagRemoveFailed(this ILogger logger, string tag, Exception ex);
}
