using Caching.NET.Internal;

namespace Caching.NET.Tests.Internal;

public sealed class RedisConnectionStringRedactorTests
{
    [Fact]
    public void Redact_StripsPasswordSegment()
    {
        const string cs = "redis.example.com:6380,password=Sup3rS3cr3t,ssl=True";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.DoesNotContain("Sup3rS3cr3t", actual);
        Assert.Contains("password=***", actual);
    }

    [Fact]
    public void Redact_StripsUserSegment()
    {
        const string cs = "redis.example.com:6380,user=admin,password=p,ssl=True";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.DoesNotContain("admin", actual);
        Assert.Contains("user=***", actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, RedisConnectionStringRedactor.Redact(input));
    }

    [Fact]
    public void Redact_KeepsNonSecretSegments()
    {
        const string cs = "redis.example.com:6380,password=p,ssl=True,abortConnect=false,connectTimeout=5000";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.Contains("redis.example.com:6380", actual);
        Assert.Contains("ssl=True", actual);
        Assert.Contains("abortConnect=false", actual);
        Assert.Contains("connectTimeout=5000", actual);
    }

    [Fact]
    public void Redact_CaseInsensitiveKeyMatch()
    {
        const string cs = "host:6380,Password=p,SSL=True,USER=u";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.Contains("Password=***", actual);
        Assert.Contains("USER=***", actual);
    }
}
