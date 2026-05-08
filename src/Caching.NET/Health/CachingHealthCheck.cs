using Caching.NET.Abstractions;
using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caching.NET.Health;

/// <summary>
/// Readiness-style health check for Caching.NET: when caching is enabled, verifies Redis PING (Redis/Hybrid),
/// then exercises <see cref="ICacheService.GetOrCreateAsync{T}"/> with a synthetic probe key.
/// For a lighter connection-only probe (Kubernetes liveness), use <see cref="CachingLivenessHealthCheck"/> with split registration.
/// </summary>
internal sealed class CachingHealthCheck : IHealthCheck
{
    private readonly ICacheService _cacheService;
    private readonly IOptions<CacheOptions> _options;
    private readonly ILogger<CachingHealthCheck> _logger;
    private readonly IConnectionMultiplexer? _multiplexer;

    /// <summary>
    /// Creates a new health check using cache service probing only.
    /// </summary>
    public CachingHealthCheck(
        ICacheService cacheService,
        IOptions<CacheOptions> options,
        ILogger<CachingHealthCheck> logger)
        : this(cacheService, options, logger, multiplexer: null)
    {
    }

    /// <summary>
    /// Creates a new health check with optional direct Redis multiplexer probing.
    /// </summary>
    public CachingHealthCheck(
        ICacheService cacheService,
        IOptions<CacheOptions> options,
        ILogger<CachingHealthCheck> logger,
        IConnectionMultiplexer? multiplexer)
    {
        _cacheService = cacheService;
        _options = options;
        _logger = logger;
        _multiplexer = multiplexer;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        CachingHealthProbe.CheckReadinessAsync(
            _cacheService,
            _options,
            _logger,
            _multiplexer,
            context,
            cancellationToken);
}

