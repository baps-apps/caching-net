using System.Globalization;
using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Caching.NET.Health;

/// <summary>
/// Readiness probe. Performs a real write-then-read round trip through every registered cache,
/// including the Redis layer when one is configured, using a reserved key inside the application's
/// own key namespace.
/// </summary>
/// <remarks>
/// Reports <see cref="HealthStatus.Degraded"/> rather than unhealthy when the distributed layer is
/// unavailable but the process can still serve from memory or the source, so a Redis outage does
/// not remove every pod from the load balancer at once.
/// </remarks>
public sealed class CachingHealthCheck : IHealthCheck
{
    private const string ProbeKeyPrefix = "__cachingnet:health:";

    /// <summary>
    /// Lifetime of a probe entry in every layer. Long enough to survive its own round trip, short
    /// enough that an abandoned probe key disappears promptly.
    /// </summary>
    internal static readonly TimeSpan ProbeDuration = TimeSpan.FromSeconds(10);

    private readonly ICacheProvider _provider;
    private readonly IOptionsMonitor<CachingOptions> _options;

    /// <summary>Creates the readiness health check.</summary>
    /// <param name="provider">Resolves the registered caches.</param>
    /// <param name="options">Per-cache configuration.</param>
    public CachingHealthCheck(ICacheProvider provider, IOptionsMonitor<CachingOptions> options)
    {
        _provider = provider;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var degraded = new List<string>();

        foreach (var cacheName in _provider.CacheNames)
        {
            var options = _options.Get(cacheName);
            if (!options.Enabled)
            {
                data[cacheName] = "disabled";
                continue;
            }

            var cache = _provider.GetCache(cacheName);
            var probeKey = ProbeKeyPrefix + cacheName;
            var probeValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                // FusionCacheService exposes probe-only engine behaviour (forced rethrow on
                // distributed failures, skipping the memory layer on read) that ICacheService
                // deliberately does not — see the remarks on ProbeSetAsync/ProbeTryGetAsync. Every
                // enabled cache reaching this point is in fact a FusionCacheService; the fallback
                // below exists only so a future non-engine ICacheService (or a disabled cache's
                // NullCacheService, if that early-continue above is ever removed) degrades to the
                // overrides-only path instead of crashing.
                var roundTripped = cache is FusionCacheService engine
                    ? await ProbeRoundTripAsync(engine, probeKey, probeValue, options.UsesDistributedLayer, cancellationToken)
                        .ConfigureAwait(false)
                    : await ProbeRoundTripAsync(cache, probeKey, probeValue, options.UsesDistributedLayer, cancellationToken)
                        .ConfigureAwait(false);

                if (!roundTripped.HasValue || roundTripped.Value != probeValue)
                {
                    degraded.Add(cacheName);
                    data[cacheName] = "round-trip mismatch";
                    continue;
                }

                data[cacheName] = options.Mode.ToString();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                degraded.Add(cacheName);
                // Only the exception type is reported: an exception message can embed an endpoint
                // or a credential fragment, and a health endpoint is often publicly reachable.
                data[cacheName] = ex.GetType().Name;
            }
        }

        if (degraded.Count == 0)
        {
            return HealthCheckResult.Healthy("Caching.NET round trip succeeded.", data);
        }

        return HealthCheckResult.Degraded(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Caching.NET round trip failed for: {string.Join(", ", degraded)}."),
            exception: null,
            data);
    }

    /// <summary>
    /// Runs the probe write-then-read against a <see cref="FusionCacheService"/>, using its
    /// engine-only probe helpers so the read actually reaches the distributed layer and a distributed
    /// failure actually surfaces. See the remarks on <see cref="FusionCacheService.ProbeSetAsync{TValue}"/>
    /// and <see cref="FusionCacheService.ProbeTryGetAsync{TValue}"/> for why this matters: without
    /// them, a Hybrid probe reads back its own local write and reports healthy through a complete
    /// distributed-layer outage.
    /// </summary>
    private static async Task<CacheValue<long>> ProbeRoundTripAsync(
        FusionCacheService cache, string key, long value, bool hasDistributedCache, CancellationToken token)
    {
        await cache.ProbeSetAsync(key, value, ProbeWriteOptions(hasDistributedCache), token).ConfigureAwait(false);
        return await cache.ProbeTryGetAsync<long>(key, ProbeOptions(), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the probe write-then-read against any other <see cref="ICacheService"/>, using only the
    /// per-call overrides every consumer can reach. Exists so a non-engine cache degrades to this
    /// weaker check instead of the pattern match in <see cref="CheckHealthAsync"/> throwing.
    /// </summary>
    private static async Task<CacheValue<long>> ProbeRoundTripAsync(
        ICacheService cache, string key, long value, bool hasDistributedCache, CancellationToken token)
    {
        await cache.SetAsync(key, value, ProbeWriteOptions(hasDistributedCache), token: token).ConfigureAwait(false);
        return await cache.TryGetAsync<long>(key, ProbeOptions(), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Overrides shared by the probe write and the probe read.
    /// </summary>
    /// <remarks>
    /// Every inherited setting that would outlive or distort a probe is overridden explicitly.
    /// <c>LocalExpiration</c> and <c>DistributedExpiration</c> are both pinned to
    /// <see cref="ProbeDuration"/> so a configured <c>Entry.DistributedExpiration</c> cannot leave the
    /// probe key in Redis for as long as a production entry. Fail-safe is disabled so the physical
    /// expiration is the logical one rather than <c>Resilience.FailSafeMaxDuration</c>, and so a
    /// retained stale value can never make a broken round trip look healthy.
    /// </remarks>
    private static CacheEntryOverrides ProbeOptions() => new()
    {
        LocalExpiration = ProbeDuration,
        DistributedExpiration = ProbeDuration,
        JitterMaxDuration = TimeSpan.Zero,
        FailSafe = false,
        SkipBackplaneNotification = true
    };

    /// <summary>
    /// Probe write options. When a distributed layer is configured the write is kept on the caller's
    /// path rather than completing in the background, so a readiness check cannot report healthy
    /// while the distributed layer is refusing writes.
    /// </summary>
    /// <remarks>
    /// <see cref="CacheEntryOverrides"/> has no per-call way to force an exception to rethrow or to
    /// skip the memory layer on read — those are cache-mode guarantees Caching.NET deliberately keeps
    /// out of the per-call override surface every consumer can reach. Against a
    /// <see cref="FusionCacheService"/>, <see cref="ProbeRoundTripAsync(FusionCacheService, string, long, bool, CancellationToken)"/>
    /// adds both back through the engine-only probe helpers below, because a health check's job is
    /// verifying infrastructure below the operation contract.
    /// </remarks>
    private static CacheEntryOverrides ProbeWriteOptions(bool hasDistributedCache)
    {
        var options = ProbeOptions();

        if (hasDistributedCache)
        {
            // Without this the write completes in the background and the caller never learns it
            // failed.
            options.AllowBackgroundDistributedOperations = false;
        }

        return options;
    }
}
