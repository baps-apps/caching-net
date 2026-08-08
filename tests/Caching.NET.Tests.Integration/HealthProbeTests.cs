using Caching.NET.Extensions;
using Caching.NET.Health;
using Caching.NET.Options;
using Caching.NET.Tests.Integration.Fixtures;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// What the readiness probe leaves behind in Redis, and what it actually verifies.
/// </summary>
[Collection(RedisCollection.Name)]
public class HealthProbeTests
{
    private readonly RedisFixture _redis;

    public HealthProbeTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration("probe", _ => null!, HealthStatus.Unhealthy, tags: null)
    };

    private static CachingHealthCheck ReadinessCheck(CacheHost host)
        => new(host.Provider, host.Resolve<IOptionsMonitor<CachingOptions>>());

    [Theory]
    [InlineData(CacheMode.Redis)]
    [InlineData(CacheMode.Hybrid)]
    public async Task ProbeKeyExpiresWithinTheProbeDuration_RegardlessOfDistributedExpiration(CacheMode mode)
    {
        var prefix = $"probe-ttl-{mode}".ToLowerInvariant();

        // A long distributed expiration must not leak into the probe entry. Without the explicit
        // reset in ProbeOptions this key inherits DistributedExpiration and lingers for hours.
        using var host = CacheHost.Create(cache =>
        {
            if (mode == CacheMode.Redis)
            {
                cache.UseRedis(_redis.ConnectionString);
            }
            else
            {
                cache.UseHybrid(_redis.ConnectionString, enableBackplane: false);
            }

            cache.WithApplicationPrefix(prefix)
                .WithDefaultExpiration(TimeSpan.FromMinutes(10))
                .WithDistributedExpiration(TimeSpan.FromHours(6))
                .WithLocalExpiration(TimeSpan.FromHours(3))
                .WithJitter(TimeSpan.FromMinutes(1))
                .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromHours(12));
        });

        var result = await ReadinessCheck(host).CheckHealthAsync(Context());
        Assert.Equal(HealthStatus.Healthy, result.Status);

        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            var keys = await RedisModeTests.FindKeysAsync(connection, $"*{prefix}:__cachingnet:health:*");
            var probeKey = Assert.Single(keys);

            var ttl = await connection.GetDatabase().KeyTimeToLiveAsync(probeKey);

            Assert.NotNull(ttl);
            Assert.True(
                ttl!.Value <= CachingHealthCheck.ProbeDuration,
                $"probe key TTL was {ttl.Value} but must not exceed the {CachingHealthCheck.ProbeDuration} probe duration");
        }
    }

    [Fact]
    public async Task ProbeDoesNotOverwriteApplicationEntries()
    {
        using var host = CacheHost.Create(cache => cache
            .UseHybrid(_redis.ConnectionString, enableBackplane: false)
            .WithApplicationPrefix("probe-isolation")
            .WithDefaultExpiration(TimeSpan.FromMinutes(10)));

        await host.Cache.SetAsync("Order:1", 42);

        for (var i = 0; i < 10; i++)
        {
            var result = await ReadinessCheck(host).CheckHealthAsync(Context());
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }

        Assert.Equal(42, await host.Cache.GetOrDefaultAsync<int>("Order:1"));
    }

    [Theory]
    [InlineData(CacheMode.Redis)]
    [InlineData(CacheMode.Hybrid)]
    public async Task ProbeReachesTheDistributedLayer(CacheMode mode)
    {
        var prefix = $"probe-reaches-{mode}".ToLowerInvariant();

        using var host = CacheHost.Create(cache =>
        {
            if (mode == CacheMode.Redis)
            {
                cache.UseRedis(_redis.ConnectionString);
            }
            else
            {
                cache.UseHybrid(_redis.ConnectionString, enableBackplane: false);
            }

            cache.WithApplicationPrefix(prefix);
        });

        Assert.Equal(HealthStatus.Healthy, (await ReadinessCheck(host).CheckHealthAsync(Context())).Status);

        // Delete the probe key behind the cache's back. A probe served from L1 would still report
        // healthy; one that genuinely reads the distributed layer writes a fresh key each time.
        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            var keys = await RedisModeTests.FindKeysAsync(connection, $"*{prefix}:__cachingnet:health:*");
            Assert.Single(keys);
            await connection.GetDatabase().KeyDeleteAsync(keys[0]);

            Assert.Equal(HealthStatus.Healthy, (await ReadinessCheck(host).CheckHealthAsync(Context())).Status);

            // The probe rewrote it, which it could only do by talking to Redis.
            var rewritten = await RedisModeTests.FindKeysAsync(connection, $"*{prefix}:__cachingnet:health:*");
            Assert.Single(rewritten);
        }
    }
}
