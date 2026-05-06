// tests/Caching.NET.Tests/Internal/StableTypeHashTests.cs
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
    public void Compute_uses_assembly_qualified_name()
    {
        // The goal is stability across runs — assembly-qualified name is the input.
        var expected = StableStringHash.Compute64(typeof(string).AssemblyQualifiedName!);
        Assert.Equal(expected, StableTypeHash.Compute<string>());
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
