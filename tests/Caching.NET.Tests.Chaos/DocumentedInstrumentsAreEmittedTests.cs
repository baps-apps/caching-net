using System.Diagnostics.Metrics;
using Caching.NET.Extensions;
using Caching.NET.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Chaos;

/// <summary>
/// Proves the instruments that only fire on a failure or background path are real.
/// </summary>
/// <remarks>
/// <c>caching.net.redis.errors</c>, <c>caching.net.backplane.errors</c> and
/// <c>caching.net.background.operations</c> are documented in <c>docs/TELEMETRY.md</c> and are the
/// instruments an operator actually alerts on, but nothing emits them on a healthy path — so
/// without a test they could be silently dead and no other suite would notice. Each one is asserted
/// here against a real outage rather than a mock, and filtered to this test's own cache name so a
/// process-wide <see cref="MeterListener"/> cannot pick up another suite's measurements.
/// </remarks>
public class DocumentedInstrumentsAreEmittedTests : IAsyncLifetime
{
    private readonly int _hostPort = FindFreeTcpPort();
    private readonly RedisContainer _redis;

    public DocumentedInstrumentsAreEmittedTests()
    {
        _redis = new RedisBuilder("redis:7.4-alpine")
            .WithPortBinding(_hostPort, 6379)
            .Build();
    }

    public async Task InitializeAsync() => await _redis.StartAsync();

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    [Fact]
    public async Task BackplanePublish_IncrementsBackgroundOperations()
    {
        const string CacheName = "instr-backplane-publish";
        using var collector = new InstrumentCollector(CacheName);
        await using var provider = BuildHost(CacheName);

        await provider.GetRequiredKeyedService<IFusionCache>(CacheName).SetAsync("Order:1", 1);

        Assert.True(
            await collector.WaitForAsync(c => c.Count("caching.net.background.operations") > 0),
            "a backplane publish must be counted as a background operation");
    }

    [Fact]
    public async Task RedisOutage_IncrementsRedisErrors()
    {
        const string CacheName = "instr-redis-errors";
        using var collector = new InstrumentCollector(CacheName);
        await using var provider = BuildHost(CacheName);
        var cache = provider.GetRequiredKeyedService<IFusionCache>(CacheName);

        await cache.SetAsync("Order:1", 1);

        await _redis.StopAsync();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await cache.GetOrSetAsync<int>($"Order:err:{i}", async _ => i);
            }

            Assert.True(
                await collector.WaitForAsync(c => c.Count("caching.net.redis.errors") > 0),
                "a distributed-layer failure must increment caching.net.redis.errors");

            // Every redis error is also counted in the aggregate errors instrument, tagged by layer.
            Assert.True(
                await collector.WaitForAsync(c => c.Count("caching.net.errors") > 0),
                "distributed-layer failures must also reach the aggregate error counter");
        }
        finally
        {
            await _redis.StartAsync();
        }
    }

    [Fact]
    public async Task BackplaneSubscribeFailure_IncrementsBackplaneErrors()
    {
        // The reachable backplane failure. Subscribe runs when the cache is built, against a Redis
        // that is not there, so it fails on its own rather than behind the distributed layer.
        const string CacheName = "instr-backplane-subscribe";
        using var collector = new InstrumentCollector(CacheName);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(CacheName, cache => cache
            .UseHybrid("127.0.0.1:1,abortConnect=false,connectTimeout=250", enableBackplane: true)
            .WithApplicationPrefix(CacheName)
            .WithRedis(r =>
            {
                r.ConnectTimeout = TimeSpan.FromMilliseconds(250);
                r.CommandTimeout = TimeSpan.FromMilliseconds(250);
            }));

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredKeyedService<IFusionCache>(CacheName);

        // Building the cache subscribes the backplane; the operation forces the lazy connect.
        await cache.GetOrSetAsync<int>("Order:1", async _ => 1);

        Assert.True(
            await collector.WaitForAsync(c => c.Count("caching.net.backplane.errors") > 0, 20_000),
            $"a backplane subscribe failure must increment caching.net.backplane.errors. Saw: {collector.Describe()}");
    }

    [Fact]
    public async Task RedisOutage_DoesNotProduceBackplaneErrors_BecauseNoPublishIsAttempted()
    {
        // Documents a real limit of the instrument rather than pretending it covers this case.
        // Caching.NET shares one multiplexer between L2 and the backplane, so "Redis is down" makes
        // the distributed write fail first and the backplane publish is never attempted: 298 writes
        // across a 30-second outage produced zero backplane measurements. An alert built on
        // caching.net.backplane.errors will therefore not fire for a Redis outage — that is what
        // caching.net.redis.errors is for.
        const string CacheName = "instr-backplane-outage";
        using var collector = new InstrumentCollector(CacheName);
        await using var provider = BuildHost(CacheName);
        var cache = provider.GetRequiredKeyedService<IFusionCache>(CacheName);

        await cache.SetAsync("Order:1", 1);

        await _redis.StopAsync();
        try
        {
            for (var i = 0; i < 40; i++)
            {
                await cache.SetAsync($"Order:bp:{i}", i);
                await cache.RemoveAsync($"Order:bp:{i}");
            }

            Assert.True(
                await collector.WaitForAsync(c => c.Count("caching.net.redis.errors") > 0),
                $"the outage must surface on the distributed-layer counter. Saw: {collector.Describe()}");

            Assert.Equal(0, collector.Count("caching.net.backplane.errors"));
        }
        finally
        {
            await _redis.StartAsync();
        }
    }

    private ServiceProvider BuildHost(string cacheName)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(cacheName, cache => cache
            .UseHybrid($"127.0.0.1:{_hostPort},abortConnect=false,connectTimeout=1000", enableBackplane: true)
            .WithApplicationPrefix(cacheName)
            .WithJitter(TimeSpan.Zero)
            .WithRedis(r =>
            {
                r.ConnectTimeout = TimeSpan.FromMilliseconds(500);
                r.CommandTimeout = TimeSpan.FromMilliseconds(500);
            })
            .WithResilience(r =>
            {
                r.DistributedCircuitBreakerDuration = TimeSpan.FromMilliseconds(200);
                r.BackplaneCircuitBreakerDuration = TimeSpan.FromMilliseconds(200);
            }));

        return services.BuildServiceProvider();
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Counts Caching.NET measurements for one cache name. The cache-name filter is what makes this
    /// safe next to other suites: a meter listener sees every measurement in the process.
    /// </summary>
    private sealed class InstrumentCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly string _cacheName;

        public InstrumentCollector(string cacheName)
        {
            _cacheName = cacheName;

            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CacheTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, CacheTelemetryAttributes.Name, StringComparison.Ordinal)
                        && string.Equals(tag.Value?.ToString(), _cacheName, StringComparison.Ordinal))
                    {
                        lock (_gate)
                        {
                            _counts.TryGetValue(instrument.Name, out var current);
                            _counts[instrument.Name] = current + value;
                        }

                        return;
                    }
                }
            });

            _listener.Start();
        }

        public string Describe()
        {
            lock (_gate)
            {
                return string.Join(", ", _counts.Select(kv => $"{kv.Key}={kv.Value}"));
            }
        }

        public long Count(string instrumentName)
        {
            lock (_gate)
            {
                return _counts.TryGetValue(instrumentName, out var value) ? value : 0;
            }
        }

        public async Task<bool> WaitForAsync(Func<InstrumentCollector, bool> condition, int timeoutMilliseconds = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (condition(this))
                {
                    return true;
                }

                await Task.Delay(25);
            }

            return condition(this);
        }

        public void Dispose() => _listener.Dispose();
    }
}
