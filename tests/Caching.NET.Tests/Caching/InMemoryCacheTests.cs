using Caching.NET.Extensions;
using Caching.NET.Options;

namespace Caching.NET.Tests.Caching;

/// <summary>
/// In-memory mode behaviour, exercised through the same public cache API a consuming application
/// uses. No Redis and no Docker involved.
/// </summary>
public class InMemoryCacheTests
{
    [Fact]
    public async Task SetThenGet_ReturnsTheValue()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("Product:1", "widget");

        Assert.Equal("widget", await cache.GetOrDefaultAsync<string>("Product:1"));
    }

    [Fact]
    public async Task Miss_ReturnsTheDefaultAndDoesNotThrow()
    {
        using var host = TestHost.BuildInMemory();

        Assert.Null(await host.Cache().GetOrDefaultAsync<string>("absent"));
        Assert.False((await host.Cache().TryGetAsync<string>("absent")).HasValue);
    }

    [Fact]
    public async Task GetOrSet_RunsTheFactoryOnceThenServesFromCache()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();
        var calls = 0;

        for (var i = 0; i < 5; i++)
        {
            await cache.GetOrSetAsync<int>("counter", async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return 7;
            });
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentGetOrSet_CoalescesIntoASingleFactoryExecution()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();
        var calls = 0;
        using var release = new SemaphoreSlim(0);

        var callers = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
            await cache.GetOrSetAsync<int>("hot", async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                await release.WaitAsync();
                return 1;
            }))).ToArray();

        // Deterministic: the factory is blocked until every caller has had a chance to arrive,
        // then released exactly once.
        //
        // Delay rather than Task.Yield, and a deadline rather than none. A yield loop on a condition
        // that never becomes true does not merely hang: it re-queues a continuation per iteration,
        // which the thread pool's hill-climbing heuristic reads as starvation and answers by
        // injecting more threads, so the process saturates several cores and never terminates.
        var deadline = Environment.TickCount64 + 10_000;
        while (Volatile.Read(ref calls) == 0)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("No factory execution started within 10 seconds.");
            }

            await Task.Delay(1);
        }

        release.Release();
        var results = await Task.WhenAll(callers);

        Assert.All(results, value => Assert.Equal(1, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Remove_EvictsTheEntry()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("Product:1", "widget");
        await cache.RemoveAsync("Product:1");

        Assert.False((await cache.TryGetAsync<string>("Product:1")).HasValue);
    }

    [Fact]
    public async Task RemoveByTag_EvictsEveryEntryCarryingTheTag()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("Product:1", "a", tags: ["category:tools"]);
        await cache.SetAsync("Product:2", "b", tags: ["category:tools"]);
        await cache.SetAsync("Product:3", "c", tags: ["category:toys"]);

        await cache.RemoveByTagAsync("category:tools");

        Assert.False((await cache.TryGetAsync<string>("Product:1")).HasValue);
        Assert.False((await cache.TryGetAsync<string>("Product:2")).HasValue);
        Assert.True((await cache.TryGetAsync<string>("Product:3")).HasValue);
    }

    [Fact]
    public async Task Clear_EvictsEverything()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("a", 1);
        await cache.SetAsync("b", 2);

        await cache.ClearAsync();

        Assert.False((await cache.TryGetAsync<int>("a")).HasValue);
        Assert.False((await cache.TryGetAsync<int>("b")).HasValue);
    }

    [Fact]
    public async Task NullValues_AreCachedSoNegativeLookupsDoNotStampedeTheSource()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            var value = await cache.GetOrSetAsync<string?>("missing-entity", async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return null;
            });
            Assert.Null(value);
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailSafe_ServesTheStaleValueWhenTheFactoryFails()
    {
        using var host = TestHost.Build(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            // JitterMaxDuration defaults to 2 seconds, which would silently stretch a 50 ms
            // expiration to anything up to 2050 ms. This test forces expiry with ExpireAsync rather
            // than waiting, but pinning the jitter keeps the configured duration meaningful.
            .WithJitter(TimeSpan.Zero)
            .WithDefaultExpiration(TimeSpan.FromMilliseconds(50))
            .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromMinutes(10), throttleDuration: TimeSpan.FromMinutes(1)));

        var cache = host.Cache();
        await cache.SetAsync("Order:1", "fresh");

        // Expire the logical entry without removing its fail-safe copy.
        await cache.ExpireAsync("Order:1");

        var value = await cache.GetOrSetAsync<string>(
            "Order:1",
            async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("source is down");
            });

        Assert.Equal("fresh", value);
    }

    [Fact]
    public async Task FactoryException_SurfacesWhenThereIsNoStaleValue()
    {
        using var host = TestHost.BuildInMemory();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.Cache().GetOrSetAsync<string>("Order:1", async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));
    }

    [Fact]
    public async Task CallerCancellation_StopsTheOperation()
    {
        using var host = TestHost.BuildInMemory();
        using var cts = new CancellationTokenSource();
        var factoryCompleted = false;

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.Cache().GetOrSetAsync<int>(
                "cancelled",
                async (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    factoryCompleted = true;
                    await Task.Yield();
                    return 1;
                },
                token: cts.Token));

        Assert.False(factoryCompleted);
        Assert.False((await host.Cache().TryGetAsync<int>("cancelled")).HasValue);
    }

    [Fact]
    public async Task PerCallDuration_OverridesTheConfiguredDefault()
    {
        using var host = TestHost.Build(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithJitter(TimeSpan.Zero)
            .WithDefaultExpiration(TimeSpan.FromHours(1)));

        var cache = host.Cache();

        // The engine has no single "duration" knob behind CacheEntryOverrides: a per-call duration
        // is expressed as both layer expirations. InMemory mode only consults the local one, but
        // setting both keeps the call's intent readable and mode-independent.
        await cache.SetAsync("short", 1, new CacheEntryOverrides
        {
            LocalExpiration = TimeSpan.FromMilliseconds(1),
            DistributedExpiration = TimeSpan.FromMilliseconds(1)
        });

        await Task.Delay(60);

        Assert.False((await cache.TryGetAsync<int>("short")).HasValue);
    }

    [Fact]
    public async Task PerCallOverrides_ChangeTheEntryTheyAreAppliedTo()
    {
        // The mirror of FailSafe_ServesTheStaleValueWhenTheFactoryFails: same cache configuration,
        // same expire-then-fail sequence, one per-call override. If the override does not reach the
        // entry, the stale value is served and this test fails.
        using var host = TestHost.Build(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithJitter(TimeSpan.Zero)
            .WithDefaultExpiration(TimeSpan.FromMinutes(5))
            .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromHours(1), throttleDuration: TimeSpan.FromMinutes(1)));

        var cache = host.Cache();
        var optOut = new CacheEntryOverrides { FailSafe = false };

        await cache.SetAsync("Order:opt-out", "fresh", optOut);
        await cache.ExpireAsync("Order:opt-out");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cache.GetOrSetAsync<string>(
                "Order:opt-out",
                async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("source is down");
                },
                options: optOut));
    }

    [Fact]
    public async Task Expire_LeavesTheEntryUsableForFailSafeButNotForReads()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("Order:1", "value");
        await cache.ExpireAsync("Order:1");

        Assert.False((await cache.TryGetAsync<string>("Order:1")).HasValue);
    }

    [Fact]
    public async Task KeysAreNamespaced_SoAnotherApplicationPrefixCannotSeeThem()
    {
        using var first = TestHost.Build(c => c.UseInMemory().WithApplicationPrefix("app-one"));
        using var second = TestHost.Build(c => c.UseInMemory().WithApplicationPrefix("app-two"));

        await first.Cache().SetAsync("shared", "one");
        await second.Cache().SetAsync("shared", "two");

        Assert.Equal("one", await first.Cache().GetOrDefaultAsync<string>("shared"));
        Assert.Equal("two", await second.Cache().GetOrDefaultAsync<string>("shared"));
    }
}
