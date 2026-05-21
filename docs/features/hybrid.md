# Hybrid Mode

Local in-memory L1 + distributed Redis L2. Best latency/scale mix for read-heavy services.

## When to use

- Read-heavy workloads where most requests hit the L1 in-process cache.
- Distributed services where a shared L2 (Redis) is needed for cross-instance consistency on miss.
- Workloads that need tag-based invalidation (Hybrid is the only mode that honours `Tags`).

Pick **InMemory** when there is no need for cross-instance sharing.
Pick **Redis** when L1 memory pressure outweighs latency gains, or when entries must be strictly consistent across instances.

## How it works

`HybridCacheService` wraps Microsoft's `HybridCache` (built on `IDistributedCache` + `IMemoryCache`).

- L1 (in-memory) serves hits without a network hop.
- L2 (Redis) is consulted on L1 miss.
- Writes go to both layers.
- Local TTL is independent of distributed TTL — control via `WithDefaultLocalExpiration` / `HybridLocalCacheExpiration`.
- Tag-based invalidation invalidates entries across L1 and L2.

## Configure

### Config-first

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "Hybrid",
    "KeyPrefix": "asm-api-prod",
    "RedisConnectionString": "rediss://elasticache.amzn.example:6380",
    "DefaultExpiration": "00:10:00",
    "HybridLocalCacheExpiration": "00:00:30"
  }
}
```

### Fluent

```csharp
services.AddCaching(b => b
    .UseHybrid("rediss://elasticache.amzn.example:6380")
    .WithKeyPrefix("asm-api-prod")
    .WithDefaultExpiration(TimeSpan.FromMinutes(10))
    .WithDefaultLocalExpiration(TimeSpan.FromSeconds(30))
    .RequireTagSupport());
```

## Tag-based invalidation

Tags are honoured **only** in Hybrid mode. Call `RequireTagSupport()` to fail startup if mode is not Hybrid (prevents silent misuse).

```csharp
var opts = new CacheCallOptions { Tags = new[] { "product", $"category:{categoryId}" } };

await cache.SetAsync($"Product:{id}", product, opts, expiration: TimeSpan.FromMinutes(5));

// Later — invalidate every entry tagged "category:42"
await cache.RemoveByTagAsync($"category:{categoryId}");
```

## Tuning

| Knob | Default | Effect |
| --- | --- | --- |
| `DefaultExpiration` | `00:10:00` | L2 TTL fallback |
| `HybridLocalCacheExpiration` | inherits `DefaultExpiration` | L1 TTL — smaller = fresher per instance, larger = lower latency |
| `MaximumPayloadBytes` | `1048576` (1 MiB) | Entries above this are not cached |
| `MaximumKeyLength` | `512` | Hard cap on final physical key length |
| `TtlJitterPercentage` | `0.10` | Spread expirations to avoid synchronized eviction storms |

## Gotchas

- `SlidingExpiration` and `AllowStaleFor` are **not honoured** in Hybrid mode (Microsoft's `HybridCache` does not expose those semantics). Use InMemory or Redis if you need them.
- Per-call mode override `Hybrid → InMemory` disables `Tags`, `SlidingExpiration`, `AllowStaleFor`.
- L1 entries are per-instance: a `RemoveAsync` from one pod only invalidates L2 + that pod's L1. Other pods drop their L1 copies on next miss/expiry. Use tag invalidation for cross-pod cache busting.

## Related

- [stampede.md](stampede.md) — stampede protection (active in all modes)
- [telemetry.md](telemetry.md) — observability
- [../INTERNALS.md](../INTERNALS.md) — payload envelope, striped locks, resilience
- [../OPERATIONS.md](../OPERATIONS.md) — ElastiCache deployment, sharding
