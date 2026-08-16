using System.Diagnostics;
using Caching.NET.Telemetry;

namespace Caching.NET.Benchmark;

/// <summary>
/// Pins the ambient span context a benchmark row is measuring: a request-path probe that runs under
/// a caller's span, or a background probe that runs under none.
/// </summary>
/// <remarks>
/// <para>
/// Caching.NET's layer decorators start a span per probe, and whether that span has a parent is what
/// separates a request-path probe from one issued on a backplane or background thread. The two are
/// different measurements with different costs, so every listener-attached row has to declare which
/// one it is — a row that leaves <see cref="Activity.Current"/> alone measures whatever the harness
/// happened to leave in the ambient context, which is a coin flip rather than a measurement.
/// </para>
/// <para>
/// The write happens per invocation, from inside the benchmark method, not once from
/// <c>[GlobalSetup]</c>. <see cref="Activity.Current"/> is an <see cref="System.Threading.AsyncLocal{T}"/>,
/// so an assignment made inside an async benchmark method belongs to that invocation's execution
/// context and is gone before the next invocation begins.
/// </para>
/// <para>
/// <see cref="Detached"/> exists so that a no-listener row can pay the same AsyncLocal write as the
/// listener-attached row it is compared against. Without it, the delta between the two rows would
/// include one arm's extra bookkeeping instead of being purely the cost of the span.
/// </para>
/// <para>
/// Listener attachment is lazy and static rather than done in <c>[GlobalSetup]</c>, for the same
/// reason spelled out in <see cref="LayerDecoratorBenchmarks"/>: BDN runs each benchmark method as
/// its own process, and <c>[GlobalSetup]</c> runs in every one of them regardless of which single row
/// that process is about to time. Attaching lazily keeps a listener out of the processes timing the
/// rows that are supposed to have none.
/// </para>
/// </remarks>
internal static class TracingScope
{
    // Deliberately not Caching.NET's own source: the parent span stands in for an application's
    // request span, which is what a real parented probe hangs off.
    private static readonly ActivitySource ParentSource = new("Caching.NET.Benchmark.Host");

    private static ActivityListener? s_listener;
    private static Activity? s_parent;
    private static Activity? s_stale;
    private static ExecutionContext? s_staleContext;

    /// <summary>Listener attached, no ambient parent — the background and backplane path.</summary>
    public static void Parentless()
    {
        EnsureListener();
        Activity.Current = null;
    }

    /// <summary>
    /// Runs <paramref name="callback"/> with a listener attached and an ambient span that has already
    /// ended — what engine background work actually sees, since ambient context flows into it from
    /// whichever request scheduled it, and that request usually finishes first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the pair of rows that says whether the default is worth anything. A gate keyed on
    /// <c>Activity.Current is null</c> would treat this as parented and emit the span; the shipped
    /// gate treats an ended span as no parent, so the <c>WhenParented</c> row should land on
    /// <see cref="Parentless"/>'s number while the <c>Always</c> row pays for a span.
    /// </para>
    /// <para>
    /// It has to be reached through a restored <see cref="ExecutionContext"/>, because
    /// <c>Activity.Current = someStoppedActivity</c> cannot set it up: the property setter rejects a
    /// finished activity outright, leaving <c>Current</c> null — measured at ~7.6&#160;µs and 192&#160;B
    /// for the rejected assignment alone, which would be most of what such a row reported. Context
    /// <i>restore</i> takes no such view, which is exactly why the stopped span reaches background
    /// work in the first place. <c>Task.Run</c> was confirmed to behave identically; this is the same
    /// mechanism with the scheduling removed, and costs ~11&#160;ns and no allocation, paid equally by
    /// both rows it is used for.
    /// </para>
    /// </remarks>
    public static void RunUnderStaleParent(ContextCallback callback, object? state)
    {
        EnsureListener();
        ExecutionContext.Run(s_staleContext ??= CaptureStale(), callback, state);
    }

    private static ExecutionContext CaptureStale()
    {
        var previous = Activity.Current;

        s_stale = ParentSource.StartActivity("bench.finished-request")
            ?? throw new InvalidOperationException(
                $"No listener sampled {ParentSource.Name}, so there is no span to capture and stop.");

        var captured = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context capture is suppressed here.");

        s_stale.Stop();
        Activity.Current = previous;
        return captured;
    }

    /// <summary>Listener attached, running under a caller's span — the request path.</summary>
    public static void Parented()
    {
        EnsureListener();
        Activity.Current = s_parent ??= ParentSource.StartActivity("bench.request")
            ?? throw new InvalidOperationException(
                $"No listener sampled {ParentSource.Name}, so there is no parent span to measure under.");
    }

    /// <summary>No listener, no ambient parent — pays the AsyncLocal write and nothing else.</summary>
    public static void Detached() => Activity.Current = null;

    /// <summary>Releases the parent span and listener. Call from <c>[GlobalCleanup]</c>.</summary>
    public static void Reset()
    {
        s_parent?.Dispose();
        s_parent = null;
        s_stale?.Dispose();
        s_stale = null;
        s_staleContext = null;
        s_listener?.Dispose();
        s_listener = null;
    }

    private static void EnsureListener()
    {
        if (s_listener is not null)
        {
            return;
        }

        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == CacheTelemetry.ActivitySourceName || source.Name == ParentSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };

        ActivitySource.AddActivityListener(listener);
        s_listener = listener;
    }
}
