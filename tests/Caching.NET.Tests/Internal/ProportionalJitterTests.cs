using Caching.NET.Internal;
using Caching.NET.Options;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// Jitter is a fraction of the entry's own lifetime, capped by <c>Entry.JitterMaxDuration</c>.
/// </summary>
/// <remarks>
/// The flat 2-second window this replaces was a rounding error against a 10-minute entry and
/// <b>seven times</b> the lifetime of a 300 ms one — an entry could comfortably outlive the duration
/// its caller asked for. These tests pin both halves: long-lived entries keep the old behaviour
/// exactly, short-lived ones scale.
/// </remarks>
public class ProportionalJitterTests
{
    private static CachingOptions Options(Action<CachingOptions> configure)
    {
        var options = new CachingOptions { ApplicationPrefix = "tests" };
        configure(options);
        return options;
    }

    private static TimeSpan MappedJitter(CachingOptions options)
        => CacheEngineFactory.MapEntryOptions(options).JitterMaxDuration;

    [Fact]
    public void ALongLivedEntryKeepsTheFamiliarTwoSecondWindow()
    {
        // 10 minutes x 0.1 = 60s, capped at the 2s default. Unchanged from flat jitter, which is the
        // point: the durations the old default was chosen for must not move.
        var jitter = MappedJitter(Options(o => o.DefaultExpiration = TimeSpan.FromMinutes(10)));

        Assert.Equal(TimeSpan.FromSeconds(2), jitter);
    }

    [Fact]
    public void AShortLivedEntryGetsProportionalJitterRatherThanTheCap()
    {
        var jitter = MappedJitter(Options(o => o.DefaultExpiration = TimeSpan.FromMilliseconds(300)));

        Assert.Equal(TimeSpan.FromMilliseconds(30), jitter);
    }

    /// <summary>
    /// An entry with a long logical duration but a short layer duration is a short-lived entry in the
    /// layer that will actually expire it, so that is the duration jitter has to respect.
    /// </summary>
    [Fact]
    public void TheShortestLayerDurationDrivesTheWindow()
    {
        var jitter = MappedJitter(Options(o =>
        {
            o.DefaultExpiration = TimeSpan.FromMinutes(10);
            o.Entry.LocalExpiration = TimeSpan.FromMilliseconds(200);
        }));

        Assert.Equal(TimeSpan.FromMilliseconds(20), jitter);
    }

    [Fact]
    public void ANullFractionRestoresTheFlatAbsoluteWindow()
    {
        var jitter = MappedJitter(Options(o =>
        {
            o.DefaultExpiration = TimeSpan.FromMilliseconds(300);
            o.Entry.JitterFraction = null;
        }));

        Assert.Equal(TimeSpan.FromSeconds(2), jitter);
    }

    [Fact]
    public void AZeroCapDisablesJitterEntirely_NoFractionCanReintroduceIt()
    {
        var jitter = MappedJitter(Options(o =>
        {
            o.DefaultExpiration = TimeSpan.FromMinutes(10);
            o.Entry.JitterMaxDuration = TimeSpan.Zero;
            o.Entry.JitterFraction = 0.5;
        }));

        Assert.Equal(TimeSpan.Zero, jitter);
    }

    [Fact]
    public void APerCallOverrideThatShortensTheEntryShortensItsJitter()
    {
        using var host = TestHost.BuildInMemory(c => c.WithDefaultExpiration(TimeSpan.FromMinutes(10)));
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides { LocalExpiration = TimeSpan.FromMilliseconds(300) },
            inner,
            host.JitterPolicy());

        // Without recomputation this kept the 2s window sized for the 10-minute default — the exact
        // mismatch proportional jitter exists to remove, reintroduced one layer up.
        Assert.Equal(TimeSpan.FromMilliseconds(30), resolved!.JitterMaxDuration);
    }

    [Fact]
    public void AnExplicitPerCallJitterMaxDurationIsHonouredLiterally()
    {
        using var host = TestHost.BuildInMemory(c => c.WithDefaultExpiration(TimeSpan.FromMinutes(10)));
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides
            {
                LocalExpiration = TimeSpan.FromMilliseconds(300),
                JitterMaxDuration = TimeSpan.FromSeconds(5)
            },
            inner,
            host.JitterPolicy());

        Assert.Equal(TimeSpan.FromSeconds(5), resolved!.JitterMaxDuration);
    }

    [Fact]
    public void APerCallFractionOverridesTheConfiguredOne()
    {
        using var host = TestHost.BuildInMemory(c => c.WithDefaultExpiration(TimeSpan.FromSeconds(10)));
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides { JitterFraction = 0.05 },
            inner,
            host.JitterPolicy());

        Assert.Equal(TimeSpan.FromMilliseconds(500), resolved!.JitterMaxDuration);
    }

    /// <summary>
    /// The behaviour the change exists for, observed end to end rather than through the mapping.
    /// </summary>
    /// <remarks>
    /// A 300 ms entry read 900 ms later must be gone. Under the old flat 2-second jitter the same
    /// entry could still be served — it outlived its stated duration by up to 7x — which is precisely
    /// what this asserts is no longer possible. The wait is a multiple of the entry's whole lifetime
    /// including its maximum jitter (300 ms + 30 ms), so it is not a tight timing race.
    /// </remarks>
    [Fact]
    public async Task AShortLivedEntryActuallyExpiresOnTime()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithDefaultExpiration(TimeSpan.FromMilliseconds(300))
            .WithFailSafe(enabled: false));
        var cache = host.Cache();

        var factoryRuns = 0;

        var first = await cache.GetOrSetAsync<string>(
            "Order:short",
            _ =>
            {
                Interlocked.Increment(ref factoryRuns);
                return Task.FromResult<string?>("v1");
            });

        await Task.Delay(900);

        var second = await cache.GetOrSetAsync<string>(
            "Order:short",
            _ =>
            {
                Interlocked.Increment(ref factoryRuns);
                return Task.FromResult<string?>("v2");
            });

        Assert.Equal("v1", first);
        Assert.Equal("v2", second);
        Assert.Equal(2, factoryRuns);
    }
}
