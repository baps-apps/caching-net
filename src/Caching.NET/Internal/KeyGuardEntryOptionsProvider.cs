using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Internal;

/// <summary>
/// Engine hook invoked with the physical cache key on every operation that does not carry explicit
/// per-entry options. Caching.NET uses it to enforce the configured key-length limit without
/// wrapping the cache API.
/// </summary>
/// <remarks>
/// Returns <c>null</c> so the cache falls back to its configured defaults, and reports
/// <c>canMutate: false</c> so nothing is cloned. The hook adds one delegate call and one integer
/// comparison per operation and allocates nothing.
/// </remarks>
internal sealed class KeyGuardEntryOptionsProvider : FusionCacheEntryOptionsProvider
{
    private readonly CacheGuard _guard;

    public KeyGuardEntryOptionsProvider(CacheGuard guard)
    {
        _guard = guard;
    }

    public override FusionCacheEntryOptions? GetEntryOptions(
        FusionCacheEntryOptionsProviderContext ctx,
        string key,
        out bool canMutate)
    {
        canMutate = false;
        _guard.ValidatePhysicalKey(key);
        return null;
    }
}
