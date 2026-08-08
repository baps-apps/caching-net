using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Benchmark;

/// <summary>
/// Redis and Hybrid paths. Requires a reachable Redis; set <c>CACHINGNET_BENCH_REDIS</c> to the
/// connection string, otherwise these benchmarks are skipped by <c>Program</c>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class RedisBenchmarks
{
    public const string ConnectionStringVariable = "CACHINGNET_BENCH_REDIS";

    private ServiceProvider _redisProvider = null!;
    private ServiceProvider _hybridProvider = null!;
    private IFusionCache _redis = null!;
    private IFusionCache _hybrid = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? throw new InvalidOperationException(
                $"Set {ConnectionStringVariable} to a Redis connection string before running the Redis benchmarks.");

        (_redisProvider, _redis) = CacheHostFactory.Create(cache => cache
            .UseRedis(connectionString)
            .WithDefaultExpiration(TimeSpan.FromMinutes(30)), cacheName: "bench-redis");

        (_hybridProvider, _hybrid) = CacheHostFactory.Create(cache => cache
            .UseHybrid(connectionString, enableBackplane: false)
            .WithDefaultExpiration(TimeSpan.FromMinutes(30)), cacheName: "bench-hybrid");

        _redis.Set("hit", CacheHostFactory.Payload.Sample(1));
        _hybrid.Set("hit", CacheHostFactory.Payload.Sample(1));
        _hybrid.Set("l2-only", CacheHostFactory.Payload.Sample(2));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _redisProvider.Dispose();
        _hybridProvider.Dispose();
    }

    [Benchmark(Description = "Redis mode hit")]
    public async Task<CacheHostFactory.Payload?> RedisHit()
        => await _redis.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");

    [Benchmark(Description = "Redis mode miss")]
    public async Task<CacheHostFactory.Payload?> RedisMiss()
        => await _redis.GetOrDefaultAsync<CacheHostFactory.Payload>($"absent-{Interlocked.Increment(ref _counter)}");

    [Benchmark(Baseline = true, Description = "Hybrid L1 hit")]
    public async Task<CacheHostFactory.Payload?> HybridL1Hit()
        => await _hybrid.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");

    [Benchmark(Description = "Hybrid L2 hit (L1 bypassed)")]
    public async Task<CacheHostFactory.Payload?> HybridL2Hit()
        => await _hybrid.GetOrDefaultAsync<CacheHostFactory.Payload>(
            "l2-only",
            options: _hybrid.CreateEntryOptions(o => o.SetSkipMemoryCacheRead(true)));

    [Benchmark(Description = "Hybrid full miss + factory")]
    public async Task<CacheHostFactory.Payload> HybridFullMiss()
    {
        var id = Interlocked.Increment(ref _counter);
        return await _hybrid.GetOrSetAsync($"miss-{id}", async _ => CacheHostFactory.Payload.Sample(id));
    }
}
