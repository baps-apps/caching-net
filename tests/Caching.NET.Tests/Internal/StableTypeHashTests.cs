// tests/Caching.NET.Tests/Internal/StableTypeHashTests.cs
using Caching.NET;
using Caching.NET.Internal;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class StableTypeHashTests
{
    [Fact]
    public void Compute_for_same_type_returns_same_hash()
    {
        Assert.Equal(StableTypeHash.Compute<string>(), StableTypeHash.Compute<string>());
    }

    [Fact]
    public void Compute_for_different_types_returns_different_hashes()
    {
        Assert.NotEqual(StableTypeHash.Compute<string>(), StableTypeHash.Compute<int>());
    }

    [Fact]
    public void Compute_uses_full_name_not_assembly_version()
    {
        var expected = StableStringHash.Compute64(typeof(string).FullName!);
        Assert.Equal(expected, StableTypeHash.Compute<string>());
    }

    [Fact]
    public void Compute_CacheSchemaAttribute_mixes_version_into_hash()
    {
        var t = typeof(SchemaAnnotated);
        var expected = StableStringHash.Compute64(string.Concat(t.FullName!, "\u001F", "v1"));
        Assert.Equal(expected, StableTypeHash.Compute<SchemaAnnotated>());
        Assert.NotEqual(StableStringHash.Compute64(t.FullName!), StableTypeHash.Compute<SchemaAnnotated>());
    }

    [CacheSchema("v1")]
    private sealed class SchemaAnnotated;

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(Dictionary<string, int>))]
    [InlineData(typeof(StableTypeHashTests))]
    public void Compute_runtime_type_matches_generic(Type type)
    {
        var generic = (ulong)typeof(StableTypeHash)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Single(m => m.Name == nameof(StableTypeHash.Compute) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(type)
            .Invoke(null, null)!;

        Assert.Equal(generic, StableTypeHash.Compute(type));
    }

    [Fact]
    public void Compute_runtime_type_honors_CacheSchemaAttribute()
    {
        Assert.Equal(StableTypeHash.Compute<SchemaAnnotated>(), StableTypeHash.Compute(typeof(SchemaAnnotated)));
    }

    [Fact]
    public void Compute_runtime_null_type_throws()
    {
        Assert.Throws<ArgumentNullException>(() => StableTypeHash.Compute(null!));
    }

    [Fact]
    public void Compute64_for_empty_string_is_deterministic()
    {
        Assert.Equal(StableStringHash.Compute64(""), StableStringHash.Compute64(""));
    }

    [Fact]
    public void Compute64_for_different_inputs_differs()
    {
        Assert.NotEqual(StableStringHash.Compute64("alpha"), StableStringHash.Compute64("beta"));
    }
}
