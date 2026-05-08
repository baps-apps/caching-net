namespace Caching.NET.Internal;

/// <summary>
/// Fixed-size pool of <see cref="SemaphoreSlim"/> instances, indexed by a stable hash
/// of the cache key. Solves the v1 lock-leak bug (per-key SemaphoreSlim entries
/// in a ConcurrentDictionary) by allocating once at startup and never adding/removing.
/// </summary>
internal sealed class StripedLockManager : IDisposable
{
    private readonly SemaphoreSlim[] _stripes;
    private readonly uint _mask;
    private bool _disposed;

    public StripedLockManager(int stripeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
        var pow2 = RoundUpToPowerOfTwo(stripeCount);
        _stripes = new SemaphoreSlim[pow2];
        for (var i = 0; i < pow2; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
        _mask = (uint)(pow2 - 1);
    }

    public int StripeCount => _stripes.Length;

    public SemaphoreSlim GetLock(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var h = StableStringHash.Compute(key);
        return _stripes[h & _mask];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _stripes)
        {
            s.Dispose();
        }
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        var v = (uint)(value - 1);
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return (int)(v + 1);
    }
}
