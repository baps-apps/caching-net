using Caching.NET.Tests.Integration.Fixtures;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// How far stampede protection actually reaches, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// The engine coalesces concurrent factory calls with an in-process memory <i>locker</i>. After the
/// lock is taken it re-checks the <i>memory cache</i> before running the factory — which is the step
/// that turns "one lock holder at a time" into "one factory execution". Redis mode bypasses the
/// memory cache, so that re-check can never hit and one extra caller runs the factory before the
/// value becomes visible through the distributed layer.
/// </para>
/// <para>
/// The consequence is small and bounded (one extra execution per cold key, not one per caller) but
/// it is real, so it is pinned here instead of being described as "protection survives". Nothing in
/// any mode is distributed single-flight: two processes racing the same cold key each run their own
/// factory, which the cross-instance test asserts directly.
/// </para>
/// </remarks>
[Collection(RedisCollection.Name)]
public class StampedeScopeTests
{
    private const int Concurrency = 50;
    private static readonly TimeSpan FactoryDuration = TimeSpan.FromMilliseconds(200);

    private readonly RedisFixture _redis;

    public StampedeScopeTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [Fact]
    public async Task InMemoryMode_CoalescesConcurrentCallersIntoOneFactoryExecution()
    {
        using var host = CacheHost.Create(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("sf-inmemory")
            .WithJitter(TimeSpan.Zero));

        Assert.Equal(1, await CountFactoryExecutionsAsync(host.Cache));
    }

    [Fact]
    public async Task HybridMode_CoalescesConcurrentCallersIntoOneFactoryExecution()
    {
        using var host = HybridHost("sf-hybrid");

        Assert.Equal(1, await CountFactoryExecutionsAsync(host.Cache));
    }

    [Fact]
    public async Task RedisMode_CoalescesConcurrentCallersButRunsOneExtraFactoryExecution()
    {
        using var host = RedisHost("sf-redis");

        var executions = await CountFactoryExecutionsAsync(host.Cache);

        // The bound is what matters: the factory does not run once per caller. The exact count is
        // asserted as a range so the test reports a regression (a return to per-caller execution)
        // rather than breaking if the engine ever closes the gap and drops it back to one.
        Assert.InRange(executions, 1, 2);
    }

    [Fact]
    public async Task StampedeProtectionIsProcessLocal_NotDistributed()
    {
        using var podA = HybridHost("sf-crosspod");
        using var podB = HybridHost("sf-crosspod");

        var executions = 0;
        var key = $"Order:{Guid.NewGuid():N}";

        async Task<int> Call(IFusionCache cache) => await cache.GetOrSetAsync(key, async token =>
        {
            Interlocked.Increment(ref executions);
            await Task.Delay(FactoryDuration, token);
            return 1;
        });

        var calls = Enumerable.Range(0, Concurrency)
            .Select(i => Call(i % 2 == 0 ? podA.Cache : podB.Cache))
            .ToArray();

        await Task.WhenAll(calls);

        // Two processes racing the same cold key each run their own factory: the locker is a
        // per-process object with no distributed lease behind it. Anything relying on a single
        // global execution (an increment, a send, a charge) must not live in a cache factory.
        Assert.Equal(2, executions);
    }

    private CacheHost HybridHost(string prefix) => CacheHost.Create(cache => cache
        .UseHybrid(_redis.ConnectionString)
        .WithApplicationPrefix(prefix)
        .WithJitter(TimeSpan.Zero)
        .WithResilience(r =>
        {
            r.AllowBackgroundDistributedOperations = false;
            r.AllowBackgroundBackplaneOperations = false;
        }));

    private CacheHost RedisHost(string prefix) => CacheHost.Create(cache => cache
        .UseRedis(_redis.ConnectionString)
        .WithApplicationPrefix(prefix)
        .WithJitter(TimeSpan.Zero)
        .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

    private static async Task<int> CountFactoryExecutionsAsync(IFusionCache cache)
    {
        var executions = 0;
        var key = $"Order:{Guid.NewGuid():N}";

        var calls = Enumerable.Range(0, Concurrency)
            .Select(_ => cache.GetOrSetAsync(key, async token =>
            {
                Interlocked.Increment(ref executions);
                await Task.Delay(FactoryDuration, token);
                return 1;
            }).AsTask())
            .ToArray();

        var results = await Task.WhenAll(calls);

        Assert.All(results, value => Assert.Equal(1, value));
        return executions;
    }
}
