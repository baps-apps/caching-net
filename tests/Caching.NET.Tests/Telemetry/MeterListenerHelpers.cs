using System.Diagnostics.Metrics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

internal static class MeterListenerHelpers
{
    public static (List<(T value, KeyValuePair<string, object?>[] tags)> values, MeterListener listener)
        CaptureCounter<T>(string instrumentName, string modeTag) where T : struct
    {
        var values = new List<(T value, KeyValuePair<string, object?>[] tags)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == CacheInstruments.MeterName && instr.Name == instrumentName)
                    l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "cache.mode" && (string?)t.Value == modeTag)
                {
                    values.Add((value, tags.ToArray()));
                    return;
                }
        });
        listener.Start();
        return (values, listener);
    }

    public static (List<(T value, KeyValuePair<string, object?>[] tags)> values, MeterListener listener)
        CaptureHistogram<T>(string instrumentName, string modeTag) where T : struct
    {
        var values = new List<(T value, KeyValuePair<string, object?>[] tags)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == CacheInstruments.MeterName && instr.Name == instrumentName)
                    l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "cache.mode" && (string?)t.Value == modeTag)
                {
                    values.Add((value, tags.ToArray()));
                    return;
                }
        });
        listener.Start();
        return (values, listener);
    }

    public static (List<(T value, KeyValuePair<string, object?>[] tags)> values, MeterListener listener)
        CaptureUpDownCounter<T>(string instrumentName, string modeTag) where T : struct
    {
        var values = new List<(T value, KeyValuePair<string, object?>[] tags)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == CacheInstruments.MeterName && instr.Name == instrumentName)
                    l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "cache.mode" && (string?)t.Value == modeTag)
                {
                    values.Add((value, tags.ToArray()));
                    return;
                }
        });
        listener.Start();
        return (values, listener);
    }

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
