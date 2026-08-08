using System.Diagnostics.Metrics;
using Caching.NET.Extensions;
using Caching.NET.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.Redis;

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
    private RedisContainer _redis = null!;
    private int _hostPort;

    public async Task InitializeAsync() => (_redis, _hostPort) = await ChaosRedis.StartAsync();

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    [Fact]
    public async Task BackplanePublish_IncrementsBackgroundOperations()
    {
        const string CacheName = "instr-backplane-publish";
        using var collector = new InstrumentCollector(CacheName);
        await using var provider = BuildHost(CacheName);

        await provider.GetRequiredKeyedService<ICacheService>(CacheName).SetAsync("Order:1", 1);

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
        var cache = provider.GetRequiredKeyedService<ICacheService>(CacheName);

        await cache.SetAsync("Order:1", 1);

        await _redis.StopAsync();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await cache.GetOrSetAsync<int>($"Order:err:{i}", async (_, _) => i);
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
        var cache = provider.GetRequiredKeyedService<ICacheService>(CacheName);

        // Building the cache subscribes the backplane; the operation forces the lazy connect.
        await cache.GetOrSetAsync<int>("Order:1", async (_, _) => 1);

        Assert.True(
            await collector.WaitForAsync(c => c.Count("caching.net.backplane.errors") > 0, 20_000),
            $"a backplane subscribe failure must increment caching.net.backplane.errors. Saw: {collector.Describe()}");
    }

    /// <summary>
    /// Documents a real limit of <c>caching.net.backplane.errors</c> rather than pretending it covers
    /// a Redis outage's prevented publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One multiplexer is shared by the distributed layer and the backplane, so when Redis goes away
    /// the L2 write fails and the engine does not go on to publish an invalidation for a write that
    /// did not land. An alert built on this counter therefore will not fire for the publishes a Redis
    /// outage prevents — that is what <c>caching.net.redis.errors</c> is for.
    /// </para>
    /// <para>
    /// <b>The distributed write is awaited here on purpose, and that is the whole point of the
    /// test.</b> Two earlier versions of this assertion were load-dependent and both flaked: a flat
    /// <c>Assert.Equal(0, ...)</c> roughly once in twelve full-solution runs, and its replacement —
    /// which allowed only <c>CircuitBreakerOpen</c> as an error type — once as well, on a
    /// <c>RedisConnectionException</c> observed reaching the counter during the outage. The cause is
    /// not the instrument but the configuration under test: with
    /// <c>AllowBackgroundDistributedOperations</c> left at its production default of <c>true</c>, the
    /// L2 write and the publish are dispatched as background work, so "the write failed, therefore
    /// nothing was published" stops being an ordering guarantee and becomes a race — the L2 write can
    /// still land while the socket is dying, and the publish that follows it then fails for real.
    /// Awaiting the write makes the ordering the claim depends on actually hold, which turns a
    /// statement that was true most of the time into one that is true every time. The default's weaker
    /// behaviour is stated in <c>docs/TELEMETRY.md</c> instead of asserted here, because an absolute
    /// negative is not what the default configuration promises.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RedisOutage_AttemptsNoBackplanePublish_WhenTheDistributedWriteFailsFirst()
    {
        const string CacheName = "instr-backplane-outage";
        using var collector = new InstrumentCollector(CacheName);
        await using var provider = BuildHost(CacheName, awaitDistributedWrites: true);
        var cache = provider.GetRequiredKeyedService<ICacheService>(CacheName);

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

            // Asserted on the operation, not on a count or an error type: a failed publish is
            // recorded as backplane_publish on caching.net.background.operations by
            // InstrumentedBackplane, so its absence is exactly the claim — and the backplane's own
            // circuit-breaker transitions, which the same outage may legitimately cause, are not
            // publish attempts and are correctly not counted as one.
            Assert.DoesNotContain("backplane_publish", collector.FailedBackgroundOperations());
        }
        finally
        {
            await _redis.StartAsync();
        }
    }

    /// <param name="cacheName">Cache name, which is also the telemetry filter for this test's collector.</param>
    /// <param name="awaitDistributedWrites">
    /// Completes distributed writes on the caller's path instead of in the background. Only set by a
    /// test whose claim depends on the L2 write's outcome being known before the backplane publish
    /// decision — see
    /// <see cref="RedisOutage_AttemptsNoBackplanePublish_WhenTheDistributedWriteFailsFirst"/>. Every
    /// other test here runs the production default.
    /// </param>
    private ServiceProvider BuildHost(string cacheName, bool awaitDistributedWrites = false)
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

                if (awaitDistributedWrites)
                {
                    r.AllowBackgroundDistributedOperations = false;
                    r.AllowBackgroundBackplaneOperations = false;
                }
            }));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Counts Caching.NET measurements for one cache name. The cache-name filter is what makes this
    /// safe next to other suites: a meter listener sees every measurement in the process.
    /// </summary>
    private sealed class InstrumentCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _errorTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failedBackgroundOperations = new(StringComparer.Ordinal);
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
                var belongsToThisCache = false;
                string? errorType = null;
                string? operation = null;
                string? result = null;

                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, CacheTelemetryAttributes.Name, StringComparison.Ordinal)
                        && string.Equals(tag.Value?.ToString(), _cacheName, StringComparison.Ordinal))
                    {
                        belongsToThisCache = true;
                    }
                    else if (string.Equals(tag.Key, CacheTelemetryAttributes.ErrorType, StringComparison.Ordinal))
                    {
                        errorType = tag.Value?.ToString();
                    }
                    else if (string.Equals(tag.Key, CacheTelemetryAttributes.Operation, StringComparison.Ordinal))
                    {
                        operation = tag.Value?.ToString();
                    }
                    else if (string.Equals(tag.Key, CacheTelemetryAttributes.Result, StringComparison.Ordinal))
                    {
                        result = tag.Value?.ToString();
                    }
                }

                if (!belongsToThisCache)
                {
                    return;
                }

                lock (_gate)
                {
                    _counts.TryGetValue(instrument.Name, out var current);
                    _counts[instrument.Name] = current + value;

                    if (errorType is not null)
                    {
                        if (!_errorTypes.TryGetValue(instrument.Name, out var types))
                        {
                            types = new HashSet<string>(StringComparer.Ordinal);
                            _errorTypes[instrument.Name] = types;
                        }

                        types.Add(errorType);
                    }

                    if (instrument.Name == "caching.net.background.operations"
                        && operation is not null
                        && string.Equals(result, CacheResults.Error, StringComparison.Ordinal))
                    {
                        _failedBackgroundOperations.Add(operation);
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

        /// <summary>
        /// The distinct <c>cache.operation</c> values recorded on
        /// <c>caching.net.background.operations</c> with <c>cache.result=error</c> — that is, which
        /// background operations actually failed, rather than merely how many did.
        /// </summary>
        public string[] FailedBackgroundOperations()
        {
            lock (_gate)
            {
                return [.. _failedBackgroundOperations];
            }
        }

        /// <summary>The distinct <c>cache.error.type</c> values seen on an instrument.</summary>
        public string[] ErrorTypes(string instrumentName)
        {
            lock (_gate)
            {
                return _errorTypes.TryGetValue(instrumentName, out var types) ? [.. types] : [];
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
