using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Resilience;
using Caching.NET.Serialization;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;

namespace Caching.NET.Services;

/// <summary>
/// <see cref="Abstractions.ICacheService"/> implementation backed by <see cref="IDistributedCache"/> (typically Redis).
/// All Redis I/O is wrapped in named Polly resilience pipelines (timeout + circuit breaker + retry)
/// and an outer per-op timeout via linked <see cref="CancellationTokenSource"/>.
/// Values are serialized via the registered <see cref="ICacheSerializer"/>.
/// </summary>
internal sealed class RedisCacheService : Abstractions.ICacheService
{
    private const string Mode = "Redis";
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);

    private readonly IDistributedCache _cache;
    private readonly IOptions<CacheOptions> _options;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly ICacheSerializer _serializer;
    private readonly ResiliencePipeline _readPipeline;
    private readonly ResiliencePipeline _writePipeline;
    private readonly ResiliencePipeline _deletePipeline;

    /// <summary>Construct a new <see cref="RedisCacheService"/>.</summary>
    public RedisCacheService(
        IDistributedCache cache,
        IOptions<CacheOptions> options,
        ILogger<RedisCacheService> logger,
        ICacheSerializer serializer,
        ResiliencePipelineRegistry<string> resiliencePipelines)
    {
        _cache = cache;
        _options = options;
        _logger = logger;
        _serializer = serializer;
        _readPipeline = resiliencePipelines.GetPipeline(ResiliencePipelineNames.RedisRead);
        _writePipeline = resiliencePipelines.GetPipeline(ResiliencePipelineNames.RedisWrite);
        _deletePipeline = resiliencePipelines.GetPipeline(ResiliencePipelineNames.RedisDelete);
    }

    private static string ClassifyError(Exception ex) => ex switch
    {
        TimeoutRejectedException => "Timeout",
        BrokenCircuitException => "CircuitOpen",
        TimeoutException => "Timeout",
        OperationCanceledException => "Cancelled",
        _ => "Unknown",
    };

    private CancellationTokenSource CreateOpCts(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.Value.RedisOperationTimeout);
        return cts;
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
            CacheInstruments.RecordMiss(Mode, "get_or_create");
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var cts = CreateOpCts(cancellationToken);
            byte[]? bytes = await _readPipeline.ExecuteAsync(
                async ct => await _cache.GetAsync(key, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var value = _serializer.Deserialize<T>(bytes);
                if (value != null)
                {
                    CacheInstruments.RecordHit(Mode, "get_or_create");
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen)
                throw;
            _logger.LogWarning(CacheLogEvents.RedisGetFailed, ex, "Redis get failed for key {Key}; executing factory (fail-open).", TruncateKey(key));
            CacheInstruments.RecordError(Mode, "get_or_create", ClassifyError(ex));
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        T result = await factory(cancellationToken).ConfigureAwait(false);
        CacheInstruments.RecordMiss(Mode, "get_or_create");
        try
        {
            await SetAsync(key, result, expiration, localExpiration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_options.Value.FailOpen)
        {
            _logger.LogError(ex, "Redis set failed after factory for key {Key}; returning value without caching.", TruncateKey(key));
            CacheInstruments.RecordError(Mode, "set", ClassifyError(ex));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (ExceedsKeyLimit(key, nameof(SetAsync))) return;

        var expirationSpan = expiration ?? _options.Value.GetDefaultExpiration() ?? DefaultExpiration;
        byte[] payload;
        try
        {
            payload = _serializer.Serialize(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(CacheLogEvents.RedisSerializationFailed, ex, "Serialization failed for key {Key}.", TruncateKey(key));
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            CacheInstruments.RecordError(Mode, "serialize", "Serialization");
            return;
        }

        if (_options.Value.MaximumPayloadBytes > 0 && payload.Length > _options.Value.MaximumPayloadBytes)
        {
            _logger.LogWarning(CacheLogEvents.RedisPayloadTooLarge, "Payload for key {Key} exceeds MaximumPayloadBytes ({Size} bytes); not caching.", TruncateKey(key), payload.Length);
            return;
        }

        byte formatId = ResolveFormatId(_serializer.FormatId);
        ulong schemaHash = StableTypeHash.Compute<T>();
        byte[] wire = PayloadEnvelope.Write(payload, formatId, schemaHash);

        try
        {
            using var cts = CreateOpCts(cancellationToken);
            var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
            await _writePipeline.ExecuteAsync(
                async ct => await _cache.SetAsync(key, wire, entryOptions, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            CacheInstruments.RecordSet(Mode);
            CacheInstruments.RecordPayloadBytes(Mode, "set", payload.Length);
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            _logger.LogError(CacheLogEvents.RedisSetFailed, ex, "Redis set failed for key {Key}.", TruncateKey(key));
            CacheInstruments.RecordError(Mode, "set", ClassifyError(ex));
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            using var cts = CreateOpCts(cancellationToken);
            await _deletePipeline.ExecuteAsync(
                async ct => await _cache.RemoveAsync(key, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            CacheInstruments.RecordRemove(Mode);
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            _logger.LogError(CacheLogEvents.RedisRemoveFailed, ex, "Redis remove failed for key {Key}.", TruncateKey(key));
            CacheInstruments.RecordError(Mode, "remove", ClassifyError(ex));
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
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(CacheLogEvents.TagNotSupported, "RemoveByTagAsync is not supported in Redis mode; no-op. Use Hybrid mode for tag support.");
        return Task.CompletedTask;
    }

    private bool ExceedsKeyLimit(string key, string operation)
    {
        var max = _options.Value.MaximumKeyLength;
        if (max <= 0) return false;
        if (key.Length <= max) return false;
        _logger.LogWarning(CacheLogEvents.RedisKeyTooLong, "Key length ({Length}) exceeds MaximumKeyLength ({Max}); skipping cache for {Operation}.", key.Length, max, operation);
        return true;
    }

    private static string TruncateKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        return key.Length <= 64 ? key : key[..64] + "...";
    }

    private static byte ResolveFormatId(string formatId) => formatId switch
    {
        "json"    => PayloadEnvelope.FormatIdJson,
        "msgpack" => PayloadEnvelope.FormatIdMsgPack,
        _         => PayloadEnvelope.FormatIdUnknown,
    };
}
