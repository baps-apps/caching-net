using Caching.NET.Extensions;

namespace Caching.NET.Tests.Extensions;

public class CacheExtensionsTests
{
    [Fact]
    public async Task GetManyAsync_ReturnsAnEntryForEveryRequestedKey()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("Order:1", "a");
        await cache.SetAsync("Order:3", "c");

        var results = await cache.GetManyAsync<string>(["Order:1", "Order:2", "Order:3"]);

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results["Order:1"]);
        Assert.Null(results["Order:2"]);
        Assert.Equal("c", results["Order:3"]);
    }

    [Fact]
    public async Task GetManyAsync_CollapsesDuplicateKeys()
    {
        using var host = TestHost.BuildInMemory();
        await host.Cache().SetAsync("dup", 1);

        var results = await host.Cache().GetManyAsync<int>(["dup", "dup"]);

        Assert.Single(results);
    }

    [Fact]
    public async Task SetManyAsync_WritesEveryEntry()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetManyAsync(new Dictionary<string, int>
        {
            ["a"] = 1,
            ["b"] = 2
        });

        Assert.Equal(1, await cache.GetOrDefaultAsync<int>("a"));
        Assert.Equal(2, await cache.GetOrDefaultAsync<int>("b"));
    }

    [Fact]
    public async Task SetManyAsync_AppliesTagsToEveryEntry()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetManyAsync(
            new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 },
            tags: ["batch"]);

        await cache.RemoveByTagAsync("batch");

        Assert.False((await cache.TryGetAsync<int>("a")).HasValue);
        Assert.False((await cache.TryGetAsync<int>("b")).HasValue);
    }

    [Fact]
    public async Task SetManyAsync_WithNoItems_IsANoOp()
    {
        using var host = TestHost.BuildInMemory();
        await host.Cache().SetManyAsync(new Dictionary<string, int>());
    }

    [Fact]
    public async Task RemoveManyAsync_RemovesEveryKeyAndSkipsBlankOnes()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("a", 1);
        await cache.SetAsync("b", 2);

        await cache.RemoveManyAsync(["a", "  ", "b"]);

        Assert.False((await cache.TryGetAsync<int>("a")).HasValue);
        Assert.False((await cache.TryGetAsync<int>("b")).HasValue);
    }

    [Fact]
    public async Task ExistsAsync_ReportsPresenceWithoutRunningAFactory()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        Assert.False(await cache.ExistsAsync<int>("Job:1"));

        await cache.SetAsync("Job:1", 1);

        Assert.True(await cache.ExistsAsync<int>("Job:1"));
    }

    [Fact]
    public async Task RefreshAsync_RunsTheFactoryEvenWhenTheEntryIsFresh()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("Leaderboard", "old");

        var refreshed = await cache.RefreshAsync("Leaderboard", async _ => "new");

        Assert.Equal("new", refreshed);
        Assert.Equal("new", await cache.GetOrDefaultAsync<string>("Leaderboard"));
    }

    [Fact]
    public async Task NullArguments_AreRejected()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetManyAsync<int>(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveManyAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await cache.SetManyAsync<int>(null!));
    }
}
