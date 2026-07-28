using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Tests.Integration.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Value-type payloads must round-trip through every mode. Hybrid is the one that historically did not:
/// its read path unwrapped a <c>HybridValueBox&lt;T&gt;</c> that no write path ever produced, so
/// <c>SetAsync(key, 42)</c> followed by <c>GetAsync&lt;int&gt;(key)</c> silently returned <c>0</c> —
/// wrong data, not just a wrong metric. Every test here runs against all three modes so the fix cannot
/// drift Hybrid away from InMemory/Redis semantics.
/// </summary>
[Collection("Redis")]
public class HybridValueTypeTests(RedisContainerFixture redis)
{
    private ICacheService BuildCache(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = mode,
            ["CacheOptions:KeyPrefix"] = $"vt{Guid.NewGuid():N}",
            ["CacheOptions:RedisConnectionString"] = redis.ConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        return services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    public static TheoryData<string> AllModes => new() { "InMemory", "Redis", "Hybrid" };

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Set_then_get_roundtrips_a_value_type(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vt:{Guid.NewGuid():N}";

        await cache.SetAsync(key, 42);

        Assert.Equal(42, await cache.GetAsync<int>(key));
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task GetOrCreate_then_get_roundtrips_a_value_type(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vt:{Guid.NewGuid():N}";

        Assert.Equal(42, await cache.GetOrCreateAsync(key, _ => Task.FromResult(42)));

        Assert.Equal(42, await cache.GetAsync<int>(key));
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task GetOrCreate_serves_a_value_type_from_cache_on_the_second_call(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vt:{Guid.NewGuid():N}";
        var factoryCalls = 0;

        Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(7);
        }

        Assert.Equal(7, await cache.GetOrCreateAsync(key, Factory));
        Assert.Equal(7, await cache.GetOrCreateAsync(key, Factory));

        Assert.Equal(1, factoryCalls);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Set_then_get_roundtrips_a_struct(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vt:{Guid.NewGuid():N}";
        var stamp = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        await cache.SetAsync(key, stamp);

        Assert.Equal(stamp, await cache.GetAsync<DateTimeOffset>(key));
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Refresh_then_get_roundtrips_a_value_type(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vt:{Guid.NewGuid():N}";

        await cache.RefreshAsync(key, _ => Task.FromResult(99));

        Assert.Equal(99, await cache.GetAsync<int>(key));
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task SetMany_then_getMany_roundtrips_value_types(string mode)
    {
        var cache = BuildCache(mode);
        var a = $"vt:{Guid.NewGuid():N}";
        var b = $"vt:{Guid.NewGuid():N}";
        var missing = $"vt:{Guid.NewGuid():N}";

        await cache.SetManyAsync(new Dictionary<string, int> { [a] = 1, [b] = 2 });
        var result = await cache.GetManyAsync<int>(new[] { a, b, missing });

        Assert.Equal(1, result[a]);
        Assert.Equal(2, result[b]);
        Assert.Equal(0, result[missing]);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task A_value_of_default_is_still_a_hit(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vt:{Guid.NewGuid():N}";

        await cache.SetAsync(key, 0);

        Assert.True(await cache.ExistsAsync(key));
        Assert.Equal(0, await cache.GetAsync<int>(key));
    }

    /// <summary>
    /// A read that misses must not leave anything behind. Hybrid has no plain "get" — reads go through
    /// <c>HybridCache.GetOrCreateAsync</c>, which stores whatever the probe factory returns — so a miss
    /// can poison the key with a placeholder and starve the next real <c>GetOrCreateAsync</c> of its
    /// factory. Covers both a value type and a reference type.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task A_read_miss_does_not_poison_a_later_get_or_create(string mode)
    {
        var cache = BuildCache(mode);
        var intKey = $"vt:{Guid.NewGuid():N}";
        var stringKey = $"vt:{Guid.NewGuid():N}";

        Assert.Equal(0, await cache.GetAsync<int>(intKey));
        Assert.Null(await cache.GetAsync<string>(stringKey));

        Assert.Equal(5, await cache.GetOrCreateAsync(intKey, _ => Task.FromResult(5)));
        Assert.Equal("v", await cache.GetOrCreateAsync(stringKey, _ => Task.FromResult("v")));
    }
}
