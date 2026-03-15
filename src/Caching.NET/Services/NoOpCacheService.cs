namespace Caching.NET.Services;

/// <summary>
/// No-op <see cref="Abstractions.ICacheService"/> implementation used when caching is disabled via configuration.
/// Registered by DI when <c>CacheOptions.Enabled</c> is false so consumers can keep depending on <see cref="Abstractions.ICacheService"/>
/// without needing null checks or feature flags. <see cref="GetOrCreateAsync{T}"/> always executes the factory; Set and Remove methods are no-op.
/// </summary>
public sealed class NoOpCacheService : Abstractions.ICacheService
{
    /// <inheritdoc />
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        return factory(cancellationToken);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
