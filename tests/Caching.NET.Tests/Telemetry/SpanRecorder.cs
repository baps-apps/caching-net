using System.Diagnostics;

namespace Caching.NET.Tests.Telemetry;

internal sealed class SpanRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public SpanRecorder(params string[] sourceNames)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => sourceNames.Contains(source.Name, StringComparer.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_activities)
                {
                    _activities.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Activities
    {
        get
        {
            lock (_activities)
            {
                return _activities.ToArray();
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}
