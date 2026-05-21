# Stampede Protection

Prevents thundering-herd backend overload when a hot key expires and many requests miss simultaneously.

## When to use

- Hot keys (top-N products, popular sessions, leaderboard tiles).
- Expensive factories (DB queries, downstream API calls, computed aggregates).
- Synchronized expirations across replicas after a deploy/restart.

Active in **all** modes by default. No opt-in needed.

## How it works

Two layers cooperate:

1. **Coalescing** — `GetOrCreateAsync` uses a per-key striped lock so only one caller runs the factory; concurrent callers await its result.
2. **TTL jitter** — every entry's TTL is randomized by ±`TtlJitterPercentage` (default ±10%) to spread expiration timestamps.
3. **Stale-while-revalidate** — when `AllowStaleFor` is set (InMemory/Redis), expired entries continue to serve for the configured window while a single background refresh runs.
4. **Stale refresh throttle** — `StaleRefreshConcurrency` caps total in-flight background refreshes process-wide.

## Configure

### Defaults (already on)

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "Hybrid",
    "KeyPrefix": "asm-api-prod",
    "TtlJitterPercentage": 0.10,
    "StripeLockCount": 1024,
    "StaleRefreshConcurrency": 256
  }
}
```

### Tune for very hot keys

```csharp
services.AddCaching(b => b
    .UseHybrid("rediss://...")
    .WithKeyPrefix("asm-api-prod")
    .WithStripedLocks(4096)              // less lock contention on hot partitions
    .WithTtlJitter(0.20)                 // wider expiry spread
    .WithStaleRefreshConcurrency(512));  // higher background refresh budget
```

### Stale-while-revalidate per call

```csharp
var opts = new CacheCallOptions
{
    AllowStaleFor = TimeSpan.FromSeconds(30) // serve stale up to 30s after expiry
};

var leaderboard = await cache.GetOrCreateAsync(
    "Leaderboard:Top10",
    ct => ComputeTop10Async(ct),
    opts,
    expiration: TimeSpan.FromMinutes(1));
```

Note: `AllowStaleFor` is honoured by **InMemory** and **Redis** modes only. Hybrid ignores it.

## Opt-out per call

```csharp
var opts = new CacheCallOptions { CoalesceConcurrent = false };
```

Use when the factory is cheap, idempotent, and callers must not wait on the leader.

## Tuning

| Knob | Default | Effect |
| --- | --- | --- |
| `StripeLockCount` | `1024` | More slots = less false sharing across unrelated hot keys. Rounded up to power of 2. |
| `TtlJitterPercentage` | `0.10` | Clamped to 0–0.5. Higher = better spread, more variance in cache hit ratio. |
| `StaleRefreshConcurrency` | `256` | Process-wide cap on background refresh fan-out — protects backend on mass-expiry. |
| `FactoryTimeout` | `00:00:30` | Bounds a single factory execution; prevents one slow factory from holding the lock forever. |

## Observability

OTel metrics emitted (see [telemetry.md](telemetry.md)):

- `cache.stale_refresh.in_flight` — up-down counter of background refresh tasks currently running. Watch this against `StaleRefreshConcurrency` to see if the throttle is saturating.
- `cache.stale_served` — counter of stale entries served while a background refresh ran (stale-while-revalidate path).
- `cache.errors` (tag `cache.error_kind=timeout`) — factory or backend timeouts. Spike here usually means the factory budget is too tight or downstream is degraded.

## Related

- [hybrid.md](hybrid.md) — distributed L1+L2
- [telemetry.md](telemetry.md) — metrics, traces
- [../INTERNALS.md](../INTERNALS.md) — `StripedLockManager`, `StaleRefreshThrottle`, `TtlJitter`
