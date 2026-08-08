using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Internal;

/// <summary>
/// Turns Caching.NET's jitter settings into the absolute jitter window the engine understands.
/// </summary>
/// <remarks>
/// <para>
/// The engine takes only an absolute <c>JitterMaxDuration</c>. An absolute window is the wrong shape
/// for short-lived entries: with the old fixed 2-second default, an entry configured to live 300 ms
/// could survive 2.3 s — jitter was <b>seven times</b> the lifetime the caller asked for, which is
/// not spreading expirations, it is ignoring the duration. The same 2 seconds against a 10-minute
/// entry is a rounding error, which is why the problem stayed invisible at the durations the default
/// was chosen for.
/// </para>
/// <para>
/// So Caching.NET expresses jitter as a <i>fraction of the entry's own lifetime</i>, and keeps the
/// absolute value as a ceiling: <c>min(duration × fraction, maxDuration)</c>. A 10-minute entry
/// still gets the familiar 2 s (60 s proposed, capped), so long-lived entries behave exactly as
/// before; a 300 ms entry now gets 30 ms instead of 2 s. Setting
/// <see cref="Options.CacheEntryOptions.JitterFraction"/> to <c>null</c> restores the pure absolute
/// behaviour.
/// </para>
/// <para>
/// The base duration is the <b>shortest</b> lifetime that governs the entry, not simply
/// <c>Duration</c>: an entry with a 10-minute logical duration but a 200 ms memory duration is a
/// short-lived entry in the layer that will actually expire it, and jitter has to respect that.
/// </para>
/// </remarks>
internal readonly struct JitterPolicy
{
    private readonly double? _fraction;
    private readonly TimeSpan _maxDuration;

    public JitterPolicy(double? fraction, TimeSpan maxDuration)
    {
        _fraction = fraction;
        _maxDuration = maxDuration;
    }

    /// <summary>The same policy with <paramref name="fraction"/> in place of the configured one.</summary>
    public JitterPolicy WithFraction(double? fraction) => new(fraction, _maxDuration);

    /// <summary>The absolute jitter window for an entry whose shortest lifetime is <paramref name="duration"/>.</summary>
    public TimeSpan For(TimeSpan duration)
    {
        if (_maxDuration <= TimeSpan.Zero)
        {
            // Jitter explicitly disabled: no fraction can reintroduce it.
            return TimeSpan.Zero;
        }

        if (_fraction is not { } fraction || fraction <= 0)
        {
            // No fraction configured: the absolute setting is the whole policy, as it was before
            // proportional jitter existed.
            return _maxDuration;
        }

        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var proportional = duration * fraction;
        return proportional < _maxDuration ? proportional : _maxDuration;
    }

    /// <summary>
    /// The shortest lifetime governing an entry: its logical duration, or either layer's own
    /// duration when one is set and shorter.
    /// </summary>
    public static TimeSpan ShortestDuration(
        TimeSpan duration, TimeSpan? memoryDuration, TimeSpan? distributedDuration)
    {
        var shortest = duration;

        if (memoryDuration is { } memory && memory < shortest)
        {
            shortest = memory;
        }

        if (distributedDuration is { } distributed && distributed < shortest)
        {
            shortest = distributed;
        }

        return shortest;
    }

    /// <summary>The shortest lifetime governing an already-built engine entry-options instance.</summary>
    public static TimeSpan ShortestDuration(FusionCacheEntryOptions options)
        => ShortestDuration(options.Duration, options.MemoryCacheDuration, options.DistributedCacheDuration);
}
