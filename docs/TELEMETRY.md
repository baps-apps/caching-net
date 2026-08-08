# Telemetry

Caching.NET v3 publishes everything under its own instrumentation names. One activity source, one
meter — the internal cache engine's own diagnostics are never registered, so nothing an application
sees ever carries an engine name.

## 1. Wiring

```csharp
using Caching.NET.Telemetry;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceName))   // branded spans
    .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterName));            // branded metrics
```

That is the only wiring there is. Caching.NET takes **no dependency on OpenTelemetry**: it publishes
`System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter` and hands you the names.

| Constant | Value |
|---|---|
| `CacheTelemetry.ActivitySourceName` | `Caching.NET` |
| `CacheTelemetry.MeterName` | `Caching.NET` |
| `CacheTelemetry.SystemName` | `caching.net` (the `cache.system` attribute value) |
| `CacheTelemetry.ActivitySourceNames` | `["Caching.NET"]` — every activity source Caching.NET emits from |
| `CacheTelemetry.MeterNames` | `["Caching.NET"]` — every meter Caching.NET emits from |

The plural forms exist for API symmetry with the singular ones (and so code that iterates "every
source/meter Caching.NET owns" has something to iterate), not because there is a second, more
detailed tier to opt into. There is exactly one source and one meter, and the singular and plural
forms register the same thing. Earlier drafts of this design considered surfacing the engine's own
sources and meters as an opt-in detail tier — that plan is gone: the engine's diagnostics are never
registered by Caching.NET, under any name, singular or plural.

### Do not register the engine's sources yourself

Caching.NET never registers them, but they still **exist in the process**. A wildcard `ActivityListener`
attached during a Hybrid operation observes:

```text
Caching.NET
ZiggyCreatures.Caching.Fusion
ZiggyCreatures.Caching.Fusion.Backplane
ZiggyCreatures.Caching.Fusion.Distributed
ZiggyCreatures.Caching.Fusion.Memory
```

With a normal `AddSource("Caching.NET")` pipeline you get only the first: measured on a real
`TracerProvider`, 533 exported spans, **all** from `Caching.NET`, none from the engine.

But OpenTelemetry auto-instrumentation, or a wildcard source filter such as `AddSource("*")`, will
pick the engine's sources up too and **double-instrument every cache operation** — two spans for one
logical read, inflating both your trace bill and any span-derived latency metric. These are internal
names that will disappear if the engine is ever swapped, so nothing should depend on them.

Register `Caching.NET` explicitly and do not add `ZiggyCreatures.*`.

## 2. Metrics

Meter `Caching.NET`. Every instrument has exactly one producer, split across two paths:

- **The adapter's synchronous path.** `Internal/FusionCacheService` — the type that implements
  `ICacheService` — records `caching.net.hits`, `caching.net.misses`, `caching.net.operations` and
  `caching.net.invalidations` (`remove`/`expire`/`remove_by_tag`/`clear`) directly, once per logical
  call, on the caller's own thread. The layer decorators
  (`InstrumentedMemoryCache`, `InstrumentedDistributedCache`) record `caching.net.layer.duration` the
  same way, once per physical probe of that layer — which can be more than once per logical call.
  `InstrumentedCacheSerializer` records `caching.net.serialization.duration` and
  `caching.net.payload.size` synchronously around each serialize/deserialize.
- **The engine's event pump.** `Internal/CacheEventBridge` subscribes to the engine's internal event
  hub at cache construction and records `caching.net.factory.executions` (foreground and background),
  the factory part of `caching.net.errors`, `caching.net.fail_safe.served`, `caching.net.evictions`,
  and `caching.net.background.operations` (eager refresh, backplane
  publish/receive) from background event-handler callbacks. These signals live here because the
  engine reuses the exact same factory delegate for a foreground call and for a background
  eager-refresh/timed-out-factory completion; only the engine's own code path knows which one just
  ran. Recording it from the adapter as well would double-count every eager-refresh cycle — measured
  at 180 records for 120 real executions before the split (see `CacheEventBridge`'s remarks).

Because of that split, **`Observability.EnableMetrics: false` has two different effects**: the
adapter's own recorders short-circuit on a single cheap flag check, and `CacheEventBridge` is never
constructed at all, so the engine stops building event arguments and queuing dispatches for this
cache — the event-pump-sourced instruments cost nothing, not just "nothing recorded".

| Instrument | Type | Unit | Meaning | Producer |
|---|---|---|---|---|
| `caching.net.operations` | Counter | `{operation}` | Logical operations, dimensioned by result. One record per `GetOrSet*`/`GetOrDefault*`/`TryGet*`/`Set*`/`Remove*`/`Expire*`/`RemoveByTag*`/`Clear*` call | Adapter |
| `caching.net.hits` | Counter | `{operation}` | One per logical `GetOrSet*`/`GetOrDefault*`/`TryGet*` read served from a cached value (a stale fail-safe read still counts as a hit) — not per physical layer probe | Adapter |
| `caching.net.misses` | Counter | `{operation}` | One per logical `GetOrSet*`/`GetOrDefault*`/`TryGet*` read with no usable value — not per physical layer probe | Adapter |
| `caching.net.errors` | Counter | `{error}` | Errors, dimensioned by layer and type | Event bridge (factory, serialization, circuit breakers) + backplane decorator + serializer decorator (corrupt payload) + Redis connection provider. **Not** the adapter: a verb that throws marks its span and rethrows, and the layer that actually failed counts it |
| `caching.net.factory.executions` | Counter | `{execution}` | Factory runs, foreground and background | Event bridge |
| `caching.net.fail_safe.served` | Counter | `{operation}` | Fail-safe activations | Event bridge |
| `caching.net.invalidations` | Counter | `{operation}` | One per caller-requested `Remove*`/`Expire*`/`RemoveByTag*`/`Clear*` call | Adapter |
| `caching.net.evictions` | Counter | `{eviction}` | One per entry dropped from the in-process memory layer — expired, evicted under `Entry.MemorySizeLimit`, replaced or removed. Engine-initiated, so it is deliberately *not* on `caching.net.operations` or `caching.net.invalidations`: a removal the application asked for would otherwise be counted twice | Event bridge |
| `caching.net.redis.errors` | Counter | `{error}` | Distributed-layer errors. Written alongside `caching.net.errors` whenever the layer dimension is `redis` | Redis connection provider + event bridge (serialization/deserialization, circuit breaker) + serializer decorator |
| `caching.net.backplane.errors` | Counter | `{error}` | Backplane subscribe, unsubscribe and publish failures, plus circuit-breaker openings. **A Redis outage does not report the publishes it prevents, but may record `CircuitBreakerOpen`, a failed re-subscribe, or — under the default background-write setting — a real publish failure** — see below | Event bridge + backplane decorator |
| `caching.net.background.operations` | Counter | `{operation}` | Eager refresh, backplane publish/receive (`cache.result=set`), and failed backplane subscribe/unsubscribe/publish (`cache.result=error`) | Event bridge (successes) + backplane decorator (failures) |
| `caching.net.guard.violations` | Counter | `{violation}` | Key, tag and payload limit breaches | Adapter / guard / serializer |
| `caching.net.redis.tls.validations` | Counter | `{validation}` | TLS handshake outcomes | Redis connection provider |
| `caching.net.serialization.duration` | Histogram | `ms` | Serialize and deserialize duration | Serializer decorator |
| `caching.net.payload.size` | Histogram | `By` | Serialized payload size | Serializer decorator |
| `caching.net.layer.duration` | Histogram | `ms` | Per-layer operation duration, gated on `Observability.EnableLayerMetrics` | Layer decorators (`cache.layer` = `memory`/`redis`) + the adapter for `cache.layer=factory`, which is the only place a factory's own duration can be timed |

Every instrument in this table has a test that it is actually emitted — including the failure-only
ones, against real outages: `DocumentedInstrumentsAreEmittedTests` (chaos), `GuardViolationMetricTests`,
`FailSafeMetricTests`, and `RedisSecurityTests` for the TLS instrument.

### Evictions are counted separately — read this before reusing a v3 pre-release dashboard

`caching.net.evictions` is a **new instrument**, and adding it changed what the two counters beside
it mean. An earlier revision of v3 routed the engine's eviction event through the invalidation
recorder, which also wrote `caching.net.operations`. The in-process memory layer raises an eviction
for `Removed` and `Replaced` as well as for expiry, so every removal, overwrite and expiry was booked
a second time on counters the adapter already owned. Measured on an in-memory cache:

| Workload | `caching.net.operations` | `caching.net.invalidations` | `caching.net.evictions` |
|---|---|---|---|
| 5 × `SetAsync` of one key | 9 → **5** | — | **4** |
| 5 × `SetAsync` + 5 × `RemoveAsync`, distinct keys | 15 → **10** | 10 → **5** | **5** |

What this means for an operator:

- **`caching.net.operations` is now exactly one record per `ICacheService` call**, so an operation
  rate or a `hits / operations` ratio reads correctly on a write- or invalidation-heavy cache. It was
  overstated by up to 2× before, and a TTL-driven cache booked an "operation" for every entry that
  merely expired.
- **`caching.net.invalidations` is caller-requested only** — one record per `Remove*`/`Expire*`/
  `RemoveByTag*`/`Clear*` call. It no longer moves when an entry expires or is overwritten.
- **Anything that used to show up as invalidation or operation churn without a caller behind it is
  now on `caching.net.evictions`.** Alert rules that watched the old counters for memory pressure
  should point at the new one. Pinned by `EvictionAccountingTests`.

### `caching.net.layer.duration` and `EnableLayerMetrics`

`caching.net.layer.duration` is the per-layer counterpart to the logical `caching.net.hits`/
`caching.net.misses` pair: it is recorded once per physical probe of the memory or distributed layer
(`cache.layer` = `memory` or `redis`), which in Hybrid mode can fire more than once for a single
logical call — an L1 miss followed by an L2 probe both record. It carries its own `cache.result`
(`hit`/`miss`/`set`/`removed`/`error`), so it is the only place per-layer truth exists; the logical
counters deliberately do not carry a `cache.layer` dimension (see `CacheTelemetryContext.RecordHit`'s
remarks). `Observability.EnableLayerMetrics` (default `true`) gates this histogram only — no counter,
including `caching.net.hits`, `caching.net.misses` and `caching.net.operations`, is affected by it.
Turn it off when the extra per-layer cardinality is not worth the histogram's cost and only the
logical hit/miss ratio matters.

### What `caching.net.backplane.errors` does and does not catch

One Redis multiplexer is shared by the distributed layer and the backplane. When Redis goes away and
the L2 write fails, the engine does not go on to publish an invalidation for a write that did not
land — so **an outage's prevented publishes do not appear on this counter**
(`RedisOutage_AttemptsNoBackplanePublish_WhenTheDistributedWriteFailsFirst`).

**That is an ordering guarantee only when the distributed write is awaited.** Under the production
default `Resilience.AllowBackgroundDistributedOperations = true`, the L2 write and the publish are
dispatched as background work, so "the write failed, therefore nothing was published" becomes a race
rather than a rule: the write can still land while the connection is dying, and the publish that
follows it then fails for real and *is* counted. The counter can also record
`cache.error.type=CircuitBreakerOpen` when the same outage opens the backplane's circuit breaker, and
a failed backplane re-subscribe during the outage is recorded too.

**So do not build an alert on the absence of publish errors, and do not treat a publish error during a
Redis outage as a second fault.** The stable statements are: a Redis outage always surfaces on
`caching.net.redis.errors`, and `caching.net.background.operations{cache.operation=backplane_publish,
cache.result=error}` is what identifies a publish failure specifically. The test above pins the
ordering claim in the configuration where it is actually a guarantee; two earlier revisions asserted it
for the default configuration instead and both flaked, the second on an observed
`RedisConnectionException` reaching the counter mid-outage.

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
| `cache.operation` | `get` (hits/misses only), `get_or_set`, `get_or_default`, `try_get`, `set`, `remove`, `expire`, `remove_by_tag`, `clear`, `serialize`, `deserialize`, `eager_refresh`, `backplane_publish`, `backplane_receive`, `backplane_subscribe`, `backplane_unsubscribe` (the last two only on a failure), and the guard-violation kinds `key_too_long`, `tag_rejected`, `payload_too_large`. `caching.net.evictions` carries no `cache.operation` — the memory layer does not report why an entry left |
| `cache.result` | `hit`, `miss`, `stale`, `set`, `removed`, `error`, `canceled` |
| `cache.layer` | `memory`, `redis`, `factory`, `backplane` |
| `cache.error.type` | Exception type name |
| `cache.background_operation` | `true` / `false` |

Never recorded: cache keys under any setting — `Security.AllowRawKeysInTelemetry` governs spans only
(§4), never a metric dimension; tag values (unless `Security.AllowTagsInTelemetry` is set, which
*is* metric-scoped); tenant or user identifiers, URLs, request identifiers, exception messages,
Redis endpoints, or arbitrary application-supplied values.

Set `Observability.IncludeCacheNameDimension: false` when an application registers many named caches
and `cache.name` becomes a cardinality problem.

### Useful queries

```text
# Hit ratio — still hits / (hits + misses): RecordHit/RecordMiss are logical-read-scoped, one call
# each per GetOrSet*/GetOrDefault*/TryGet* invocation, so the ratio is not inflated by the engine
# probing a layer more than once per logical read (tag lookups, lock double-checks).
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

## 3. Tracing

### Span catalogue

| Span | Emitted by | When |
|---|---|---|
| `cache.get_or_set` | `FusionCacheService` | Every `GetOrSetAsync`/`GetOrSet` call. `cache.result` is `hit`, `miss` or `stale`; `cache.factory.executed` records whether the factory ran |
| `cache.factory` | `FusionCacheService` | Nested inside `cache.get_or_set`, only when the factory actually runs (a miss) |
| `cache.get_or_default` | `FusionCacheService` | Every `GetOrDefaultAsync`/`GetOrDefault` call. **Deliberately carries no `cache.result` on a successful read** — pinned by `OperationSpanTests.WarmRead_EmitsItsOwnSpanWithNoResultTag`. The hit/miss outcome of the call is still on `caching.net.hits`/`.misses`/`.operations`; use `cache.try_get` when the span itself has to carry it |
| `cache.try_get` | `FusionCacheService` | Every `TryGetAsync`/`TryGet` call |
| `cache.set` | `FusionCacheService` | Every `SetAsync`/`Set` call |
| `cache.remove` | `FusionCacheService` | Every `RemoveAsync`/`Remove` call |
| `cache.expire` | `FusionCacheService` | Every `ExpireAsync`/`Expire` call |
| `cache.remove_by_tag` | `FusionCacheService` | Every `RemoveByTagAsync`/`RemoveByTag` call |
| `cache.clear` | `FusionCacheService` | Every `ClearAsync`/`Clear` call. The one operation span with **no key attribute** — there is no key to attach |
| `cache.memory.get` / `.set` / `.remove` | `InstrumentedMemoryCache` | Every physical probe of the in-process layer |
| `cache.redis.get` / `.set` / `.refresh` / `.remove` | `InstrumentedDistributedCache` | Every physical probe of the distributed layer |
| `cache.serialize` / `cache.deserialize` | `InstrumentedCacheSerializer` | A value crosses the wire boundary to or from the distributed layer |

Every span carries `cache.system`, `cache.mode`, `cache.name`. Operation spans (the `FusionCacheService`
rows) also carry `cache.key.fingerprint` — or `cache.key` when `Security.AllowRawKeysInTelemetry` is
set (see §4) — except `cache.clear`, which has no key. Most also carry `cache.result` and, where
relevant, `cache.layer` and `cache.factory.executed`; the two exceptions are `cache.get_or_default`,
which never carries `cache.result` on a successful read, and `cache.get_or_set` in Hybrid mode, which
omits `cache.layer` on a hit (see the worked example below). `cache.operation` is a *metric*
dimension, not a span tag — the operation is the span name — apart from `cache.serialize` /
`cache.deserialize`, which carry it explicitly along with `cache.layer=redis` and
`cache.payload.bytes`. All spans are created only when a listener is attached to the `Caching.NET`
source; `Observability.EnableTracing: false` suppresses them even with a listener attached.

### Worked examples

**InMemory, cold miss** — `GetOrSetAsync` with nothing cached:

```text
cache.get_or_set        cache.result=miss cache.layer=factory cache.factory.executed=true
└── cache.memory.get    cache.result=miss                         (probed by the engine, found nothing)
└── cache.factory       cache.result=hit                          (the caller's factory ran and returned a value)
└── cache.memory.set    cache.result=set                          (the produced value is stored in L1)
```

**InMemory, warm hit** — the same key, second call:

```text
cache.get_or_set        cache.result=hit cache.layer=memory cache.factory.executed=false
└── cache.memory.get    cache.result=hit
```

**Redis mode ("L2 only"), cold miss:**

```text
cache.get_or_set        cache.result=miss cache.factory.executed=true    (no cache.layer — see below)
└── cache.redis.get     cache.result=miss
└── cache.factory       cache.result=hit
└── cache.serialize                                                     (the produced value is encoded for the wire)
└── cache.redis.set     cache.result=set
```

**Redis mode, warm hit:**

```text
cache.get_or_set        cache.result=hit cache.layer=redis cache.factory.executed=false
└── cache.redis.get     cache.result=hit
└── cache.deserialize                                                   (the stored value is decoded off the wire)
```

**Hybrid, cold miss** — both layers probed, both layers populated:

```text
cache.get_or_set        cache.result=miss cache.layer=factory cache.factory.executed=true
├── cache.memory.get    cache.result=miss
├── cache.redis.get     cache.result=miss
├── cache.factory       cache.result=hit
├── cache.serialize
├── cache.redis.set     cache.result=set
└── cache.memory.set    cache.result=set
```

**Hybrid, warm hit** — this is the case worth reading closely. `cache.get_or_set` in Hybrid mode
**omits `cache.layer` on a hit**, because a Hybrid hit can be answered by L1 *or* L2 (L2 after an L1
miss — exactly the case an operator investigates: a cold instance, a post-deploy restart, an evicted
L1 entry, a short `Entry.LocalExpiration`), and the engine's hit signal carries no level information
on the common path. Reporting `memory` when Redis actually answered would be worse than reporting
nothing, so `InMemory` and `Redis` modes get a tautological `cache.layer` (each has exactly one
layer), and `Hybrid` does not — per-layer truth for Hybrid lives on the child spans and on
`caching.net.layer.duration` instead:

```text
# Answered from L1:
cache.get_or_set        cache.result=hit cache.factory.executed=false   (no cache.layer)
└── cache.memory.get    cache.result=hit

# Answered from L2 after an L1 miss:
cache.get_or_set        cache.result=hit cache.factory.executed=false   (no cache.layer)
├── cache.memory.get    cache.result=miss
└── cache.redis.get     cache.result=hit
```

### Never recorded on a Caching.NET span

Cache values, serialized payloads, connection strings, credentials, tokens, user or tenant
identifiers, PII. Exceptions are recorded with type only (`cache.error.type`), never a message.

Cache keys are the one attribute that is opt-in rather than always-absent — see §4.

Cancellation is not reported as an internal error: a cancelled caller produces
`OperationCanceledException` and no error counter increment.

## 4. Raw cache keys: `AllowRawKeysInTelemetry`

By default, every operation span carries a non-reversible key fingerprint
(`cache.key.fingerprint`, produced by `ICacheGuard.Fingerprint`) instead of the caller's key. Setting
`Security.AllowRawKeysInTelemetry` (default `false`) switches every operation span to
`cache.key` — the literal key the application passed to the cache, prefix included — for that cache
instance:

```csharp
services.AddCaching(cache => cache
    .UseInMemory()
    .WithApplicationPrefix("orders-api")
    .WithSecurity(security => security.AllowRawKeysInTelemetry = true));
```

`CacheTelemetryContext.StartOperation` makes the choice once per span, so there is no processor or
collector step required either way — this replaced an earlier design where the raw key was an
unavoidable property of engine-owned spans that had to be stripped downstream. Now it is a first-class
setting: off by default because a cache key routinely embeds a tenant id, user id, email or order
number, and span attributes are indexed and retained under the tracing backend's own policy, readable
by everyone with trace access. Treat it as a data-flow decision, not a debug toggle — see
[SECURITY.md §9](SECURITY.md#9-raw-keys-in-telemetry-allowrawkeysintelemetry).

Metrics and logs are unaffected by this setting: neither ever carries a raw key (see §5 for log
redaction, which has its own, separate `Security.AllowRawKeysInLogs` flag).

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
rendered message and the structured property. This is a separate switch from `AllowRawKeysInTelemetry`
(§4): one controls log lines, the other controls trace spans, and they can be set independently. See
[SECURITY.md](SECURITY.md#key-redaction-in-engine-log-lines).

### Engine per-operation lines and `Observability.EngineOperationLogLevel`

The internal cache engine logs every cache call, and every cache result, at `Information` — the level
a production application normally runs at. Measured on this package that is **2.04 log lines per
`GetOrSet`**, each carrying a full dump of the entry's resolved options. On a hot path that is a
logging bill rather than a diagnostic.

Caching.NET therefore rewrites those lines to `Observability.EngineOperationLogLevel`, which defaults
to `Debug`. With the default, running the `Caching.NET` category at `Information` costs **zero**
engine lines per operation (measured: 0 lines across 100 `GetOrSet` calls); dropping the category to
`Debug` brings all of them back (measured: 3.42 lines per operation).

| `EngineOperationLogLevel` | Effect |
|---|---|
| `Debug` (default) | Per-operation lines are written at `Debug`, so they are off at production levels and one filter change away. |
| `Information` | The engine's native verbosity, for a low-traffic service or a local repro. |
| `None` | Per-operation lines are dropped entirely. |

Only lines the engine emits at exactly `Information` are rewritten — warnings and errors are never
downgraded. Because per-operation chatter is the only thing the engine reports at `Information`,
setting any of the `Observability.*LogLevel` diagnostic properties to `Information` while this rewrite
is active would put that diagnostic in the same bucket; `CachingOptionsValidator` rejects that
combination at startup rather than silently lowering a level an operator deliberately raised.

### Choosing a level for the `Caching.NET` category in production

The Caching.NET messages an operator actually needs — startup summary, guard violations, corrupt
payloads, Redis lifecycle, fail-safe activation, Redis degradation — are all at `Warning` or above,
apart from the startup summary and the connection-restored line.

```json
{ "Logging": { "LogLevel": { "Caching.NET": "Warning", "Caching.NET.Redis": "Information" } } }
```

`Caching.NET.Redis` at `Information` keeps connection-lifecycle lines, which are low-volume and the
first thing anyone looks for during an incident. `Information` on the root category is now safe as a
standing setting — the engine chatter that made it expensive is at `Debug`. Drop the category to
`Debug` when reproducing a specific cache problem. The startup summary is independent of this and can
be kept with `Observability.LogStartupSummary`.

### Storm suppression

During a Redis outage the distributed circuit breaker opens for
`Resilience.DistributedCircuitBreakerDuration` (5s default), which suppresses both retries and their
log entries. A chaos test asserts that 200 cache operations during a full outage produce
substantially fewer than 200 warning-level entries.

## 6. Overhead

Measured — see [BENCHMARKS.md](BENCHMARKS.md#telemetry-overhead). On the in-memory hit path:
133.2 ns / 192 B with telemetry off, 153.4 ns / 192 B with metrics on and no trace listener, 439.0 ns
/ 1,464 B with a trace listener attached. Metrics-only cost is small — roughly 20 ns and no extra
allocation on a hit, because `FusionCacheService` now calls `CacheTelemetryContext.RecordHit`/
`RecordMiss` directly, one producer per signal, gated on a single flag check, rather than routing
through the engine's own event dispatch the way earlier revisions did. A live trace listener is the
expensive tier, because it is the only tier that actually walks a span's tag list.

`EnableTracing` and `EnableMetrics` still default to `true`. `ActivitySource.HasListeners()` and the
metrics-enabled flag are checked before any attribute value is built, so no *attribute* is allocated
when nobody is listening.

On by default is still the right choice for a service, where a cache hit stands in for a database
round trip. Turn metrics off for a cache whose hit path is itself the hot loop.
