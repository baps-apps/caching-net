using System.Text.Json.Serialization;
using Caching.NET.Serialization;

namespace Caching.NET.Tests.Serialization;

public sealed partial class JsonCacheSerializerTests
{
    public sealed record SampleDto(int Id, string Name, DateTime At);

    public sealed record Unregistered(int Value);

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
    public void Deserialize_EmptyMemory_ReturnsDefault()
    {
        var sut = new JsonCacheSerializer();
        Assert.Null(sut.Deserialize<SampleDto?>(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Deserialize_NonGeneric_RoundTrips_LikeGeneric()
    {
        var sut = new JsonCacheSerializer();
        var dto = new SampleDto(7, "beta", new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc));
        var bytes = sut.Serialize(dto);

        var generic = sut.Deserialize<SampleDto>(bytes);
        var runtime = sut.Deserialize(bytes, typeof(SampleDto));

        Assert.IsType<SampleDto>(runtime);
        Assert.Equal(generic, runtime);
    }

    [Fact]
    public void Deserialize_NonGeneric_EmptyMemory_ReturnsNull()
    {
        var sut = new JsonCacheSerializer();
        Assert.Null(sut.Deserialize(ReadOnlyMemory<byte>.Empty, typeof(SampleDto)));
    }

    [Fact]
    public void Deserialize_NonGeneric_NullType_Throws()
    {
        var sut = new JsonCacheSerializer();
        Assert.Throws<ArgumentNullException>(() => sut.Deserialize("{}"u8.ToArray(), null!));
    }

    [Fact]
    public void Deserialize_NonGeneric_WithJsonContext_RegisteredType_Works()
    {
        var sut = new JsonCacheSerializer(SampleJsonContext.Default);
        var dto = new SampleDto(1, "ctx", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var bytes = sut.Serialize(dto);

        var runtime = sut.Deserialize(bytes, typeof(SampleDto));

        Assert.Equal(dto, Assert.IsType<SampleDto>(runtime));
    }

    [Fact]
    public void Deserialize_NonGeneric_WithJsonContext_UnregisteredType_MatchesGenericBehavior()
    {
        var sut = new JsonCacheSerializer(SampleJsonContext.Default);
        var bytes = "{\"Value\":5}"u8.ToArray();

        var genericThrew = false;
        try { _ = sut.Deserialize<Unregistered>(bytes); }
        catch (NotSupportedException) { genericThrew = true; }

        var runtimeThrew = false;
        try { _ = sut.Deserialize(bytes, typeof(Unregistered)); }
        catch (NotSupportedException) { runtimeThrew = true; }

        Assert.True(genericThrew);
        Assert.Equal(genericThrew, runtimeThrew);
    }

    [JsonSerializable(typeof(SampleDto))]
    internal sealed partial class SampleJsonContext : JsonSerializerContext;
}
