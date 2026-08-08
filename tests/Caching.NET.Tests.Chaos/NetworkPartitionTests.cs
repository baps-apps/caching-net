using System.Diagnostics;
using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Health;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.Redis;

namespace Caching.NET.Tests.Chaos;

/// <summary>
/// A true network partition, as distinct from the clean outage <see cref="RedisOutageTests"/>
/// covers.
/// </summary>
/// <remarks>
/// <para>
/// Stopping a container is the <i>polite</i> failure: the host refuses the port, so every pending
/// and subsequent command fails immediately with a connection error. A partition is the rude one —
/// the peer simply stops answering. TCP connections stay <c>ESTABLISHED</c>, nothing is refused and
/// nothing is reset, so the client cannot learn anything until its own command timeout fires. That
/// is the failure that hangs request threads in production, and it is not exercised by a stop/start
/// test.
/// </para>
/// <para>
/// <c>docker pause</c> freezes the container's processes with the socket still open, which
/// reproduces exactly that blackhole. These tests therefore assert on <b>latency bounds</b> as much
/// as on values: the contract that matters during a partition is that a caller is released on
/// Caching.NET's timeouts rather than on Redis's, and that L1 keeps answering.
/// </para>
/// </remarks>
public class NetworkPartitionTests : IAsyncLifetime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Generous relative to <see cref="CommandTimeout"/> and the 500 ms distributed soft timeout: the
    /// point is to catch a caller blocked on a frozen Redis (which has no timeout of its own and
    /// would hang indefinitely), not to measure scheduler jitter on a loaded CI machine.
    /// </summary>
    private static readonly TimeSpan CallerBudget = TimeSpan.FromSeconds(5);

    private RedisContainer _redis = null!;
    private int _hostPort;
    private bool _paused;

    public async Task InitializeAsync() => (_redis, _hostPort) = await ChaosRedis.StartAsync();

    public async Task DisposeAsync()
    {
        // A paused container cannot be removed, and a test that fails mid-partition would otherwise
        // leak it for the rest of the session.
        await HealAsync();
        await _redis.DisposeAsync();
    }

    private string ConnectionString
        => $"127.0.0.1:{_hostPort},abortConnect=false,connectTimeout=1000";

    private async Task PartitionAsync()
    {
        if (!_paused)
        {
            await _redis.PauseAsync();
            _paused = true;
        }
    }

    private async Task HealAsync()
    {
        if (_paused)
        {
            await _redis.UnpauseAsync();
            _paused = false;
        }
    }

    private ServiceProvider BuildHost(string prefix, Action<CachingBuilder>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(cache =>
        {
            cache.UseHybrid(ConnectionString, enableBackplane: true)
                .WithApplicationPrefix(prefix)
                .WithJitter(TimeSpan.Zero)
                .WithDefaultExpiration(TimeSpan.FromMinutes(5))
                .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromHours(1))
                .WithRedis(r =>
                {
                    r.ConnectTimeout = CommandTimeout;
                    r.CommandTimeout = CommandTimeout;
                });
            extra?.Invoke(cache);
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The core promise of Hybrid during a partition: a warm key is answered by L1 and the caller is
    /// never made to wait for a Redis that will never answer.
    /// </summary>
    [Fact]
    public async Task DuringAPartition_AWarmKeyIsStillServedFromL1WithoutWaitingForRedis()
    {
        await using var provider = BuildHost("partition-warm");
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("Order:1", "v1");
        Assert.Equal("v1", await cache.GetOrDefaultAsync<string>("Order:1"));

        await PartitionAsync();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var value = await cache.GetOrDefaultAsync<string>("Order:1");
                var elapsed = Stopwatch.GetElapsedTime(started);

                Assert.Equal("v1", value);
                Assert.True(
                    elapsed < CallerBudget,
                    $"read {i} took {elapsed.TotalMilliseconds:0} ms during a partition; the caller was "
                    + "waiting on a frozen Redis instead of being served from L1");
            }
        }
        finally
        {
            await HealAsync();
        }
    }

    /// <summary>
    /// A key no instance has seen before, requested while Redis is unreachable. The factory has to
    /// run and the caller has to be released — a partition must degrade the cache, not the request.
    /// </summary>
    [Fact]
    public async Task DuringAPartition_AColdKeyRunsTheFactoryAndReleasesTheCaller()
    {
        await using var provider = BuildHost("partition-cold");
        var cache = provider.GetRequiredService<ICacheService>();

        // Force the connection to exist before freezing it, so this exercises a blackholed
        // established connection rather than a connect that never completes.
        await cache.SetAsync("warmup", "x");

        await PartitionAsync();
        try
        {
            var factoryRuns = 0;
            var started = Stopwatch.GetTimestamp();

            var value = await cache.GetOrSetAsync<string>(
                "Order:cold",
                _ =>
                {
                    Interlocked.Increment(ref factoryRuns);
                    return Task.FromResult<string?>("produced");
                });

            var elapsed = Stopwatch.GetElapsedTime(started);

            Assert.Equal("produced", value);
            Assert.Equal(1, factoryRuns);
            Assert.True(
                elapsed < CallerBudget,
                $"a cold-key GetOrSet took {elapsed.TotalMilliseconds:0} ms during a partition");

            // The produced value is in L1, so the next read is immediate and does not re-run the
            // factory even though L2 is still unreachable.
            Assert.Equal("produced", await cache.GetOrDefaultAsync<string>("Order:cold"));
            Assert.Equal(1, factoryRuns);
        }
        finally
        {
            await HealAsync();
        }
    }

    /// <summary>
    /// Writes issued into a blackhole must not block the caller, and must not be silently lost once
    /// the partition heals — auto-recovery is what replays them.
    /// </summary>
    [Fact]
    public async Task AWriteMadeDuringAPartition_ReachesRedisAfterItHeals()
    {
        await using var writer = BuildHost("partition-write");
        var cache = writer.GetRequiredService<ICacheService>();

        await cache.SetAsync("warmup", "x");

        await PartitionAsync();
        try
        {
            var started = Stopwatch.GetTimestamp();
            await cache.SetAsync("Order:during", "written-during-partition");
            var elapsed = Stopwatch.GetElapsedTime(started);

            Assert.True(
                elapsed < CallerBudget,
                $"a write took {elapsed.TotalMilliseconds:0} ms during a partition; the caller was not released");
        }
        finally
        {
            await HealAsync();
        }

        // A second, independent instance can only see the value if it actually reached Redis.
        await using var reader = BuildHost("partition-write");
        var readerCache = reader.GetRequiredService<ICacheService>();

        var visible = await WaitForAsync(
            async () => await readerCache.GetOrDefaultAsync<string>("Order:during") == "written-during-partition",
            TimeSpan.FromSeconds(60));

        Assert.True(visible, "the write made during the partition never reached Redis after it healed");
    }

    /// <summary>
    /// Readiness has to notice a blackhole, not just a refused port, and recover on its own once the
    /// partition heals — without restarting the process.
    /// </summary>
    [Fact]
    public async Task ReadinessDegradesDuringAPartitionAndRecoversWithoutARestart()
    {
        await using var provider = BuildHost("partition-health");

        var caches = provider.GetRequiredService<ICacheProvider>();
        var options = provider.GetRequiredService<IOptionsMonitor<CachingOptions>>();
        var liveness = new CachingLivenessHealthCheck(caches);
        var readiness = new CachingHealthCheck(caches, options);

        Assert.Equal(HealthStatus.Healthy, (await readiness.CheckHealthAsync(HealthContext())).Status);

        await PartitionAsync();
        try
        {
            // Liveness must never depend on Redis: failing it during a partition would restart every
            // pod at once, turning a cache degradation into an outage.
            Assert.Equal(HealthStatus.Healthy, (await liveness.CheckHealthAsync(HealthContext())).Status);

            var degraded = await WaitForAsync(
                async () => (await readiness.CheckHealthAsync(HealthContext())).Status == HealthStatus.Degraded,
                TimeSpan.FromSeconds(30));

            Assert.True(degraded, "readiness never noticed the partition");
        }
        finally
        {
            await HealAsync();
        }

        var recovered = await WaitForAsync(
            async () => (await readiness.CheckHealthAsync(HealthContext())).Status == HealthStatus.Healthy,
            TimeSpan.FromSeconds(60));

        Assert.True(recovered, "readiness never recovered after the partition healed");
    }

    /// <summary>
    /// Two instances across a partition: the backplane cannot deliver, so each keeps serving its own
    /// L1 copy — and once the partition heals, invalidation works again rather than leaving the two
    /// permanently diverged.
    /// </summary>
    [Fact]
    public async Task CrossInstanceInvalidationResumesAfterThePartitionHeals()
    {
        await using var podA = BuildHost("partition-pods");
        await using var podB = BuildHost("partition-pods");
        var a = podA.GetRequiredService<ICacheService>();
        var b = podB.GetRequiredService<ICacheService>();

        // Foreground write: AllowBackgroundDistributedOperations is on by default, so a plain
        // SetAsync returns before the value reaches Redis and pod B's read below races it. That race
        // is real but it is not what this test is about — the precondition has to be durable before
        // the partition starts, or the test fails for the wrong reason (observed under parallel load).
        await a.SetAsync(
            "Order:shared",
            "v1",
            new CacheEntryOverrides { AllowBackgroundDistributedOperations = false });

        Assert.Equal("v1", await b.GetOrDefaultAsync<string>("Order:shared"));

        await PartitionAsync();
        try
        {
            // Neither instance can reach Redis, so both keep answering from their own L1.
            Assert.Equal("v1", await a.GetOrDefaultAsync<string>("Order:shared"));
            Assert.Equal("v1", await b.GetOrDefaultAsync<string>("Order:shared"));
        }
        finally
        {
            await HealAsync();
        }

        // After healing, an invalidation from A must reach B again.
        var invalidated = await WaitForAsync(
            async () =>
            {
                await a.RemoveAsync("Order:shared");
                return !(await b.TryGetAsync<string>("Order:shared")).HasValue;
            },
            TimeSpan.FromSeconds(60));

        Assert.True(invalidated, "cross-instance invalidation never resumed after the partition healed");
    }

    private static HealthCheckContext HealthContext() => new()
    {
        Registration = new HealthCheckRegistration("caching-net", _ => null!, HealthStatus.Unhealthy, tags: null)
    };

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Still partitioned or still recovering.
            }

            await Task.Delay(100);
        }

        return false;
    }
}
