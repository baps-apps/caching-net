using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Telemetry;
using Caching.NET.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Pins that <c>FusionCacheService.ResolveHitLayer</c> reports <c>redis</c> for a Redis-mode hit:
/// <c>Redis</c> mode never touches L1 memory (see <c>CacheEngineFactory.MapEntryOptions</c>), so a hit
/// there always came from Redis and is tautologically attributable, unlike a Hybrid hit — see
/// <c>HybridLayerAttributionTests</c> and <c>NoDoubleCountingTests.HybridHit_CarriesNoLayerTag</c>
/// for the case where the layer genuinely cannot be known and the tag is omitted instead of guessed.
/// </summary>
[Collection(RedisCollection.Name)]
public class RedisModeTelemetryTests
{
    private const string CacheName = "redis-layer-tag";

    private readonly RedisFixture _redis;

    public RedisModeTelemetryTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [Fact]
    public async Task Hit_OnARedisModeCache_ReportsTheRedisLayer()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = CacheHost.CreateMulti(services => services.AddCaching(CacheName, cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix(CacheName)
            .WithJitter(TimeSpan.Zero)
            .WithDefaultExpiration(TimeSpan.FromMinutes(5))
            // The write must complete before the second call reads it back.
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false)));

        var cache = host.ResolveNamed<ICacheService>(CacheName);

        // The first call is a miss (the factory runs and writes through Redis). Redis mode skips
        // the memory layer entirely, so the second call can only be answered by reading Redis back
        // — a genuine hit that the bug under test mis-tagged as "memory".
        var first = await cache.GetOrSetAsync<int>("Order:1", (_, _) => Task.FromResult(1));
        var second = await cache.GetOrSetAsync<int>("Order:1", (_, _) => Task.FromResult(2));

        Assert.Equal(1, first);
        Assert.Equal(1, second);

        // SpanRecorder installs a process-wide ActivityListener and this assembly runs its
        // collections in parallel, so any concurrently running class's cache.get_or_set span tagged
        // result=hit would otherwise make Assert.Single throw "more than one match". Every span
        // carries cache.name unconditionally, so filtering on this class's own named cache is what
        // makes the assertion trustworthy rather than merely convenient.
        var spans = recorder.Activities
            .Where(a => a.OperationName == "cache.get_or_set"
                && Equals(a.GetTagItem(CacheTelemetryAttributes.Name), CacheName))
            .ToArray();
        var hitSpan = Assert.Single(spans, s => Equals(s.GetTagItem(CacheTelemetryAttributes.Result), CacheResults.Hit));

        Assert.Equal(CacheLayers.Redis, hitSpan.GetTagItem(CacheTelemetryAttributes.Layer));
    }
}
