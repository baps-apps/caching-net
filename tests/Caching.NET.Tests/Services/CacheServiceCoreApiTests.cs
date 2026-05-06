using Caching.NET.Abstractions;
using Caching.NET.Options;
using Caching.NET.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Services;

public class CacheServiceCoreApiTests
{
    private static InMemoryCacheService BuildInMemory()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var opts = MsOptions.Options.Create(new CacheOptions { KeyPrefix = "core-api" });
        return new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);
    }

    [Fact]
    public async Task GetAsync_returns_default_for_missing_key()
    {
        ICacheService svc = BuildInMemory();
        var v = await svc.GetAsync<string>("missing");
        Assert.Null(v);
    }

    [Fact]
    public async Task GetAsync_returns_value_after_SetAsync()
    {
        ICacheService svc = BuildInMemory();
        await svc.SetAsync("k", "v");
        var v = await svc.GetAsync<string>("k");
        Assert.Equal("v", v);
    }

    [Fact]
    public async Task ExistsAsync_returns_false_for_missing_then_true_after_set()
    {
        ICacheService svc = BuildInMemory();
        Assert.False(await svc.ExistsAsync("k"));
        await svc.SetAsync("k", "v");
        Assert.True(await svc.ExistsAsync("k"));
    }

    [Fact]
    public async Task RefreshAsync_runs_factory_and_overwrites_existing_entry()
    {
        ICacheService svc = BuildInMemory();
        await svc.SetAsync("k", "old");
        await svc.RefreshAsync("k", _ => Task.FromResult("new"));
        Assert.Equal("new", await svc.GetAsync<string>("k"));
    }

    [Fact]
    public async Task RefreshAsync_writes_value_when_key_absent()
    {
        ICacheService svc = BuildInMemory();
        await svc.RefreshAsync("k", _ => Task.FromResult("first"));
        Assert.Equal("first", await svc.GetAsync<string>("k"));
    }
}
