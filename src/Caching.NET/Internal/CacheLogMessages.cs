using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Source-generated log messages. No cached value, serialized payload, raw cache key, Redis
/// endpoint, connection string or credential is ever passed to these methods.
/// </summary>
internal static partial class CacheLogMessages
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Caching.NET initialized. CacheName: {CacheName} Mode: {Mode} MemoryLayer: {MemoryLayer} RedisLayer: {RedisLayer} Backplane: {Backplane} FailSafe: {FailSafe} Serializer: {Serializer} Compression: {Compression} Tracing: {Tracing} Metrics: {Metrics}")]
    public static partial void StartupSummary(
        ILogger logger,
        string cacheName,
        string mode,
        string memoryLayer,
        string redisLayer,
        string backplane,
        string failSafe,
        string serializer,
        string compression,
        string tracing,
        string metrics);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Caching.NET is disabled for cache {CacheName}. Reads miss, writes are discarded and factories run on every call.")]
    public static partial void CachingDisabled(ILogger logger, string cacheName);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Warning,
        Message = "Cache key exceeds the configured maximum of {MaximumKeyLength} characters (actual {ActualLength}) on cache {CacheName}. Key: {KeyReference}.")]
    public static partial void KeyTooLong(ILogger logger, string cacheName, int maximumKeyLength, int actualLength, string keyReference);

    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Warning,
        Message = "Serialized payload of {PayloadBytes} bytes exceeds the configured maximum of {MaximumPayloadBytes} bytes on cache {CacheName}. The entry was not cached.")]
    public static partial void PayloadTooLarge(ILogger logger, string cacheName, long payloadBytes, long maximumPayloadBytes);

    [LoggerMessage(
        EventId = 3012,
        Level = LogLevel.Warning,
        Message = "Rejected an unreadable cache payload on cache {CacheName} ({Reason}). The entry is treated as a miss and will be rewritten by the next factory execution.")]
    public static partial void CorruptPayload(ILogger logger, string cacheName, string reason);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Information,
        Message = "Redis TLS handshake for cache {CacheName}: subject={CertificateSubject} issuer={CertificateIssuer} thumbprint={CertificateThumbprint} notAfter={CertificateNotAfter:o}")]
    public static partial void RedisTlsHandshake(
        ILogger logger,
        string cacheName,
        string certificateSubject,
        string certificateIssuer,
        string certificateThumbprint,
        DateTime certificateNotAfter);

    [LoggerMessage(
        EventId = 3021,
        Level = LogLevel.Error,
        Message = "Rejected the Redis TLS certificate for cache {CacheName}: {PolicyErrors}. Strict validation is {StrictValidation}.")]
    public static partial void RedisTlsRejected(ILogger logger, string cacheName, string policyErrors, bool strictValidation);

    [LoggerMessage(
        EventId = 3022,
        Level = LogLevel.Warning,
        Message = "Accepted a Redis TLS certificate name mismatch for cache {CacheName} because strict certificate validation is disabled. Enable Redis.StrictCertificateValidation in production.")]
    public static partial void RedisTlsNameMismatchAccepted(ILogger logger, string cacheName);

    [LoggerMessage(
        EventId = 3030,
        Level = LogLevel.Warning,
        Message = "Redis connection for cache {CacheName} reported an error on {Endpoint}: {FailureType}.")]
    public static partial void RedisConnectionFailed(ILogger logger, string cacheName, string endpoint, string failureType);

    [LoggerMessage(
        EventId = 3031,
        Level = LogLevel.Information,
        Message = "Redis connection for cache {CacheName} was restored on {Endpoint}.")]
    public static partial void RedisConnectionRestored(ILogger logger, string cacheName, string endpoint);

    [LoggerMessage(
        EventId = 3032,
        Level = LogLevel.Critical,
        Message = "Caching.NET could not open the Redis connection for cache {CacheName}. Configuration: {RedactedConfiguration}.")]
    public static partial void RedisConnectionUnavailable(ILogger logger, Exception exception, string cacheName, string redactedConfiguration);

    [LoggerMessage(
        EventId = 3040,
        Level = LogLevel.Warning,
        Message = "Cache entry tag rejected on cache {CacheName}: {Reason}.")]
    public static partial void TagRejected(ILogger logger, string cacheName, string reason);

    [LoggerMessage(
        EventId = 3050,
        Level = LogLevel.Warning,
        Message = "Cache {CacheName} has Resilience.AllowBackgroundDistributedOperations disabled, so the distributed write runs on the caller's path. A serialization failure — including an entry over Serialization.MaximumPayloadBytes ({MaximumPayloadBytes} bytes) — will surface to the caller as an exception even though Resilience.ThrowOnSerializationErrors is false. Re-enable background distributed operations, or handle the exception at the call site.")]
    public static partial void ForegroundSerializationFailuresSurface(ILogger logger, string cacheName, long maximumPayloadBytes);

    [LoggerMessage(
        EventId = 3051,
        Level = LogLevel.Warning,
        Message = "Cache {CacheName} is in Hybrid mode with the backplane disabled. Writes and removals made by one instance do not evict the in-process copy held by any other instance, so every replica can serve a stale value for up to its local lifetime ({LocalExpiration}). Set Backplane.Enabled to true for any deployment with more than one replica, or accept that window deliberately.")]
    public static partial void HybridWithoutBackplane(ILogger logger, string cacheName, TimeSpan localExpiration);
}
