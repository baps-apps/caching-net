using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Benchmark;

/// <summary>
/// In-memory mode hot paths: hit, miss, factory execution and concurrent get-or-set.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class InMemoryBenchmarks
{
    private ServiceProvider _provider = null!;
    private IFusionCache _cache = null!;
    private int _missCounter;

    [Params(1, 64)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (_provider, _cache) = CacheHostFactory.Create(cache => cache
            .UseInMemory()
            .WithDefaultExpiration(TimeSpan.FromMinutes(30)));

        _cache.Set("hit", CacheHostFactory.Payload.Sample(1));
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    [Benchmark(Baseline = true, Description = "L1 hit")]
    public async Task<CacheHostFactory.Payload?> MemoryHit()
        => await _cache.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");

    [Benchmark(Description = "L1 miss (no factory)")]
    public async Task<CacheHostFactory.Payload?> MemoryMiss()
        => await _cache.GetOrDefaultAsync<CacheHostFactory.Payload>($"absent-{Interlocked.Increment(ref _missCounter)}");

    [Benchmark(Description = "Factory execution")]
    public async Task<CacheHostFactory.Payload> FactoryExecution()
    {
        var id = Interlocked.Increment(ref _missCounter);
        return await _cache.GetOrSetAsync($"factory-{id}", async _ => CacheHostFactory.Payload.Sample(id));
    }

    [Benchmark(Description = "Concurrent get-or-set on one key")]
    public async Task ConcurrentGetOrSet()
    {
        var tasks = new Task[Concurrency];
        for (var i = 0; i < Concurrency; i++)
        {
            tasks[i] = _cache.GetOrSetAsync("hot", async _ => CacheHostFactory.Payload.Sample(0)).AsTask();
        }

        await Task.WhenAll(tasks);
    }
}
