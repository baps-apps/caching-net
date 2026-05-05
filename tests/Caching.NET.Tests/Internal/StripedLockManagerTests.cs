using Caching.NET.Internal;

namespace Caching.NET.Tests.Internal;

public sealed class StripedLockManagerTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(1023, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void Ctor_RoundsStripeCountUpToPowerOfTwo(int requested, int expected)
    {
        using var sut = new StripedLockManager(requested);
        Assert.Equal(expected, sut.StripeCount);
    }

    [Fact]
    public void GetLock_ReturnsSameInstanceForSameKey()
    {
        using var sut = new StripedLockManager(1024);
        var a = sut.GetLock("orders:42");
        var b = sut.GetLock("orders:42");
        Assert.Same(a, b);
    }

    [Fact]
    public void GetLock_DistributesKeysAcrossStripes()
    {
        using var sut = new StripedLockManager(64);
        var seen = new HashSet<SemaphoreSlim>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < 1000; i++)
        {
            seen.Add(sut.GetLock($"key:{i}"));
        }
        Assert.True(seen.Count >= 50, $"Expected >=50 stripes used, got {seen.Count}");
    }

    [Fact]
    public async Task GetLock_OneAtATime_SerializesConcurrentSameKeyHolders()
    {
        using var sut = new StripedLockManager(1024);
        var holding = 0;
        var maxObserved = 0;
        async Task RunAsync()
        {
            var sem = sut.GetLock("hot-key");
            await sem.WaitAsync();
            try
            {
                var current = Interlocked.Increment(ref holding);
                int prevMax;
                do { prevMax = maxObserved; if (current <= prevMax) break; }
                while (Interlocked.CompareExchange(ref maxObserved, current, prevMax) != prevMax);
                await Task.Delay(10);
                Interlocked.Decrement(ref holding);
            }
            finally { sem.Release(); }
        }
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => RunAsync()));
        Assert.Equal(1, maxObserved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_RejectsNonPositiveStripeCount(int bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StripedLockManager(bad));
    }
}
