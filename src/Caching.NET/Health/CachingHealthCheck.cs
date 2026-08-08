using System.Globalization;
using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

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
                await cache.SetAsync(
                    probeKey,
                    probeValue,
                    ProbeWriteOptions(cache),
                    token: cancellationToken).ConfigureAwait(false);

                var roundTripped = await cache.TryGetAsync<long>(
                    probeKey,
                    ProbeReadOptions(cache),
                    cancellationToken).ConfigureAwait(false);

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
    /// Options shared by the probe write and the probe read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CreateEntryOptions</c> duplicates the cache's configured defaults, so every inherited
    /// setting that would outlive or distort a probe is reset explicitly. In particular
    /// <c>DistributedCacheDuration</c> and <c>MemoryCacheDuration</c> are cleared to <c>null</c> so
    /// both layers fall back to <see cref="ProbeDuration"/>: left inherited, a configured
    /// <c>Entry.DistributedExpiration</c> would override the probe's own duration and leave the
    /// probe key in Redis for as long as a production entry.
    /// </para>
    /// <para>
    /// Fail-safe is disabled so the physical expiration is the logical one rather than
    /// <c>Resilience.FailSafeMaxDuration</c>, and so a retained stale value can never make a broken
    /// round trip look healthy.
    /// </para>
    /// </remarks>
    private static FusionCacheEntryOptions ProbeOptions(IFusionCache cache)
        => cache.CreateEntryOptions(options =>
        {
            options.Duration = ProbeDuration;
            options.DistributedCacheDuration = null;
            options.MemoryCacheDuration = null;
            options.JitterMaxDuration = TimeSpan.Zero;
            options.IsFailSafeEnabled = false;
            options.EagerRefreshThreshold = null;
            options.SetSkipBackplaneNotifications(true);
        });

    /// <summary>
    /// Probe write options. The distributed write is awaited and its failures are surfaced, so a
    /// readiness check cannot report healthy while the distributed layer is refusing writes.
    /// </summary>
    private static FusionCacheEntryOptions ProbeWriteOptions(IFusionCache cache)
    {
        var options = ProbeOptions(cache);

        if (cache.HasDistributedCache)
        {
            // Both are required: without the first the write completes in the background and the
            // caller never learns it failed; without the second the failure is logged and swallowed.
            options.AllowBackgroundDistributedCacheOperations = false;
            options.ReThrowDistributedCacheExceptions = true;
        }

        return options;
    }

    /// <summary>
    /// Probe read options. When a distributed layer is configured the read bypasses the memory layer
    /// so that it actually reaches it.
    /// </summary>
    /// <remarks>
    /// Without this, a Hybrid probe reads back the value its own write just placed in L1 and reports
    /// healthy without ever contacting the distributed layer — including while that layer is down.
    /// The memory layer is left alone when there is no distributed layer, because in InMemory mode
    /// skipping it would turn every probe into a miss.
    /// </remarks>
    private static FusionCacheEntryOptions ProbeReadOptions(IFusionCache cache)
    {
        var options = ProbeOptions(cache);

        if (cache.HasDistributedCache)
        {
            options.SetSkipMemoryCacheRead(true);
            options.ReThrowDistributedCacheExceptions = true;
        }

        return options;
    }
}
