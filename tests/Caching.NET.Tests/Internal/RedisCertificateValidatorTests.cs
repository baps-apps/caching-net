using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;

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
}
