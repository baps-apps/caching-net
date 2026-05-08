using Caching.NET.Options;
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisTlsTests
{
    private readonly RedisContainerFixture _redis;
    public RedisTlsTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task Strict_validation_with_self_signed_cert_rejects_connection()
    {
        // ssl=true against a plain-text Redis server forces an SSL handshake that fails.
        // FailOpen=false + ThrowOnFailure=true ensures the error propagates instead of being swallowed.
        var brokenTlsConn = _redis.ConnectionString + ",ssl=true,abortConnect=true,connectTimeout=2000";

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var (sp, cache) = IntegrationServiceProvider.Build(
                brokenTlsConn,
                "rt-tls",
                configureServices: s => s.PostConfigure<CacheOptions>(o =>
                {
                    o.FailOpen = false;
                    o.ThrowOnFailure = true;
                }));
            await using var _ = (ServiceProvider)sp;
            await cache.SetAsync("k", "v");
        });
    }
}
