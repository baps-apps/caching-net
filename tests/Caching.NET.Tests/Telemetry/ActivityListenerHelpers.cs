using System.Diagnostics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

internal static class ActivityListenerHelpers
{
    /// <summary>
    /// Captures every completed Caching.NET activity. The ActivitySource is process-wide, so filter
    /// the returned list (by tag, e.g. cache.key_hash) rather than asserting on its total count.
    /// </summary>
    public static (List<Activity> activities, ActivityListener listener) Capture()
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CacheInstruments.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (captured) captured.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return (captured, listener);
    }

    public static string? Tag(this Activity activity, string name) =>
        activity.GetTagItem(name)?.ToString();

    /// <summary>
    /// Copies the captured list under the same lock <see cref="Capture"/>'s <c>ActivityStopped</c>
    /// callback uses to add to it. Every assertion that enumerates a captured list (instead of adding
    /// to or clearing it under the lock itself) should snapshot first: activities complete — and get
    /// added — on whatever thread finishes the call, which for background stale refreshes is a
    /// ThreadPool thread that can race a foreground `Where`/`Single`/`Contains` over the same list.
    /// </summary>
    public static List<Activity> Snapshot(this List<Activity> activities)
    {
        lock (activities) return new List<Activity>(activities);
    }

    /// <summary>Clears the captured list under the same lock used to add to it.</summary>
    public static void ClearCaptured(this List<Activity> activities)
    {
        lock (activities) activities.Clear();
    }

    /// <summary>
    /// Captures Caching.NET activities within a test-scoped trace. Establishes a parent Activity context
    /// so that cache calls have a unique TraceId, preventing cross-test interference when xUnit parallelizes.
    /// The returned scope must be disposed to cleanly finalize the parent activity.
    /// </summary>
    public static TraceScope CaptureWithTraceScope()
    {
        var testSource = new ActivitySource("test-scope");
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == CacheInstruments.ActivitySourceName || source.Name == "test-scope",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (captured) captured.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        // Create and set the parent activity as current without disposing it immediately,
        // so child activities inherit its TraceId.
        var parent = testSource.StartActivity("test-scope");
        if (parent is null)
            throw new InvalidOperationException("Failed to create parent activity for trace scope");
        return new TraceScope(captured, listener, parent, testSource);
    }

    /// <summary>
    /// Scope returned by CaptureWithTraceScope that manages parent activity lifecycle and cleanup.
    /// </summary>
    public class TraceScope : IDisposable
    {
        private readonly Activity _parent;
        private readonly ActivitySource _source;
        public List<Activity> Activities { get; }
        public ActivityListener Listener { get; }
        public ActivityTraceId TraceId { get; }

        internal TraceScope(List<Activity> activities, ActivityListener listener, Activity parent, ActivitySource source)
        {
            Activities = activities;
            Listener = listener;
            _parent = parent;
            _source = source;
            TraceId = parent.TraceId;
        }

        /// <summary>Copies <see cref="Activities"/> under the same lock the capturing listener adds under.</summary>
        public List<Activity> Snapshot() => Activities.Snapshot();

        public void Dispose()
        {
            _parent?.Dispose();
            Listener?.Dispose();
            _source?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
