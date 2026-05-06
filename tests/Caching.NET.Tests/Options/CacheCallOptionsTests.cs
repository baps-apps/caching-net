using Caching.NET.Options;
using Xunit;

namespace Caching.NET.Tests.Options;

public class CacheCallOptionsTests
{
    [Fact]
    public void Defaults_match_spec()
    {
        var o = new CacheCallOptions();
        Assert.Null(o.Mode);
        Assert.False(o.BypassCache);
        Assert.False(o.ForceRefresh);
        Assert.True(o.CoalesceConcurrent);
        Assert.Null(o.FactoryTimeout);
        Assert.Null(o.AbsoluteExpiration);
        Assert.Null(o.SlidingExpiration);
        Assert.Null(o.AllowStaleFor);
        Assert.Null(o.Tags);
        Assert.Null(o.JitterPercentage);
    }

    [Fact]
    public void All_properties_round_trip_via_init()
    {
        var tags = new[] { "tenant:a", "kind:order" };
        var o = new CacheCallOptions
        {
            Mode = CacheMode.Redis,
            BypassCache = true,
            ForceRefresh = true,
            CoalesceConcurrent = false,
            FactoryTimeout = TimeSpan.FromSeconds(5),
            AbsoluteExpiration = TimeSpan.FromMinutes(10),
            SlidingExpiration = TimeSpan.FromMinutes(2),
            AllowStaleFor = TimeSpan.FromMinutes(1),
            Tags = tags,
            JitterPercentage = 0.05,
        };

        Assert.Equal(CacheMode.Redis, o.Mode);
        Assert.True(o.BypassCache);
        Assert.True(o.ForceRefresh);
        Assert.False(o.CoalesceConcurrent);
        Assert.Equal(TimeSpan.FromSeconds(5), o.FactoryTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), o.AbsoluteExpiration);
        Assert.Equal(TimeSpan.FromMinutes(2), o.SlidingExpiration);
        Assert.Equal(TimeSpan.FromMinutes(1), o.AllowStaleFor);
        Assert.Equal(tags, o.Tags);
        Assert.Equal(0.05, o.JitterPercentage);
    }
}
