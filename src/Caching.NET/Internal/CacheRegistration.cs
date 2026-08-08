namespace Caching.NET.Internal;

/// <summary>
/// One registered cache, resolved lazily. Registered as a plain singleton so
/// <see cref="CacheProvider"/> can be built from <c>IEnumerable&lt;CacheRegistration&gt;</c>
/// instead of capturing an <see cref="IServiceProvider"/>.
/// </summary>
internal sealed class CacheRegistration
{
    private readonly Func<CacheInstance> _resolve;

    public CacheRegistration(string cacheName, bool isDefault, Func<CacheInstance> resolve)
    {
        CacheName = cacheName;
        IsDefault = isDefault;
        _resolve = resolve;
    }

    public string CacheName { get; }

    public bool IsDefault { get; }

    public CacheInstance Instance => _resolve();
}
