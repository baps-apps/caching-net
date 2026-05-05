using Caching.NET.Serialization;

namespace Caching.NET.Tests.Serialization;

public sealed class JsonCacheSerializerTests
{
    public sealed record SampleDto(int Id, string Name, DateTime At);

    [Fact]
    public void FormatId_IsJson()
    {
        Assert.Equal("json", new JsonCacheSerializer().FormatId);
    }

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        var sut = new JsonCacheSerializer();
        var dto = new SampleDto(42, "alpha", new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        var bytes = sut.Serialize(dto);
        var actual = sut.Deserialize<SampleDto>(bytes);
        Assert.Equal(dto, actual);
    }

    [Fact]
    public void RoundTrip_NullValue_ReturnsNull()
    {
        var sut = new JsonCacheSerializer();
        var bytes = sut.Serialize<SampleDto?>(null);
        var actual = sut.Deserialize<SampleDto?>(bytes);
        Assert.Null(actual);
    }

    [Fact]
    public void Deserialize_EmptySpan_ReturnsDefault()
    {
        var sut = new JsonCacheSerializer();
        Assert.Null(sut.Deserialize<SampleDto?>(ReadOnlySpan<byte>.Empty));
    }
}
