using System.Diagnostics;
using System.Diagnostics.Metrics;
using Caching.NET.Abstractions;

namespace Caching.NET.Telemetry;

/// <summary>
/// <see cref="ICacheTelemetry"/> implementation that emits metrics and traces using
/// <see cref="Meter"/> and <see cref="ActivitySource"/>. Applications can bridge these
/// to OpenTelemetry by registering appropriate listeners/exporters.
/// </summary>
public sealed class OpenTelemetryCacheTelemetry : ICacheTelemetry
{
    private const string MeterName = "Caching.NET.Cache";
    private const string ActivitySourceName = "Caching.NET.Cache";

    private static readonly Meter Meter = new(MeterName);
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("cache.requests");
    private static readonly Counter<long> Hits = Meter.CreateCounter<long>("cache.hits");
    private static readonly Counter<long> Misses = Meter.CreateCounter<long>("cache.misses");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("cache.failures");

    /// <inheritdoc />
    public void OnCacheHit(string key, string mode)
    {
        Hits.Add(1, GetTags("get_or_create", mode));
        Requests.Add(1, GetTags("get_or_create", mode));
    }

    /// <inheritdoc />
    public void OnCacheMiss(string key, string mode)
    {
        Misses.Add(1, GetTags("get_or_create", mode));
        Requests.Add(1, GetTags("get_or_create", mode));
    }

    /// <inheritdoc />
    public void OnCacheSet(string key, string mode)
    {
        Requests.Add(1, GetTags("set", mode));
    }

    /// <inheritdoc />
    public void OnCacheRemove(string key, string mode)
    {
        Requests.Add(1, GetTags("remove", mode));
    }

    /// <inheritdoc />
    public void OnCacheRemoveByTag(string tag, string mode)
    {
        Requests.Add(1, GetTags("remove_by_tag", mode));
    }

    /// <inheritdoc />
    public void OnCacheError(string operation, string keyOrTag, string mode, Exception exception)
    {
        Failures.Add(1, GetTags(operation, mode));

        using var activity = ActivitySource.StartActivity($"cache.{operation}", ActivityKind.Internal);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("cache.mode", mode);
        activity.SetTag("cache.operation", operation);
        if (!string.IsNullOrEmpty(keyOrTag))
        {
            activity.SetTag("cache.key_prefix", Truncate(keyOrTag));
        }
        activity.SetTag("exception.type", exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <inheritdoc />
    public void OnFactoryTimeout(string key, string mode, TimeSpan timeout)
    {
        using var activity = ActivitySource.StartActivity("cache.factory_timeout", ActivityKind.Internal);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("cache.mode", mode);
        activity.SetTag("cache.key_prefix", Truncate(key));
        activity.SetTag("cache.factory_timeout_ms", timeout.TotalMilliseconds);
        activity.SetStatus(ActivityStatusCode.Error, "Factory execution exceeded configured timeout.");
    }

    private static KeyValuePair<string, object?>[] GetTags(string operation, string mode) =>
    [
        new("cache.operation", operation),
        new("cache.mode", mode),
    ];

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= 64 ? value : value[..64] + "...";
    }
}

