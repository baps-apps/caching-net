using Caching.NET.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET;

/// <summary>
/// Passed to a get-or-set factory. Exposes the stale value when one exists, conditional-request
/// metadata, and per-execution overrides for adaptive expiration.
/// </summary>
/// <typeparam name="TValue">The cached value type.</typeparam>
/// <example>
/// <code><![CDATA[
/// var order = await cache.GetOrSetAsync("Order:42", async (ctx, ct) =>
/// {
///     var response = await http.GetAsync($"/orders/42?etag={ctx.ETag}", ct);
///     if (response.StatusCode == HttpStatusCode.NotModified)
///     {
///         return ctx.NotModified();
///     }
///
///     ctx.ETag = response.Headers.ETag?.Tag;
///     ctx.Overrides.DistributedExpiration = TimeSpan.FromMinutes(30);
///     return await response.Content.ReadFromJsonAsync<Order>(ct);
/// });
/// ]]></code>
/// </example>
public sealed class CacheFactoryContext<TValue>
{
    private readonly FusionCacheFactoryExecutionContext<TValue>? _inner;
    private readonly Internal.JitterPolicy _jitter;
    private string? _detachedETag;
    private DateTimeOffset? _detachedLastModified;

    internal CacheFactoryContext(FusionCacheFactoryExecutionContext<TValue> inner, Internal.JitterPolicy jitter)
    {
        _inner = inner;
        _jitter = jitter;
        Overrides = new CacheEntryOverrides();
    }

    /// <summary>Context for a disabled cache: no stale value, nothing to adapt.</summary>
    internal CacheFactoryContext()
    {
        _inner = null;
        Overrides = new CacheEntryOverrides();
    }

    /// <summary>Whether a previously cached value is available for conditional refresh.</summary>
    public bool HasStaleValue => _inner?.HasStaleValue ?? false;

    /// <summary>The previously cached value, when one exists.</summary>
    public CacheValue<TValue> StaleValue => _inner is { HasStaleValue: true }
        ? CacheValue<TValue>.Of(_inner.StaleValue.Value)
        : CacheValue<TValue>.None;

    /// <summary>Entity tag carried with the cached entry, for conditional requests.</summary>
    public string? ETag
    {
        get => _inner is null ? _detachedETag : _inner.ETag;
        set
        {
            if (_inner is null)
            {
                _detachedETag = value;
            }
            else
            {
                _inner.ETag = value;
            }
        }
    }

    /// <summary>Last-modified timestamp carried with the cached entry.</summary>
    public DateTimeOffset? LastModified
    {
        get => _inner is null ? _detachedLastModified : _inner.LastModified;
        set
        {
            if (_inner is null)
            {
                _detachedLastModified = value;
            }
            else
            {
                _inner.LastModified = value;
            }
        }
    }

    /// <summary>
    /// Overrides applied to the entry this execution produces. Set any property to change the
    /// entry's behaviour for this execution only; unset properties keep the configured defaults.
    /// </summary>
    public CacheEntryOverrides Overrides { get; }

    /// <summary>
    /// Signals that the upstream value has not changed, so the existing cached entry is kept and
    /// its lifetime restarted.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no stale value to keep.</exception>
    public TValue NotModified()
    {
        if (_inner is not { HasStaleValue: true })
        {
            throw new InvalidOperationException("NotModified() requires a stale value. Check HasStaleValue first.");
        }

        return _inner.NotModified();
    }

    /// <summary>
    /// Signals that the upstream failed in a way that does not warrant an exception, so fail-safe
    /// serves the stale value if one exists.
    /// </summary>
    /// <param name="reason">Recorded by the engine for diagnostics.</param>
    public TValue Fail(string reason) => _inner is null
        ? throw new InvalidOperationException("Fail() is not available on a disabled cache.")
        : _inner.Fail(reason);

    /// <summary>Applies <see cref="Overrides"/> onto the engine context. Called by the adapter.</summary>
    /// <remarks>
    /// Mutates the engine's options object in place rather than assigning a new one: the engine's
    /// own idiom is in-place mutation and <c>Options</c> may be get-only.
    /// </remarks>
    internal void ApplyOverrides()
    {
        if (_inner is null)
        {
            return;
        }

        // The cache's jitter policy comes along so that an adaptive override which shortens the
        // entry shortens its jitter with it, exactly as a per-call override does.
        Internal.CacheEntryOverridesMapper.Apply(Overrides, _inner.Options, _jitter);
    }
}
