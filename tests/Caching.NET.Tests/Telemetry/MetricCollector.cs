using System.Diagnostics.Metrics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

/// <summary>Collects Caching.NET metric measurements for assertions.</summary>
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];
    private readonly object _gate = new();

    public MetricCollector(params string[] instrumentNames)
    {
        var wanted = new HashSet<string>(instrumentNames, StringComparer.Ordinal);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CacheTelemetry.MeterName
                && (wanted.Count == 0 || wanted.Contains(instrument.Name)))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return _measurements.ToArray();
            }
        }
    }

    public long Total(string instrumentName)
        => Measurements.Where(m => m.Instrument == instrumentName).Sum(m => (long)m.Value);

    /// <summary>
    /// Every measurement one named cache produced. The listener is process-wide, so an assertion
    /// scoped to a single cache has to say so: <c>cache.name</c> is on every measurement, which
    /// makes it the isolation the assertion should lean on rather than on nothing else happening to
    /// run at the same time.
    /// </summary>
    public IReadOnlyList<Measurement> Own(string cacheName)
        => Measurements
            .Where(m => Equals(m.Tags.GetValueOrDefault(CacheTelemetryAttributes.Name), cacheName))
            .ToArray();

    /// <summary>Total for one instrument, restricted to one named cache. See <see cref="Own"/>.</summary>
    public long Total(string instrumentName, string cacheName)
        => Own(cacheName).Where(m => m.Instrument == instrumentName).Sum(m => (long)m.Value);

    public IEnumerable<Measurement> For(string instrumentName)
        => Measurements.Where(m => m.Instrument == instrumentName);

    public IReadOnlyCollection<string> AllTagKeys()
        => AllTagKeys(Measurements);

    public IReadOnlyCollection<string> AllTagKeys(IEnumerable<Measurement> measurements)
        => measurements.SelectMany(m => m.Tags.Keys).Distinct(StringComparer.Ordinal).ToArray();

    public IReadOnlyCollection<string> AllTagValues()
        => AllTagValues(Measurements);

    public IReadOnlyCollection<string> AllTagValues(IEnumerable<Measurement> measurements)
        => measurements
            .SelectMany(m => m.Tags.Values)
            .Select(v => v?.ToString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument, value, map));
        }
    }

    public void Flush() => _listener.RecordObservableInstruments();

    /// <summary>
    /// Waits for measurements to arrive. Cache events are dispatched on the engine's background
    /// event pump — deliberately, so telemetry never sits on the caller's path — so an assertion
    /// made immediately after a cache call can race the recorder.
    /// </summary>
    public async Task<bool> WaitForAsync(Func<MetricCollector, bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition(this))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition(this);
    }

    public void Dispose() => _listener.Dispose();

    internal sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
}
