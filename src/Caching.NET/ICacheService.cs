using Caching.NET.Options;

namespace Caching.NET;

/// <summary>
/// The Caching.NET cache operation contract. Resolve the default cache by injecting this type, or a
/// named cache with <c>[FromKeyedServices("name")]</c>.
/// </summary>
/// <remarks>
/// Every operation applies the cache's configured defaults — mode, durations, fail-safe, timeouts
/// and the key guard. Supplying <see cref="CacheEntryOverrides"/> changes only the properties it
/// sets.
/// </remarks>
public interface ICacheService
{
    /// <summary>Logical name of this cache instance.</summary>
    string CacheName { get; }

    /// <summary>Returns the cached value, running <paramref name="factory"/> on a miss.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value when none is cached.</param>
    /// <param name="failSafeDefaultValue">Returned when the factory fails and no stale value exists.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry, for later tag invalidation.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>
    /// Returns the cached value, running <paramref name="factory"/> on a miss. A context-free
    /// counterpart to <see cref="GetOrSetAsync{TValue}(string, Func{CacheFactoryContext{TValue}, CancellationToken, Task{TValue}}, CacheValue{TValue}, CacheEntryOverrides, IEnumerable{string}, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// In the context-taking overload, <typeparamref name="TValue"/> appears only inside
    /// <see cref="CacheFactoryContext{TValue}"/> — a lambda <i>parameter</i> type — so the compiler
    /// cannot bind the lambda before <typeparamref name="TValue"/> is known and every call must name
    /// it explicitly (<c>GetOrSetAsync&lt;Order&gt;(...)</c>). Here <typeparamref name="TValue"/> also
    /// appears in the factory's return type, so ordinary lambda type inference applies and the call
    /// compiles without a type argument. Prefer this overload; drop to the context-taking one only
    /// when the factory needs to inspect a stale value, call <c>NotModified()</c>/<c>Fail()</c>, or
    /// adjust the entry's options adaptively.
    /// </remarks>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value when none is cached.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry, for later tag invalidation.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue?>> factory,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Returns the cached value, or <paramref name="defaultValue"/> when none is cached.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="defaultValue">Returned on a miss.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default);

    /// <summary>Reads the cached value, distinguishing a cached <c>null</c> from a miss.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask<CacheValue<TValue>> TryGetAsync<TValue>(
        string key,
        CacheEntryOverrides? options = null,
        CancellationToken token = default);

    /// <summary>Writes a value.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask SetAsync<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Removes an entry from every layer.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Marks an entry expired, keeping it eligible for fail-safe.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask ExpireAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Invalidates every entry carrying <paramref name="tag"/>.</summary>
    /// <param name="tag">The tag to invalidate.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is null, empty or whitespace.</exception>
    ValueTask RemoveByTagAsync(string tag, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Invalidates every entry in this cache.</summary>
    /// <param name="allowFailSafe">Whether cleared entries stay eligible for fail-safe.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask ClearAsync(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>
    /// Synchronous
    /// <see cref="GetOrSetAsync{TValue}(string, Func{CacheFactoryContext{TValue}, CancellationToken, Task{TValue}}, CacheValue{TValue}, CacheEntryOverrides, IEnumerable{string}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value when none is cached.</param>
    /// <param name="failSafeDefaultValue">Returned when the factory fails and no stale value exists.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry.</param>
    /// <param name="token">Cancellation token.</param>
    TValue? GetOrSet<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, TValue?> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>
    /// Synchronous context-free counterpart to
    /// <see cref="GetOrSetAsync{TValue}(string, Func{CancellationToken, Task{TValue}}, CacheEntryOverrides, IEnumerable{string}, CancellationToken)"/>.
    /// See its remarks for when to prefer this over
    /// <see cref="GetOrSet{TValue}(string, Func{CacheFactoryContext{TValue}, CancellationToken, TValue}, CacheValue{TValue}, CacheEntryOverrides, IEnumerable{string}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value when none is cached.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry, for later tag invalidation.</param>
    /// <param name="token">Cancellation token, passed to <paramref name="factory"/>.</param>
    TValue? GetOrSet<TValue>(
        string key,
        Func<CancellationToken, TValue?> factory,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Synchronous <see cref="GetOrDefaultAsync{TValue}"/>.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="defaultValue">Returned on a miss.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    TValue? GetOrDefault<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default);

    /// <summary>Synchronous <see cref="TryGetAsync{TValue}"/>.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    CacheValue<TValue> TryGet<TValue>(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="SetAsync{TValue}"/>.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry.</param>
    /// <param name="token">Cancellation token.</param>
    void Set<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Synchronous <see cref="RemoveAsync"/>.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    void Remove(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="ExpireAsync"/>.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    void Expire(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="RemoveByTagAsync"/>.</summary>
    /// <param name="tag">The tag to invalidate.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is null, empty or whitespace.</exception>
    void RemoveByTag(string tag, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="ClearAsync"/>.</summary>
    /// <param name="allowFailSafe">Whether cleared entries stay eligible for fail-safe.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    void Clear(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default);
}
