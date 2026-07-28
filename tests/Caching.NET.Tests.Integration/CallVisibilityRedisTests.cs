using System.Diagnostics;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Telemetry;
using Caching.NET.Tests.Integration.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// End-to-end coverage of the per-call telemetry (Tasks 1-7) against a real Redis instance, so Redis
/// and Hybrid modes are exercised for real rather than against fakes.
/// </summary>
[Collection("Redis")]
public class CallVisibilityRedisTests
{
    private readonly RedisContainerFixture _redis;

    public CallVisibilityRedisTests(RedisContainerFixture redis) => _redis = redis;

    /// <summary>
    /// Captures Caching.NET activities within a test-scoped trace. <see cref="CacheInstruments.Activity"/>
    /// and <see cref="CacheInstruments.Meter"/> are process-wide statics, so a per-test DI container does not
    /// isolate captured telemetry, and every test class in this assembly shares the "Redis" collection. This
    /// helper starts a dedicated parent Activity on a private, per-call <see cref="ActivitySource"/> before
    /// any cache call runs; because .NET flows the ambient <see cref="Activity.Current"/> through async
    /// continuations, every span the cache emits underneath becomes a child sharing the parent's random
    /// TraceId. Filtering captured spans by that TraceId isolates exactly this call, immune to whatever else
    /// is running concurrently in the process.
    /// <para>
    /// The unit-test project (Caching.NET.Tests) instead filters by <c>cache.key_hash</c>, computed via the
    /// internal <c>Caching.NET.Internal.StableStringHash</c> helper -- but <c>Caching.NET.csproj</c> only
    /// grants <c>InternalsVisibleTo</c> to Caching.NET.Tests/.Chaos/.Properties/.Benchmark, not to this
    /// Integration project, and this task may not change production code. TraceId scoping needs no internal
    /// access, so it is used here instead of the key-hash mechanism.
    /// </para>
    /// </summary>
    private static (List<Activity> activities, ActivityListener listener, Activity parent, ActivitySource source) CaptureWithTraceScope()
    {
        var testSource = new ActivitySource($"integration-test-scope-{Guid.NewGuid():N}");
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CacheInstruments.ActivitySourceName || source.Name == testSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (captured) captured.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        var parent = testSource.StartActivity("test-scope")
            ?? throw new InvalidOperationException("Failed to start parent activity for trace scope");
        return (captured, listener, parent, testSource);
    }

    private ICacheService BuildCache(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = mode,
            ["CacheOptions:KeyPrefix"] = "vis",
            ["CacheOptions:IncludeKeyHashInTraces"] = "true",
            ["CacheOptions:RedisConnectionString"] = _redis.ConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        return services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    [Theory]
    [InlineData("Redis")]
    [InlineData("Hybrid")]
    public async Task Miss_then_hit_reports_source_then_cache_end_to_end(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vis:{Guid.NewGuid():N}";
        var (activities, listener, parent, source) = CaptureWithTraceScope();
        using var listenerScope = listener;
        using var sourceScope = source;
        var traceId = parent.TraceId;

        try
        {
            var first = await cache.GetOrCreateAsync(key, async ct =>
            {
                await Task.Delay(120, ct);
                return "v";
            });
            Assert.Equal("v", first);

            var second = await cache.GetOrCreateAsync(key, _ => Task.FromResult("other"));
            Assert.Equal("v", second);
        }
        finally
        {
            parent.Dispose();
        }

        var spans = activities
            .Where(a => a.TraceId == traceId && a.GetTagItem("cache.operation")?.ToString() == "get_or_create")
            .OrderBy(a => a.StartTimeUtc)
            .ToList();
        Assert.Equal(2, spans.Count);

        Assert.Equal("source", spans[0].GetTagItem("cache.served_from")?.ToString());
        Assert.Equal(mode, spans[0].GetTagItem("cache.mode")?.ToString());
        var factoryMs = double.Parse(spans[0].GetTagItem("cache.factory_ms")!.ToString()!);
        Assert.True(factoryMs >= 100, $"factory_ms should reflect the 120ms delay, got {factoryMs}");
        // The source call's total must cover the factory plus the cache work around it.
        Assert.True(spans[0].Duration.TotalMilliseconds >= factoryMs);

        Assert.Equal("cache", spans[1].GetTagItem("cache.served_from")?.ToString());
        Assert.Equal(mode, spans[1].GetTagItem("cache.mode")?.ToString());
        Assert.Null(spans[1].GetTagItem("cache.factory_ms"));
    }

    /// <summary>
    /// Presence for a value-type read cannot come from a null check on the returned value — a missing
    /// <c>int</c> and a cached <c>0</c> are both <c>0</c>. The routing layer reads through the internal
    /// presence-aware probe instead; this pins that both real backends implement it correctly, including
    /// the case where the cached value *is* <c>default(T)</c>. Round-trip correctness for value types
    /// across all three modes lives in <see cref="HybridValueTypeTests"/>.
    /// </summary>
    [Theory]
    [InlineData("Redis")]
    [InlineData("Hybrid")]
    public async Task Value_type_reads_report_presence_end_to_end(string mode)
    {
        var cache = BuildCache(mode);
        var missingKey = $"vis:{Guid.NewGuid():N}";
        var zeroKey = $"vis:{Guid.NewGuid():N}";
        await cache.SetAsync(zeroKey, 0);

        var (activities, listener, parent, source) = CaptureWithTraceScope();
        using var listenerScope = listener;
        using var sourceScope = source;
        var traceId = parent.TraceId;

        try
        {
            Assert.Equal(0, await cache.GetAsync<int>(missingKey));
            Assert.Equal(0, await cache.GetAsync<int>(zeroKey));
        }
        finally
        {
            parent.Dispose();
        }

        var spans = activities
            .Where(a => a.TraceId == traceId && a.GetTagItem("cache.operation")?.ToString() == "get")
            .OrderBy(a => a.StartTimeUtc)
            .ToList();
        Assert.Equal(2, spans.Count);
        Assert.Equal("none", spans[0].GetTagItem("cache.served_from")?.ToString());
        Assert.Equal("cache", spans[1].GetTagItem("cache.served_from")?.ToString());
    }
}
