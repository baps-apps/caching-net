using System.Diagnostics;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Internal;

/// <summary>
/// Implements <see cref="ICacheService"/> over the internal cache engine. This is the only type in
/// Caching.NET that calls an engine <i>operation</i>; <see cref="CacheEngineFactory"/> is the only
/// type that performs engine <i>setup</i>.
/// </summary>
/// <remarks>
/// Swapping engines means adding a sibling implementation of <see cref="ICacheService"/> and
/// changing the one line in <see cref="CacheEngineFactory"/> that constructs this class. This is
/// also the only type that emits Caching.NET's own <c>cache.*</c> operation spans: the engine's
/// telemetry sources are never registered, so every span a consumer sees originates here.
/// </remarks>
internal sealed class FusionCacheService : ICacheService
{
    private readonly IFusionCache _inner;
    private readonly CacheGuard _guard;
    private readonly CacheTelemetryContext _telemetry;
    private readonly JitterPolicy _jitter;

    public FusionCacheService(
        IFusionCache inner, CacheGuard guard, CacheTelemetryContext telemetry, JitterPolicy jitter)
    {
        _inner = inner;
        _guard = guard;
        _telemetry = telemetry;
        _jitter = jitter;
    }

    /// <summary>The engine cache, for disposal ordering in <see cref="CacheInstance"/>.</summary>
    public IFusionCache Inner => _inner;

    public string CacheName => _guard.CacheName;

    public async ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var materializedTags = Validate(key, tags);

        using var activity = _telemetry.StartOperation("cache.get_or_set", key);
        var factoryExecuted = false;
        var factoryFailed = false;

        try
        {
            var value = await _inner.GetOrSetAsync<TValue?>(
                key,
                async (ctx, ct) =>
                {
                    factoryExecuted = true;
                    var wrapped = new CacheFactoryContext<TValue>(ctx!, _jitter);
                    using var factoryActivity = _telemetry.StartActivity("cache.factory");
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        var produced = await factory(wrapped, ct).ConfigureAwait(false);
                        wrapped.ApplyOverrides();
                        factoryActivity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Hit);
                        _telemetry.RecordLayer(CacheLayers.Factory, "get", CacheResults.Hit, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        return produced;
                    }
                    catch (Exception ex)
                    {
                        // factoryExecuted/factoryFailed ARE read by this call, at the snapshot below —
                        // that read is the race: the engine can invoke this exact closure again in the
                        // background (eager refresh — see CacheEventBridge's remarks) while this
                        // foreground call is still between the await returning and taking its snapshot.
                        // If that write lands inside that window, THIS call's own outcome — which was
                        // actually a hit, since no exception reached here on the foreground path — gets
                        // misclassified as a miss (cache.result=miss, cache.layer=factory,
                        // factory_executed=true) for that one call. Accepted, not eliminated: not
                        // observed in 40 measured eager-refresh cycles, and bounded to a single
                        // misattributed span/counter pair rather than compounding.
                        factoryFailed = true;
                        // MarkError owns the cache.result tag, so a caller-cancelled factory is not
                        // overwritten back to "error" by a second SetTag here.
                        MarkError(factoryActivity, ex, ct);
                        _telemetry.RecordLayer(CacheLayers.Factory, "get", Outcome(ex, ct), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        throw;
                    }
                },
                ToMaybe(failSafeDefaultValue),
                Resolve(options),
                materializedTags,
                token).ConfigureAwait(false);

            // Snapshot once, immediately after the await, rather than re-reading the captured fields
            // at each use below: the engine can invoke this closure again in the background (eager
            // refresh) after this call has already returned, and while the timing means that write
            // and this read are not observed to race in practice, taking one consistent local copy
            // here — instead of reading factoryExecuted/factoryFailed three separate times further
            // down — removes any chance of this method observing two different values for what should
            // be a single fact about its own invocation.
            var executed = factoryExecuted;
            var failed = factoryFailed;

            // A factory that failed but a call that still returned a value means fail-safe served a
            // stale (or default) value instead of propagating the exception: report that as the
            // logical outcome, not as a miss.
            var result = failed ? CacheResults.Stale : executed ? CacheResults.Miss : CacheResults.Hit;

            if (activity is not null)
            {
                activity.SetTag(CacheTelemetryAttributes.Result, result);
                var layer = executed ? CacheLayers.Factory : ResolveHitLayer();
                if (layer is not null)
                {
                    activity.SetTag(CacheTelemetryAttributes.Layer, layer);
                }

                activity.SetTag(CacheTelemetryAttributes.FactoryExecuted, executed);
            }

            _telemetry.RecordOperation("get_or_set", result);
            RecordLogicalReadResult(result);

            return value;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue?>> factory,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Delegates to the context-taking overload with a factory that ignores the context, rather
        // than duplicating its guard/span/counter logic here: one code path means there is no second
        // place for those rules to drift.
        return GetOrSetAsync<TValue>(
            key,
            (_, ct) => factory(ct),
            default,
            options,
            tags,
            token);
    }

    public async ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.get_or_default", key);
        try
        {
            // Implemented via TryGetAsync rather than delegating straight to the engine's own
            // GetOrDefaultAsync: only TryGetAsync's MaybeValue tells the caller (and therefore
            // caching.net.hits/misses/operations) whether the read was a hit or a miss — comparing the
            // returned value against defaultValue cannot do that reliably, since a hit can legitimately
            // return a value equal to defaultValue. This previously blocked only the *span*'s
            // cache.result tag (no factory involved, so there is no fail-safe divergence between the
            // two methods to risk — fail-safe activates on factory failure, and neither of these reads
            // calls one); the span still deliberately carries no cache.result tag, matching
            // OperationSpanTests.WarmRead_EmitsItsOwnSpanWithNoResultTag, but the counters are not
            // bound by that choice.
            var result = await _inner.TryGetAsync<TValue>(key, Resolve(options), token).ConfigureAwait(false);
            var resultTag = result.HasValue ? CacheResults.Hit : CacheResults.Miss;
            _telemetry.RecordOperation("get_or_default", resultTag);
            RecordLogicalReadResult(resultTag);
            return result.HasValue ? result.Value : defaultValue;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public async ValueTask<CacheValue<TValue>> TryGetAsync<TValue>(
        string key,
        CacheEntryOverrides? options = null,
        CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.try_get", key);
        try
        {
            var result = await _inner.TryGetAsync<TValue>(key, Resolve(options), token).ConfigureAwait(false);
            var resultTag = result.HasValue ? CacheResults.Hit : CacheResults.Miss;
            activity?.SetTag(CacheTelemetryAttributes.Result, resultTag);
            _telemetry.RecordOperation("try_get", resultTag);
            RecordLogicalReadResult(resultTag);
            return result.HasValue ? CacheValue<TValue>.Of(result.Value) : CacheValue<TValue>.None;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public async ValueTask SetAsync<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        var materializedTags = Validate(key, tags);
        using var activity = _telemetry.StartOperation("cache.set", key);
        try
        {
            await _inner.SetAsync(key, value, Resolve(options), materializedTags, token).ConfigureAwait(false);
            // Tagged only after the write actually completes: tagging before the await would claim
            // success for a write that goes on to throw (a serialization error, or a Redis error
            // surfaced by ThrowOnDistributedCacheErrors).
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Set);
            _telemetry.RecordSet("set");
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public async ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.remove", key);
        try
        {
            // Awaited, not returned: a `using` span on a returned task closes when the method
            // returns, which would record the duration of starting the work rather than doing it.
            await _inner.RemoveAsync(key, Resolve(options), token).ConfigureAwait(false);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("remove");
            _telemetry.RecordOperation("remove", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public async ValueTask ExpireAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.expire", key);
        try
        {
            await _inner.ExpireAsync(key, Resolve(options), token).ConfigureAwait(false);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("expire");
            _telemetry.RecordOperation("expire", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public async ValueTask RemoveByTagAsync(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        // Checked on the verb, not delegated to the guard: CacheGuard.ValidateTags returns
        // immediately when Security.TagPolicy is Ignore, so with that policy a null or blank tag
        // reached the engine and the call silently did nothing. An unusable argument is an argument
        // error whatever the tag *limits* policy says.
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        _guard.ValidateTags([tag]);
        // The tag stands in for the key argument here, and is subject to the same
        // AllowRawKeysInTelemetry switch as every other operation span.
        using var activity = _telemetry.StartOperation("cache.remove_by_tag", tag);
        try
        {
            await _inner.RemoveByTagAsync(tag, Resolve(options), token).ConfigureAwait(false);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("remove_by_tag");
            _telemetry.RecordOperation("remove_by_tag", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public async ValueTask ClearAsync(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        // No key to attach: use the plain activity rather than StartOperation.
        using var activity = _telemetry.StartActivity("cache.clear");
        try
        {
            await _inner.ClearAsync(allowFailSafe, Resolve(options), token).ConfigureAwait(false);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("clear");
            _telemetry.RecordOperation("clear", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public TValue? GetOrSet<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, TValue?> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var materializedTags = Validate(key, tags);

        using var activity = _telemetry.StartOperation("cache.get_or_set", key);
        var factoryExecuted = false;
        var factoryFailed = false;

        try
        {
            var value = _inner.GetOrSet<TValue?>(
                key,
                (ctx, ct) =>
                {
                    factoryExecuted = true;
                    var wrapped = new CacheFactoryContext<TValue>(ctx!, _jitter);
                    using var factoryActivity = _telemetry.StartActivity("cache.factory");
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        var produced = factory(wrapped, ct);
                        wrapped.ApplyOverrides();
                        factoryActivity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Hit);
                        _telemetry.RecordLayer(CacheLayers.Factory, "get", CacheResults.Hit, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        return produced;
                    }
                    catch (Exception ex)
                    {
                        // See the async overload's identical comment: this call's own snapshot below
                        // does read this field, and a background eager-refresh re-invocation of this
                        // closure racing that read can misclassify this one foreground call's outcome
                        // as a miss instead of a hit. Accepted, not eliminated — see there for the
                        // measured bound.
                        factoryFailed = true;
                        // See the async overload: MarkError owns the cache.result tag.
                        MarkError(factoryActivity, ex, ct);
                        _telemetry.RecordLayer(CacheLayers.Factory, "get", Outcome(ex, ct), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        throw;
                    }
                },
                ToMaybe(failSafeDefaultValue),
                Resolve(options),
                materializedTags,
                token);

            // Snapshot once — see the async overload's identical comment.
            var executed = factoryExecuted;
            var failed = factoryFailed;

            // A factory that failed but a call that still returned a value means fail-safe served a
            // stale (or default) value instead of propagating the exception: report that as the
            // logical outcome, not as a miss.
            var result = failed ? CacheResults.Stale : executed ? CacheResults.Miss : CacheResults.Hit;

            if (activity is not null)
            {
                activity.SetTag(CacheTelemetryAttributes.Result, result);
                var layer = executed ? CacheLayers.Factory : ResolveHitLayer();
                if (layer is not null)
                {
                    activity.SetTag(CacheTelemetryAttributes.Layer, layer);
                }

                activity.SetTag(CacheTelemetryAttributes.FactoryExecuted, executed);
            }

            _telemetry.RecordOperation("get_or_set", result);
            RecordLogicalReadResult(result);

            return value;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public TValue? GetOrSet<TValue>(
        string key,
        Func<CancellationToken, TValue?> factory,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Same reasoning as the async overload above: delegate to the context-taking overload so
        // there is exactly one code path for the guards, spans and counters.
        return GetOrSet<TValue>(
            key,
            (_, ct) => factory(ct),
            default,
            options,
            tags,
            token);
    }

    public TValue? GetOrDefault<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        // No cache.result tag: see GetOrDefaultAsync above. Implemented via TryGet for the same
        // reason as GetOrDefaultAsync.
        using var activity = _telemetry.StartOperation("cache.get_or_default", key);
        try
        {
            var result = _inner.TryGet<TValue>(key, Resolve(options), token);
            var resultTag = result.HasValue ? CacheResults.Hit : CacheResults.Miss;
            _telemetry.RecordOperation("get_or_default", resultTag);
            RecordLogicalReadResult(resultTag);
            return result.HasValue ? result.Value : defaultValue;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public CacheValue<TValue> TryGet<TValue>(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.try_get", key);
        try
        {
            var result = _inner.TryGet<TValue>(key, Resolve(options), token);
            var resultTag = result.HasValue ? CacheResults.Hit : CacheResults.Miss;
            activity?.SetTag(CacheTelemetryAttributes.Result, resultTag);
            _telemetry.RecordOperation("try_get", resultTag);
            RecordLogicalReadResult(resultTag);
            return result.HasValue ? CacheValue<TValue>.Of(result.Value) : CacheValue<TValue>.None;
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public void Set<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        var materializedTags = Validate(key, tags);
        using var activity = _telemetry.StartOperation("cache.set", key);
        try
        {
            _inner.Set(key, value, Resolve(options), materializedTags, token);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Set);
            _telemetry.RecordSet("set");
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public void Remove(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.remove", key);
        try
        {
            _inner.Remove(key, Resolve(options), token);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("remove");
            _telemetry.RecordOperation("remove", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public void Expire(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.expire", key);
        try
        {
            _inner.Expire(key, Resolve(options), token);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("expire");
            _telemetry.RecordOperation("expire", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public void RemoveByTag(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        // See RemoveByTagAsync: the argument check does not depend on Security.TagPolicy.
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        _guard.ValidateTags([tag]);
        using var activity = _telemetry.StartOperation("cache.remove_by_tag", tag);
        try
        {
            _inner.RemoveByTag(tag, Resolve(options), token);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("remove_by_tag");
            _telemetry.RecordOperation("remove_by_tag", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    public void Clear(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        using var activity = _telemetry.StartActivity("cache.clear");
        try
        {
            _inner.Clear(allowFailSafe, Resolve(options), token);
            activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);
            _telemetry.RecordInvalidation("clear");
            _telemetry.RecordOperation("clear", CacheResults.Removed);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex, token);
            throw;
        }
    }

    /// <summary>
    /// Writes a health-check probe entry. Used only by <see cref="Health.CachingHealthCheck"/>.
    /// </summary>
    /// <remarks>
    /// When a distributed layer is configured, <c>ReThrowDistributedCacheExceptions</c> is forced on
    /// so a readiness probe cannot report healthy while the distributed layer is refusing writes.
    /// This is a probe-only concern: an ordinary <see cref="SetAsync{TValue}"/> call honours the
    /// cache's configured <c>Resilience.ThrowOnDistributedCacheErrors</c> instead, and
    /// <see cref="CacheEntryOverrides"/> deliberately has no per-call way to force this, since
    /// <see cref="Health.CachingHealthCheck"/>'s job — verifying infrastructure below the operation
    /// contract — is not a guarantee every other consumer needs or should be able to reach for. This
    /// probe path deliberately emits no Caching.NET span: it is infrastructure verification, not a
    /// cache operation an application performed.
    /// </remarks>
    internal ValueTask ProbeSetAsync<TValue>(
        string key, TValue value, CacheEntryOverrides probeOverrides, CancellationToken token)
    {
        var options = CacheEntryOverridesMapper.Apply(probeOverrides, _inner.CreateEntryOptions(), _jitter);

        if (_inner.HasDistributedCache)
        {
            options.ReThrowDistributedCacheExceptions = true;
        }

        return _inner.SetAsync(key, value, options, tags: null, token);
    }

    /// <summary>
    /// Reads a health-check probe entry. Used only by <see cref="Health.CachingHealthCheck"/>.
    /// </summary>
    /// <remarks>
    /// When a distributed layer is configured, the read bypasses the memory layer so that it
    /// actually reaches the distributed layer. Without this, a Hybrid probe reads back the value its
    /// own write just placed in L1 and reports healthy without ever contacting the distributed layer
    /// — including while that layer is down. The memory layer is left alone when there is no
    /// distributed layer, because in InMemory mode skipping it would turn every probe into a miss.
    /// <c>EagerRefreshThreshold</c> is also cleared so a configured eager refresh can never turn a
    /// probe read into a background factory execution.
    /// </remarks>
    internal async ValueTask<CacheValue<TValue>> ProbeTryGetAsync<TValue>(
        string key, CacheEntryOverrides probeOverrides, CancellationToken token)
    {
        var options = CacheEntryOverridesMapper.Apply(probeOverrides, _inner.CreateEntryOptions(), _jitter);
        options.EagerRefreshThreshold = null;

        if (_inner.HasDistributedCache)
        {
            options.ReThrowDistributedCacheExceptions = true;
            options.SetSkipMemoryCacheRead(true);
        }

        var result = await _inner.TryGetAsync<TValue>(key, options, token).ConfigureAwait(false);
        return result.HasValue ? CacheValue<TValue>.Of(result.Value) : CacheValue<TValue>.None;
    }

    private FusionCacheEntryOptions? Resolve(CacheEntryOverrides? options)
        => CacheEntryOverridesMapper.Resolve(options, _inner, _jitter);

    /// <summary>
    /// The layer a cache <i>hit</i> is attributed to, or <c>null</c> when it cannot be known. The
    /// engine does not report which level actually answered on the common path, but
    /// <see cref="CacheMode.InMemory"/> and <see cref="CacheMode.Redis"/> each use exactly one layer
    /// (see <see cref="CacheEngineFactory.MapEntryOptions"/>), so a hit there is tautologically
    /// attributable. In <see cref="CacheMode.Hybrid"/>, a hit can be answered by either layer — most
    /// notably L2 after an L1 miss, which is exactly the case an operator investigates (cold instance,
    /// post-deploy, post-eviction, short L1 TTL) — and the engine's <c>Hit</c> event carries no level
    /// information on the common path. Reporting <c>memory</c> when Redis actually answered is worse
    /// than reporting nothing, so Hybrid omits the <c>cache.layer</c> tag entirely; per-layer truth for
    /// Hybrid lives on the decorator-owned <c>caching.net.layer.duration</c> metric and on the
    /// <c>cache.memory.*</c>/<c>cache.redis.*</c> child spans instead.
    /// </summary>
    private string? ResolveHitLayer() => _telemetry.Mode switch
    {
        nameof(CacheMode.InMemory) => CacheLayers.Memory,
        nameof(CacheMode.Redis) => CacheLayers.Redis,
        _ => null
    };

    /// <summary>
    /// Records <c>caching.net.hits</c>/<c>caching.net.misses</c> for one logical read, from the
    /// caller-facing result already computed for the span/operation tag. A <see cref="CacheResults.Stale"/>
    /// result still counts as a hit — the value came from the cache, just past its logical expiration
    /// — so the hit ratio in <c>docs/TELEMETRY.md</c> stays meaningful; only <see cref="CacheResults.Miss"/>
    /// counts as a miss.
    /// </summary>
    private void RecordLogicalReadResult(string result)
    {
        if (result == CacheResults.Miss)
        {
            _telemetry.RecordMiss("get");
        }
        else
        {
            _telemetry.RecordHit("get");
        }
    }

    /// <summary>
    /// Marks a span's failure outcome and records the exception type, without the exception message.
    /// </summary>
    /// <remarks>
    /// A cancellation the caller asked for is not a cache fault and is deliberately <b>not</b> marked
    /// <see cref="ActivityStatusCode.Error"/>. In ASP.NET Core the ambient token is
    /// <c>HttpContext.RequestAborted</c>, so every client that navigates away mid-request cancels the
    /// cache calls on that request; marking those Error made ordinary user behaviour indistinguishable
    /// from a Redis outage on an error-rate dashboard. Such a span is tagged
    /// <c>cache.result=canceled</c> instead, so the outcome is still visible and still queryable.
    /// <para>
    /// The check is deliberately narrower than "is an <see cref="OperationCanceledException"/>": it
    /// also requires <paramref name="token"/> to actually be cancelled. A cache that surfaces an
    /// <see cref="OperationCanceledException"/> from somewhere the caller did not ask for — an
    /// internal timeout, a disposed connection cancelling its own work — keeps reporting an error,
    /// which is what it is.
    /// </para>
    /// </remarks>
    private static void MarkError(Activity? activity, Exception ex, CancellationToken token)
    {
        if (activity is null)
        {
            return;
        }

        var outcome = Outcome(ex, token);
        activity.SetTag(CacheTelemetryAttributes.Result, outcome);

        if (outcome == CacheResults.Canceled)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(CacheTelemetryAttributes.ErrorType, ex.GetType().Name);
    }

    /// <summary>
    /// The <c>cache.result</c> value for a failed operation: <see cref="CacheResults.Canceled"/> when
    /// the caller's own token asked for it, otherwise <see cref="CacheResults.Error"/>. Shared by the
    /// span tag and the layer-duration dimension so a cancellation is never counted as a factory
    /// error in one place and a cancellation in the other.
    /// </summary>
    private static string Outcome(Exception ex, CancellationToken token)
        => ex is OperationCanceledException && token.IsCancellationRequested
            ? CacheResults.Canceled
            : CacheResults.Error;

    /// <summary>
    /// Validates the key, and the tags when any were supplied. Tags are materialised once so the
    /// guard and the engine never enumerate a lazy sequence twice.
    /// </summary>
    private string[]? Validate(string key, IEnumerable<string>? tags)
    {
        _guard.ValidateKey(key);

        if (tags is null)
        {
            return null;
        }

        var materialized = tags as string[] ?? [.. tags];
        _guard.ValidateTags(materialized);
        return materialized;
    }

    private static MaybeValue<TValue?> ToMaybe<TValue>(CacheValue<TValue?> value)
        => value.HasValue ? MaybeValue<TValue?>.FromValue(value.Value) : default;
}
