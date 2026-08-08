# Telemetry

Caching.NET v3 publishes everything under its own instrumentation names. This file covers the wiring
and the one place where the internal engine's naming does surface.

## 1. Wiring

```csharp
using Caching.NET.Telemetry;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceName))   // branded spans, no cache keys
    .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterName));            // branded metrics, no overlap
```

That is the recommended wiring. The plural forms (`ActivitySourceNames`, `MeterNames`) add the
engine's own sources and meters: more detail, at the cost of **exported cache keys** (tracing, see
§2.1) and **double-counted operations** (metrics, see below). Add them deliberately, not by default.

Caching.NET takes **no dependency on OpenTelemetry**. It publishes `System.Diagnostics.ActivitySource`
and `System.Diagnostics.Metrics.Meter` and hands you the names.

| Constant | Value |
|---|---|
| `CacheTelemetry.ActivitySourceName` | `Caching.NET` |
| `CacheTelemetry.MeterName` | `Caching.NET` |
| `CacheTelemetry.SystemName` | `caching.net` (the `cache.system` attribute value) |
| `CacheTelemetry.ActivitySourceNames` | Caching.NET + the engine's four sources |
| `CacheTelemetry.MeterNames` | Caching.NET + the engine's four meters |

## 2. The integration decision

Four options were on the table for reconciling Caching.NET telemetry with the engine's own
diagnostics. Caching.NET uses a combination of two.

| Option | Used? |
|---|---|
| Consume engine diagnostics and re-emit them as Caching.NET telemetry | **For metrics — yes.** The event bridge subscribes to the engine's event hub and records against the Caching.NET meter. |
| Add Caching.NET spans only for what Caching.NET owns | **Yes.** `cache.serialize` and `cache.deserialize` come from the serializer decorator, a component Caching.NET owns outright. |
| Reuse engine diagnostics, documenting registration through Caching.NET | **Yes, for operation-level spans.** `CacheTelemetry.ActivitySourceNames` carries the names so application code never types them. |
| Suppress engine spans | **No.** They are the useful low-level detail. An application that does not want them registers only `CacheTelemetry.ActivitySourceName`. |

### Registering both meters counts the same operation twice

`CacheTelemetry.MeterNames` contains the Caching.NET meter **and** the engine's four meters, and the
two overlap: one hit increments `caching.net.hits` *and* `fusioncache.cache.hit`. That is intended —
the engine meters carry per-level detail (`fusioncache.memory.*`, `fusioncache.distributed.*`) that
Caching.NET does not re-emit — but a dashboard must not add the two families together, and an
application that only wants the branded metrics should register `CacheTelemetry.MeterName` alone:

```csharp
.WithMetrics(m => m.AddMeter(CacheTelemetry.MeterName));         // Caching.NET metrics only
.WithMetrics(m => m.AddMeter(CacheTelemetry.EngineMeterNames));  // add per-layer detail knowingly
```

| Property | Contents |
|---|---|
| `CacheTelemetry.MeterName` | The Caching.NET meter. Recommended default. |
| `CacheTelemetry.EngineMeterNames` | The four engine meters. Per-layer detail, **overlaps** the branded meter. |
| `CacheTelemetry.MeterNames` | Both. Convenient, but do not sum the two families on a dashboard. |
| `CacheTelemetry.ActivitySourceName` | The Caching.NET source (`cache.serialize` / `cache.deserialize`). No cache keys. Recommended default. |
| `CacheTelemetry.EngineActivitySourceNames` | The four engine sources. Operation and per-layer spans; no span overlap, but **they export the raw cache key** — see §2.1. |
| `CacheTelemetry.ActivitySourceNames` | Both. Most detail, **exports cache keys**. |

### 2.1 Engine spans export the raw cache key

Every engine operation span carries the full physical cache key — prefix included — in the
`fusioncache.operation.key` attribute:

```text
source: ZiggyCreatures.Caching.Fusion
span:   get or set from cache
tags:   fusioncache.cache.name=default
        fusioncache.operation.key=orders-api:prod:Order:user-4815162342   <-- raw key
```

The engine exposes no option to suppress it, and Caching.NET cannot strip it without wrapping every
cache call. So this is a property of the engine sources, not a setting:

- Registering **`CacheTelemetry.ActivitySourceName` alone keeps every cache key out of the tracing
  backend.** Caching.NET's own spans never carry one. This is asserted by
  `SpanKeyExposureTests.CachingNetSpans_NeverCarryTheCacheKey`.
- Registering `EngineActivitySourceNames` or `ActivitySourceNames` **exports cache keys.** That is
  fine when keys are opaque identifiers; it is a data-protection decision when a key embeds a user
  id, tenant id, email or token. `SpanKeyExposureTests.EngineSpans_DoCarryTheRawPhysicalKey_…` pins
  the behaviour so this warning cannot quietly go stale.
- To keep the detail without the keys, drop the attribute in the collector. The name is published as
  `CacheTelemetry.EngineKeyAttributeName` so application code does not have to hard-code it:

```csharp
// OpenTelemetry processor: keep engine spans, drop the key attribute.
sealed class DropCacheKeyProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
        => activity.SetTag(CacheTelemetry.EngineKeyAttributeName, null);
}
```

Metrics and logs are unaffected: neither ever carries a raw key.

### The honest consequence

Operation-level spans (`get`, `set`, `get or set`, memory level, distributed level, backplane) arrive
in a tracing backend under `ZiggyCreatures.Caching.Fusion*` source names. **Metrics and logs are
fully Caching.NET-branded; low-level trace source names are not.**

Renaming those spans would require intercepting every cache operation — the delegating wrapper this
release exists to remove. Re-emitting them alongside would double every span. Neither trade was
worth making for a source-name string that appears only in an observability backend, never in
application code or configuration.

If source names matter more than span detail:

```csharp
// Caching.NET spans only: cache.serialize / cache.deserialize.
.WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceName));
```

## 3. Metrics

Meter `Caching.NET`. Recorded from the engine's event pump, off the caller's path.

With `Observability.EnableMetrics: false` Caching.NET does not subscribe to the event hub at all, so
the engine stops building event arguments and queueing dispatches for this cache — the cost goes to
zero rather than to a no-op handler.

| Instrument | Type | Unit | Meaning |
|---|---|---|---|
| `caching.net.operations` | Counter | `{operation}` | Operations, dimensioned by result |
| `caching.net.hits` | Counter | `{operation}` | Reads served from a cached value |
| `caching.net.misses` | Counter | `{operation}` | Reads with no usable value |
| `caching.net.errors` | Counter | `{error}` | Errors, dimensioned by layer and type |
| `caching.net.factory.executions` | Counter | `{execution}` | Factory runs, foreground and background |
| `caching.net.fail_safe.served` | Counter | `{operation}` | Fail-safe activations (one per activation; the stale read itself is also counted as a hit with `cache.result=stale`) |
| `caching.net.invalidations` | Counter | `{operation}` | Removals, expirations, tag invalidations, clears, evictions |
| `caching.net.redis.errors` | Counter | `{error}` | Distributed-layer errors |
| `caching.net.backplane.errors` | Counter | `{error}` | Backplane subscribe, unsubscribe and publish failures, plus circuit-breaker openings. **Does not fire for a plain Redis outage** — see below |
| `caching.net.background.operations` | Counter | `{operation}` | Eager refresh, backplane publish/receive |
| `caching.net.guard.violations` | Counter | `{violation}` | Key, tag and payload limit breaches |
| `caching.net.redis.tls.validations` | Counter | `{validation}` | TLS handshake outcomes |
| `caching.net.serialization.duration` | Histogram | `ms` | Serialize and deserialize duration |
| `caching.net.payload.size` | Histogram | `By` | Serialized payload size |

Every instrument in this table has a test that it is actually emitted — including the failure-only
ones, against real outages: `DocumentedInstrumentsAreEmittedTests` (chaos),
`GuardViolationMetricTests`, `FailSafeMetricTests`, and `RedisSecurityTests` for the TLS instrument.

### What `caching.net.backplane.errors` does and does not catch

One Redis multiplexer is shared by the distributed layer and the backplane. When Redis goes away,
the L2 write fails first and **the backplane publish is never attempted**, so no backplane error is
recorded: measured at 298 writes across a 30-second outage with the counter reading zero
(`RedisOutage_DoesNotProduceBackplaneErrors_BecauseNoPublishIsAttempted`).

| Situation | Instrument that fires |
|---|---|
| Redis unreachable | `caching.net.redis.errors` |
| Backplane subscribe fails at startup | `caching.net.backplane.errors` |
| Publish or unsubscribe throws | `caching.net.backplane.errors` |
| Backplane circuit breaker opens | `caching.net.backplane.errors` |

**Alert on `caching.net.redis.errors` for connectivity.** Use `caching.net.backplane.errors` for the
narrower question of whether cross-pod invalidation is wired up and working.

### Dimensions

Allowed, and asserted by a unit test:

| Dimension | Values |
|---|---|
| `cache.system` | `caching.net` |
| `cache.mode` | `InMemory`, `Redis`, `Hybrid` |
| `cache.name` | Configured cache name |
| `cache.operation` | `get`, `set`, `remove`, `expire`, `remove_by_tag`, `clear`, `eviction`, `serialize`, `deserialize`, `eager_refresh`, `backplane_publish`, `backplane_receive`, guard-violation kinds |
| `cache.result` | `hit`, `miss`, `stale`, `set`, `removed`, `error` |
| `cache.layer` | `memory`, `redis`, `factory`, `backplane` |
| `cache.error.type` | Exception type name |
| `cache.background_operation` | `true` / `false` |

Never recorded: cache keys, tag values, tenant or user identifiers, URLs, request identifiers,
exception messages, Redis endpoints, or arbitrary application-supplied values.

Set `Observability.IncludeCacheNameDimension: false` when an application registers many named caches
and `cache.name` becomes a cardinality problem.

### Useful queries

```text
# Hit ratio
sum(rate(caching_net_hits_total[5m]))
  / (sum(rate(caching_net_hits_total[5m])) + sum(rate(caching_net_misses_total[5m])))

# Redis health, by application
sum by (cache_name) (rate(caching_net_redis_errors_total[5m]))

# Fail-safe firing = an upstream dependency is failing
sum(rate(caching_net_fail_safe_served_total[5m]))

# Limit breaches = a key/tag/payload defect
sum by (cache_operation) (rate(caching_net_guard_violations_total[5m]))
```

Exported metric names depend on your exporter's naming convention; the examples above use the
Prometheus convention.

## 4. Tracing

### Caching.NET spans

| Span | When |
|---|---|
| `cache.serialize` | A value is serialized for the distributed layer |
| `cache.deserialize` | A value is read back from the distributed layer |

Attributes: `cache.system`, `cache.mode`, `cache.name`, `cache.operation`, `cache.layer`,
`cache.payload.bytes`.

Caching.NET never attaches `cache.key.fingerprint` itself — the engine resolves entry options before
the operation span starts, so nothing can add a key attribute to that span without wrapping every
call. `CacheTelemetryAttributes.KeyFingerprint` is published as the attribute *name* for
applications that want to add it from `ICacheGuard.Fingerprint(key)`.

Spans are created only when a listener is attached. `Observability.EnableTracing: false` suppresses
them entirely, even with a listener.

### Engine spans

Operation, memory-level, distributed-level and backplane spans come from the engine's sources, which
are included in `CacheTelemetry.ActivitySourceNames`. **They carry the raw physical cache key** —
see §2.1 before registering them.

### Never recorded on a Caching.NET span

Cache values, serialized payloads, cache keys, connection strings, credentials, tokens, user or
tenant identifiers, PII. Exceptions are recorded with type only.

This covers the `Caching.NET` activity source. The engine sources record the cache key (§2.1);
nothing else in that list appears on them either.

Cancellation is not reported as an internal error: a cancelled caller produces
`OperationCanceledException` and no error counter increment.

## 5. Logging

| Category | Contents |
|---|---|
| `Caching.NET` | Startup summary, guard violations, corrupt payloads, and all engine output (re-categorised) |
| `Caching.NET.Redis` | Connection lifecycle, TLS handshake and rejection |
| `Caching.NET.Backplane` | Backplane publish/subscribe |
| `Caching.NET.Security` | Reserved |
| `Caching.NET.Configuration` | Reserved |

```json
{ "Logging": { "LogLevel": { "Caching.NET": "Warning", "Caching.NET.Redis": "Information" } } }
```

That is the production setting; see
[Choosing a level for the `Caching.NET` category](#choosing-a-level-for-the-cachingnet-category-in-production)
for why the root category is not `Information`.

| Level | Used for |
|---|---|
| Trace | Detailed development diagnostics (engine) |
| Debug | Cache flow, internal decisions, synthetic timeouts |
| Information | Startup summary, connection restored, first TLS handshake, **one engine line per cache operation** |
| Warning | Stale value served, Redis degradation, corrupt payload, oversized payload, limit breach |
| Error | Failed cache operations, TLS rejection |
| Critical | Redis connection could not be opened at all |

Per-class levels are configurable through `Observability.*LogLevel`, including
`SyntheticTimeoutLogLevel`, which defaults to `Debug` because soft timeouts are expected under load
and would otherwise flood logs.

Hot paths use source-generated logging (`CacheLogMessages`), so nothing is allocated when a level is
disabled.

### Cache keys in engine log lines

The engine writes the physical cache key into a structured `CacheKey` property on its per-operation
lines. Caching.NET's logger adapter replaces that value with the `ICacheGuard.Fingerprint` digest
before the line reaches any provider, unless `Security.AllowRawKeysInLogs` is set — in both the
rendered message and the structured property. See
[SECURITY.md](SECURITY.md#key-redaction-in-engine-log-lines).

### Choosing a level for the `Caching.NET` category in production

`Information` on this category costs roughly **one engine log line per cache operation**. That is
useful in development and on a low-traffic service; on a hot path it is a meaningful share of an
application's log bill and ingestion budget. The Caching.NET messages an operator actually needs —
startup summary, guard violations, corrupt payloads, Redis lifecycle, fail-safe activation, Redis
degradation — are all at `Warning` or above, apart from the startup summary and the
connection-restored line.

```json
{ "Logging": { "LogLevel": { "Caching.NET": "Warning", "Caching.NET.Redis": "Information" } } }
```

`Caching.NET.Redis` at `Information` keeps connection-lifecycle lines, which are low-volume and the
first thing anyone looks for during an incident. Turn the root category up to `Information` or
`Debug` when reproducing a specific cache problem, not as a standing setting. The startup summary is
independent of this and can be kept with `Observability.LogStartupSummary`.

### Storm suppression

During a Redis outage the distributed circuit breaker opens for
`Resilience.DistributedCircuitBreakerDuration` (5s default), which suppresses both retries and their
log entries. A chaos test asserts that 200 cache operations during a full outage produce
substantially fewer than 200 warning-level entries.

## 6. Overhead

Measured — see [BENCHMARKS.md](BENCHMARKS.md#telemetry-overhead). On the in-memory hit path:
115 ns / 192 B with telemetry off, 305 ns / 543 B with metrics on, 476 ns / 544 B with a trace
listener attached. Metrics are not free — roughly 190 ns and 350 B per hit, spent on the engine's
event dispatch rather than on attribute construction.

On by default is still the right choice for a service, where a cache hit stands in for a database
round trip. Turn metrics off for a cache whose hit path is itself the hot loop.
