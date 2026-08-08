using Caching.NET.Tests.Integration.Fixtures;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Multi-pod behaviour across real operating-system processes.
/// </summary>
/// <remarks>
/// The other Hybrid tests build two service providers inside one process, which share a CLR, a
/// thread pool and a heap. That is enough to exercise the code paths but not enough to claim
/// multi-pod correctness. Each pod here is a separate process with its own L1, talking to one Redis,
/// which is the deployment these guarantees are written for.
/// </remarks>
[Collection(RedisCollection.Name)]
public class MultiProcessPodTests
{
    private const int PropagationTimeoutMilliseconds = 20_000;

    private readonly RedisFixture _redis;

    public MultiProcessPodTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private Task<PodProcess> StartAsync(string prefix) => PodProcess.StartAsync("hybrid", prefix, _redis.ConnectionString);

    [Fact]
    public async Task ValueWrittenByOneProcess_IsReadableByAnother()
    {
        await using var podA = await StartAsync("xproc-share");
        await using var podB = await StartAsync("xproc-share");

        Assert.Equal("ok", await podA.SendAsync("set Order:1 v1"));

        // Pod B has a cold L1, so reading v1 proves it came through Redis.
        Assert.Equal("v1", await podB.SendAsync("get Order:1"));
    }

    [Fact]
    public async Task WriteOnOneProcess_InvalidatesTheOtherProcessesLocalCopy()
    {
        await using var podA = await StartAsync("xproc-invalidate");
        await using var podB = await StartAsync("xproc-invalidate");

        Assert.Equal("ok", await podA.SendAsync("set Order:2 v1"));

        // Warm pod B's L1 with the first value, so only a backplane message can dislodge it.
        Assert.Equal("v1", await podB.SendAsync("get Order:2"));

        Assert.Equal("ok", await podA.SendAsync("set Order:2 v2"));

        Assert.Equal("ok", await podB.SendAsync($"poll Order:2 v2 {PropagationTimeoutMilliseconds}"));
    }

    [Fact]
    public async Task RemovalOnOneProcess_ReachesTheOtherProcess()
    {
        await using var podA = await StartAsync("xproc-remove");
        await using var podB = await StartAsync("xproc-remove");

        Assert.Equal("ok", await podA.SendAsync("set Order:3 v1"));
        Assert.Equal("v1", await podB.SendAsync("get Order:3"));

        Assert.Equal("ok", await podA.SendAsync("remove Order:3"));

        Assert.Equal("ok", await podB.SendAsync($"pollmissing Order:3 {PropagationTimeoutMilliseconds}"));
    }

    [Fact]
    public async Task TagInvalidationOnOneProcess_ReachesTheOtherProcess()
    {
        await using var podA = await StartAsync("xproc-tags");
        await using var podB = await StartAsync("xproc-tags");

        Assert.Equal("ok", await podA.SendAsync("settagged Product:1 v1 category:tools"));
        Assert.Equal("v1", await podB.SendAsync("get Product:1"));

        Assert.Equal("ok", await podA.SendAsync("removebytag category:tools"));

        Assert.Equal("ok", await podB.SendAsync($"pollmissing Product:1 {PropagationTimeoutMilliseconds}"));
    }

    [Fact]
    public async Task ClearOnOneProcess_ReachesTheOtherProcess()
    {
        await using var podA = await StartAsync("xproc-clear");
        await using var podB = await StartAsync("xproc-clear");

        Assert.Equal("ok", await podA.SendAsync("set Order:4 v1"));
        Assert.Equal("v1", await podB.SendAsync("get Order:4"));

        Assert.Equal("ok", await podA.SendAsync("clear"));

        Assert.Equal("ok", await podB.SendAsync($"pollmissing Order:4 {PropagationTimeoutMilliseconds}"));
    }

    [Fact]
    public async Task ApplicationPrefix_IsolatesProcessesOfDifferentApplications()
    {
        await using var appOne = await StartAsync("xproc-iso-one");
        await using var appTwo = await StartAsync("xproc-iso-two");

        Assert.Equal("ok", await appOne.SendAsync("set shared one"));
        Assert.Equal("ok", await appTwo.SendAsync("set shared two"));

        Assert.Equal("ok", await appOne.SendAsync("removebytag anything"));
        Assert.Equal("ok", await appOne.SendAsync("clear"));

        // One application's clear must not reach another application's entries, even though both
        // are connected to the same Redis and the same backplane server.
        Assert.Equal("two", await appTwo.SendAsync("get shared"));
    }

    [Fact]
    public async Task RestartedProcess_RebuildsItsLocalLayerFromRedis()
    {
        await using var survivor = await StartAsync("xproc-restart");

        await using (var original = await StartAsync("xproc-restart"))
        {
            Assert.Equal("ok", await original.SendAsync("set Order:5 v1"));
        }

        // A brand-new process with an empty L1 sees the value that outlived the one that wrote it.
        await using var restarted = await StartAsync("xproc-restart");
        Assert.Equal("v1", await restarted.SendAsync("get Order:5"));
        Assert.Equal("v1", await survivor.SendAsync("get Order:5"));
    }
}
