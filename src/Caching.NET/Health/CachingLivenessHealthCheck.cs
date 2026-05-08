using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caching.NET.Health;

/// <summary>
/// Lightweight liveness probe for Kubernetes-style setups: verifies configuration and (for Redis/Hybrid)
/// that the multiplexer reports connected — without Redis PING or cache write traffic.
/// Pair with <see cref="CachingHealthCheck"/> (readiness) by registering health checks with split enabled
/// (see <c>Caching.NET.Extensions.ServiceCollectionExtensions.AddCachingHealthChecks</c> and <c>splitLivenessReadiness: true</c>).
/// </summary>
internal sealed class CachingLivenessHealthCheck : IHealthCheck
{
    private readonly IOptions<CacheOptions> _options;
    private readonly IConnectionMultiplexer? _multiplexer;

    /// <summary>
    /// Creates a liveness check; multiplexer is resolved from DI when registered for Redis/Hybrid modes.
    /// </summary>
    public CachingLivenessHealthCheck(
        IOptions<CacheOptions> options,
        IConnectionMultiplexer? multiplexer = null)
    {
        _options = options;
        _multiplexer = multiplexer;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        CachingHealthProbe.CheckLivenessAsync(_options, _multiplexer, context, cancellationToken);
}
