namespace Caching.NET.Options;

/// <summary>
/// When the infrastructure decorators emit a span for a single layer probe.
/// </summary>
/// <remarks>
/// <para>
/// A layer span (<c>cache.memory.*</c>, <c>cache.redis.*</c>, <c>cache.serialize</c>,
/// <c>cache.deserialize</c>, <c>cache.backplane.publish</c>) describes one physical probe, which is
/// only meaningful next to the operation that caused it. When a cache call runs on a request thread
/// the probe has that context, because the caller's span is its parent. When the same probe is
/// issued on a backplane subscriber or an engine background thread it has no <i>live</i> span above
/// it, and becomes its own root trace: a sub-millisecond span, alone, with nothing indicating what
/// caused it or what it belonged to.
/// </para>
/// <para>
/// "Has a parent" means a parent that has not ended. Ambient trace context flows with the
/// <see cref="System.Threading.ExecutionContext"/>, so engine background work usually still carries
/// the span of the request that scheduled it, finished well before the probe runs. Treating that as
/// a parent would suppress almost nothing and would file each probe under an unrelated request as a
/// child starting after its parent ended, so a stopped ambient span counts as no parent.
/// </para>
/// <para>
/// This does not gate <i>measurement</i>. Per-layer duration on <c>caching.net.layer.duration</c>,
/// payload size, hit and miss counters are all recorded regardless — dropping a span drops the trace
/// noise, not the timing. Operation spans (<c>cache.get_or_set</c>, <c>cache.factory</c>,
/// <c>cache.backplane.receive</c> and the rest) are never gated by this setting, so background work
/// keeps a root span of its own for its probes to hang from.
/// </para>
/// </remarks>
public enum CacheLayerTracing
{
    /// <summary>
    /// Emit a layer span for every probe, parented or not. The pre-3.1 behaviour: background and
    /// backplane probes each produce a single-span root trace.
    /// </summary>
    Always,

    /// <summary>
    /// Emit a layer span only when the probe runs under a span that is still running. The default:
    /// request-path probes stay fully traced, and probes with no live caller to attach to are
    /// measured but not traced.
    /// </summary>
    WhenParented,

    /// <summary>
    /// Never emit layer spans. Operation spans and every metric are unaffected.
    /// </summary>
    Never
}
