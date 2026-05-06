using Caching.NET.Keys;
using Xunit;

namespace Caching.NET.Tests.Keys;

public class CacheKeyBuilderTests
{
    private sealed class Order { }

    [Fact]
    public void For_with_id_builds_TypeName_id()
    {
        var key = CacheKey.For<Order>(12345).Build();
        Assert.Equal("Order:12345", key);
    }

    [Fact]
    public void WithVariant_appends_variant_segment()
    {
        var key = CacheKey.For<Order>(12345).WithVariant("v2").Build();
        Assert.Equal("Order:12345:v2", key);
    }

    [Fact]
    public void WithSegment_appends_arbitrary_segment()
    {
        var key = CacheKey.For<Order>(12345).WithSegment("region-eu").Build();
        Assert.Equal("Order:12345:region-eu", key);
    }

    [Fact]
    public void Multiple_segments_chain()
    {
        var key = CacheKey.For<Order>(12345).WithVariant("v2").WithSegment("eu").Build();
        Assert.Equal("Order:12345:v2:eu", key);
    }

    [Fact]
    public void Build_throws_when_id_contains_colon_or_whitespace()
    {
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>("bad:id").Build());
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>("bad id").Build());
    }

    [Fact]
    public void Build_throws_when_segment_contains_colon_or_whitespace()
    {
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>(1).WithSegment("a b").Build());
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>(1).WithSegment("a:b").Build());
    }

    [Fact]
    public void Build_throws_when_total_length_exceeds_256()
    {
        var huge = new string('x', 260);
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>(huge).Build());
    }
}
