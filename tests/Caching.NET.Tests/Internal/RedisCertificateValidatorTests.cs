using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// The TLS certificate policy, exercised directly. A permissive setting must relax exactly one
/// thing — a name mismatch — and nothing else: an untrusted chain or a missing certificate stays a
/// rejection whatever the configuration says.
/// </summary>
public class RedisCertificateValidatorTests
{
    private static (RedisCertificateValidator Validator, GuardLoggingTests.RecordingLogger Logger) Build(bool strict)
    {
        var options = new CachingOptions { CacheName = "default", ApplicationPrefix = "app" };
        var logger = new GuardLoggingTests.RecordingLogger();
        return (new RedisCertificateValidator("default", strict, logger, new CacheTelemetryContext(options)), logger);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=caching-net-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoPolicyErrors_IsAcceptedUnderEitherSetting(bool strict)
    {
        var (validator, _) = Build(strict);
        using var certificate = SelfSigned();

        Assert.True(validator.Validate(this, certificate, chain: null, SslPolicyErrors.None));
    }

    [Fact]
    public void NameMismatch_IsRejectedWhenStrict()
    {
        var (validator, logger) = Build(strict: true);
        using var certificate = SelfSigned();

        Assert.False(validator.Validate(this, certificate, chain: null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void NameMismatch_IsAcceptedOnlyWhenPermissive_AndIsLoudAboutIt()
    {
        var (validator, logger) = Build(strict: false);
        using var certificate = SelfSigned();

        Assert.True(validator.Validate(this, certificate, chain: null, SslPolicyErrors.RemoteCertificateNameMismatch));

        // Accepting a mismatch silently would make the weakened setting invisible in production.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.EventId == 3022);
    }

    [Fact]
    public void UntrustedChain_IsRejectedEvenWhenPermissive()
    {
        var (validator, logger) = Build(strict: false);
        using var certificate = SelfSigned();

        // Permissive relaxes the host name, not the trust chain. A rogue endpoint presenting its own
        // certificate must not be accepted because someone loosened a DNS-name setting.
        Assert.False(validator.Validate(this, certificate, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.EventId == 3021);
    }

    [Fact]
    public void MissingCertificate_IsRejectedEvenWhenPermissive()
    {
        var (validator, _) = Build(strict: false);

        Assert.False(validator.Validate(this, certificate: null, chain: null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void CombinedErrorsIncludingANameMismatch_AreRejectedWhenPermissive()
    {
        var (validator, _) = Build(strict: false);
        using var certificate = SelfSigned();

        // Only an exact, sole name mismatch is tolerated.
        Assert.False(validator.Validate(
            this,
            certificate,
            chain: null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void HandshakeDetails_AreLoggedOnceAndCarryNoPrivateMaterial()
    {
        var (validator, logger) = Build(strict: true);
        using var certificate = SelfSigned();

        validator.Validate(this, certificate, chain: null, SslPolicyErrors.None);
        validator.Validate(this, certificate, chain: null, SslPolicyErrors.None);

        var handshakes = logger.Entries.Where(e => e.EventId == 3020).ToArray();
        Assert.Single(handshakes);
        Assert.Contains("caching-net-test", handshakes[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", handshakes[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    // StackExchange.Redis hands ConfigurationOptions.CertificateValidation straight to SslStream as
    // its userCertificateValidationCallback. That field is a *multicast* RemoteCertificateValidationCallback:
    // invoking it runs every subscriber in order but the caller only ever observes the LAST
    // subscriber's return value — earlier results are discarded. RedisConnectionProvider.BuildConfiguration
    // must therefore never attach Caching.NET's validator and an application callback as two separate
    // `+=` subscriptions (an application callback returning true would then silently override a
    // rejection); it composes them into one delegate before subscribing. These tests invoke that
    // composed delegate the same way SslStream would, via reflection onto the event's backing field
    // (ConfigurationOptions exposes no public way to raise it), to prove the composition is a real AND.
    private static RemoteCertificateValidationCallback GetComposedCallback(RedisOptions redisOptions)
    {
        var cachingOptions = new CachingOptions { CacheName = "default", ApplicationPrefix = "app" };
        var provider = new RedisConnectionProvider(
            "default",
            redisOptions,
            new GuardLoggingTests.RecordingLogger(),
            new CacheTelemetryContext(cachingOptions));

        var configuration = provider.BuildConfiguration();

        var field = typeof(ConfigurationOptions)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(RemoteCertificateValidationCallback));

        return (RemoteCertificateValidationCallback)field.GetValue(configuration)!;
    }

    [Fact]
    public void ApplicationCallbackReturningTrue_CannotRescueACertificateCachingNetRejected()
    {
        var redisOptions = new RedisOptions
        {
            Configuration = "localhost:6379",
            UseTls = true,
            StrictCertificateValidation = true,
            ValidateServerCertificate = (_, _, _, _) => true
        };

        var callback = GetComposedCallback(redisOptions);
        using var certificate = SelfSigned();

        // An untrusted chain is rejected by Caching.NET's own validator regardless of strictness.
        // If the application callback's `true` were the one SslStream observed, this would pass.
        var accepted = callback(this, certificate, chain: null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(accepted);
    }

    [Fact]
    public void ApplicationCallbackReturningFalse_CanTightenACertificateCachingNetAccepted()
    {
        var redisOptions = new RedisOptions
        {
            Configuration = "localhost:6379",
            UseTls = true,
            StrictCertificateValidation = true,
            ValidateServerCertificate = (_, _, _, _) => false
        };

        var callback = GetComposedCallback(redisOptions);
        using var certificate = SelfSigned();

        // Caching.NET's own validator accepts a clean handshake; the application callback must still
        // be able to reject it, proving the composition tightens rather than merely mirroring ours.
        var accepted = callback(this, certificate, chain: null, SslPolicyErrors.None);

        Assert.False(accepted);
    }

    [Fact]
    public void NoApplicationCallback_PreservesCachingNetsOwnResult()
    {
        var redisOptions = new RedisOptions
        {
            Configuration = "localhost:6379",
            UseTls = true,
            StrictCertificateValidation = true
        };

        var callback = GetComposedCallback(redisOptions);
        using var certificate = SelfSigned();

        Assert.True(callback(this, certificate, chain: null, SslPolicyErrors.None));
        Assert.False(callback(this, certificate, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
