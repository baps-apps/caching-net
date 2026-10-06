using Caching.NET.Extensions;
using Caching.NET.Options;
using Caching.NET.Tests.Integration.Fixtures;
using StackExchange.Redis;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// The exact bytes Caching.NET writes as a Redis key.
/// </summary>
/// <remarks>
/// Every other Redis assertion in this suite matches keys with a wildcard, which means none of them
/// would notice a change to the layout. Operators write eviction policies, key scans, memory reports
/// and runbooks against this string, so it is asserted literally. By default the key starts with
/// the application prefix; the engine's wire-format segment appears only when
/// <see cref="RedisOptions.KeyVersionPlacement"/> asks for it.
/// </remarks>
[Collection(RedisCollection.Name)]
public class PhysicalKeyLayoutTests
{
    /// <summary>
    /// Wire-format version the engine writes into a distributed key when
    /// <see cref="RedisOptions.KeyVersionPlacement"/> is <see cref="CacheKeyVersionPlacement.Prefix"/>
    /// or <see cref="CacheKeyVersionPlacement.Suffix"/>. A future engine release that bumps it
    /// invalidates every stored entry under those placements, which is the reason to fail loudly here
    /// rather than discover it as an unexplained cold cache in production.
    /// </summary>
    private const string EngineWireFormatVersion = "v2";

    private readonly RedisFixture _redis;

    public PhysicalKeyLayoutTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [Fact]
    public async Task DefaultCache_WritesApplicationEnvironmentThenCallerKeyWithNoVersionSegment()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("layout-app")
            .WithEnvironmentPrefix("layout-env")
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        await host.Cache.SetAsync("Order:1", 1);

        Assert.Equal(
            "layout-app:layout-env:Order:1",
            await SingleKeyAsync("*layout-app*Order:1*"));
    }

    [Fact]
    public async Task NamedCache_AppendsTheCacheNameSoTwoCachesCannotShareAKeySpace()
    {
        using var host = CacheHost.CreateMulti(services => services
            .AddCaching("layout-hot", cache => cache
                .UseRedis(_redis.ConnectionString)
                .WithApplicationPrefix("layout-named")
                .WithResilience(r => r.AllowBackgroundDistributedOperations = false)));

        await host.Provider.GetCache("layout-hot").SetAsync("Order:2", 2);

        Assert.Equal(
            "layout-named:layout-hot:Order:2",
            await SingleKeyAsync("*layout-named*Order:2*"));
    }

    [Fact]
    public async Task TenantPrefix_SitsBetweenTheEnvironmentAndTheCallerKey()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("layout-t")
            .WithEnvironmentPrefix("prod")
            .WithTenantPrefix("tenant-7")
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        await host.Cache.SetAsync("Order:3", 3);

        Assert.Equal(
            "layout-t:prod:tenant-7:Order:3",
            await SingleKeyAsync("*layout-t:prod*Order:3*"));
    }

    [Fact]
    public async Task RedisInstancePrefix_IsAppliedOutsideEverythingElse()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("layout-inst")
            .WithRedis(r =>
            {
                r.InstancePrefix = "legacy::";
                r.AbortOnConnectFail = false;
            })
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        await host.Cache.SetAsync("Order:4", 4);

        Assert.Equal(
            "legacy::layout-inst:Order:4",
            await SingleKeyAsync("*layout-inst*Order:4*"));
    }

    [Fact]
    public async Task PrefixPlacement_PutsTheEngineVersionInFrontOfTheApplicationPrefix()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("layout-pre")
            .WithKeyVersionPlacement(CacheKeyVersionPlacement.Prefix)
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        await host.Cache.SetAsync("Order:5", 5);

        Assert.Equal(
            $"{EngineWireFormatVersion}:layout-pre:Order:5",
            await SingleKeyAsync("*layout-pre*Order:5*"));
    }

    [Fact]
    public async Task SuffixPlacement_KeepsTheApplicationPrefixFirstAndTheEngineVersionLast()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("layout-suf")
            .WithKeyVersionPlacement(CacheKeyVersionPlacement.Suffix)
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        await host.Cache.SetAsync("Order:6", 6);

        Assert.Equal(
            $"layout-suf:Order:6:{EngineWireFormatVersion}",
            await SingleKeyAsync("*layout-suf*Order:6*"));
    }

    [Fact]
    public async Task PrefixPlacement_LandsInsideTheRedisInstancePrefix()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("layout-inst-pre")
            .WithKeyVersionPlacement(CacheKeyVersionPlacement.Prefix)
            .WithRedis(r =>
            {
                r.InstancePrefix = "legacy::";
                r.AbortOnConnectFail = false;
            })
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        await host.Cache.SetAsync("Order:7", 7);

        // The Redis adapter prepends InstancePrefix after the engine has already built its key, so
        // it lands in front of the wire-format segment rather than in front of the caller's key.
        Assert.Equal(
            $"legacy::{EngineWireFormatVersion}:layout-inst-pre:Order:7",
            await SingleKeyAsync("*layout-inst-pre*Order:7*"));
    }

    private async Task<string> SingleKeyAsync(string pattern)
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            var keys = await RedisModeTests.FindKeysAsync(connection, pattern);
            return (string)keys.Single()!;
        }
    }
}
