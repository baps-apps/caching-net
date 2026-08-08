namespace Caching.NET.Options;

/// <summary>
/// Fail-safe, timeout, circuit-breaker and auto-recovery behaviour.
/// </summary>
/// <remarks>
/// Caching.NET does not add a retry or circuit-breaker layer of its own: the cache engine and the
/// Redis client already implement both, and stacking a third would multiply latency during an
/// outage. These knobs configure the built-in behaviour.
/// </remarks>
public sealed class CacheResilienceOptions
{
    /// <summary>
    /// When <c>true</c> (default) an expired entry is retained beyond its logical expiration and
    /// served if the factory fails or times out, instead of surfacing the error to the caller.
    /// </summary>
    public bool FailSafeEnabled { get; set; } = true;

    /// <summary>
    /// How long past its logical expiration an entry stays usable as a fail-safe fallback.
    /// Must be greater than or equal to <see cref="CachingOptions.DefaultExpiration"/>.
    /// Default 2 hours.
    /// </summary>
    public TimeSpan FailSafeMaxDuration { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// After a stale value is served, how long before the factory is retried. Prevents a failing
    /// dependency from being hammered once per request. Must be greater than zero. Default 30 seconds.
    /// </summary>
    public TimeSpan FailSafeThrottleDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for a factory before falling back to a stale value, while letting the
    /// factory keep running in the background. Requires <see cref="FailSafeEnabled"/> and an
    /// available stale value. <see cref="Timeout.InfiniteTimeSpan"/> disables it (default).
    /// </summary>
    public TimeSpan FactorySoftTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Hard ceiling on factory execution. On expiry the call fails (or returns a stale value when
    /// fail-safe is on). Must be greater than or equal to <see cref="FactorySoftTimeout"/>.
    /// Default 30 seconds.
    /// </summary>
    public TimeSpan FactoryHardTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When a factory exceeds <see cref="FactorySoftTimeout"/>, keep running it in the background
    /// and store the result when it completes. Default <c>true</c>.
    /// </summary>
    public bool AllowTimedOutFactoryBackgroundCompletion { get; set; } = true;

    /// <summary>
    /// How long a distributed-cache read may take before falling back to a stale value.
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables it. Default 500&#160;ms.
    /// </summary>
    public TimeSpan DistributedSoftTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Hard ceiling on a single distributed-cache operation. Must be greater than or equal to
    /// <see cref="DistributedSoftTimeout"/>. Default 2 seconds.
    /// </summary>
    public TimeSpan DistributedHardTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Complete distributed-cache writes in the background instead of blocking the caller.
    /// Default <c>true</c>.
    /// </summary>
    public bool AllowBackgroundDistributedOperations { get; set; } = true;

    /// <summary>Publish backplane notifications in the background instead of blocking the caller. Default <c>true</c>.</summary>
    public bool AllowBackgroundBackplaneOperations { get; set; } = true;

    /// <summary>
    /// After a distributed-cache error, stop attempting distributed operations for this long.
    /// Prevents retry and log storms during a Redis outage. <see cref="TimeSpan.Zero"/> disables
    /// the breaker. Default 5 seconds.
    /// </summary>
    public TimeSpan DistributedCircuitBreakerDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Same as <see cref="DistributedCircuitBreakerDuration"/>, for the backplane. Default 5 seconds.</summary>
    public TimeSpan BackplaneCircuitBreakerDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Queue distributed-cache and backplane operations that failed during an outage and replay
    /// them once the dependency recovers. Default <c>true</c>.
    /// </summary>
    public bool AutoRecoveryEnabled { get; set; } = true;

    /// <summary>Delay between auto-recovery sweeps. Default 2 seconds.</summary>
    public TimeSpan AutoRecoveryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cap on queued auto-recovery items, bounding memory growth during a long outage.
    /// <c>null</c> means unbounded. Default 1000.
    /// </summary>
    public int? AutoRecoveryMaxItems { get; set; } = 1000;

    /// <summary>Maximum replay attempts per queued item. <c>null</c> means unlimited. Default 5.</summary>
    public int? AutoRecoveryMaxRetryCount { get; set; } = 5;

    /// <summary>
    /// Surface distributed-cache (Redis) errors to the caller instead of degrading to the memory
    /// layer or the factory. Default <c>false</c> — errors are logged and counted, never swallowed
    /// silently.
    /// </summary>
    public bool ThrowOnDistributedCacheErrors { get; set; }

    /// <summary>
    /// Surface serialization and deserialization errors to the caller. Default <c>false</c>: a
    /// corrupt or unreadable payload is logged, counted, treated as a miss, and overwritten by the
    /// next factory result.
    /// </summary>
    /// <remarks>
    /// <b>Setting this to <c>false</c> does not hold on the write path when
    /// <see cref="AllowBackgroundDistributedOperations"/> is also <c>false</c>.</b> With background
    /// operations off the serializer runs on the caller's path, and the engine's foreground
    /// distributed write propagates the exception regardless — including the
    /// <see cref="CacheSerializationOptions.MaximumPayloadBytes"/> guard, which then fails the
    /// request instead of leaving the value uncached. Caching.NET logs a warning naming this
    /// combination at startup and cannot intercept it without wrapping every cache call. Reads are
    /// unaffected.
    /// </remarks>
    public bool ThrowOnSerializationErrors { get; set; }

    /// <summary>Surface backplane errors to the caller. Default <c>false</c>.</summary>
    public bool ThrowOnBackplaneErrors { get; set; }

    /// <summary>
    /// When a factory throws and no fail-safe value is available, rethrow the original exception
    /// instead of wrapping it. Default <c>true</c> so application error handling keeps working.
    /// </summary>
    public bool ThrowOriginalExceptions { get; set; } = true;
}
