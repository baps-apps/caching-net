using System.Diagnostics;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion.Serialization;

namespace Caching.NET.Internal;

/// <summary>
/// Wraps the wire serializer to add the things Caching.NET owns: payload framing and compression,
/// the payload-size limit, corrupt-payload rejection, and Caching.NET-branded serialization spans
/// and metrics.
/// </summary>
/// <remarks>
/// This is the one place in the pipeline that sees the raw bytes, so it is where the
/// oversized-value and decompression-bomb guards live. It is a decorator over a four-method
/// interface, not a re-implementation of the cache API.
/// </remarks>
internal sealed class InstrumentedCacheSerializer : IFusionCacheSerializer
{
    private const string SerializeOperation = "serialize";
    private const string DeserializeOperation = "deserialize";

    private readonly IFusionCacheSerializer _inner;
    private readonly CacheSerializationOptions _options;
    private readonly CacheTelemetryContext _telemetry;
    private readonly ILogger _logger;
    private readonly string _cacheName;

    public InstrumentedCacheSerializer(
        IFusionCacheSerializer inner,
        CacheSerializationOptions options,
        string cacheName,
        CacheTelemetryContext telemetry,
        ILogger logger)
    {
        _inner = inner;
        _options = options;
        _cacheName = cacheName;
        _telemetry = telemetry;
        _logger = logger;
    }

    public byte[] Serialize<T>(T? obj)
    {
        var timestamp = Stopwatch.GetTimestamp();
        using var activity = _telemetry.StartActivity("cache.serialize");

        var payload = _inner.Serialize(obj);
        EnforceWriteLimit(payload.Length);
        var framed = PayloadCodec.Encode(payload, _options.Compression);

        Complete(activity, SerializeOperation, timestamp, framed.Length);
        return framed;
    }

    public async ValueTask<byte[]> SerializeAsync<T>(T? obj, CancellationToken token = default)
    {
        var timestamp = Stopwatch.GetTimestamp();
        using var activity = _telemetry.StartActivity("cache.serialize");

        var payload = await _inner.SerializeAsync(obj, token).ConfigureAwait(false);
        EnforceWriteLimit(payload.Length);
        var framed = PayloadCodec.Encode(payload, _options.Compression);

        Complete(activity, SerializeOperation, timestamp, framed.Length);
        return framed;
    }

    public T? Deserialize<T>(byte[] data)
    {
        var timestamp = Stopwatch.GetTimestamp();
        using var activity = _telemetry.StartActivity("cache.deserialize");

        EnforceReadLimit(data.Length);
        var payload = Decode(data);
        var value = _inner.Deserialize<T>(payload);

        Complete(activity, DeserializeOperation, timestamp, data.Length);
        return value;
    }

    public async ValueTask<T?> DeserializeAsync<T>(byte[] data, CancellationToken token = default)
    {
        var timestamp = Stopwatch.GetTimestamp();
        using var activity = _telemetry.StartActivity("cache.deserialize");

        EnforceReadLimit(data.Length);
        var payload = Decode(data);
        var value = await _inner.DeserializeAsync<T>(payload, token).ConfigureAwait(false);

        Complete(activity, DeserializeOperation, timestamp, data.Length);
        return value;
    }

    private byte[] Decode(byte[] data)
    {
        try
        {
            return PayloadCodec.Decode(data, _options.Compression);
        }
        catch (PayloadCodec.CorruptPayloadException ex)
        {
            _telemetry.RecordError(CacheLayers.Redis, nameof(PayloadCodec.CorruptPayloadException));
            CacheLogMessages.CorruptPayload(_logger, _cacheName, ex.Reason);
            throw;
        }
    }

    private void EnforceWriteLimit(long payloadBytes)
    {
        if (payloadBytes <= _options.MaximumPayloadBytes)
        {
            return;
        }

        _telemetry.RecordGuardViolation("payload_too_large");
        CacheLogMessages.PayloadTooLarge(_logger, _cacheName, payloadBytes, _options.MaximumPayloadBytes);
        throw new InvalidOperationException(
            $"Caching.NET refused to cache a {payloadBytes}-byte payload because it exceeds Serialization.MaximumPayloadBytes ({_options.MaximumPayloadBytes}). Cache a smaller projection, or raise the limit deliberately.");
    }

    private void EnforceReadLimit(long payloadBytes)
    {
        if (payloadBytes <= _options.MaximumPayloadBytes + 1)
        {
            return;
        }

        _telemetry.RecordGuardViolation("payload_too_large");
        CacheLogMessages.PayloadTooLarge(_logger, _cacheName, payloadBytes, _options.MaximumPayloadBytes);
        throw new PayloadCodec.CorruptPayloadException(
            $"stored payload of {payloadBytes} bytes exceeds the configured maximum");
    }

    private void Complete(Activity? activity, string operation, long startTimestamp, long payloadBytes)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _telemetry.RecordSerialization(operation, elapsed, payloadBytes);

        if (activity is not null)
        {
            activity.SetTag(CacheTelemetryAttributes.Operation, operation);
            activity.SetTag(CacheTelemetryAttributes.Layer, CacheLayers.Redis);
            activity.SetTag(CacheTelemetryAttributes.PayloadBytes, payloadBytes);
        }
    }
}
