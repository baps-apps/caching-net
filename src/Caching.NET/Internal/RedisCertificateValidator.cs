using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

internal sealed class RedisCertificateValidator
{
    private readonly ILogger<RedisCertificateValidator> _logger;
    private readonly bool _strict;
    private bool _firstValidationLogged;

    public RedisCertificateValidator(ILogger<RedisCertificateValidator> logger, bool strict)
    {
        _logger = logger;
        _strict = strict;
    }

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (!_firstValidationLogged && certificate is X509Certificate2 cert2)
        {
            _firstValidationLogged = true;
            _logger.LogInformation(
                "Redis TLS first validation: subject={Subject} issuer={Issuer} thumbprint={Thumbprint} expires={Expires:o}",
                cert2.Subject, cert2.Issuer, cert2.Thumbprint, cert2.NotAfter);
        }

        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            CacheInstruments.RecordTlsValidation("Redis", "ok");
            return true;
        }

        var classification = Classify(sslPolicyErrors);
        CacheInstruments.RecordTlsValidation("Redis", classification);

        if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            _logger.LogWarning("Redis TLS chain error: {Errors}", sslPolicyErrors);

        if (!_strict && sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
            return true;

        return false;
    }

    private static string Classify(SslPolicyErrors err)
    {
        if (err.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return "untrusted";
        if (err.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return "name_mismatch";
        if (err.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) return "chain_error";
        return "untrusted";
    }
}
