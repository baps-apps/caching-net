using Caching.NET.Abstractions;
using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caching.NET.Health;

/// <summary>
/// Shared implementation for <see cref="CachingHealthCheck"/> and <see cref="CachingLivenessHealthCheck"/>.
/// </summary>
internal static class CachingHealthProbe
{
    internal static string ProbeKey { get; } =
        $"caching-net:health:probe:{Environment.MachineName}:{Environment.ProcessId}";

    internal static Task<HealthCheckResult> CheckLivenessAsync(
        IOptions<CacheOptions> options,
        IConnectionMultiplexer? multiplexer,
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new HealthCheckResult(
                status: context.Registration.FailureStatus,
                description: "Caching liveness probe canceled."));
        }

        if (!options.Value.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Caching is disabled via configuration."));
        }

        if (options.Value.Mode is CacheMode.Redis or CacheMode.Hybrid)
        {
            if (multiplexer is null)
            {
                return Task.FromResult(new HealthCheckResult(
                    status: context.Registration.FailureStatus,
                    description: "Redis multiplexer is unavailable for health probing."));
            }

            if (!multiplexer.IsConnected)
            {
                return Task.FromResult(new HealthCheckResult(
                    status: context.Registration.FailureStatus,
                    description: "Redis multiplexer is disconnected."));
            }

            return Task.FromResult(HealthCheckResult.Healthy("Caching.NET liveness: Redis connection is up."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Caching.NET liveness: in-memory mode."));
    }

    internal static async Task<HealthCheckResult> CheckReadinessAsync(
        ICacheService cacheService,
        IOptions<CacheOptions> options,
        ILogger logger,
        IConnectionMultiplexer? multiplexer,
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return HealthCheckResult.Healthy("Caching is disabled via configuration.");
        }

        if (options.Value.Mode is CacheMode.Redis or CacheMode.Hybrid)
        {
            if (multiplexer is null)
            {
                return new HealthCheckResult(
                    status: context.Registration.FailureStatus,
                    description: "Redis multiplexer is unavailable for health probing.");
            }

            if (!multiplexer.IsConnected)
            {
                return new HealthCheckResult(
                    status: context.Registration.FailureStatus,
                    description: "Redis multiplexer is disconnected.");
            }
        }

        try
        {
            if (options.Value.Mode is CacheMode.Redis or CacheMode.Hybrid && multiplexer is not null)
            {
                _ = await multiplexer.GetDatabase().PingAsync().ConfigureAwait(false);
            }

            var probeExists = await cacheService.ExistsAsync(ProbeKey, cancellationToken).ConfigureAwait(false);
            if (!probeExists)
            {
                await cacheService.GetOrCreateAsync(
                    ProbeKey,
                    static _ => Task.FromResult(true),
                    expiration: TimeSpan.FromMinutes(5),
                    localExpiration: null,
                    cancellationToken).ConfigureAwait(false);
            }

            return HealthCheckResult.Healthy("Caching.NET is reachable and operational.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Caching.NET health probe failed for key {ProbeKey}.", ProbeKey);
            return new HealthCheckResult(
                status: context.Registration.FailureStatus,
                description: "Caching.NET health probe failed.",
                exception: ex);
        }
    }
}
