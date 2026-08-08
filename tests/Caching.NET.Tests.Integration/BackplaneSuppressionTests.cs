using Caching.NET.Tests.Integration.Fixtures;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// <see cref="Caching.NET.Options.CacheEntryOverrides.SkipBackplaneNotification"/> across real
/// operating-system processes.
/// </summary>
/// <remarks>
/// Like <see cref="MultiProcessPodTests"/>, this drives two pods in separate processes rather than two
/// service providers in one, because the guarantee under test — one process's write not evicting
/// another process's in-memory copy — is exactly the kind of thing that two providers sharing a CLR
/// and a heap cannot falsify. A write carrying <c>SkipBackplaneNotification</c> must not invalidate
/// another process's L1 copy; without it, bulk warm-up would publish one invalidation per entry to
/// every instance.
/// </remarks>
[Collection(RedisCollection.Name)]
public class BackplaneSuppressionTests
{
    private const int PropagationTimeoutMilliseconds = 20_000;

    // The window used to prove the *absence* of a propagated value. There is no way to prove a
    // negative for an unbounded amount of time, so this is deliberately a small multiple of the
    // propagation latency the positive-case tests observe in practice (well under a second on a local
    // Redis) rather than the full PropagationTimeoutMilliseconds safety margin used for the positive
    // assertion below, which would make this test pay 20s on every run purely to fail to observe
    // something that was never going to arrive.
    private const int AbsenceWindowMilliseconds = 3_000;

    private readonly RedisFixture _redis;

    public BackplaneSuppressionTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private Task<PodProcess> StartAsync(string prefix) => PodProcess.StartAsync("hybrid", prefix, _redis.ConnectionString);

    [Fact]
    public async Task SkipBackplaneNotification_LeavesTheOtherProcessL1Intact()
    {
        await using var podA = await StartAsync("xproc-skip-backplane");
        await using var podB = await StartAsync("xproc-skip-backplane");

        Assert.Equal("ok", await podA.SendAsync("set k 1"));

        // Warm pod B's L1, so only a backplane message (or its absence) can be observed below.
        Assert.Equal("1", await podB.SendAsync("get k"));

        Assert.Equal("ok", await podA.SendAsync("set-nobackplane k 2"));

        // Pod B must still read the stale L1 value: no invalidation was published for this write. A
        // fixed sleep followed by one read would be racing the backplane rather than proving its
        // absence, so this polls for the wrong (propagated) value over the whole window and only
        // passes if that value never shows up.
        Assert.Equal("timeout last=1", await podB.SendAsync($"poll k 2 {AbsenceWindowMilliseconds}"));

        // Without this, step above would also pass for a backplane that is simply broken, so prove
        // the backplane still works by publishing an ordinary write next.
        Assert.Equal("ok", await podA.SendAsync("set k 3"));

        Assert.Equal("ok", await podB.SendAsync($"poll k 3 {PropagationTimeoutMilliseconds}"));
    }
}
