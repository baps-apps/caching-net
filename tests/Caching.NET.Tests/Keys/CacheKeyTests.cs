using Caching.NET.Keys;

// Deliberately not "Caching.NET.Tests.Keys": that name would shadow Caching.NET.Keys for every
// other test in the assembly that qualifies it as "Keys.".
namespace Caching.NET.Tests.KeyBuilding;

/// <summary>
/// The type segment has to distinguish closed generics: two different closed types sharing one key
/// is a type-confusion bug, not merely a collision — the second reader deserializes the first
/// writer's payload into the wrong shape.
/// </summary>
public class CacheKeyTests
{
    [Fact]
    public void PlainType_UsesItsSimpleName()
        => Assert.Equal("Order:1", CacheKey.For<Order>(1).Build());

    [Fact]
    public void ClosedGenericsOverTheSameOpenType_DoNotShareAKey()
    {
        var ints = CacheKey.For<List<int>>("1").Build();
        var strings = CacheKey.For<List<string>>("1").Build();

        Assert.NotEqual(ints, strings);
        Assert.Equal("List_Int32:1", ints);
        Assert.Equal("List_String:1", strings);
    }

    [Fact]
    public void NestedGenerics_AreFullyExpandedInTheSegment()
        => Assert.Equal(
            "Dictionary_String_List_Int32:1",
            CacheKey.For<Dictionary<string, List<int>>>("1").Build());

    [Fact]
    public void TypeSegment_NeverContainsABacktickOrAnExtraSeparator()
    {
        var key = CacheKey.For<Dictionary<string, List<Order>>>("1").Build();

        Assert.DoesNotContain('`', key);
        Assert.Equal(1, key.Count(c => c == ':'));
    }

    [Fact]
    public void IdIsFormattedInvariantly()
        => Assert.Equal("Order:1.5", CacheKey.For<Order>(1.5m).Build());

    private sealed record Order(int Id);
}
