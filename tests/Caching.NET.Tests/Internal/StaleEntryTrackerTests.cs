using System.Collections.Concurrent;
using System.Reflection;
using Caching.NET.Internal;

namespace Caching.NET.Tests.Internal;

public sealed class StaleEntryTrackerTests
{
    [Fact]
    public void Register_PrunesExpiredEntries_WhenTrackerGrows()
    {
        var tracker = new StaleEntryTracker();

        for (var i = 0; i < 5000; i++)
        {
            tracker.Register($"k{i}", TimeSpan.Zero, TimeSpan.Zero);
        }

        tracker.Register("trigger", TimeSpan.Zero, TimeSpan.Zero);

        var count = GetCount(tracker);
        Assert.True(count < 5001);
    }

    private static int GetCount(StaleEntryTracker tracker)
    {
        var field = typeof(StaleEntryTracker).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var entries = Assert.IsType<ConcurrentDictionary<string, StaleMetadata>>(field!.GetValue(tracker));
        return entries.Count;
    }
}
