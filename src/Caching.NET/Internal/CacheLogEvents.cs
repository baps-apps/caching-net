using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Centralized log event IDs for cache logging. Helps observability teams build stable queries and alerts.
/// </summary>
internal static class CacheLogEvents
{
    // Redis operations
    public static readonly EventId RedisGetFailed = new(1000, nameof(RedisGetFailed));
    public static readonly EventId RedisSetFailed = new(1001, nameof(RedisSetFailed));
    public static readonly EventId RedisRemoveFailed = new(1002, nameof(RedisRemoveFailed));
    public static readonly EventId RedisSerializationFailed = new(1003, nameof(RedisSerializationFailed));
    public static readonly EventId RedisKeyTooLong = new(1004, nameof(RedisKeyTooLong));
    public static readonly EventId RedisPayloadTooLarge = new(1005, nameof(RedisPayloadTooLarge));

    public static readonly EventId RedisEnvelopeInvalid = new(1106, nameof(RedisEnvelopeInvalid));
    public static readonly EventId RedisFormatDrift = new(1107, nameof(RedisFormatDrift));
    public static readonly EventId RedisSchemaDrift = new(1108, nameof(RedisSchemaDrift));

    // Hybrid operations
    public static readonly EventId HybridGetFailed = new(1100, nameof(HybridGetFailed));
    public static readonly EventId HybridSetFailed = new(1101, nameof(HybridSetFailed));
    public static readonly EventId HybridRemoveFailed = new(1102, nameof(HybridRemoveFailed));
    public static readonly EventId HybridTagRemoveFailed = new(1103, nameof(HybridTagRemoveFailed));
    public static readonly EventId HybridCacheDisabled = new(1104, nameof(HybridCacheDisabled));

    // Tag APIs not supported
    public static readonly EventId TagNotSupported = new(1200, nameof(TagNotSupported));
}

