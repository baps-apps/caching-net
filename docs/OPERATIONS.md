# Operations

Running Caching.NET v3 in production: failure behaviour, Kubernetes wiring, and health checks.

## 1. Failure behaviour

For every condition, what the caller sees and what shows up in telemetry.

| Condition | Returns | Factory runs | Throws | Logs | Error metric | Recovery |
|---|---|---|---|---|---|---|
| L1 hit | cached value | no | no | — | — | — |
| L1 miss, L2 hit (Hybrid) | cached value, L1 warmed | no | no | — | — | — |
| L1 and L2 miss | factory result | yes | no | — | — | — |
| Redis unreachable at startup | L1/factory results | yes | no | Critical on hard connect failure | `caching.net.errors{layer=redis}` | Background reconnect |
| Redis unavailable at runtime | L1 value (Hybrid) or factory result | yes on miss | no¹ | Warning | `caching.net.redis.errors` | Circuit breaker, then auto-recovery |
| Redis soft timeout | stale value if available, else continues | maybe | no | Debug | `caching.net.errors` | — |
| Redis hard timeout | treated as a miss | yes | no¹ | Debug | `caching.net.errors` | — |
| Redis restart | resumes automatically | — | no | Information (`connection restored`) | — | Shared multiplexer reconnects; queued ops replay |
| Network partition | as "unavailable" | yes on miss | no¹ | Warning, then suppressed | `CircuitBreakerOpen` | Breaker closes on success |
| Redis auth failure | as "unavailable" | yes on miss | no¹ | Error | `caching.net.redis.errors` | Fix credentials and restart |
| Redis TLS failure | connection rejected | yes on miss | no¹ | Error (policy errors only) | `caching.net.redis.tls.validations` | Fix the certificate |
| Corrupt Redis payload | treated as a miss | yes | no¹ | Warning | `caching.net.errors{error.type=CorruptPayloadException}` | Overwritten by the next factory result |
| Oversized value on write, background distributed ops **on** (default) | caller still gets its value | yes | no | Warning | `caching.net.guard.violations` | Not cached |
| Oversized value on write, background distributed ops **off** | — | yes | **yes** — `InvalidOperationException` | Warning | `caching.net.guard.violations` | See "Foreground writes surface serialization failures" below |
| Oversized stored payload on read | treated as a miss | yes | no¹ | Warning | `caching.net.guard.violations` | Overwritten |
| Backplane unavailable | operations continue | — | no² | Warning | `caching.net.backplane.errors` (only if a publish is attempted — a Redis outage shows on `caching.net.redis.errors` instead) | Auto-recovery replays notifications |
| Serializer failure, background distributed ops **on** | not written to L2 | — | no | Warning | `caching.net.errors` | — |
| Serializer failure, background distributed ops **off** | — | — | **yes** — the serializer's exception | Warning | `caching.net.errors` | See below |
| Factory exception, stale value available | stale value | yes | no | Warning (fail-safe activation) | `caching.net.factory.executions{result=error}` | Retried after `FailSafeThrottleDuration` |
| Factory exception, no stale value | — | yes | **yes** — the original exception | Warning | `caching.net.factory.executions{result=error}` | — |
| Factory soft timeout | stale value | yes (continues in background) | no | Debug | — | Late result stored if `AllowTimedOutFactoryBackgroundCompletion` |
| Factory hard timeout | stale value, else throws | yes (abandoned) | if no stale value | Debug | `SyntheticTimeoutException` | — |
| Caller cancellation | — | maybe | **yes** — `OperationCanceledException` | — | **no** (not an error) | Nothing cached |
| Key over the limit | — | no | **yes** — `ArgumentException` | Warning | `caching.net.guard.violations` | Fix the key |
| Concurrent callers, same key | one factory result | once per instance | no | — | — | — |
| Pod restart | L1 empty, refilled from L2 | only on full miss | no | Startup summary | — | — |
| Application shutdown | — | — | no | — | — | Cache graph disposed in order |
| Redis outage, background distributed ops **on** (default) | as "unavailable" | yes on miss | no¹ | Warning | `caching.net.redis.errors` | Also raises `TaskScheduler.UnobservedTaskException` — see below |

¹ Unless the matching `Resilience.ThrowOn*` flag is set.
² Unless `Resilience.ThrowOnBackplaneErrors` is set.

Nothing is silently swallowed: every degraded path increments a counter and writes a log entry.

### Unobserved task exceptions during a Redis outage

With `Resilience.AllowBackgroundDistributedOperations: true` (the default) the engine schedules
distributed reads, writes and backplane publishes as background tasks. When Redis goes away, some of
those tasks fault after nothing is left awaiting them, and the Redis client's
`RedisConnectionException` reaches `TaskScheduler.UnobservedTaskException` at the next garbage
collection. Measured: 50 cache operations across a full outage produced three, plus one
`SocketClosed` on `UNSUBSCRIBE` when a cache was disposed during the outage.

What this does and does not mean:

- **It does not crash the process.** Since .NET Core, an unobserved task exception is reported and
  discarded, not rethrown on the finalizer thread.
- **It does not lose observability.** The same failures are already counted on
  `caching.net.redis.errors` and logged at the configured level; the unobserved exception is a
  duplicate signal, not the only one.
- **It will show up in a host that subscribes to `TaskScheduler.UnobservedTaskException`** — some
  crash reporters and error trackers do, and will attribute cache-layer connection errors to the
  application, precisely during an incident when the noise is least welcome.

Caching.NET does not own those tasks and cannot observe them without wrapping every cache call,
which is the delegating layer v3 exists to remove. If your error tracker escalates unobserved task
exceptions, filter `StackExchange.Redis.RedisConnectionException` from that channel and rely on
`caching.net.redis.errors` for the signal instead.

### Foreground writes surface serialization failures

`Resilience.ThrowOnSerializationErrors: false` does **not** hold when
`Resilience.AllowBackgroundDistributedOperations` is also `false`. With background operations off the
serializer runs on the caller's path, and the engine's foreground distributed write propagates the
exception regardless of the re-throw setting. Measured, both cases:

| `AllowBackgroundDistributedOperations` | Value over `MaximumPayloadBytes` | Unserializable value (cycle, unsupported type) |
|---|---|---|
| `true` (default) | not cached, caller unaffected | not cached, caller unaffected |
| `false` | **`InvalidOperationException` to the caller** | **`JsonException` to the caller** |

Caching.NET logs a warning naming this combination at startup (event `3050`) and cannot intercept it
at run time without wrapping every cache call. Pick one:

- **Leave background distributed operations on** (the default). The write is fire-and-forget, and an
  oversized or unserializable value degrades to "not cached".
- **Keep them off** — for read-your-writes consistency — and treat a cache write as something that
  can throw: bound the size of what you cache, and wrap the call site.

This matters most where the payload size follows user input: with foreground writes, one oversized
value turns a request into a `500` instead of a cache miss.

Both behaviours are pinned by `RedisModeTests.OversizedValue_*`.

## 2. Kubernetes

### Configuration

```json
{
  "CacheOptions": {
    "Mode": "Hybrid",
    "ApplicationPrefix": "orders-api",
    "EnvironmentPrefix": "prod",
    "DefaultExpiration": "00:10:00",
    "Entry": { "LocalExpiration": "00:01:00" },
    "Redis": {
      "Configuration": "redis-master.cache.svc.cluster.local:6379,abortConnect=false",
      "ConnectTimeout": "00:00:05",
      "CommandTimeout": "00:00:02"
    },
    "Backplane": { "Enabled": true },
    "Resilience": { "FailSafeEnabled": true, "AutoRecoveryEnabled": true }
  }
}
```

- **`abortConnect=false`** (the default) lets a pod start before Redis is ready. Pod and Redis start
  in an arbitrary order; a cache should never be the reason a rollout stalls.
- **`Entry.LocalExpiration` shorter than `DefaultExpiration`** bounds staleness if the backplane is
  ever down. It is the safety net behind the backplane, not a replacement for it. It may never
  exceed `Entry.DistributedExpiration`; startup validation rejects that, because the in-process copy
  would outlive the authoritative Redis entry.
- **`Backplane.Enabled: true`** for anything with more than one replica. It defaults to `false` when
  the cache is bound from configuration — only the `UseHybrid(...)` builder turns it on — so a
  Hybrid cache without it logs a startup warning (event 3051) naming the stale window.
- **`Logging:LogLevel:Caching.NET: Warning`** in production is still the tightest setting, but
  `Information` is now safe too: the engine's per-operation lines (measured at 2.04 per `GetOrSet`)
  are rewritten to `Observability.EngineOperationLogLevel`, `Debug` by default. Everything an
  operator needs during an incident is `Warning` or above.
- **Distinct `ApplicationPrefix` per service** and **distinct `EnvironmentPrefix` per environment**
  when they share a Redis database.

### Health probes

```csharp
builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithHealthChecks(splitLivenessReadiness: true));

app.MapHealthChecks("/health/live",  new() { Predicate = r => r.Tags.Contains("liveness") });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("readiness") });
```

```yaml
livenessProbe:
  httpGet: { path: /health/live, port: 8080 }
  periodSeconds: 10
readinessProbe:
  httpGet: { path: /health/ready, port: 8080 }
  periodSeconds: 10
```

- **Liveness performs no I/O.** If it probed Redis, a Redis outage would restart every pod in the
  deployment simultaneously — turning a degraded cache into an outage.
- **Readiness performs a real write-then-read round trip** through every registered cache, using a
  reserved key inside the application's own namespace. In Redis and Hybrid modes the read bypasses
  L1 so it actually reaches Redis, and the L2 write is awaited with errors surfaced — so an outage
  cannot hide behind a local hit. The probe entry lives at most 10 seconds in every layer regardless
  of the configured expirations, has fail-safe off, and generates no backplane traffic.
- Readiness reports **Degraded**, not Unhealthy, when the distributed layer fails, so a Redis outage
  does not remove every pod from the load balancer at once. Map Degraded to your own policy if you
  want a different outcome.
- **Redis loss is therefore visible on `/health/ready` in both distributed modes**, alongside the
  `caching.net.redis.errors` metric. Liveness stays Healthy by design.
- Health output reports exception **types** only — a health endpoint is often reachable, and an
  exception message can carry an endpoint or a credential fragment.
- A disabled cache reports `disabled` and is not probed.

### Scaling

Hybrid mode is designed for horizontal scale. Each pod keeps its own L1; L2 and the backplane are
shared. Adding pods adds L1 capacity without adding Redis load for repeated reads. Removing a pod
loses only its L1.

## 3. Runbook

### Hit ratio dropped

1. Check `caching.net.invalidations` — is the application invalidating more than expected? This
   counter is caller-requested only: `Remove*`/`Expire*`/`RemoveByTag*`/`Clear*`, one record per call.
2. Check `caching.net.evictions` — entries the in-process memory layer dropped on its own (expiry,
   `Entry.MemorySizeLimit`, replacement). A rise here with a flat `caching.net.invalidations` means
   memory pressure or a short local lifetime, not application code invalidating.
3. Check `caching.net.errors{layer=redis}` — L2 failures turn every read into a miss.
4. Check whether `Entry.LocalExpiration` or `DefaultExpiration` changed in a recent deploy.
5. Check `caching.net.guard.violations` — oversized values are silently not cached (by design), so a
   payload that grew past the limit shows up here, not as an error.

### Redis command rate is higher than the cache read rate

Expected in `Redis` mode, and roughly `1 + hitRatio × (2 + tagsPerEntry)` times the read rate — tag and
`Clear` markers are read from Redis on every hit so that an invalidation cannot be hidden by a local
copy. Confirm with `INFO commandstats`: the excess appears as `hmget`, against keys prefixed
`__fc:t:`. It rises when the hit ratio rises, which is the opposite of the usual intuition.

Levers, in order of preference: move to `Hybrid` mode (markers become local and the cost disappears
from the read path), reduce tags per entry, or accept it. Do not "fix" it by making markers local in
`Redis` mode — that is the defect this behaviour exists to prevent. See
[Capacity](#4-capacity).

### One pod serves stale data

1. Is `Backplane.Enabled` true? In Hybrid mode without it, a pod serves its L1 copy until
   `Entry.LocalExpiration` elapses.
2. Check `caching.net.backplane.errors`.
3. Confirm every pod uses the same `ApplicationPrefix` — a mismatch splits the backplane channel.

### Redis outage

Expected behaviour: Hybrid degrades to L1 + factory, Redis mode falls through to the factory,
neither throws. Watch `caching.net.redis.errors` and, when Redis returns, the
`Redis connection restored` Information entry. Auto-recovery replays queued writes and notifications
— no restart needed.

If the application *is* throwing, check `Resilience.ThrowOnDistributedCacheErrors`.

### Upstream dependency failing

`caching.net.fail_safe.served` rising means expired entries are being served because factories are
failing. The cache is doing its job; the dependency is the problem. `FailSafeThrottleDuration`
(30s default) bounds how often the failing dependency is retried.

### Cold cache after a deploy

Expected once when upgrading from v2 — key layout and wire format both changed. Entries repopulate
on demand and v2 orphans expire by TTL. If the miss storm is a problem, deploy during a quiet
period or pre-warm the hot keys.

### Miss burst in the first moments after a pod starts

In `Redis` and `Hybrid` mode, reads issued while the Redis connection is still being established
return a **miss**, not an error. The write side is unaffected — a `Set` issued in that window still
lands — but a `GetOrSet` sees a miss and runs its factory, so a cold pod briefly calls the origin for
everything it is asked for.

Measured on loopback Redis, from two independent cold processes: the first read missed at ~126 ms and
the first successful round-trip completed at ~145–167 ms. With TLS, authentication and a real network
the window will be longer. Nothing above `Debug` is logged, so there is no warning to correlate
against.

This is a deliberate trade. `CachingStartupService` builds every cache at host start and lets the
Redis client begin connecting, but **does not block on the connection** — with `AbortOnConnectFail`
off, a pod must still become ready while Redis is starting. Blocking would convert a Redis outage
into a failed rollout.

The same reasoning explains a defaults pairing that looks inverted: `Redis.ConnectTimeout` is 5s while
`Resilience.DistributedHardTimeout` is 2s. An individual operation is therefore abandoned before a
slow connection finishes establishing, rather than holding a request for the full connect timeout.
The connection continues to warm in the background and later operations succeed. Raising
`DistributedHardTimeout` above `ConnectTimeout` trades cold-start misses for cold-start latency —
make that change deliberately, not by accident.

**What to do.** Nothing, if the origin can absorb one pod's cold traffic. If it cannot: stagger pod
starts, pre-warm the hot keys, or raise `Resilience.DistributedHardTimeout` so early reads wait for
the connection instead of missing. Watch `caching.net.misses` and `caching.net.factory.executions`
against pod age — a burst confined to the first seconds after start is this, not a cache defect.

### Startup fails with `OptionsValidationException`

Read the message. Every failure names the property and the fix, and all failures are reported at
once. See the [troubleshooting table](../README.md#26-troubleshooting).

## 4. Capacity

- **Memory layer.** Unbounded by default. `Entry.MemorySizeLimit` is the cap, and it is **not a byte
  or megabyte budget**: it is a ceiling on the **summed `Entry.Size` the cached entries declare**, in
  whatever unit the application charges. Caching.NET cannot measure the memory footprint of an
  arbitrary cached object, so nothing here is weighed in bytes.
  - With `Entry.Size: 1` — what `CachingBuilder.WithMemorySizeLimit(limit, defaultEntrySize: 1)` sets
    — every entry charges one unit, so the limit is simply a cap on the **number of entries** held in
    memory. `WithMemorySizeLimit(limit: 10_000)` means at most 10 000 entries, whatever they weigh.
  - To make it approximate bytes, charge each entry a size in the unit you choose — per call via
    `CacheEntryOverrides.Size`, or as the `Entry.Size` default — and express the limit in that same
    unit. Sizing a pod against a limit whose entries all charge `1` will not bound resident memory:
    200 entries of ~400 KB each (about 78 MB) sit comfortably under a limit of 10 000.
  - Once a limit is set, an entry with **no** size — neither per call nor via `Entry.Size` — is not
    cached at all. Startup validation therefore rejects a limit with no `Entry.Size`, and rejects
    `Entry.Size` of zero or less: an entry that charges nothing can never move the sum, so the cap
    would look configured while the memory layer stayed unbounded.
  - **`Clear()` does not release memory, and is not a remedy for memory pressure.** Clearing is
    implemented as a marker every read compares itself against (see
    [ARCHITECTURE §3.1](ARCHITECTURE.md#31-the-mode-also-has-to-reach-the-tag-markers)), so entries
    become logically invisible while staying physically resident until the memory layer expires or
    evicts them on its own. Measured: 100 000 small entries occupied 114 MB, and the managed heap was
    still 113 MB thirty seconds after `ClearAsync()` — including reads that touched the cleared keys.
    The only things that bound resident memory are `Entry.MemorySizeLimit` and the entries' own
    lifetimes. The same 100 000 writes against `WithMemorySizeLimit(5_000)` settled at 6 MB.
  - Size the pod's memory request against the cap, not against expected traffic. With no cap, L1 grows
    to whatever the key space and the lifetimes allow, which in a memory-limited container is an OOM
    risk rather than an eviction.
- **Redis command volume per read.** Sizing a Redis instance against "one command per cache read" is
  wrong in `Redis` mode. Because tag and `Clear` markers are authoritative there (see
  [ARCHITECTURE §3.1](ARCHITECTURE.md#31-the-mode-also-has-to-reach-the-tag-markers)), a read that
  **hits** costs `3 + n` commands for an entry with `n` tags — the entry, the two reserved `Clear`
  markers, and one marker per tag. A read that **misses** costs 1: with no entry, there is nothing a
  marker could invalidate.
  - Plan against the **hit** rate, not the request rate. `commands ≈ requests × (1 + hitRatio × (2 + n))`.
    At a 95% hit ratio with two tags per entry, 10 000 reads/s becomes ≈48 000 Redis commands/s; the
    same traffic at a 20% hit ratio becomes ≈18 000.
  - Measured on loopback (see [BENCHMARKS](BENCHMARKS.md#redis-mode-the-cost-of-authoritative-tag-markers)),
    this is ×3.10 read latency untagged and ×4.92 with two tags, and roughly **double the per-read
    allocation** — 6.08 KB → 12.2 KB untagged, 19.0 KB with two tags. Budget pod CPU and GC headroom
    for that, not just Redis.
  - `Hybrid` pays none of this on the read path (measured 421.8 ns L1 hit, unchanged; 740.6 ns with two
    tags, still no round trip). **A service in `Redis` mode for latency rather than for cross-instance
    consistency should be in `Hybrid` mode.**
  - Fewer tags per entry is the direct lever: each tag is one more command on every hit.
- **Redis.** With fail-safe on (the default) the **physical** Redis TTL is the entry's duration
  **plus** `Resilience.FailSafeMaxDuration`, not the longer of the two: the stale copy has to outlive
  logical expiry for fail-safe to have anything to serve. Under the defaults a one-minute entry
  occupies Redis for two hours and one minute. Budget Redis memory against that sum, and lower
  `Resilience.FailSafeMaxDuration` if it is the dominant term. Verified directly against `TTL` on a
  live key.
- **Payload size.** `Serialization.MaximumPayloadBytes` (1 MiB default) is a guardrail, not a target.
  Values close to it are a signal to cache a projection instead of a whole aggregate.
- **Auto-recovery queue.** Bounded by `Resilience.AutoRecoveryMaxItems` (1000 default), which caps
  memory growth during a long outage.
