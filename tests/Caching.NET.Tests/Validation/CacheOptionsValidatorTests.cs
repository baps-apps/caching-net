using Caching.NET.Options;
using Caching.NET.Validation;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Validation;

public sealed class CacheOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(CacheOptions o)
        => new CacheOptionsValidator().Validate(name: null, o);

    private static CacheOptions ValidBaseline() => new()
    {
        KeyPrefix = "orders-svc:v1",
        Mode = CacheMode.InMemory,
        MaximumKeyLength = 512,
        MaximumPayloadBytes = 1_048_576,
        StripeLockCount = 1024,
        TtlJitterPercentage = 0.10,
        FactoryTimeout = TimeSpan.FromSeconds(30),
        RedisOperationTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void Valid_Baseline_Succeeds()
    {
        Assert.True(Validate(ValidBaseline()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has*wildcard")]
    [InlineData("has?wildcard")]
    [InlineData(":leading-colon")]
    public void Invalid_KeyPrefix_Fails(string bad)
    {
        var o = ValidBaseline(); o.KeyPrefix = bad;
        Assert.True(Validate(o).Failed);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("orders-svc:v1")]
    [InlineData("o.s.v1_2")]
    public void Valid_KeyPrefix_Succeeds(string ok)
    {
        var o = ValidBaseline(); o.KeyPrefix = ok;
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void RedisMode_RequiresConnectionString()
    {
        var o = ValidBaseline(); o.Mode = CacheMode.Redis; o.RedisConnectionString = null;
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void HybridMode_DoesNotRequireConnectionString()
    {
        // v2 tolerates Redis-less Hybrid (in-memory-only) for ergonomics. Strict enforcement
        // applies only to Mode=Redis.
        var o = ValidBaseline(); o.Mode = CacheMode.Hybrid; o.RedisConnectionString = null;
        Assert.True(Validate(o).Succeeded);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(8193)]
    public void Invalid_MaxKeyLength_Fails(int bad)
    {
        var o = ValidBaseline(); o.MaximumKeyLength = bad;
        Assert.True(Validate(o).Failed);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(65537)]
    public void Invalid_StripeLockCount_Fails(int bad)
    {
        var o = ValidBaseline(); o.StripeLockCount = bad;
        Assert.True(Validate(o).Failed);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(0.51)]
    public void Invalid_TtlJitter_Fails(double bad)
    {
        var o = ValidBaseline(); o.TtlJitterPercentage = bad;
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void RedisConnectionString_Redacted_InValidationFailureMessage()
    {
        var o = ValidBaseline();
        o.Mode = CacheMode.Redis;
        o.RedisConnectionString = "redis:6380,password=topsecret,ssl=True";
        o.MaximumKeyLength = 1; // force a failure so the connection string is included in the message
        var r = Validate(o);
        Assert.True(r.Failed);
        var combined = string.Join(" | ", r.Failures!);
        Assert.DoesNotContain("topsecret", combined);
    }
}
