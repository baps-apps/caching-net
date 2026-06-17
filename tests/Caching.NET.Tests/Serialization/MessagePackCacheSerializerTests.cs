using Caching.NET.Serialization;
using MessagePack;
using Xunit;

namespace Caching.NET.Tests.Serialization;

public class MessagePackCacheSerializerTests
{
    [MessagePackObject]
    public sealed class Dto
    {
        [Key(0)] public int Id { get; set; }
        [Key(1)] public string? Name { get; set; }
    }

    [Fact]
    public void FormatId_is_msgpack()
    {
        var s = new MessagePackCacheSerializer();
        Assert.Equal("msgpack", s.FormatId);
    }

    [Fact]
    public void Round_trip_preserves_value()
    {
        var s = new MessagePackCacheSerializer();
        var input = new Dto { Id = 42, Name = "hello" };

        var bytes = s.Serialize(input);
        var output = s.Deserialize<Dto>(bytes);

        Assert.NotNull(output);
        Assert.Equal(42, output!.Id);
        Assert.Equal("hello", output.Name);
    }

    [Fact]
    public void Deserialize_non_generic_round_trips_like_generic()
    {
        var s = new MessagePackCacheSerializer();
        var input = new Dto { Id = 9, Name = "rt" };
        var bytes = s.Serialize(input);

        var runtime = s.Deserialize(bytes, typeof(Dto));

        var typed = Assert.IsType<Dto>(runtime);
        Assert.Equal(9, typed.Id);
        Assert.Equal("rt", typed.Name);
    }

    [Fact]
    public void Deserialize_non_generic_empty_returns_null()
    {
        var s = new MessagePackCacheSerializer();
        Assert.Null(s.Deserialize(ReadOnlyMemory<byte>.Empty, typeof(Dto)));
    }

    [Fact]
    public void Deserialize_non_generic_null_type_throws()
    {
        var s = new MessagePackCacheSerializer();
        Assert.Throws<ArgumentNullException>(() => s.Deserialize(new byte[] { 0x00 }, null!));
    }

    [Fact]
    public void Deserialize_of_garbage_bytes_returns_null_or_throws_MessagePackException()
    {
        var s = new MessagePackCacheSerializer();
        try
        {
            var v = s.Deserialize<Dto>(new byte[] { 0xFF, 0xFE, 0xFD });
            // Some inputs decode to null; that's acceptable.
            Assert.Null(v);
        }
        catch (MessagePackSerializationException) { /* also acceptable */ }
    }
}
