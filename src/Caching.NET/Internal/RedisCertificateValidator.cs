using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Per-cache Redis TLS certificate policy. Attached to the connection configuration, so each cache
/// instance carries its own policy and no process-wide mutable state is involved.
/// </summary>
internal sealed class RedisCertificateValidator
{
    private readonly ILogger _logger;
    private readonly CacheTelemetryContext _telemetry;
    private readonly string _cacheName;
    private readonly bool _strict;
    private int _handshakeLogged;

    public RedisCertificateValidator(string cacheName, bool strict, ILogger logger, CacheTelemetryContext telemetry)
    {
        _cacheName = cacheName;
        _strict = strict;
        _logger = logger;
        _telemetry = telemetry;
    }

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (Interlocked.CompareExchange(ref _handshakeLogged, 1, 0) == 0
            && certificate is X509Certificate2 certificate2
            && _logger.IsEnabled(LogLevel.Information))
        {
            CacheLogMessages.RedisTlsHandshake(
                _logger,
                _cacheName,
                certificate2.Subject,
                certificate2.Issuer,
                certificate2.Thumbprint,
                certificate2.NotAfter);
        }

        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            _telemetry.RecordTlsValidation("ok");
            return true;
        }

        if (!_strict && sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
        {
            _telemetry.RecordTlsValidation("name_mismatch_accepted");
            CacheLogMessages.RedisTlsNameMismatchAccepted(_logger, _cacheName);
            return true;
        }

        _telemetry.RecordTlsValidation(Classify(sslPolicyErrors));
        CacheLogMessages.RedisTlsRejected(_logger, _cacheName, sslPolicyErrors.ToString(), _strict);
        return false;
    }

    private static string Classify(SslPolicyErrors errors)
    {
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return "certificate_missing";
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            return "chain_error";
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return "name_mismatch";
        }

        return "untrusted";
    }
}
