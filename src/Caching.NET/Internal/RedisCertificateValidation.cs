using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace Caching.NET.Internal;

/// <summary>
/// Redis server certificate validation.
/// By default (strict == false), behaves like a development-friendly callback:
///   allows <see cref="System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch"/> but rejects other errors.
/// When strict == true, rejects any SSL policy errors (including hostname mismatches).
/// </summary>
public static class RedisCertificateValidation
{
    /// <summary>
    /// Default callback for StackExchange.Redis <see cref="StackExchange.Redis.ConfigurationOptions.CertificateValidation"/>.
    /// Uses non-strict validation (allows name mismatches) to match typical dev/test and many existing production setups.
    /// To opt into strict validation, use <see cref="ValidateServerCertificate(object,X509Certificate?,X509Chain?,System.Net.Security.SslPolicyErrors,bool)"/>
    /// with <c>strict: true</c>.
    /// </summary>
    /// <param name="sender">The sender object passed by the TLS stack (typically the <see cref="System.Net.Security.SslStream"/>).</param>
    /// <param name="certificate">The server certificate presented during the TLS handshake, or <c>null</c> if none was provided.</param>
    /// <param name="chain">The certificate chain built from the server certificate, or <c>null</c>.</param>
    /// <param name="sslPolicyErrors">The SSL policy errors detected during certificate validation.</param>
    /// <returns><c>true</c> if the certificate is accepted; <c>false</c> if the connection should be rejected.</returns>
    public static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        System.Net.Security.SslPolicyErrors sslPolicyErrors)
        => ValidateServerCertificate(sender, certificate, chain, sslPolicyErrors, strict: false);

    /// <summary>
    /// Callback for StackExchange.Redis <see cref="StackExchange.Redis.ConfigurationOptions.CertificateValidation"/>.
    /// When <paramref name="strict"/> is false (default), allows <see cref="System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch"/>
    /// but rejects all other SSL policy errors. When true, rejects any non-<see cref="System.Net.Security.SslPolicyErrors.None"/>
    /// result, including hostname mismatches.
    /// </summary>
    /// <param name="_">The sender (unused).</param>
    /// <param name="__">The server certificate (unused; validation is based solely on <paramref name="sslPolicyErrors"/>).</param>
    /// <param name="___">The certificate chain (unused).</param>
    /// <param name="sslPolicyErrors">The SSL policy errors detected during the TLS handshake.</param>
    /// <param name="strict">
    /// When <c>false</c>, <see cref="System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch"/> is tolerated (e.g. for self-signed Redis certs).
    /// When <c>true</c>, any SSL policy error causes the connection to be rejected.
    /// </param>
    /// <returns><c>true</c> if the certificate is accepted; <c>false</c> to abort the connection.</returns>
    public static bool ValidateServerCertificate(
        object _,
        X509Certificate? __,
        X509Chain? ___,
        System.Net.Security.SslPolicyErrors sslPolicyErrors,
        bool strict)
    {
        if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
        {
            return true;
        }

        if (!strict && sslPolicyErrors == System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)
        {
            Trace.TraceWarning("Redis certificate name mismatch allowed (non-strict validation). Errors: {0}", sslPolicyErrors);
            return true;
        }

        Trace.TraceWarning("Redis certificate validation error (strict={0}): {1}", strict, sslPolicyErrors);
        return false;
    }
}
