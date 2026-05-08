namespace Caching.NET.Internal;

/// <summary>
/// Bounds the number of concurrent background stale-while-revalidate refreshes.
/// </summary>
internal sealed class StaleRefreshThrottle : IDisposable
{
    private readonly SemaphoreSlim _gate;

    public StaleRefreshThrottle(int maxConcurrent)
    {
        if (maxConcurrent < 1) maxConcurrent = 1;
        _gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public bool TryAcquire() => _gate.Wait(0);
    public void Release() => _gate.Release();

    public void Dispose() => _gate.Dispose();
}
