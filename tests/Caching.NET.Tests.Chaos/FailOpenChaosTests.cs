using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caching.NET.Tests.Chaos;

public class FailOpenChaosTests
{
    private sealed class AlwaysThrowDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("chaos-boom");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("chaos-boom");
        public void Refresh(string key) => throw new InvalidOperationException("chaos-boom");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("chaos-boom");
        public void Remove(string key) => throw new InvalidOperationException("chaos-boom");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("chaos-boom");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException("chaos-boom");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("chaos-boom");
    }

    [Fact]
    public async Task FailOpen_runs_factory_when_backend_throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Set up Redis mode — connection string won't be used because we override IDistributedCache
        services.AddCaching(b => b.UseRedis("localhost:6379").WithKeyPrefix("chaos-test"));

        // Override IDistributedCache with our always-throwing fake (last registration wins)
        services.AddSingleton<IDistributedCache, AlwaysThrowDistributedCache>();

        // FailOpen is true by default; ThrowOnFailure is false by default — make it explicit
        services.PostConfigure<CacheOptions>(o =>
        {
            o.FailOpen = true;
            o.ThrowOnFailure = false;
        });

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<ICacheService>();

        var got = await cache.GetOrCreateAsync("k", _ => Task.FromResult("from-factory"));
        Assert.Equal("from-factory", got);
    }
}
