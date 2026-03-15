using Caching.NET.Abstractions;

namespace Caching.NET.Telemetry;

/// <summary>
/// Default <see cref="ICacheTelemetry"/> implementation that performs no work.
/// This keeps Caching.NET telemetry-agnostic while allowing applications to plug
/// in their own implementation when desired.
/// </summary>
public sealed class NoopCacheTelemetry : ICacheTelemetry
{
    /// <inheritdoc />
    public void OnCacheHit(string key, string mode) { }

    /// <inheritdoc />
    public void OnCacheMiss(string key, string mode) { }

    /// <inheritdoc />
    public void OnCacheSet(string key, string mode) { }

    /// <inheritdoc />
    public void OnCacheRemove(string key, string mode) { }

    /// <inheritdoc />
    public void OnCacheRemoveByTag(string tag, string mode) { }

    /// <inheritdoc />
    public void OnCacheError(string operation, string keyOrTag, string mode, Exception exception) { }

    /// <inheritdoc />
    public void OnFactoryTimeout(string key, string mode, TimeSpan timeout) { }
}

