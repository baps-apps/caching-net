using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Caching.NET.Options;
using Caching.NET.Internal;

namespace Caching.NET.Services;

/// <summary>
/// <see cref="Abstractions.ICacheService"/> implementation backed by <see cref="IDistributedCache"/> (typically Redis).
/// Values are serialized with <see cref="JsonSerializer"/>. Tag methods are no-op because Redis does not natively support tags.
/// The <c>localExpiration</c> parameter on methods is accepted to match the shared abstraction but is ignored.
/// When <see cref="CacheOptions.FailOpen"/> is true (default), cache failures fall back to the factory; when false, exceptions are propagated.
/// </summary>
/// <param name="cache">The <see cref="IDistributedCache"/> instance to use (typically backed by StackExchange.Redis).</param>
/// <param name="options">Bound <see cref="CacheOptions"/> that control expiration defaults and fail-open behavior.</param>
/// <param name="telemetry">Telemetry sink for recording cache hits, misses, and errors.</param>
/// <param name="logger">Logger for recording operational warnings and errors.</param>
/// <param name="serializerOptions">
/// Optional custom JSON serializer options. When <c>null</c> or not registered, a default case-insensitive serializer is used.
/// </param>
public sealed class RedisCacheService(
    IDistributedCache cache,
    IOptions<CacheOptions> options,
    Abstractions.ICacheTelemetry telemetry,
    ILogger<RedisCacheService> logger,
    IOptions<CacheSerializerOptions>? serializerOptions = null) : Abstractions.ICacheService
{
    private readonly JsonSerializerOptions _jsonOptions = serializerOptions?.Value?.JsonSerializerOptions ?? DefaultJsonOptions;
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions DefaultJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));

        if (ExceedsKeyLimit(key, nameof(GetOrCreateAsync)))
        {
            telemetry.OnCacheMiss(key, "Redis");
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            byte[]? bytes = await cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var value = JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
                if (value != null)
                {
                    telemetry.OnCacheHit(key, "Redis");
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            if (options.Value.ThrowOnFailure && !options.Value.FailOpen)
                throw;
            logger.LogWarning(CacheLogEvents.RedisGetFailed, ex, "Redis get failed for key {Key}; executing factory (fail-open).", TruncateKey(key));
            telemetry.OnCacheError("get_or_create", key, "Redis", ex);
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        T result = await factory(cancellationToken).ConfigureAwait(false);
        telemetry.OnCacheMiss(key, "Redis");
        try
        {
            await SetAsync(key, result, expiration, localExpiration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (options.Value.FailOpen)
        {
            logger.LogError(ex, "Redis set failed after factory for key {Key}; returning value without caching.", TruncateKey(key));
            telemetry.OnCacheError("set", key, "Redis", ex);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (ExceedsKeyLimit(key, nameof(SetAsync)))
        {
            return;
        }

        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? DefaultExpiration;
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(CacheLogEvents.RedisSerializationFailed, ex, "Serialization failed for key {Key}.", TruncateKey(key));
            if (options.Value.ThrowOnFailure && !options.Value.FailOpen)
                throw;
            telemetry.OnCacheError("serialize", key, "Redis", ex);
            return;
        }

        if (options.Value.MaximumPayloadBytes > 0 && bytes.Length > options.Value.MaximumPayloadBytes)
        {
            logger.LogWarning(CacheLogEvents.RedisPayloadTooLarge, "Payload for key {Key} exceeds MaximumPayloadBytes ({Size} bytes); not caching.", TruncateKey(key), bytes.Length);
            return;
        }

        try
        {
            var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
            await cache.SetAsync(key, bytes, entryOptions, cancellationToken).ConfigureAwait(false);
            telemetry.OnCacheSet(key, "Redis");
        }
        catch (Exception ex)
        {
            if (options.Value.ThrowOnFailure && !options.Value.FailOpen)
                throw;
            logger.LogError(CacheLogEvents.RedisSetFailed, ex, "Redis set failed for key {Key}.", TruncateKey(key));
            telemetry.OnCacheError("set", key, "Redis", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            telemetry.OnCacheRemove(key, "Redis");
        }
        catch (Exception ex)
        {
            if (options.Value.ThrowOnFailure && !options.Value.FailOpen)
                throw;
            logger.LogError(CacheLogEvents.RedisRemoveFailed, ex, "Redis remove failed for key {Key}.", TruncateKey(key));
            telemetry.OnCacheError("remove", key, "Redis", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys == null) return;
        foreach (var key in keys)
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        logger.LogDebug(CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in Redis mode; no-op for tag {Tag}. Use Hybrid mode for tag support.", tag);
        telemetry.OnCacheRemoveByTag(tag, "Redis");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        logger.LogDebug(CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in Redis mode; no-op. Use Hybrid mode for tag support.");
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                telemetry.OnCacheRemoveByTag(tag, "Redis");
            }
        }
        return Task.CompletedTask;
    }

    private bool ExceedsKeyLimit(string key, string operation)
    {
        var max = options.Value.MaximumKeyLength;
        if (max <= 0) return false;
        if (key.Length <= max) return false;
        logger.LogWarning(CacheLogEvents.RedisKeyTooLong, "Key length ({Length}) exceeds MaximumKeyLength ({Max}); skipping cache for {Operation}.", key.Length, max, operation);
        return true;
    }

    private static string TruncateKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        return key.Length <= 64 ? key : key[..64] + "...";
    }
}
