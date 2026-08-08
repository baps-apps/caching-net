using Caching.NET.Extensions;
using Caching.NET.Tests.Integration.Fixtures;
using StackExchange.Redis;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// The exact bytes Caching.NET writes as a Redis key.
/// </summary>
/// <remarks>
/// Every other Redis assertion in this suite matches keys with a wildcard, which means none of them
/// would notice a change to the layout. Operators write eviction policies, key scans, memory reports
/// and runbooks against this string, so it is asserted literally — including the engine's
/// <c>v2:</c> wire-format segment, which sits in front of the Caching.NET prefix and is not
/// something Caching.NET chooses.
/// </remarks>
[Collection(RedisCollection.Name)]
public class PhysicalKeyLayoutTests
{
    /// <summary>
    /// Wire-format segment the engine prepends to every distributed key. A future engine release
    /// that bumps this invalidates every stored entry, which is the reason to fail loudly here
    /// rather than discover it as an unexplained cold cache in production.
    /// </summary>
    private const string EngineWireFormatPrefix = "v2:";

    private readonly RedisFixture _redis;

    public PhysicalKeyLayoutTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [Fact]
    public async Task DefaultCache_WritesEngineWireFormatThenApplicationEnvironmentThenCallerKey()
    {
        using var host = CacheHost.Create(cache => cache
            .UseRedis(_redis.ConnectionString)
            .WithApplicationPrefix("layout-app")
            .WithEnvironmentPrefix("layout-env")
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        await host.Cache.SetAsync("Order:1", 1);

        Assert.Equal(
            $"{EngineWireFormatPrefix}layout-app:layout-env:Order:1",
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
            $"{EngineWireFormatPrefix}layout-named:layout-hot:Order:2",
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
            $"{EngineWireFormatPrefix}layout-t:prod:tenant-7:Order:3",
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

        // The Redis adapter prepends InstancePrefix after the engine has already built its key, so
        // it lands in front of the wire-format segment rather than in front of the caller's key.
        Assert.Equal(
            $"legacy::{EngineWireFormatPrefix}layout-inst:Order:4",
            await SingleKeyAsync("*layout-inst*Order:4*"));
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
