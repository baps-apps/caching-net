using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Pins what reaches a tracing backend. Caching.NET emits every cache span itself, so the raw key is
/// present only when the application asks for it.
/// </summary>
// See the remark on OperationSpanTests: the activity source is process-wide, so this shares the
// caching-net-metrics collection with the other classes that emit real "cache.*" spans.
[Collection(MetricsCollection.Name)]
public class SpanKeyExposureTests
{
    private const string SecretBearingKey = "Order:user-4815162342";

    [Fact]
    public async Task ByDefault_SpansCarryAFingerprintAndNeverTheKey()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory();

        await host.Cache().GetOrSetAsync<int>(SecretBearingKey, (_, _) => Task.FromResult(1));

        var spans = recorder.Activities.Where(a => a.OperationName.StartsWith("cache.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(spans);

        foreach (var span in spans)
        {
            Assert.DoesNotContain(
                span.TagObjects,
                tag => tag.Value?.ToString()?.Contains(SecretBearingKey, StringComparison.Ordinal) == true);
        }

        Assert.Contains(spans, s => s.GetTagItem(CacheTelemetryAttributes.KeyFingerprint) is not null);
    }

    [Fact]
    public async Task WhenOptedIn_SpansCarryTheCallerKey()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory(c => c.WithSecurity(s => s.AllowRawKeysInTelemetry = true));

        await host.Cache().GetOrSetAsync<int>(SecretBearingKey, (_, _) => Task.FromResult(1));

        var keys = recorder.Activities
            .Select(a => a.GetTagItem(CacheTelemetryAttributes.Key)?.ToString())
            .Where(v => v is not null)
            .ToArray();

        Assert.Contains(SecretBearingKey, keys);
        // The caller's key, not the prefixed physical key.
        Assert.DoesNotContain(keys, k => k!.StartsWith("tests:", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheBrandedSourceIsPublished()
    {
        Assert.Equal([CacheTelemetry.ActivitySourceName], CacheTelemetry.ActivitySourceNames);
        Assert.Equal([CacheTelemetry.MeterName], CacheTelemetry.MeterNames);
    }
}
