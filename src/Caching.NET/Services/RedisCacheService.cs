using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Caching.NET.Options;
using Caching.NET.Internal;

namespace Caching.NET.Services;

/// <summary>
/// <see cref="Abstractions.ICacheService"/> implementation backed by <see cref="IDistributedCache"/> (typically Redis).
/// Values are serialized with <see cref="System.Text.Json.JsonSerializer"/>. Tag methods are no-op because Redis does not natively support tags.
/// The <c>localExpiration</c> parameter on methods is accepted to match the shared abstraction but is ignored.
/// When <see cref="CacheOptions.FailOpen"/> is true (default), cache failures fall back to the factory; when false, exceptions are propagated.
/// </summary>
public sealed class RedisCacheService : Abstractions.ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly CacheOptions _options;
    private readonly Abstractions.ICacheTelemetry _telemetry;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions DefaultJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <param name="cache">The <see cref="IDistributedCache"/> instance to use (typically backed by StackExchange.Redis).</param>
    /// <param name="options">Bound <see cref="CacheOptions"/> that control expiration defaults and fail-open behavior.</param>
    /// <param name="telemetry">Telemetry sink for recording cache hits, misses, and errors.</param>
    /// <param name="logger">Logger for recording operational warnings and errors.</param>
    /// <param name="serializerOptions">
    /// Optional custom JSON serializer options. When <c>null</c> or not registered, a default case-insensitive serializer is used.
    /// </param>
    public RedisCacheService(
        IDistributedCache cache,
        IOptions<CacheOptions> options,
        Abstractions.ICacheTelemetry telemetry,
        ILogger<RedisCacheService> logger,
        IOptions<CacheSerializerOptions>? serializerOptions = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? new CacheOptions();
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jsonOptions = serializerOptions?.Value?.JsonSerializerOptions ?? DefaultJsonOptions;
    }

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
            _telemetry.OnCacheMiss(key, "Redis");
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            byte[]? bytes = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var value = JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
                if (value != null)
                {
                    _telemetry.OnCacheHit(key, "Redis");
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            if (_options.ThrowOnFailure && !_options.FailOpen)
                throw;
            _logger.LogWarning(CacheLogEvents.RedisGetFailed, ex, "Redis get failed for key {Key}; executing factory (fail-open).", TruncateKey(key));
            _telemetry.OnCacheError("get_or_create", key, "Redis", ex);
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        T result = await factory(cancellationToken).ConfigureAwait(false);
        _telemetry.OnCacheMiss(key, "Redis");
        try
        {
            await SetAsync(key, result, expiration, localExpiration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_options.FailOpen)
        {
            _logger.LogError(ex, "Redis set failed after factory for key {Key}; returning value without caching.", TruncateKey(key));
            _telemetry.OnCacheError("set", key, "Redis", ex);
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

        var expirationSpan = expiration ?? _options.GetDefaultExpiration() ?? DefaultExpiration;
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(CacheLogEvents.RedisSerializationFailed, ex, "Serialization failed for key {Key}.", TruncateKey(key));
            if (_options.ThrowOnFailure && !_options.FailOpen)
                throw;
            _telemetry.OnCacheError("serialize", key, "Redis", ex);
            return;
        }

        if (_options.MaximumPayloadBytes.HasValue && bytes.Length > _options.MaximumPayloadBytes.Value)
        {
            _logger.LogWarning(CacheLogEvents.RedisPayloadTooLarge, "Payload for key {Key} exceeds MaximumPayloadBytes ({Size} bytes); not caching.", TruncateKey(key), bytes.Length);
            return;
        }

        try
        {
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
            await _cache.SetAsync(key, bytes, options, cancellationToken).ConfigureAwait(false);
            _telemetry.OnCacheSet(key, "Redis");
        }
        catch (Exception ex)
        {
            if (_options.ThrowOnFailure && !_options.FailOpen)
                throw;
            _logger.LogError(CacheLogEvents.RedisSetFailed, ex, "Redis set failed for key {Key}.", TruncateKey(key));
            _telemetry.OnCacheError("set", key, "Redis", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            _telemetry.OnCacheRemove(key, "Redis");
        }
        catch (Exception ex)
        {
            if (_options.ThrowOnFailure && !_options.FailOpen)
                throw;
            _logger.LogError(CacheLogEvents.RedisRemoveFailed, ex, "Redis remove failed for key {Key}.", TruncateKey(key));
            _telemetry.OnCacheError("remove", key, "Redis", ex);
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
        _logger.LogDebug(CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in Redis mode; no-op for tag {Tag}. Use Hybrid mode for tag support.", tag);
        _telemetry.OnCacheRemoveByTag(tag, "Redis");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in Redis mode; no-op. Use Hybrid mode for tag support.");
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _telemetry.OnCacheRemoveByTag(tag, "Redis");
            }
        }
        return Task.CompletedTask;
    }

    private bool ExceedsKeyLimit(string key, string operation)
    {
        if (!_options.MaximumKeyLength.HasValue) return false;
        if (key.Length <= _options.MaximumKeyLength.Value) return false;
        _logger.LogWarning(CacheLogEvents.RedisKeyTooLong, "Key length ({Length}) exceeds MaximumKeyLength ({Max}); skipping cache for {Operation}.", key.Length, _options.MaximumKeyLength.Value, operation);
        return true;
    }

    private static string TruncateKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        return key.Length <= 64 ? key : key[..64] + "...";
    }
}
