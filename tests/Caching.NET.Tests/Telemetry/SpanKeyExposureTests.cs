using System.Diagnostics;
using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Pins what actually reaches a tracing backend, because the two halves are not the same.
/// </summary>
/// <remarks>
/// Caching.NET's own spans carry no cache key. The engine-level sources published through
/// <see cref="CacheTelemetry.EngineActivitySourceNames"/> attach the full physical key to every
/// operation span and offer no option to suppress it, so registering them is an explicit decision
/// to export cache keys. Both facts are asserted here so neither can silently change: if a future
/// engine version stops recording the key, the second test fails and the documentation warning can
/// be removed; if Caching.NET ever starts recording one, the first test fails.
/// </remarks>
public class SpanKeyExposureTests
{
    private const string SecretBearingKey = "Order:user-4815162342";

    [Fact]
    public async Task CachingNetSpans_NeverCarryTheCacheKey()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory();

        await host.Cache().GetOrSetAsync<int>(SecretBearingKey, async _ => 1);
        await host.Cache().GetOrDefaultAsync<int>(SecretBearingKey);

        foreach (var activity in recorder.Activities)
        {
            Assert.DoesNotContain(
                activity.TagObjects,
                tag => tag.Value?.ToString()?.Contains(SecretBearingKey, StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task EngineSpans_DoCarryTheRawPhysicalKey_WhichIsWhyRegisteringThemIsOptIn()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.EngineActivitySourceNames);
        using var host = TestHost.BuildInMemory();

        await host.Cache().GetOrSetAsync<int>(SecretBearingKey, async _ => 1);

        // A MeterListener-style ActivityListener sees the whole process, so other tests running in
        // parallel contribute their own key attributes. Only this test's key is asserted on.
        var keyTags = recorder.Activities
            .SelectMany(a => a.TagObjects)
            .Where(t => string.Equals(t.Key, CacheTelemetry.EngineKeyAttributeName, StringComparison.Ordinal))
            .Select(t => t.Value?.ToString())
            .Where(value => value?.Contains(SecretBearingKey, StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(keyTags);

        // The recorded value is the physical key: the application prefix plus the caller's key.
        Assert.All(keyTags, value => Assert.Equal($"tests:{SecretBearingKey}", value));
    }

    [Fact]
    public void EngineSourcesAreNotPartOfTheBrandedSourceAlone()
    {
        Assert.Single(new[] { CacheTelemetry.ActivitySourceName });
        Assert.DoesNotContain(CacheTelemetry.EngineActivitySourceNames, n => n == CacheTelemetry.ActivitySourceName);
        Assert.Contains(CacheTelemetry.ActivitySourceNames, n => n == CacheTelemetry.ActivitySourceName);
        Assert.All(
            CacheTelemetry.EngineActivitySourceNames,
            n => Assert.Contains(CacheTelemetry.ActivitySourceNames, x => x == n));
    }

    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _activities = [];

        public SpanRecorder(params string[] sourceNames)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => sourceNames.Contains(source.Name, StringComparer.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_activities)
                    {
                        _activities.Add(activity);
                    }
                }
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Activities
        {
            get
            {
                lock (_activities)
                {
                    return _activities.ToArray();
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
