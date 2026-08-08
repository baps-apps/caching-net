# Health checks

Caching.NET ships two health checks. Register them through the builder or directly on an
`IHealthChecksBuilder`.

## Registration

```csharp
// Through the Caching.NET builder (recommended).
builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithHealthChecks(name: "caching-net", splitLivenessReadiness: true));

// Or directly.
builder.Services.AddHealthChecks()
    .AddCachingHealthChecks(name: "caching-net", splitLivenessReadiness: true);
```

| `splitLivenessReadiness` | Registers |
|---|---|
| `false` (default) | `caching-net` → `CachingHealthCheck` |
| `true` | `caching-net-liveness` (tag `liveness`) → `CachingLivenessHealthCheck`, and `caching-net-readiness` (tag `readiness`) → `CachingHealthCheck` |

## `CachingLivenessHealthCheck` — liveness

Confirms only that the Caching.NET object graph resolves. **No I/O.**

Always `Healthy`. Description lists the registered cache names.

A liveness probe that touched Redis would restart every pod in a deployment during a Redis outage,
converting a degraded cache into an outage. It must not depend on an external dependency.

## `CachingHealthCheck` — readiness

Performs a real write-then-read round trip through **every** registered cache, and in Redis and
Hybrid modes the round trip genuinely reaches Redis.

- Probe key: `__cachingnet:health:{cacheName}`, inside the application's own key namespace.
- Result data: one entry per cache — the mode name on success, `disabled` for a disabled cache,
  `round-trip mismatch` when the read comes back missing or different, or the **exception type**
  when the round trip threw.

### Probe entry options

The probe starts from the cache's configured defaults and then overrides everything that would
distort or outlive a health check:

| Setting | Probe value | Why |
|---|---|---|
| `Duration` | 10 s | Long enough to survive its own round trip |
| `DistributedCacheDuration` | `null` | Falls back to `Duration`. Left inherited, a configured `Entry.DistributedExpiration` **overrides** the probe duration — a 6-hour setting produced a 6-hour probe key |
| `MemoryCacheDuration` | `null` | Same, for L1 |
| `JitterMaxDuration` | `0` | A probe doesn't need expiry spreading |
| `IsFailSafeEnabled` | `false` | Keeps the physical TTL equal to the logical one instead of `Resilience.FailSafeMaxDuration`, and stops a retained stale value from masking a broken round trip |
| `EagerRefreshThreshold` | `null` | No background refresh of a probe |
| `SkipBackplaneNotifications` | `true` | No cross-pod traffic every probe interval |
| `SkipMemoryCacheRead` (read) | `true` **when a distributed layer exists** | Forces the read to reach Redis. Left off, a Hybrid probe reads back its own L1 write and never contacts Redis at all |
| `AllowBackgroundDistributedCacheOperations` (write) | `false` **when a distributed layer exists** | The L2 write is awaited, so its failure is observable |
| `ReThrowDistributedCacheExceptions` | `true` **when a distributed layer exists** | Distributed errors are swallowed by default; the probe needs them |

The three distributed-layer overrides are conditional on `IFusionCache.HasDistributedCache`. In
InMemory mode, skipping the memory read would turn every probe into a round-trip mismatch.

Net effect: the probe key's TTL in Redis is at most 10 seconds regardless of how the cache is
configured, and a Redis outage surfaces as `Degraded` in both Redis and Hybrid modes.

| Outcome | Status |
|---|---|
| Every cache round-trips | `Healthy` |
| Any cache fails or mismatches | `Degraded` |

**Why Degraded rather than Unhealthy:** in Hybrid mode the process can still serve from L1 and from
its source when Redis is down. Reporting Unhealthy would pull every pod out of the load balancer at
once for a fault the application is designed to survive. Map Degraded to your own policy if you want
different behaviour; `failureStatus` on the registration controls what a check *failure* reports,
and the Degraded result is what the check itself returns.

**Why only the exception type is reported:** a health endpoint is frequently reachable, and an
exception message can carry an endpoint or a credential fragment.

## Kubernetes

```csharp
app.MapHealthChecks("/health/live",  new() { Predicate = r => r.Tags.Contains("liveness") });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("readiness") });
```

```yaml
livenessProbe:
  httpGet: { path: /health/live, port: 8080 }
  initialDelaySeconds: 5
  periodSeconds: 10
readinessProbe:
  httpGet: { path: /health/ready, port: 8080 }
  initialDelaySeconds: 5
  periodSeconds: 10
```

## Sample response

```json
{
  "status": "Healthy",
  "results": {
    "caching-net-readiness": {
      "status": "Healthy",
      "description": "Caching.NET round trip succeeded.",
      "data": { "default": "Hybrid", "short-lived": "InMemory" }
    }
  }
}
```

Degraded, with Redis down:

```json
{
  "status": "Degraded",
  "results": {
    "caching-net-readiness": {
      "status": "Degraded",
      "description": "Caching.NET round trip failed for: default.",
      "data": { "default": "RedisConnectionException", "short-lived": "InMemory" }
    }
  }
}
```
