using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Caching.NET.Health;

/// <summary>
/// Liveness probe. Confirms only that the Caching.NET object graph resolves; it performs no I/O.
/// </summary>
/// <remarks>
/// A liveness probe must not depend on an external dependency: if it did, a Redis outage would
/// restart every pod in the deployment rather than degrading the cache. Use
/// <see cref="CachingHealthCheck"/> for readiness.
/// </remarks>
public sealed class CachingLivenessHealthCheck : IHealthCheck
{
    private readonly ICacheProvider _provider;

    /// <summary>Creates the liveness health check.</summary>
    /// <param name="provider">Resolves the registered caches.</param>
    public CachingLivenessHealthCheck(ICacheProvider provider)
    {
        _provider = provider;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var names = _provider.CacheNames;
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Caching.NET is registered with {names.Count} cache(s): {(names.Count == 0 ? "(none)" : string.Join(", ", names))}."));
    }
}
