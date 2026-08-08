using StackExchange.Redis;

namespace Caching.NET.Options;

/// <summary>
/// Redis connection settings for <see cref="CacheMode.Redis"/> and <see cref="CacheMode.Hybrid"/>.
/// </summary>
/// <remarks>
/// One <see cref="IConnectionMultiplexer"/> is created per distinct configuration and shared by the
/// distributed cache layer and the backplane. Connections are never opened per operation.
/// </remarks>
public sealed class RedisOptions
{
    /// <summary>
    /// StackExchange.Redis connection string, for example
    /// <c>"redis-0.cache.svc:6379,ssl=true,abortConnect=false"</c>. Required unless
    /// <see cref="ConfigureConnection"/> is supplied in code. Never logged or traced verbatim —
    /// credentials are redacted before any diagnostic output.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>Redis logical database index. <c>null</c> uses the connection-string default.</summary>
    public int? Database { get; set; }

    /// <summary>
    /// Optional extra prefix applied by the Redis adapter on top of the Caching.NET key prefix.
    /// Usually unnecessary: <see cref="CachingOptions.ApplicationPrefix"/> already isolates
    /// applications. Provided for interoperating with an existing key layout.
    /// </summary>
    public string InstancePrefix { get; set; } = string.Empty;

    /// <summary>Require TLS for the connection regardless of the connection string. Default <c>false</c>.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// When <c>true</c> (default) any TLS policy error rejects the connection. When <c>false</c>,
    /// a certificate name mismatch is tolerated — acceptable only for a private endpoint whose
    /// DNS name differs from the certificate subject, never for a public endpoint.
    /// </summary>
    public bool StrictCertificateValidation { get; set; } = true;

    /// <summary>Connect timeout. Default 5 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-command timeout. Default 2 seconds.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Connection attempts before giving up on the initial connect. Default 3.</summary>
    public int ConnectRetry { get; set; } = 3;

    /// <summary>
    /// When <c>false</c> (default) the application starts even if Redis is unreachable and
    /// reconnects in the background. Recommended for Kubernetes, where Redis and the pod can start
    /// in any order.
    /// </summary>
    public bool AbortOnConnectFail { get; set; }

    /// <summary>Keep-alive interval in seconds. <c>null</c> uses the client default.</summary>
    public int? KeepAliveSeconds { get; set; }

    /// <summary>Client name reported to Redis (visible in <c>CLIENT LIST</c>). Defaults to the cache name.</summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Code-only hook to adjust the parsed connection configuration immediately before connecting
    /// (for example to set a credential provider). Not bound from configuration. Applied last, so
    /// it overrides every other property here.
    /// </summary>
    public Action<ConfigurationOptions>? ConfigureConnection { get; set; }
}
