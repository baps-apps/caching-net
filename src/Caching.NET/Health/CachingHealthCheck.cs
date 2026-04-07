using Caching.NET.Abstractions;
using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Health;

/// <summary>
/// Simple health check for Caching.NET that verifies the cache pipeline is operational.
/// This is intentionally lightweight and avoids heavy probing; it is designed to be composed
/// with infrastructure-specific checks (for example, dedicated Redis health checks).
/// </summary>
/// <param name="cacheService">The resolved <see cref="ICacheService"/> to probe during health checks.</param>
/// <param name="options">Bound <see cref="CacheOptions"/> used to determine whether caching is enabled.</param>
/// <param name="logger">Logger for recording health-check probe failures.</param>
public sealed class CachingHealthCheck(
    ICacheService cacheService,
    IOptions<CacheOptions> options,
    ILogger<CachingHealthCheck> logger) : IHealthCheck
{

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            // When caching is disabled by configuration, treat this component as healthy; the cache
            // is intentionally out of the request path.
            return HealthCheckResult.Healthy("Caching is disabled via configuration.");
        }

        // For in-memory-only modes we can return healthy as long as the service resolves.
        // For Redis/Hybrid we execute a very cheap get-or-create call on a synthetic key. This
        // respects FailOpen semantics: if the cache backend is unavailable and FailOpen is true,
        // the factory runs and we still treat the check as healthy (since requests will succeed).
        // When FailOpen is false, failures bubble and we surface them as Unhealthy.
        const string probeKey = "caching-net:health:probe";

        try
        {
            await cacheService.GetOrCreateAsync(
                probeKey,
                static _ => Task.FromResult(true),
                expiration: TimeSpan.FromMinutes(5),
                localExpiration: null,
                cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("Caching.NET is reachable and operational.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Caching.NET health probe failed for key {ProbeKey}.", probeKey);
            return new HealthCheckResult(
                status: context.Registration.FailureStatus,
                description: "Caching.NET health probe failed.",
                exception: ex);
        }
    }
}

