using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Benchmark;

/// <summary>
/// Redis and Hybrid paths. Requires a reachable Redis; set <c>CACHINGNET_BENCH_REDIS</c> to the
/// connection string, otherwise these benchmarks are skipped by <c>Program</c>.
/// </summary>
/// <remarks>
/// There is no "Hybrid L2 hit" row. Measuring one meant asking the cache to skip its memory layer
/// for a single call, which Caching.NET's additive overrides deliberately cannot express — and the
/// only way to issue that read without the engine is a Redis-mode cache, which is exactly what
/// "Redis mode hit" already measures. The row would have been a second name for the same code path.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class RedisBenchmarks
{
    public const string ConnectionStringVariable = "CACHINGNET_BENCH_REDIS";

    private ServiceProvider _redisProvider = null!;
    private ServiceProvider _hybridProvider = null!;
    private ICacheService _redis = null!;
    private ICacheService _hybrid = null!;
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

        _redis.Set("tagged-hit", CacheHostFactory.Payload.Sample(2), tags: Tags);
        _hybrid.Set("tagged-hit", CacheHostFactory.Payload.Sample(2), tags: Tags);
    }

    private static readonly string[] Tags = ["category:tools", "tenant:acme"];

    [GlobalCleanup]
    public void Cleanup()
    {
        _redisProvider.Dispose();
        _hybridProvider.Dispose();
    }

    [Benchmark(Description = "Redis mode hit")]
    public async Task<CacheHostFactory.Payload?> RedisHit()
        => await _redis.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");

    /// <remarks>
    /// Redis mode keeps tag markers out of the memory layer, so a read costs the entry plus the two
    /// reserved <c>Clear</c> markers plus one marker per tag. This row is what capacity planning has
    /// to be based on for any cache that tags its entries; "Redis mode hit" is the untagged floor.
    /// </remarks>
    [Benchmark(Description = "Redis mode hit, 2 tags")]
    public async Task<CacheHostFactory.Payload?> RedisTaggedHit()
        => await _redis.GetOrDefaultAsync<CacheHostFactory.Payload>("tagged-hit");

    [Benchmark(Description = "Redis mode miss")]
    public async Task<CacheHostFactory.Payload?> RedisMiss()
        => await _redis.GetOrDefaultAsync<CacheHostFactory.Payload>($"absent-{Interlocked.Increment(ref _counter)}");

    [Benchmark(Baseline = true, Description = "Hybrid L1 hit")]
    public async Task<CacheHostFactory.Payload?> HybridL1Hit()
        => await _hybrid.GetOrDefaultAsync<CacheHostFactory.Payload>("hit");

    /// <remarks>
    /// The counterpart to <see cref="RedisTaggedHit"/>: Hybrid may serve markers from memory, so this
    /// row is the evidence that the marker rule costs Hybrid nothing on the read path.
    /// </remarks>
    [Benchmark(Description = "Hybrid L1 hit, 2 tags")]
    public async Task<CacheHostFactory.Payload?> HybridTaggedL1Hit()
        => await _hybrid.GetOrDefaultAsync<CacheHostFactory.Payload>("tagged-hit");

    [Benchmark(Description = "Hybrid full miss + factory")]
    public async Task<CacheHostFactory.Payload> HybridFullMiss()
    {
        var id = Interlocked.Increment(ref _counter);
        return await _hybrid.GetOrSetAsync<CacheHostFactory.Payload>($"miss-{id}", async (_, _) => CacheHostFactory.Payload.Sample(id))
            ?? throw new InvalidOperationException("factory returned null");
    }
}
