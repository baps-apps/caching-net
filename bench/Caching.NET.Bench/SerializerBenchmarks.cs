using BenchmarkDotNet.Attributes;
using Caching.NET.Serialization;

namespace Caching.NET.Bench;

[MemoryDiagnoser]
public class SerializerBenchmarks
{
    private readonly JsonCacheSerializer _json = new();
    private readonly MessagePackCacheSerializer _msgpack = new();
    private Payload _payload = default!;

    [Params(100, 10_000)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new Payload { Data = new string('x', PayloadSize) };
    }

    [Benchmark(Baseline = true)]
    public byte[] Json_Serialize() => _json.Serialize(_payload);

    [Benchmark]
    public byte[] MessagePack_Serialize() => _msgpack.Serialize(_payload);

    public sealed class Payload { public string? Data { get; set; } }
}
