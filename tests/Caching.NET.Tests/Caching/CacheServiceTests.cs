using Caching.NET.Options;

namespace Caching.NET.Tests.Caching;

public class CacheServiceTests
{
    [Fact]
    public async Task GetOrSetAsync_RunsFactoryOnMiss_ThenCaches()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();
        var calls = 0;

        var first = await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(7); });
        var second = await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(9); });

        Assert.Equal(7, first);
        Assert.Equal(7, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TryGetAsync_DistinguishesCachedNullFromMiss()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        var miss = await cache.TryGetAsync<string?>("absent");
        await cache.SetAsync<string?>("present", null);
        var hit = await cache.TryGetAsync<string?>("present");

        Assert.False(miss.HasValue);
        Assert.True(hit.HasValue);
        Assert.Null(hit.Value);
    }

    [Fact]
    public async Task RemoveAsync_EvictsTheEntry()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("k", 1);
        await cache.RemoveAsync("k");

        Assert.False((await cache.TryGetAsync<int>("k")).HasValue);
    }

    [Fact]
    public async Task RemoveByTagAsync_EvictsTaggedEntries()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("a", 1, tags: ["group"]);
        await cache.SetAsync("b", 2, tags: ["group"]);
        await cache.SetAsync("c", 3);

        await cache.RemoveByTagAsync("group");

        Assert.False((await cache.TryGetAsync<int>("a")).HasValue);
        Assert.False((await cache.TryGetAsync<int>("b")).HasValue);
        Assert.True((await cache.TryGetAsync<int>("c")).HasValue);
    }

    [Fact]
    public async Task ClearAsync_EvictsEverything()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("a", 1);
        await cache.ClearAsync();

        Assert.False((await cache.TryGetAsync<int>("a")).HasValue);
    }

    [Fact]
    public void SyncVerbs_BehaveLikeTheirAsyncTwins()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        cache.Set("k", 5);
        Assert.Equal(5, cache.GetOrDefault<int>("k"));
        Assert.True(cache.TryGet<int>("k").HasValue);

        cache.Remove("k");
        Assert.False(cache.TryGet<int>("k").HasValue);

        var produced = cache.GetOrSet<int>("j", (_, _) => 11);
        Assert.Equal(11, produced);
    }

    // The three invalidation verbs below are the sync half of the contract and each has its own
    // hand-written body — key guard, span, engine call, result tag, invalidation counter — not a
    // compiler-generated forwarder to the async form. Each therefore needs its own observable
    // assertion: no-op'ing all three used to leave the whole unit suite green.
    [Fact]
    public void Expire_EvictsTheEntry()
    {
        // Fail-safe off: with it on, an expired entry is still available as a stale value, so a
        // no-op Expire and a working one could not be told apart by a subsequent read.
        using var host = TestHost.BuildInMemory(c => c.WithFailSafe(false));
        var cache = host.Cache();

        cache.Set("k", 1);
        Assert.True(cache.TryGet<int>("k").HasValue);

        cache.Expire("k");

        Assert.False(cache.TryGet<int>("k").HasValue);
    }

    [Fact]
    public void RemoveByTag_EvictsTaggedEntriesAndLeavesTheRest()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        cache.Set("a", 1, tags: ["group"]);
        cache.Set("b", 2, tags: ["group"]);
        cache.Set("c", 3);

        cache.RemoveByTag("group");

        Assert.False(cache.TryGet<int>("a").HasValue);
        Assert.False(cache.TryGet<int>("b").HasValue);
        // The untagged entry proves the verb removed by tag rather than clearing everything.
        Assert.True(cache.TryGet<int>("c").HasValue);
    }

    [Fact]
    public void Clear_EvictsEverything()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet<int>("a").HasValue);

        cache.Clear();

        Assert.False(cache.TryGet<int>("a").HasValue);
        Assert.False(cache.TryGet<int>("b").HasValue);
    }

    [Fact]
    public async Task Overrides_AreAdditive_FailSafeSurvivesAnOverrideThatTouchesAnUnrelatedProperty()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithDefaultExpiration(TimeSpan.FromMinutes(10))
            .WithFailSafe(true, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(1)));
        var cache = host.Cache();

        await cache.SetAsync("k", 1);
        await cache.ExpireAsync("k");

        // Size is unrelated to fail-safe. If a supplied CacheEntryOverrides replaced the cache's
        // configured defaults instead of layering on top of them, this override would silently
        // disable fail-safe for the call, and the factory failure below would propagate instead of
        // falling back to the expired value.
        var served = await cache.GetOrSetAsync<int>(
            "k",
            async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("source is down");
            },
            options: new CacheEntryOverrides { Size = 1 });

        Assert.Equal(1, served);
    }

    [Fact]
    public async Task KeyGuard_FiresEvenWhenOverridesArePassed()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithMaximumKeyLength(20)
            .WithSecurity(s => s.KeyLengthPolicy = CacheGuardPolicy.Throw));
        var cache = host.Cache();

        var tooLong = new string('x', 64);

        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.SetAsync(tooLong, 1, new CacheEntryOverrides { Size = 1 }).AsTask());
    }

    [Fact]
    public async Task TagGuard_FiresOnEveryCallThatSuppliesTags()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithSecurity(s =>
            {
                s.TagPolicy = CacheGuardPolicy.Throw;
                s.MaximumTagCount = 1;
            }));
        var cache = host.Cache();

        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.SetAsync("k", 1, tags: ["a", "b"]).AsTask());
    }

    [Fact]
    public async Task FactoryContext_ExposesStaleValueAndNotModified()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithDefaultExpiration(TimeSpan.FromMilliseconds(50))
            .WithJitter(TimeSpan.Zero)
            .WithFailSafe(true, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(1)));
        var cache = host.Cache();

        await cache.GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));
        await Task.Delay(120);

        bool? sawStale = null;
        int? staleValue = null;

        var refreshed = await cache.GetOrSetAsync<int>("k", (ctx, _) =>
        {
            sawStale = ctx.HasStaleValue;
            staleValue = ctx.StaleValue.Value;
            return Task.FromResult(ctx.NotModified());
        });

        Assert.True(sawStale);
        Assert.Equal(1, staleValue);
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task ContextFreeFactory_InfersTheValueType()
    {
        using var host = TestHost.BuildInMemory();

        // The point of this test is that it compiles without a type argument: TValue appears in the
        // factory's own return type here (unlike the context-taking overload, where TValue appears
        // only inside CacheFactoryContext<TValue> — a lambda parameter type ordinary inference cannot
        // see through). The value assertion below is secondary.
        var value = await host.Cache().GetOrSetAsync("Order:1", async _ => await Task.FromResult(41) + 1);

        Assert.Equal(42, value);
    }

    [Fact]
    public void ContextFreeFactory_Sync_InfersTheValueType()
    {
        using var host = TestHost.BuildInMemory();

        var value = host.Cache().GetOrSet("Order:1", _ => 41 + 1);

        Assert.Equal(42, value);
    }

    // The context-free sync overload takes a Func<CancellationToken, TValue?>, so the caller must have
    // a way to supply that token. It originally did not, which made sync + cancellation + inference
    // impossible; this pins the forwarding so the parameter cannot be dropped again.
    [Fact]
    public void ContextFreeFactory_Sync_ForwardsTheCallersToken()
    {
        using var host = TestHost.BuildInMemory();
        using var cts = new CancellationTokenSource();
        var observed = CancellationToken.None;

        host.Cache().GetOrSet<int>("token-forwarding", ct => { observed = ct; return 1; }, token: cts.Token);

        Assert.True(observed.CanBeCanceled);
        Assert.False(observed.IsCancellationRequested);

        cts.Cancel();
        Assert.True(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task ContextFreeFactory_RunsOnMissThenCaches()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();
        var calls = 0;

        var first = await cache.GetOrSetAsync("context-free-k", async _ => { calls++; return await Task.FromResult(7); });
        var second = await cache.GetOrSetAsync("context-free-k", async _ => { calls++; return await Task.FromResult(9); });

        Assert.Equal(7, first);
        Assert.Equal(7, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ContextFreeFactory_Sync_RunsOnMissThenCaches()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();
        var calls = 0;

        var first = cache.GetOrSet("context-free-j", _ => { calls++; return 7; });
        var second = cache.GetOrSet("context-free-j", _ => { calls++; return 9; });

        Assert.Equal(7, first);
        Assert.Equal(7, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ContextFreeFactory_KeyGuardFires()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithMaximumKeyLength(20)
            .WithSecurity(s => s.KeyLengthPolicy = CacheGuardPolicy.Throw));
        var cache = host.Cache();

        var tooLong = new string('x', 64);

        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.GetOrSetAsync(tooLong, async _ => await Task.FromResult(1)).AsTask());
    }

    [Fact]
    public void ContextFreeFactory_Sync_KeyGuardFires()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithMaximumKeyLength(20)
            .WithSecurity(s => s.KeyLengthPolicy = CacheGuardPolicy.Throw));
        var cache = host.Cache();

        var tooLong = new string('x', 64);

        Assert.Throws<ArgumentException>(() => cache.GetOrSet(tooLong, _ => 1));
    }

    [Fact]
    public async Task ContextFreeFactory_DisabledCache_RunsFactoryEveryTimeAndCachesNothing()
    {
        using var host = TestHost.Build(c => c.UseInMemory().WithApplicationPrefix("tests").Disable());
        var cache = host.DisabledCache();
        var calls = 0;

        var first = await cache.GetOrSetAsync("k", async _ => { calls++; return await Task.FromResult(1); });
        var second = await cache.GetOrSetAsync("k", async _ => { calls++; return await Task.FromResult(1); });

        Assert.Equal(2, calls);
        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void ContextFreeFactory_Sync_DisabledCache_RunsFactoryEveryTimeAndCachesNothing()
    {
        using var host = TestHost.Build(c => c.UseInMemory().WithApplicationPrefix("tests").Disable());
        var cache = host.DisabledCache();
        var calls = 0;

        var first = cache.GetOrSet("k", _ => { calls++; return 1; });
        var second = cache.GetOrSet("k", _ => { calls++; return 1; });

        Assert.Equal(2, calls);
        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task FactoryContext_OverridesDriveAdaptiveExpiration()
    {
        // Fail-safe is on by default and would serve the entry as a stale value past its expiry
        // regardless of whether the override ever reached the engine, which is exactly why this
        // assertion previously could not fail: deleting CacheFactoryContext.ApplyOverrides() left it
        // green. Turning fail-safe off here means the entry can only still have a value after the
        // configured 40 ms default has elapsed if the per-execution override actually applied.
        using var host = TestHost.BuildInMemory(c => c
            .WithDefaultExpiration(TimeSpan.FromMilliseconds(40))
            .WithJitter(TimeSpan.Zero)
            .WithFailSafe(false));
        var cache = host.Cache();

        await cache.GetOrSetAsync<int>("k", (ctx, _) =>
        {
            ctx.Overrides.LocalExpiration = TimeSpan.FromMinutes(10);
            return Task.FromResult(3);
        });

        await Task.Delay(120);

        // The configured default would have expired by now; the per-execution override did not.
        Assert.True((await cache.TryGetAsync<int>("k")).HasValue);
    }

    /// <summary>
    /// A blank tag is an argument error, not a tag-limit breach, so it must be rejected whatever
    /// <c>Security.TagPolicy</c> says. <c>CacheGuard.ValidateTags</c> returns immediately under
    /// <see cref="CacheGuardPolicy.Ignore"/>, so before the verbs checked the argument themselves,
    /// <c>RemoveByTagAsync("   ")</c> was accepted and silently did nothing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveByTagAsync_RejectsABlankTagEvenWhenTheTagPolicyIsIgnore(string tag)
    {
        using var host = TestHost.BuildInMemory(c => c.Options.Security.TagPolicy = CacheGuardPolicy.Ignore);
        var cache = host.Cache();

        await Assert.ThrowsAsync<ArgumentException>(() => cache.RemoveByTagAsync(tag).AsTask());
        Assert.Throws<ArgumentException>(() => cache.RemoveByTag(tag));
    }
}
