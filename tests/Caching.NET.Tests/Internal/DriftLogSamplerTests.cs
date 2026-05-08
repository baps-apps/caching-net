using System.Collections.Concurrent;
using System.Reflection;
using Caching.NET.Internal;

namespace Caching.NET.Tests.Internal;

public sealed class DriftLogSamplerTests
{
    [Fact]
    public void ShouldLog_SameFingerprintWithinWindow_IsSampled()
    {
        ResetState();
        Assert.True(DriftLogSampler.ShouldLog("schema_drift", "k1"));
        Assert.False(DriftLogSampler.ShouldLog("schema_drift", "k1"));
    }

    [Fact]
    public void ShouldLog_DifferentFingerprints_AreIndependent()
    {
        ResetState();
        Assert.True(DriftLogSampler.ShouldLog("schema_drift", "k1"));
        Assert.True(DriftLogSampler.ShouldLog("schema_drift", "k2"));
        Assert.True(DriftLogSampler.ShouldLog("format_drift", "k1"));
    }

    [Fact]
    public void ShouldLog_ConcurrentProducers_DoNotDeadlock()
    {
        ResetState();
        var errors = new ConcurrentQueue<Exception>();
        Parallel.For(0, 2000, i =>
        {
            try
            {
                _ = DriftLogSampler.ShouldLog("schema_drift", $"k{i % 64}");
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        Assert.Empty(errors);
    }

    private static void ResetState()
    {
        var field = typeof(DriftLogSampler).GetField("s_lastLogTicks", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var dict = Assert.IsType<ConcurrentDictionary<string, long>>(field!.GetValue(null));
        dict.Clear();
    }
}
