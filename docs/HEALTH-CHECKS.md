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

`CachingHealthCheck` builds every probe's `CacheEntryOverrides` explicitly (`ProbeOptions()` /
`ProbeWriteOptions()` in `Health/CachingHealthCheck.cs`) rather than inheriting the cache's configured
defaults, so nothing about a production entry's lifetime or resilience settings can distort or
outlive a health check:

| `CacheEntryOverrides` field | Probe value | Why |
|---|---|---|
| `LocalExpiration` | 10 s (`CachingHealthCheck.ProbeDuration`) | Long enough to survive its own round trip; a configured `Entry.LocalExpiration` is never consulted, so it cannot leave a stale probe key behind |
| `DistributedExpiration` | 10 s | Same, for L2. A configured `Entry.DistributedExpiration` (say, 6 hours) would otherwise leave the probe key in Redis for 6 hours — this field is set explicitly precisely to prevent that |
| `JitterMaxDuration` | `TimeSpan.Zero` | A probe doesn't need expiry spreading |
| `FailSafe` | `false` | Keeps the physical TTL equal to the logical one instead of `Resilience.FailSafeMaxDuration`, and stops a retained stale value from masking a broken round trip |
| `SkipBackplaneNotification` | `true` | No cross-pod traffic every probe interval |
| `AllowBackgroundDistributedOperations` (write only) | `false` **when `CachingOptions.UsesDistributedLayer`** | The L2 write is awaited, so its failure is observable, rather than completing after the caller has already been released |

Two more settings are applied, but **not** through `CacheEntryOverrides` — they are engine-only
probe behaviour that the public per-call override surface deliberately does not expose, because
`SkipMemoryCacheRead` is exactly the kind of mode-encoding flag `CacheEntryOverrides` is designed to
exclude (see docs/ARCHITECTURE.md §3). `CachingHealthCheck` reaches them through
`FusionCacheService`'s **internal** `ProbeSetAsync`/`ProbeTryGetAsync` helpers instead, which operate
directly on the engine's own entry-options object below the `ICacheService` contract:

| Engine-only setting | Probe value | Why |
|---|---|---|
| `SkipMemoryCacheRead` (read) | `true` **when the cache uses a distributed layer** | Forces the read to reach Redis. Left off, a Hybrid probe reads back its own L1 write and never contacts Redis at all |
| `ReThrowDistributedCacheExceptions` | `true` **when the cache uses a distributed layer** | Distributed errors are swallowed by default; the probe needs them to surface as an exception so `CheckHealthAsync` can catch them |
| `EagerRefreshThreshold` | `null` (read only) | No background refresh of a probe, regardless of the configured `Entry.EagerRefreshThreshold` |

Both rows in that second table are keyed on the engine's own `HasDistributedCache` (checked inside
`FusionCacheService`'s `ProbeSetAsync`/`ProbeTryGetAsync`, on its private engine reference — a
different flag from `CachingOptions.UsesDistributedLayer` above, though the two agree in practice for
every mode Caching.NET maps). In InMemory mode neither fires: skipping the memory read there would
turn every probe into a round-trip mismatch, since InMemory mode has nowhere else for the value to
be.

Every cache reaching this probe is, in practice, a `FusionCacheService` — `CachingHealthCheck` pattern
matches on that concrete internal type to reach `ProbeSetAsync`/`ProbeTryGetAsync`, and falls back to
the plain `ICacheService` overrides-only path (`SetAsync`/`TryGetAsync` with just the
`CacheEntryOverrides` table above) for any other implementation, so a future non-engine
`ICacheService` — or a disabled cache's `NullCacheService`, should the early `Enabled: false` skip
above ever be removed — degrades to the weaker check instead of throwing.

Net effect: the probe key's TTL in Redis is at most 10 seconds regardless of how the cache is
configured, and a Redis outage surfaces as `Degraded` in both Redis and Hybrid modes. That last part
is what the forced read-through and forced rethrow above buy: without them the readiness probe reads
back its own L1 write and reports `Healthy` through a complete Redis outage, which is exactly what a
pre-release revision did.

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
