using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Caching.NET.Internal;

/// <summary>
/// Static gateway for StackExchange.Redis certificate validation callbacks.
/// Delegates to the registered <see cref="RedisCertificateValidator"/> when available,
/// falling back to permissive non-strict behaviour before DI wires the validator.
/// </summary>
internal static class RedisCertificateValidation
{
    private static RedisCertificateValidator? _validator;

    public static void Configure(RedisCertificateValidator validator) => _validator = validator;

    public static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        var v = _validator;
        if (v is not null) return v.Validate(sender, certificate, chain, sslPolicyErrors);
        // Fallback: behave like the non-strict default (allows name mismatches, rejects chain errors).
        return sslPolicyErrors == SslPolicyErrors.None
            || sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch;
    }
}
