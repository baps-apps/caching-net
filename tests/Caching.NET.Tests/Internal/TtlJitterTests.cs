using Caching.NET.Internal;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class TtlJitterTests
{
    [Fact]
    public void Apply_with_zero_percentage_returns_input()
    {
        var ttl = TimeSpan.FromMinutes(10);
        Assert.Equal(ttl, TtlJitter.Apply(ttl, 0.0));
    }

    [Fact]
    public void Apply_with_negative_percentage_returns_input()
    {
        var ttl = TimeSpan.FromMinutes(10);
        Assert.Equal(ttl, TtlJitter.Apply(ttl, -0.10));
    }

    [Fact]
    public void Apply_with_ten_percent_jitter_stays_within_bounds_over_many_calls()
    {
        var ttl = TimeSpan.FromMinutes(10);
        var lower = ttl - TimeSpan.FromMinutes(1);
        var upper = ttl + TimeSpan.FromMinutes(1);

        for (int i = 0; i < 1000; i++)
        {
            var jittered = TtlJitter.Apply(ttl, 0.10);
            Assert.InRange(jittered, lower, upper);
        }
    }

    [Fact]
    public void Apply_clamps_percentage_at_0_5()
    {
        var ttl = TimeSpan.FromSeconds(100);
        var lower = TimeSpan.FromSeconds(50);
        var upper = TimeSpan.FromSeconds(150);

        for (int i = 0; i < 1000; i++)
        {
            var jittered = TtlJitter.Apply(ttl, 1.0); // > 0.5 should clamp
            Assert.InRange(jittered, lower, upper);
        }
    }

    [Fact]
    public void Apply_returns_at_least_one_millisecond_when_jitter_would_zero_ttl()
    {
        var jittered = TtlJitter.Apply(TimeSpan.FromMilliseconds(2), 0.50);
        Assert.True(jittered >= TimeSpan.FromMilliseconds(1));
    }
}
