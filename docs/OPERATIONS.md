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
- **`Logging:LogLevel:Caching.NET: Warning`** in production. `Information` on that category costs
  about one engine log line per cache operation; everything an operator needs during an incident is
  `Warning` or above.
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

1. Check `caching.net.invalidations` — is something invalidating more than expected?
2. Check `caching.net.errors{layer=redis}` — L2 failures turn every read into a miss.
3. Check whether `Entry.LocalExpiration` or `DefaultExpiration` changed in a recent deploy.
4. Check `caching.net.guard.violations` — oversized values are silently not cached (by design), so a
   payload that grew past the limit shows up here, not as an error.

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

### Startup fails with `OptionsValidationException`

Read the message. Every failure names the property and the fix, and all failures are reported at
once. See the [troubleshooting table](../README.md#26-troubleshooting).

## 4. Capacity

- **Memory layer.** Unbounded by default. Set `Entry.MemorySizeLimitMegabytes` together with
  `Entry.Size` when the working set could outgrow the pod's memory limit — validation rejects a size
  limit without a default entry size, because entries would then be rejected by the memory layer.
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
