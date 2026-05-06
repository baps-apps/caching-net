using Caching.NET.Abstractions;
using Caching.NET.Options;
using Caching.NET.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caching.NET.Tests.Services;

public class CacheServiceBatchTests
{
    private static InMemoryCacheService BuildInMemory()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var opts = Microsoft.Extensions.Options.Options.Create(new CacheOptions { KeyPrefix = "batch" });
        return new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);
    }

    [Fact]
    public async Task SetMany_then_GetMany_returns_all_values()
    {
        ICacheService svc = BuildInMemory();
        var items = new Dictionary<string, string>
        {
            ["a"] = "1",
            ["b"] = "2",
            ["c"] = "3",
        };
        await svc.SetManyAsync(items);

        var results = await svc.GetManyAsync<string>(new[] { "a", "b", "c", "missing" });

        Assert.Equal(4, results.Count);
        Assert.Equal("1", results["a"]);
        Assert.Equal("2", results["b"]);
        Assert.Equal("3", results["c"]);
        Assert.Null(results["missing"]);
    }

    [Fact]
    public async Task RemoveMany_removes_all_listed_keys()
    {
        ICacheService svc = BuildInMemory();
        await svc.SetManyAsync(new Dictionary<string, string>
        {
            ["x"] = "1",
            ["y"] = "2",
            ["z"] = "3",
        });

        await svc.RemoveManyAsync(new[] { "x", "y" });

        Assert.False(await svc.ExistsAsync("x"));
        Assert.False(await svc.ExistsAsync("y"));
        Assert.True(await svc.ExistsAsync("z"));
    }

    [Fact]
    public async Task GetMany_with_empty_input_returns_empty_dictionary()
    {
        ICacheService svc = BuildInMemory();
        var results = await svc.GetManyAsync<string>(Array.Empty<string>());
        Assert.Empty(results);
    }

    [Fact]
    public async Task SetMany_with_null_throws_ArgumentNullException()
    {
        ICacheService svc = BuildInMemory();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            svc.SetManyAsync<string>(items: null!));
    }
}
