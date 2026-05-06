using Caching.NET.Serialization;
using FsCheck;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class SerializerRoundTripProperties
{
    public sealed class StringWrap
    {
        public string? S { get; set; }
    }

    public sealed class IntWrap
    {
        public int V { get; set; }
    }

    [Property]
    public bool Json_round_trip_preserves_string(NonNull<string> input)
    {
        var s = new JsonCacheSerializer();
        var bytes = s.Serialize(new StringWrap { S = input.Get });
        var got = s.Deserialize<StringWrap>(bytes);
        return got is not null && got.S == input.Get;
    }

    [Property]
    public bool Json_round_trip_preserves_int(int input)
    {
        var s = new JsonCacheSerializer();
        var bytes = s.Serialize(new IntWrap { V = input });
        var got = s.Deserialize<IntWrap>(bytes);
        return got is not null && got.V == input;
    }

    [Property]
    public bool MessagePack_round_trip_preserves_int(int input)
    {
        var s = new MessagePackCacheSerializer();
        var bytes = s.Serialize(new IntWrap { V = input });
        var got = s.Deserialize<IntWrap>(bytes);
        return got is not null && got.V == input;
    }

    [Property]
    public bool MessagePack_round_trip_preserves_string(NonNull<string> input)
    {
        var s = new MessagePackCacheSerializer();
        var bytes = s.Serialize(new StringWrap { S = input.Get });
        var got = s.Deserialize<StringWrap>(bytes);
        return got is not null && got.S == input.Get;
    }
}
