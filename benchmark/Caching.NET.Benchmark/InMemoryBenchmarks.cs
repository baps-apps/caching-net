using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Benchmark;

/// <summary>
/// In-memory mode hot paths: hit, miss, factory execution and concurrent get-or-set.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class InMemoryBenchmarks
{
    private ServiceProvider _provider = null!;
    private ICacheService _cache = null!;
    private int _missCounter;

    /// <summary>
    /// Read only by <see cref="ConcurrentGetOrSet"/>, but declared at class level, so the other
    /// three benchmarks are measured twice under a parameter they ignore. That duplication is
    /// deliberate and load-bearing, not an oversight: docs/BENCHMARKS.md publishes those rows at
    /// both 1 and 64 precisely to show that hit and miss cost stays flat as concurrency rises.
    /// Moving the parameter to a dedicated class would halve the run time and delete that reading.
    /// </summary>
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
        return await _cache.GetOrSetAsync<CacheHostFactory.Payload>($"factory-{id}", async (_, _) => CacheHostFactory.Payload.Sample(id))
            ?? throw new InvalidOperationException("factory returned null");
    }

    [Benchmark(Description = "Concurrent get-or-set on one key")]
    public async Task ConcurrentGetOrSet()
    {
        var tasks = new Task[Concurrency];
        for (var i = 0; i < Concurrency; i++)
        {
            tasks[i] = _cache.GetOrSetAsync<CacheHostFactory.Payload>("hot", async (_, _) => CacheHostFactory.Payload.Sample(0)).AsTask();
        }

        await Task.WhenAll(tasks);
    }
}
