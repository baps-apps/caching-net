using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Tests.Internal;

public class CacheGuardTests
{
    private static CacheGuard Build(Action<CachingOptions>? configure = null)
    {
        var options = new CachingOptions
        {
            CacheName = "default",
            ApplicationPrefix = "app"
        };
        configure?.Invoke(options);
        return new CacheGuard(options, new CacheTelemetryContext(options), NullLogger.Instance);
    }

    [Fact]
    public void KeyWithinLimit_IsAccepted() => Build().ValidateKey("Product:1");

    [Fact]
    public void OverlongKey_ThrowsByDefault()
    {
        var guard = Build(o => o.Security.MaximumKeyLength = 20);

        var ex = Assert.Throws<ArgumentException>(() => guard.ValidateKey(new string('k', 100)));

        Assert.Contains("MaximumKeyLength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyLimit_AccountsForThePrefix()
    {
        // Prefix "app:" is 4 characters, so a 17-character key breaks a 20-character limit.
        var guard = Build(o => o.Security.MaximumKeyLength = 20);

        guard.ValidateKey(new string('k', 16));
        Assert.Throws<ArgumentException>(() => guard.ValidateKey(new string('k', 17)));
    }

    [Fact]
    public void WarnPolicy_DoesNotThrow()
    {
        var guard = Build(o =>
        {
            o.Security.MaximumKeyLength = 5;
            o.Security.KeyLengthPolicy = CacheGuardPolicy.Warn;
        });

        guard.ValidateKey(new string('k', 100));
    }

    [Fact]
    public void IgnorePolicy_DoesNotThrow()
    {
        var guard = Build(o =>
        {
            o.Security.MaximumKeyLength = 5;
            o.Security.KeyLengthPolicy = CacheGuardPolicy.Ignore;
        });

        guard.ValidateKey(new string('k', 100));
    }

    [Fact]
    public void EmptyKey_IsRejected()
    {
        var guard = Build();
        Assert.Throws<ArgumentException>(() => guard.ValidateKey("   "));
    }

    [Fact]
    public void TooManyTags_AreRejected()
    {
        var guard = Build(o => o.Security.MaximumTagCount = 2);

        var ex = Assert.Throws<ArgumentException>(() => guard.ValidateTags(["a", "b", "c"]));

        Assert.Contains("MaximumTagCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlongTag_IsRejected()
    {
        var guard = Build(o => o.Security.MaximumTagLength = 4);

        Assert.Throws<ArgumentException>(() => guard.ValidateTags(["far-too-long"]));
    }

    [Fact]
    public void EmptyTag_IsRejected()
    {
        var guard = Build();
        Assert.Throws<ArgumentException>(() => guard.ValidateTags([" "]));
    }

    [Fact]
    public void ValidTags_AreAccepted() => Build().ValidateTags(["category:1", "tenant:acme"]);

    [Fact]
    public void Fingerprint_IsStableAndDoesNotContainTheKey()
    {
        var guard = Build();
        const string Key = "Order:12345:user@example.com";

        var first = guard.Fingerprint(Key);
        var second = guard.Fingerprint(Key);

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
        Assert.DoesNotContain("example.com", first, StringComparison.Ordinal);
        Assert.NotEqual(first, guard.Fingerprint(Key + "x"));
    }
}
