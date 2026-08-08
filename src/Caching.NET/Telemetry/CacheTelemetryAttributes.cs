namespace Caching.NET.Telemetry;

/// <summary>
/// Attribute and dimension names used by Caching.NET spans and metrics. Only low-cardinality,
/// non-sensitive values are ever recorded: no cache values, no serialized payloads, no raw keys,
/// no connection strings, no credentials, no user or tenant identifiers.
/// </summary>
/// <remarks>
/// This guarantee covers the <c>Caching.NET</c> activity source and the <c>Caching.NET</c> meter.
/// It does <b>not</b> cover the engine-level sources in
/// <see cref="CacheTelemetry.EngineActivitySourceNames"/>, which attach the raw physical cache key
/// to every operation span — see that member before registering them.
/// </remarks>
public static class CacheTelemetryAttributes
{
    /// <summary>Always <c>caching.net</c>.</summary>
    public const string System = "cache.system";

    /// <summary>Configured cache mode: <c>InMemory</c>, <c>Redis</c> or <c>Hybrid</c>.</summary>
    public const string Mode = "cache.mode";

    /// <summary>Logical cache-instance name.</summary>
    public const string Name = "cache.name";

    /// <summary>Operation being performed, for example <c>get</c>, <c>set</c>, <c>serialize</c>.</summary>
    public const string Operation = "cache.operation";

    /// <summary>Outcome: <c>hit</c>, <c>miss</c>, <c>stale</c>, <c>set</c>, <c>removed</c> or <c>error</c>.</summary>
    public const string Result = "cache.result";

    /// <summary>Layer the outcome came from: <c>memory</c>, <c>redis</c>, <c>factory</c> or <c>backplane</c>.</summary>
    public const string Layer = "cache.layer";

    /// <summary>Whether the factory delegate ran.</summary>
    public const string FactoryExecuted = "cache.factory.executed";

    /// <summary>Whether a stale value was served because the factory failed or timed out.</summary>
    public const string FailSafeUsed = "cache.fail_safe.used";

    /// <summary>Whether the work happened on a background path rather than the caller's path.</summary>
    public const string BackgroundOperation = "cache.background_operation";

    /// <summary>Exception type name. Exception messages are never recorded as attributes.</summary>
    public const string ErrorType = "cache.error.type";

    /// <summary>
    /// Attribute name for a non-reversible key fingerprint. Caching.NET never sets it: the engine
    /// resolves entry options before it starts the operation span, so there is no hook that can
    /// attach a key attribute to that span without wrapping every cache call. Applications that
    /// want the correlation set it themselves from <see cref="ICacheGuard.Fingerprint(string)"/>,
    /// which is why the name lives here rather than in application code.
    /// </summary>
    public const string KeyFingerprint = "cache.key.fingerprint";

    /// <summary>Serialized payload size in bytes.</summary>
    public const string PayloadBytes = "cache.payload.bytes";
}

/// <summary>
/// Values recorded for <see cref="CacheTelemetryAttributes.Result"/>.
/// </summary>
public static class CacheResults
{
    /// <summary>A usable, non-expired value was returned from the cache.</summary>
    public const string Hit = "hit";

    /// <summary>No usable value was found.</summary>
    public const string Miss = "miss";

    /// <summary>An expired value was returned by fail-safe.</summary>
    public const string Stale = "stale";

    /// <summary>A value was written.</summary>
    public const string Set = "set";

    /// <summary>One or more entries were removed or invalidated.</summary>
    public const string Removed = "removed";

    /// <summary>The operation failed.</summary>
    public const string Error = "error";
}

/// <summary>
/// Values recorded for <see cref="CacheTelemetryAttributes.Layer"/>.
/// </summary>
public static class CacheLayers
{
    /// <summary>In-process memory layer (L1).</summary>
    public const string Memory = "memory";

    /// <summary>Distributed Redis layer (L2).</summary>
    public const string Redis = "redis";

    /// <summary>The caller-supplied factory delegate.</summary>
    public const string Factory = "factory";

    /// <summary>The cross-instance invalidation channel.</summary>
    public const string Backplane = "backplane";
}
