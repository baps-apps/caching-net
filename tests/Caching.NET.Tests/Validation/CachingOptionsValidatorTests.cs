using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caching.NET.Options;
using Caching.NET.Validation;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Validation;

public class CachingOptionsValidatorTests
{
    private static readonly CachingOptionsValidator Validator = new();

    private static CachingOptions Valid() => new()
    {
        CacheName = "default",
        ApplicationPrefix = "orders-api",
        Mode = CacheMode.InMemory
    };

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=caching-net-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static ValidateOptionsResult Validate(CachingOptions options, string name = "default")
        => Validator.Validate(name, options);

    private static void AssertFails(CachingOptions options, string expectedFragment)
    {
        var result = Validate(options);
        Assert.True(result.Failed, "Expected validation to fail but it succeeded.");
        Assert.Contains(
            expectedFragment,
            string.Join(" | ", result.Failures ?? []),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidInMemoryOptions_Pass() => Assert.True(Validate(Valid()).Succeeded);

    [Fact]
    public void DisabledCache_SkipsValidationEntirely()
    {
        var options = new CachingOptions { Enabled = false, Mode = CacheMode.Redis };
        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void MissingApplicationPrefix_Fails()
    {
        var options = Valid();
        options.ApplicationPrefix = string.Empty;
        AssertFails(options, "ApplicationPrefix is required");
    }

    [Fact]
    public void PrefixContainingSeparator_Fails()
    {
        var options = Valid();
        options.ApplicationPrefix = "orders:api";
        AssertFails(options, "must not contain ':'");
    }

    [Fact]
    public void InvalidCacheName_Fails()
    {
        var options = Valid();
        options.CacheName = "bad name";
        AssertFails(options, "is invalid");
    }

    [Fact]
    public void CacheNameMismatch_Fails()
    {
        var options = Valid();
        options.CacheName = "something-else";
        var result = Validate(options, "default");
        Assert.True(result.Failed);
        Assert.Contains("does not match", string.Join(" ", result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void RedisModeWithoutConnection_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        AssertFails(options, "Redis.Configuration is not set");
    }

    [Fact]
    public void RedisModeWithoutConfiguration_Fails()
    {
        // Redis.Configuration is now the only way to supply endpoints — ConfigureConnection, the
        // former code-first escape hatch, no longer exists.
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = null;

        AssertFails(options, "Redis.Configuration is not set");
    }

    // A URI-form connection string is what most managed Redis providers hand out, but
    // StackExchange.Redis takes the whole string as a HOST NAME rather than parsing the scheme and
    // userinfo. The connection then always fails, and the credentials — now part of the "endpoint" —
    // are echoed inside the RedisConnectionException and reach the log at Warning level. Measured
    // against the packed package: password and username both present in a Warning record. Redaction
    // cannot reach a secret embedded in a third-party exception string, so this must fail fast
    // before a connection is attempted.
    [Theory]
    [InlineData("redis://adminuser:hunter2@127.0.0.1:6379")]
    [InlineData("rediss://adminuser:hunter2@cache.example.com:6380")]
    [InlineData("redis://127.0.0.1:6379")]
    [InlineData("redis://adminuser:hunter2@127.0.0.1:6379,abortConnect=false")]
    public void UriFormConnectionString_Fails(string configuration)
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = configuration;

        AssertFails(options, "looks like a URI");
    }

    [Fact]
    public void CommaDelimitedConnectionStringWithCredentials_IsAccepted()
    {
        // The documented form must keep working — the rule above must not reject it.
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "127.0.0.1:6379,password=hunter2,user=adminuser,ssl=true";

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void HybridModeWithoutConnection_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Hybrid;
        AssertFails(options, "Redis.Configuration is not set");
    }

    [Fact]
    public void InMemoryModeWithRedisConnection_Fails()
    {
        var options = Valid();
        options.Redis.Configuration = "localhost:6379";
        AssertFails(options, "InMemory mode never opens a Redis connection");
    }

    [Fact]
    public void BackplaneWithoutRedis_Fails()
    {
        var options = Valid();
        options.Backplane.Enabled = true;
        AssertFails(options, "backplane needs a Redis connection");
    }

    [Fact]
    public void BackplaneInRedisMode_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        options.Backplane.Enabled = true;
        AssertFails(options, "keeps no local entries to invalidate");
    }

    [Fact]
    public void ZeroExpiration_Fails()
    {
        var options = Valid();
        options.DefaultExpiration = TimeSpan.Zero;
        AssertFails(options, "DefaultExpiration must be greater than zero");
    }

    [Fact]
    public void NegativeExpiration_Fails()
    {
        var options = Valid();
        options.DefaultExpiration = TimeSpan.FromMinutes(-1);
        AssertFails(options, "DefaultExpiration must be greater than zero");
    }

    [Fact]
    public void SoftTimeoutGreaterThanHardTimeout_Fails()
    {
        var options = Valid();
        options.Resilience.FactorySoftTimeout = TimeSpan.FromSeconds(20);
        options.Resilience.FactoryHardTimeout = TimeSpan.FromSeconds(5);
        AssertFails(options, "must not exceed Resilience.FactoryHardTimeout");
    }

    [Fact]
    public void DistributedSoftTimeoutGreaterThanHardTimeout_Fails()
    {
        var options = Valid();
        options.Resilience.DistributedSoftTimeout = TimeSpan.FromSeconds(5);
        options.Resilience.DistributedHardTimeout = TimeSpan.FromSeconds(1);
        AssertFails(options, "must not exceed Resilience.DistributedHardTimeout");
    }

    [Fact]
    public void FailSafeMaxDurationBelowExpiration_Fails()
    {
        var options = Valid();
        options.DefaultExpiration = TimeSpan.FromHours(3);
        options.Resilience.FailSafeMaxDuration = TimeSpan.FromMinutes(1);
        AssertFails(options, "shorter than DefaultExpiration");
    }

    [Fact]
    public void InvalidEagerRefreshThreshold_Fails()
    {
        var options = Valid();
        options.Entry.EagerRefreshThreshold = 1.5f;
        AssertFails(options, "EagerRefreshThreshold must be between 0 and 1");
    }

    [Fact]
    public void NegativeJitter_Fails()
    {
        var options = Valid();
        options.Entry.JitterMaxDuration = TimeSpan.FromSeconds(-1);
        AssertFails(options, "JitterMaxDuration must not be negative");
    }

    [Fact]
    public void MemorySizeLimitWithoutEntrySize_Fails()
    {
        var options = Valid();
        options.Entry.MemorySizeLimit = 64;
        AssertFails(options, "Entry.Size is not");
    }

    /// <summary>
    /// The memory layer evicts by comparing the summed <c>Entry.Size</c> of the cached entries against
    /// <c>Entry.MemorySizeLimit</c>. A default size of 0 charges nothing per entry, so the limit can
    /// never be reached — a cap that looks configured and does nothing, which is the same class of
    /// silent non-guard that made the old <c>MemorySizeLimitMegabytes</c> misleading.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MemorySizeLimitWithNonPositiveEntrySize_Fails(long size)
    {
        var options = Valid();
        options.Entry.MemorySizeLimit = 64;
        options.Entry.Size = size;
        AssertFails(options, "Entry.Size must be greater than zero when Entry.MemorySizeLimit is set");
    }

    [Fact]
    public void MemorySizeLimitWithAPositiveEntrySize_Passes()
    {
        var options = Valid();
        options.Entry.MemorySizeLimit = 64;
        options.Entry.Size = 1;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void ZeroMaximumPayloadBytes_Fails()
    {
        var options = Valid();
        options.Serialization.MaximumPayloadBytes = 0;
        AssertFails(options, "MaximumPayloadBytes must be greater than zero");
    }

    [Fact]
    public void DecompressionCeilingBelowPayloadLimit_Fails()
    {
        var options = Valid();
        options.Serialization.MaximumPayloadBytes = 1_000_000;
        options.Serialization.Compression.Enabled = true;
        options.Serialization.Compression.MaximumDecompressedBytes = 1024;
        AssertFails(options, "MaximumDecompressedBytes");
    }

    [Fact]
    public void ZeroMaximumKeyLength_Fails()
    {
        var options = Valid();
        options.Security.MaximumKeyLength = 0;
        AssertFails(options, "MaximumKeyLength must be greater than zero");
    }

    [Fact]
    public void MaximumKeyLengthShorterThanPrefix_Fails()
    {
        var options = Valid();
        options.ApplicationPrefix = "a-very-long-application-prefix-value";
        options.Security.MaximumKeyLength = 10;
        AssertFails(options, "leaves no room");
    }

    [Fact]
    public void ZeroMaximumTagCount_Fails()
    {
        var options = Valid();
        options.Security.MaximumTagCount = 0;
        AssertFails(options, "MaximumTagCount must be greater than zero");
    }

    [Fact]
    public void NegativeRedisDatabase_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        options.Redis.Database = -2;
        AssertFails(options, "Redis.Database must not be negative");
    }

    [Fact]
    public void PermissiveTlsWithoutTls_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        options.Redis.StrictCertificateValidation = false;
        AssertFails(options, "TLS is not enabled");
    }

    [Fact]
    public void PermissiveTlsWithTls_Passes()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        options.Redis.UseTls = true;
        options.Redis.StrictCertificateValidation = false;
        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void ClientCertificateWithoutTls_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        using var certificate = SelfSigned();
        options.Redis.ClientCertificate = certificate;

        AssertFails(options, "require Redis.UseTls=true");
    }

    [Fact]
    public void ValidateServerCertificateWithoutTls_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        options.Redis.ValidateServerCertificate = (_, _, _, _) => true;

        AssertFails(options, "require Redis.UseTls=true");
    }

    [Fact]
    public void ClientCertificateWithUseTls_Passes()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        options.Redis.UseTls = true;
        using var certificate = SelfSigned();
        options.Redis.ClientCertificate = certificate;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void ClientCertificateWithSslInConnectionString_Passes()
    {
        // UseTls and a connection string carrying ssl=true are two different ways to enable TLS.
        // The rule must recognise both, or this configuration would fail for no reason.
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379,ssl=true";
        using var certificate = SelfSigned();
        options.Redis.ClientCertificate = certificate;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void EveryFailure_IsReportedAtOnce()
    {
        var options = Valid();
        options.ApplicationPrefix = string.Empty;
        options.DefaultExpiration = TimeSpan.Zero;
        options.Security.MaximumTagLength = 0;

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.True(result.Failures!.Count() >= 3);
    }

    [Fact]
    public void FailureMessages_AreScopedToTheCacheName()
    {
        var options = Valid();
        options.DefaultExpiration = TimeSpan.Zero;

        var result = Validator.Validate("short-lived", options);

        Assert.Contains("Caching[short-lived]", result.Failures!.First(), StringComparison.Ordinal);
    }

    [Fact]
    public void HybridWithLocalExpirationLongerThanDistributed_Fails()
    {
        // The in-process copy would outlive the authoritative Redis entry, so this instance keeps
        // answering with data every other instance has already refetched.
        var options = Valid();
        options.Mode = CacheMode.Hybrid;
        options.Redis.Configuration = "localhost:6379";
        options.DefaultExpiration = TimeSpan.FromMinutes(10);
        options.Entry.LocalExpiration = TimeSpan.FromHours(6);
        options.Entry.DistributedExpiration = TimeSpan.FromMinutes(1);

        AssertFails(options, "Entry.LocalExpiration");
    }

    [Fact]
    public void HybridWithLocalExpirationLongerThanTheInheritedDistributedDuration_Fails()
    {
        // DistributedExpiration unset means it inherits DefaultExpiration, so the comparison has to
        // be against the effective value rather than only against an explicitly configured one.
        var options = Valid();
        options.Mode = CacheMode.Hybrid;
        options.Redis.Configuration = "localhost:6379";
        options.DefaultExpiration = TimeSpan.FromMinutes(1);
        options.Entry.LocalExpiration = TimeSpan.FromHours(1);

        AssertFails(options, "Entry.LocalExpiration");
    }

    [Fact]
    public void HybridWithShorterLocalExpiration_Passes()
    {
        var options = Valid();
        options.Mode = CacheMode.Hybrid;
        options.Redis.Configuration = "localhost:6379";
        options.DefaultExpiration = TimeSpan.FromMinutes(10);
        options.Entry.LocalExpiration = TimeSpan.FromSeconds(30);
        options.Entry.DistributedExpiration = TimeSpan.FromMinutes(30);

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void RedisModeWithALongLocalExpiration_Passes()
    {
        // Redis mode holds nothing locally, so the pair cannot disagree and the rule must not fire.
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = "localhost:6379";
        options.DefaultExpiration = TimeSpan.FromMinutes(1);
        options.Entry.LocalExpiration = TimeSpan.FromHours(6);

        Assert.True(Validate(options).Succeeded);
    }

    /// <summary>
    /// <c>EngineOperationLogLevel</c> rewrites everything the engine logs at Information, so a
    /// diagnostic level deliberately raised to Information would be quietly lowered again. That
    /// combination has to fail at startup rather than during an incident.
    /// </summary>
    [Theory]
    [InlineData(nameof(CacheObservabilityOptions.DistributedCacheErrorLogLevel))]
    [InlineData(nameof(CacheObservabilityOptions.BackplaneErrorLogLevel))]
    [InlineData(nameof(CacheObservabilityOptions.SerializationErrorLogLevel))]
    [InlineData(nameof(CacheObservabilityOptions.FailSafeActivationLogLevel))]
    [InlineData(nameof(CacheObservabilityOptions.FactoryErrorLogLevel))]
    [InlineData(nameof(CacheObservabilityOptions.SyntheticTimeoutLogLevel))]
    public void DiagnosticLevelAtInformation_WhileEngineOperationLinesAreRewritten_Fails(string propertyName)
    {
        var options = Valid();
        options.Observability.EngineOperationLogLevel = Microsoft.Extensions.Logging.LogLevel.Debug;
        typeof(CacheObservabilityOptions)
            .GetProperty(propertyName)!
            .SetValue(options.Observability, Microsoft.Extensions.Logging.LogLevel.Information);

        AssertFails(options, $"Observability.{propertyName} is Information");
    }

    [Fact]
    public void DiagnosticLevelAtInformation_WithNativeEngineVerbosity_Passes()
    {
        var options = Valid();
        options.Observability.EngineOperationLogLevel = Microsoft.Extensions.Logging.LogLevel.Information;
        options.Observability.FactoryErrorLogLevel = Microsoft.Extensions.Logging.LogLevel.Information;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void DefaultObservabilityLevels_Pass()
        => Assert.True(Validate(Valid()).Succeeded);

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.1d)]
    [InlineData(1.5d)]
    public void JitterFractionOutsideZeroToOne_Fails(double fraction)
    {
        var options = Valid();
        options.Entry.JitterFraction = fraction;

        AssertFails(options, "Entry.JitterFraction must be greater than 0 and at most 1");
    }

    [Theory]
    [InlineData(0.1d)]
    [InlineData(1d)]
    [InlineData(null)]
    public void ValidJitterFraction_Passes(double? fraction)
    {
        var options = Valid();
        options.Entry.JitterFraction = fraction;

        Assert.True(Validate(options).Succeeded);
    }
}
