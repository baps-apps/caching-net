using Caching.NET.Options;

namespace Caching.NET.Internal;

/// <summary>
/// The cache registered when <see cref="CachingOptions.Enabled"/> is <c>false</c>: reads always
/// miss, writes are discarded, and get-or-set factories run on every call.
/// </summary>
/// <remarks>
/// No engine object, memory cache, Redis connection or backplane is created. This exists so that
/// disabling the cache is a configuration change rather than a code change in the application.
/// </remarks>
internal sealed class NullCacheService : ICacheService
{
    public NullCacheService(string cacheName) => CacheName = cacheName;

    public string CacheName { get; }

    public async ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return await factory(new CacheFactoryContext<TValue>(), token).ConfigureAwait(false);
    }

    public ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue?>> factory,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Delegates to the context-taking overload above, matching FusionCacheService: one code path,
        // so "runs the factory every time and caches nothing" only has to be true in one place.
        return GetOrSetAsync<TValue>(key, (_, ct) => factory(ct), default, options, tags, token);
    }

    public ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key, TValue? defaultValue = default, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.FromResult(defaultValue);

    public ValueTask<CacheValue<TValue>> TryGetAsync<TValue>(
        string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.FromResult(CacheValue<TValue>.None);

    public ValueTask SetAsync<TValue>(
        string key, TValue value, CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask ExpireAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask RemoveByTagAsync(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask ClearAsync(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public TValue? GetOrSet<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, TValue?> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory(new CacheFactoryContext<TValue>(), token);
    }

    public TValue? GetOrSet<TValue>(
        string key,
        Func<CancellationToken, TValue?> factory,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return GetOrSet<TValue>(key, (_, ct) => factory(ct), default, options, tags, token);
    }

    public TValue? GetOrDefault<TValue>(
        string key, TValue? defaultValue = default, CacheEntryOverrides? options = null, CancellationToken token = default)
        => defaultValue;

    public CacheValue<TValue> TryGet<TValue>(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => CacheValue<TValue>.None;

    public void Set<TValue>(
        string key, TValue value, CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null, CancellationToken token = default)
    {
    }

    public void Remove(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }

    public void Expire(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }

    public void RemoveByTag(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }

    public void Clear(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }
}
