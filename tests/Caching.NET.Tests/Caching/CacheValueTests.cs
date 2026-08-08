namespace Caching.NET.Tests.Caching;

public class CacheValueTests
{
    [Fact]
    public void None_HasNoValue()
    {
        var value = CacheValue<int>.None;

        Assert.False(value.HasValue);
        Assert.Equal(0, value.GetValueOrDefault());
        Assert.Equal(-1, value.GetValueOrDefault(-1));
    }

    [Fact]
    public void Of_CarriesTheValue()
    {
        var value = CacheValue<string>.Of("hello");

        Assert.True(value.HasValue);
        Assert.Equal("hello", value.Value);
        Assert.Equal("hello", value.GetValueOrDefault("fallback"));
    }

    [Fact]
    public void Value_OnEmpty_Throws()
    {
        var value = CacheValue<string>.None;

        Assert.Throws<InvalidOperationException>(() => value.Value);
    }

    [Fact]
    public void Of_Null_StillHasValue()
    {
        // A cached null is a hit, not a miss: the distinction is the whole point of the type.
        var value = CacheValue<string?>.Of(null);

        Assert.True(value.HasValue);
        Assert.Null(value.Value);
    }

    [Fact]
    public void Deconstruct_ExposesBothParts()
    {
        var (hasValue, value) = CacheValue<int>.Of(7);

        Assert.True(hasValue);
        Assert.Equal(7, value);
    }

    [Fact]
    public void Default_IsNone()
    {
        CacheValue<int> value = default;

        Assert.False(value.HasValue);
    }
}
