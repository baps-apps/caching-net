using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Health;
using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.Redis;

namespace Caching.NET.Tests.Chaos;

/// <summary>
/// What Hybrid mode does when Redis goes away, comes back, or was never there. Each test owns a
/// container so it can stop and start it without disturbing anything else.
/// </summary>
public class RedisOutageTests : IAsyncLifetime
{
    // A fixed host port is required: Docker re-randomises published ports across stop/start, and
    // these tests restart the container to prove the cache reconnects on its own.
    private RedisContainer _redis = null!;
    private int _hostPort;

    public async Task InitializeAsync() => (_redis, _hostPort) = await ChaosRedis.StartAsync();

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    private string ConnectionString => $"127.0.0.1:{_hostPort},abortConnect=false,connectTimeout=1000";

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
                    r.ConnectTimeout = TimeSpan.FromSeconds(1);
                    r.CommandTimeout = TimeSpan.FromSeconds(1);
                });
            extra?.Invoke(cache);
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReadinessDetectsARedisOutageInHybridMode_LivenessDoesNot()
    {
        await using var provider = BuildHost("chaos-health");

        var caches = provider.GetRequiredService<ICacheProvider>();
        var options = provider.GetRequiredService<IOptionsMonitor<CachingOptions>>();
        var liveness = new CachingLivenessHealthCheck(caches);
        var readiness = new CachingHealthCheck(caches, options);

        Assert.Equal(HealthStatus.Healthy, (await liveness.CheckHealthAsync(HealthContext())).Status);
        Assert.Equal(HealthStatus.Healthy, (await readiness.CheckHealthAsync(HealthContext())).Status);

        await _redis.StopAsync();
        try
        {
            // Liveness must never depend on Redis: failing it would restart every pod at once.
            Assert.Equal(HealthStatus.Healthy, (await liveness.CheckHealthAsync(HealthContext())).Status);

            // Readiness must see it. Before the probe bypassed L1, Hybrid read back its own local
            // write and reported Healthy with Redis stopped.
            var down = await readiness.CheckHealthAsync(HealthContext());
            Assert.Equal(HealthStatus.Degraded, down.Status);
            Assert.NotEqual("Hybrid", down.Data["default"]);
        }
        finally
        {
            await _redis.StartAsync();
        }

        var recovered = await WaitForAsync(
            async () => (await readiness.CheckHealthAsync(HealthContext())).Status == HealthStatus.Healthy,
            TimeSpan.FromSeconds(30));

        Assert.True(recovered, "readiness never recovered after Redis came back");
    }

    [Fact]
    public async Task ReadinessDetectsARedisOutageInRedisMode()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(cache => cache
            .UseRedis(ConnectionString)
            .WithApplicationPrefix("chaos-health-redis")
            .WithRedis(r =>
            {
                r.ConnectTimeout = TimeSpan.FromSeconds(1);
                r.CommandTimeout = TimeSpan.FromSeconds(1);
            }));

        await using var provider = services.BuildServiceProvider();
        var readiness = new CachingHealthCheck(
            provider.GetRequiredService<ICacheProvider>(),
            provider.GetRequiredService<IOptionsMonitor<CachingOptions>>());

        Assert.Equal(HealthStatus.Healthy, (await readiness.CheckHealthAsync(HealthContext())).Status);

        await _redis.StopAsync();
        try
        {
            Assert.Equal(HealthStatus.Degraded, (await readiness.CheckHealthAsync(HealthContext())).Status);
        }
        finally
        {
            await _redis.StartAsync();
        }
    }

    private static HealthCheckContext HealthContext() => new()
    {
        Registration = new HealthCheckRegistration("caching-net", _ => null!, HealthStatus.Unhealthy, tags: null)
    };

    [Fact]
    public async Task RedisUnavailableAtStartup_StillProducesAWorkingCache()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(cache => cache
            .UseHybrid("127.0.0.1:1,abortConnect=false,connectTimeout=250", enableBackplane: false)
            .WithApplicationPrefix("chaos-cold-start")
            .WithRedis(r =>
            {
                r.ConnectTimeout = TimeSpan.FromMilliseconds(250);
                r.CommandTimeout = TimeSpan.FromMilliseconds(250);
            }));

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        // The pod must become useful even when Redis is not reachable: L1 and the factory still work.
        var value = await cache.GetOrSetAsync<int>("Order:1", async (_, _) => 7);

        Assert.Equal(7, value);
        Assert.Equal(7, await cache.GetOrDefaultAsync<int>("Order:1"));
    }

    [Fact]
    public async Task RedisOutageDuringRuntime_DegradesToTheMemoryLayerInsteadOfFailing()
    {
        await using var provider = BuildHost("chaos-outage");
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("Order:2", 1);
        Assert.Equal(1, await cache.GetOrDefaultAsync<int>("Order:2"));

        await _redis.StopAsync();
        try
        {
            // L1 still answers.
            Assert.Equal(1, await cache.GetOrDefaultAsync<int>("Order:2"));

            // A new key falls through to the factory rather than throwing.
            var fresh = await cache.GetOrSetAsync<int>("Order:3", async (_, _) => 2);
            Assert.Equal(2, fresh);
        }
        finally
        {
            await _redis.StartAsync();
        }
    }

    [Fact]
    public async Task RedisRestart_IsRecoveredWithoutRestartingTheApplication()
    {
        await using var provider = BuildHost("chaos-restart");
        await using var probe = BuildRedisProbe("chaos-restart");
        var cache = provider.GetRequiredService<ICacheService>();
        var fromRedis = probe.GetRequiredService<ICacheService>();

        await cache.SetAsync("Order:4", 1);

        // Proves up front that the probe shares the cache's key space. Without this, a prefix drift
        // would surface 30 seconds later as "the cache never resumed writing to Redis after it came
        // back", pointing the reader at reconnection instead of at the probe.
        Assert.Equal(1, (await fromRedis.TryGetAsync<int>("Order:4")).GetValueOrDefault());

        await _redis.StopAsync();
        await cache.GetOrSetAsync<int>("Order:5", async (_, _) => 2);
        await _redis.StartAsync();

        // The shared multiplexer reconnects on its own; no new connection is created per operation.
        var recovered = await WaitForAsync(
            async () =>
            {
                await cache.SetAsync("Order:6", 3);
                return (await fromRedis.TryGetAsync<int>("Order:6")).GetValueOrDefault() == 3;
            },
            TimeSpan.FromSeconds(30));

        Assert.True(recovered, "the cache never resumed writing to Redis after it came back");
    }

    [Fact]
    public async Task FailSafe_ServesStaleDataWhileTheSourceIsDown()
    {
        await using var provider = BuildHost("chaos-failsafe", cache => cache
            .WithDefaultExpiration(TimeSpan.FromMilliseconds(100))
            .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromHours(1), throttleDuration: TimeSpan.FromMinutes(5)));

        var cache = provider.GetRequiredService<ICacheService>();

        await cache.GetOrSetAsync<string>("Order:7", async (_, _) => "fresh");
        await cache.ExpireAsync("Order:7");

        var served = await cache.GetOrSetAsync<string>("Order:7", async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("upstream is down");
        });

        Assert.Equal("fresh", served);
    }

    [Fact]
    public async Task FactoryHardTimeout_FallsBackToStaleDataRatherThanHanging()
    {
        await using var provider = BuildHost("chaos-timeout", cache => cache
            .WithDefaultExpiration(TimeSpan.FromMilliseconds(100))
            .WithFailSafe(enabled: true, maxDuration: TimeSpan.FromHours(1), throttleDuration: TimeSpan.FromMinutes(5))
            .WithFactoryTimeouts(softTimeout: TimeSpan.FromMilliseconds(50), hardTimeout: TimeSpan.FromMilliseconds(200)));

        var cache = provider.GetRequiredService<ICacheService>();

        await cache.GetOrSetAsync<string>("Order:8", async (_, _) => "fresh");
        await cache.ExpireAsync("Order:8");

        var served = await cache.GetOrSetAsync<string>("Order:8", async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return "too-late";
        });

        Assert.Equal("fresh", served);
    }

    [Fact]
    public async Task BackplaneUnavailable_DoesNotBreakCacheOperations()
    {
        await using var provider = BuildHost("chaos-backplane");
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("Order:9", 1);

        await _redis.StopAsync();
        try
        {
            // Backplane publish fails in the background; the caller must not see it.
            await cache.SetAsync("Order:10", 2);
            Assert.Equal(2, await cache.GetOrDefaultAsync<int>("Order:10"));
            await cache.RemoveAsync("Order:10");
        }
        finally
        {
            await _redis.StartAsync();
        }
    }

    [Fact]
    public async Task NoLogOrRetryStorm_DuringAProlongedOutage()
    {
        var services = new ServiceCollection();
        var counter = new CountingLoggerProvider();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(counter);
        });
        services.AddCaching(cache => cache
            .UseHybrid(ConnectionString, enableBackplane: true)
            .WithApplicationPrefix("chaos-storm")
            .WithRedis(r =>
            {
                r.ConnectTimeout = TimeSpan.FromMilliseconds(250);
                r.CommandTimeout = TimeSpan.FromMilliseconds(250);
            })
            .WithResilience(r => r.DistributedCircuitBreakerDuration = TimeSpan.FromSeconds(5)));

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        await cache.SetAsync("warmup", 1);

        await _redis.StopAsync();
        try
        {
            counter.Reset();
            for (var i = 0; i < 200; i++)
            {
                await cache.GetOrSetAsync<int>($"Order:storm:{i}", async (_, _) => i);
            }

            // The circuit breaker short-circuits the distributed layer, so 200 operations produce a
            // handful of entries, not one per operation. The bound is deliberately far below 200:
            // "fewer than one per operation" would still pass at 199, which is a log storm.
            // Measured on this suite: 2.
            Assert.True(counter.Count <= 10, $"expected suppressed logging during the outage, saw {counter.Count} entries for 200 operations");
        }
        finally
        {
            await _redis.StartAsync();
        }
    }

    /// <summary>
    /// A Redis-mode cache over the same application prefix as <see cref="BuildHost"/>, used to read
    /// an entry without any chance of a local copy answering.
    /// </summary>
    /// <remarks>
    /// Bypassing L1 for one call was previously done with the engine's own per-call options.
    /// Caching.NET's overrides are additive and deliberately cannot escape a cache's mode, so the
    /// bypass is expressed the way an application would have to express it: a second cache whose
    /// mode has no memory layer, reading the same physical keys.
    /// </remarks>
    private ServiceProvider BuildRedisProbe(string prefix)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(cache => cache
            .UseRedis(ConnectionString)
            .WithApplicationPrefix(prefix)
            .WithJitter(TimeSpan.Zero)
            .WithRedis(r =>
            {
                r.ConnectTimeout = TimeSpan.FromSeconds(1);
                r.CommandTimeout = TimeSpan.FromSeconds(1);
            }));

        return services.BuildServiceProvider();
    }

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
                // Still recovering.
            }

            await Task.Delay(100);
        }

        return false;
    }

    private sealed class CountingLoggerProvider : ILoggerProvider
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public ILogger CreateLogger(string categoryName) => new CountingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CountingLogger : ILogger
        {
            private readonly CountingLoggerProvider _owner;

            public CountingLogger(CountingLoggerProvider owner)
            {
                _owner = owner;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    Interlocked.Increment(ref _owner._count);
                }
            }
        }
    }
}
