using System.Diagnostics.Metrics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

internal static class MeterListenerHelpers
{
    public static MeterListener ForCounter(string instrumentName, out List<long> observed)
    {
        var capture = new List<long>();
        observed = capture;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CacheInstruments.MeterName && instr.Name == instrumentName)
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => capture.Add(value));
        listener.Start();
        return listener;
    }

    public static MeterListener ForCounterWithTags(string instrumentName, out List<(long value, Dictionary<string, object?> tags)> observed)
    {
        var capture = new List<(long, Dictionary<string, object?>)>();
        observed = capture;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CacheInstruments.MeterName && instr.Name == instrumentName)
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kv in tags) dict[kv.Key] = kv.Value;
            capture.Add((value, dict));
        });
        listener.Start();
        return listener;
    }
}
