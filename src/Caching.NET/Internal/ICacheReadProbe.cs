namespace Caching.NET.Internal;

/// <summary>
/// Outcome of a read that reports presence separately from the value.
/// </summary>
/// <remarks>
/// <see cref="Abstractions.ICacheService.GetAsync{T}"/> returns <c>T?</c>, which cannot express
/// "missing" for a value type: a cached <c>0</c> and a missing <c>int</c> are the same value, and a
/// null check on an unconstrained <c>T</c> is unconditionally true once <c>T</c> is a struct. Callers
/// that must distinguish the two — the routing layer's <c>cache.served_from</c> /
/// <c>cache.hit_count</c> tagging — read through <see cref="ICacheReadProbe"/> instead.
/// </remarks>
internal readonly record struct CacheProbe<T>(bool Found, T? Value);

/// <summary>
/// Presence-aware reads, implemented by every concrete cache service. Internal on purpose:
/// <see cref="Abstractions.ICacheService"/> is the stable public contract and does not grow members.
/// </summary>
internal interface ICacheReadProbe
{
    /// <summary>Reads one key, reporting whether it was present.</summary>
    Task<CacheProbe<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>Reads many keys, reporting presence per key.</summary>
    Task<IReadOnlyDictionary<string, CacheProbe<T>>> TryGetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull;
}
