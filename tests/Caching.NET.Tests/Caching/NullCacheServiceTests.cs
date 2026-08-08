namespace Caching.NET.Tests.Caching;

public class NullCacheServiceTests
{
    private static ICacheService Build() => TestHost
        .Build(c => c.UseInMemory().WithApplicationPrefix("tests").Disable())
        .DisabledCache();

    [Fact]
    public async Task ReadsAlwaysMiss()
    {
        var cache = Build();

        await cache.SetAsync("k", 1);

        Assert.False((await cache.TryGetAsync<int>("k")).HasValue);
        Assert.Equal(0, await cache.GetOrDefaultAsync<int>("k"));
        Assert.Equal(-1, await cache.GetOrDefaultAsync("k", -1));
    }

    [Fact]
    public async Task FactoryRunsOnEveryCall()
    {
        var cache = Build();
        var calls = 0;

        await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(1); });
        await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(1); });

        Assert.Equal(2, calls);
    }

    /// <summary>
    /// Disabling the cache is a configuration change, so every invalidation verb has to stay
    /// callable: an application that calls <c>RemoveByTagAsync</c> after a write must keep working
    /// with <c>Enabled = false</c>. Completing without throwing is the behaviour, and the reads
    /// afterwards pin that the no-op left the contract intact rather than the verb having been
    /// skipped by an exception the test swallowed.
    /// </summary>
    [Fact]
    public async Task InvalidationVerbsAreNoOps()
    {
        var cache = Build();

        await cache.RemoveAsync("k");
        await cache.ExpireAsync("k");
        await cache.RemoveByTagAsync("t");
        await cache.ClearAsync();

        Assert.False((await cache.TryGetAsync<int>("k")).HasValue);
        Assert.Equal(7, await cache.GetOrSetAsync<int>("k", (_, _) => Task.FromResult(7)));
    }

    /// <summary>The sync twin of <see cref="InvalidationVerbsAreNoOps"/> — see there.</summary>
    [Fact]
    public void SyncInvalidationVerbsAreNoOps()
    {
        var cache = Build();

        cache.Remove("k");
        cache.Expire("k");
        cache.RemoveByTag("t");
        cache.Clear();

        Assert.False(cache.TryGet<int>("k").HasValue);
        Assert.Equal(7, cache.GetOrSet<int>("k", (_, _) => 7));
    }

    [Fact]
    public void SyncVerbsMatchAsync()
    {
        var cache = Build();
        var calls = 0;

        cache.Set("k", 1);
        Assert.False(cache.TryGet<int>("k").HasValue);
        Assert.Equal(0, cache.GetOrDefault<int>("k"));
        Assert.Equal(-1, cache.GetOrDefault("k", -1));
        Assert.Equal(5, cache.GetOrSet<int>("k", (_, _) => { calls++; return 5; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CacheNameIsPreserved()
    {
        Assert.Equal(CachingDefaults.DefaultCacheName, Build().CacheName);
    }
}
