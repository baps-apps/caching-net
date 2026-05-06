using Caching.NET.Internal;
using FsCheck;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class StripedLockManagerProperties
{
    [Property]
    public bool Same_key_always_returns_same_stripe(NonEmptyString key)
    {
        using var mgr = new StripedLockManager(256);
        return ReferenceEquals(mgr.GetLock(key.Get), mgr.GetLock(key.Get));
    }

    [Property]
    public bool Stripe_count_is_power_of_two(PositiveInt requested)
    {
        var count = Math.Min(requested.Get, 4096); // cap to avoid huge allocations
        using var mgr = new StripedLockManager(count);
        var actual = mgr.StripeCount;
        // Power-of-two check: n & (n-1) == 0
        return actual > 0 && (actual & (actual - 1)) == 0;
    }

    [Property]
    public bool Different_keys_may_share_stripe_but_never_throws(NonEmptyString a, NonEmptyString b)
    {
        using var mgr = new StripedLockManager(16);
        _ = mgr.GetLock(a.Get);
        _ = mgr.GetLock(b.Get);
        return true;
    }
}
