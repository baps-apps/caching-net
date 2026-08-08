using BenchmarkDotNet.Attributes;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using ZiggyCreatures.Caching.Fusion.Serialization;
using ZiggyCreatures.Caching.Fusion.Serialization.NeueccMessagePack;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Caching.NET.Benchmark;

/// <summary>
/// Wire-format cost, including Caching.NET's framing, size guard and compression.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class SerializationBenchmarks
{
    private IFusionCacheSerializer _json = null!;
    private IFusionCacheSerializer _messagePack = null!;
    private IFusionCacheSerializer _compressed = null!;

    private CacheHostFactory.Payload _payload = null!;
    private byte[] _jsonBytes = null!;
    private byte[] _messagePackBytes = null!;
    private byte[] _compressedBytes = null!;

    [Params(256, 64 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _json = Build(new FusionCacheSystemTextJsonSerializer(), compress: false);
        _messagePack = Build(new FusionCacheNeueccMessagePackSerializer(), compress: false);
        _compressed = Build(new FusionCacheSystemTextJsonSerializer(), compress: true);

        _payload = new CacheHostFactory.Payload(1, "bench", new string('d', PayloadSize), DateTimeOffset.UnixEpoch);
        _jsonBytes = _json.Serialize(_payload);
        _messagePackBytes = _messagePack.Serialize(_payload);
        _compressedBytes = _compressed.Serialize(_payload);
    }

    [Benchmark(Baseline = true, Description = "JSON serialize")]
    public byte[] JsonSerialize() => _json.Serialize(_payload);

    [Benchmark(Description = "JSON deserialize")]
    public CacheHostFactory.Payload? JsonDeserialize() => _json.Deserialize<CacheHostFactory.Payload>(_jsonBytes);

    [Benchmark(Description = "MessagePack serialize")]
    public byte[] MessagePackSerialize() => _messagePack.Serialize(_payload);

    [Benchmark(Description = "MessagePack deserialize")]
    public CacheHostFactory.Payload? MessagePackDeserialize()
        => _messagePack.Deserialize<CacheHostFactory.Payload>(_messagePackBytes);

    [Benchmark(Description = "JSON + Brotli serialize")]
    public byte[] CompressedSerialize() => _compressed.Serialize(_payload);

    [Benchmark(Description = "JSON + Brotli deserialize")]
    public CacheHostFactory.Payload? CompressedDeserialize()
        => _compressed.Deserialize<CacheHostFactory.Payload>(_compressedBytes);

    private static IFusionCacheSerializer Build(IFusionCacheSerializer inner, bool compress)
    {
        var options = new CachingOptions { CacheName = "bench", ApplicationPrefix = "bench" };
        options.Serialization.MaximumPayloadBytes = 16 * 1024 * 1024;
        options.Serialization.Compression.Enabled = compress;
        options.Serialization.Compression.ThresholdBytes = 128;

        return new InstrumentedCacheSerializer(
            inner,
            options.Serialization,
            "bench",
            new CacheTelemetryContext(options),
            NullLogger.Instance);
    }
}
