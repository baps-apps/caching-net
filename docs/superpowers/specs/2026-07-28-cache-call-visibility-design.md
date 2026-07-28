# Per-Call Cache Visibility — Design

**Date:** 2026-07-28
**Status:** Approved, ready for implementation planning
**Target version:** 2.3.0 (additive; no `ICacheService` change)

## Goal

For every call into Caching.NET, a consumer must be able to answer:

1. Which mode handled it (`InMemory`, `Redis`, `Hybrid`)?
2. Which operation was it?
3. Was the value served from cache, or from the source (factory)?
4. How long did the whole call take?
5. When it came from the source, how long did the source take — separately from cache cost?

Carriers are OpenTelemetry **metrics** and **traces** only. No per-call log line.

## Non-goals

- **L1-vs-L2 attribution inside Hybrid mode.** A cache-served Hybrid call will not say whether the value came from local memory or Redis. Deliberately cut: it required an `IDistributedCache` decorator plus an `AsyncLocal` call scope, which is most of the complexity for one field. It can be added later as a `cache.tier` tag without changing anything defined here. In **Redis mode this is not a gap** — a cache-served call does nothing but talk to Redis, so total *is* Redis latency.
- Separate write-back and stampede-lock-wait phase instruments.
- Changing `ICacheService`. Per the API stability contract in CLAUDE.md, all additions are instruments, tags, and internals.
- A per-call log line.

## Current state (as of commit on `main` plus uncommitted duration work)

- `cache.operation.duration` is recorded inside each backend service around each operation. Composite operations nest: `refresh` also emits `set`, `set_many` emits one `set` per item, Hybrid `get_many`/`exists` emit inner `get` samples. Summing across operations double counts.
- The histogram carries only `cache.mode` + `cache.operation`. Hit and miss samples are indistinguishable, so a blended p99 cannot be attributed to cache vs source.
- Factory (source) time is never recorded as its own value. On a miss it is folded into the `get_or_create` sample.
- Four paths record no duration at all: force refresh, background stale refresh, per-call bypass, and `Enabled=false` — plus Redis keys over `MaximumKeyLength`.
- `CacheInstruments.Activity` exists but no code calls `StartActivity`. `CacheOptions.IncludeKeyHashInTraces` is never read.

## Design

### Recording site

`RoutingCacheService` is the single entry point every consumer call passes through. It owns the per-call record: one span, one `cache.operation.duration` sample, at most one `cache.factory.duration` sample. Per-service recording is removed, which eliminates nesting and double counting.

Consequence: a call made directly against a concrete service (`InMemoryCacheService`, `RedisCacheService`, `HybridCacheService`) emits no total. Only tests do that; DI always registers `RoutingCacheService` as `ICacheService`.

### No ambient state required

Routing *creates* the factory wrapper before dispatching, so the wrapper closes over a local object. No `AsyncLocal`, no DI interception, no decorator. A consumer whose factory itself calls the cache nests naturally: each call has its own local recorder and its own span, and the outer call's `factory_ms` legitimately includes the inner call's total.

### New component: `CacheCallRecorder`

`internal sealed class CacheCallRecorder : IDisposable`, new file `src/Caching.NET/Telemetry/CacheCallRecorder.cs`. Keeps span and metric logic out of `RoutingCacheService`, which is already ~730 lines.

```csharp
static CacheCallRecorder Start(string mode, string operation, CacheOptions options, string? rawKey);
Func<CancellationToken, Task<T>> WrapFactory<T>(Func<CancellationToken, Task<T>> factory);
void SetMode(string resolvedMode);          // routing resolves the backend after entry
void MarkServedFromCache();                 // single-key read found a value
void MarkNotFound();                        // single-key read found nothing
void MarkBatch(int hits, int misses);       // batch read outcome
void MarkCoalesced();                       // waited on another caller's factory
void MarkMissReason(string reason);
void MarkError(string errorKind, bool thrownToCaller);
void Dispose();                             // emits metrics, stamps and stops the span
```

Responsibilities:

- **Start** — captures `Stopwatch.GetTimestamp()`, calls `CacheInstruments.Activity.StartActivity($"cache {operation}", ActivityKind.Internal)`, and when `options.IncludeKeyHashInTraces` is true adds `cache.key_hash` from `StableStringHash.Compute64(rawKey)` (hex, 16 chars). Raw keys never reach a span.
- **WrapFactory** — returns a delegate that times each invocation and **accumulates** elapsed ticks into `FactoryTicks`, sets `FactoryRan`, and re-throws unchanged. Accumulation matters because some paths invoke the factory more than once across a call (backend error fallback after a failed read).
- **Dispose** — computes total, resolves `served_from`, records metrics, stamps span tags, sets span status, disposes the `Activity`. Safe to call once; idempotent.

`Activity` is null when nothing is sampling, so all tag writes are behind the standard null check. `StartActivity` returning null is the no-tracing fast path.

### `served_from` resolution

Evaluated once, in `Dispose`:

| Condition | `cache.served_from` |
| ---- | ---- |
| Operation is write-shaped (`set`, `set_many`, `remove`, `remove_many`, `remove_by_tag`, `clear`) | tag omitted entirely |
| `FactoryRan` | `source` |
| Batch read, hits > 0 and misses > 0 | `mixed` |
| Batch read, hits > 0 and misses == 0 | `cache` |
| Single-key read, `MarkServedFromCache` called | `cache` |
| Otherwise (read found nothing) | `none` |

Write ops omit the tag rather than carry a meaningless value — no bogus Prometheus series.

A **coalesced waiter** reports `served_from=cache` with `cache.coalesced=true` and emits **no** `cache.factory.duration` sample. Reading `RoutingCacheService`'s stripe-lock path settles this: a waiter blocks on the `SemaphoreSlim`, and once the winner releases it, the waiter performs a real backend read that finds the value the winner wrote. Its own factory never runs, so `cache` is the accurate answer; `cache.coalesced=true` is what explains a total far above normal cache-served latency. Detection is exact and threshold-free — `SemaphoreSlim.WaitAsync` returns an already-completed task on the uncontended path, so a task that is not yet complete means this call waited.

Known limit: `HybridCache`'s internal stampede handling is invisible to the library, so Hybrid waiters carry no `coalesced` tag.

> **Superseded:** implementation proved the routing-level stripe lock coalesces every mode, including Hybrid, so Hybrid waiters *do* carry `cache.coalesced=true` — the limit above only ever applied to `HybridCache`'s own internal (invisible) stampede handling beneath that lock. See [docs/TELEMETRY.md](../../TELEMETRY.md).

### Instruments

| Instrument | Type | Unit | Change |
| ---------- | ---- | ---- | ------ |
| `cache.operation.duration` | Histogram | `ms` | recording moves to Routing; one sample per call; gains `cache.served_from` |
| `cache.factory.duration` | Histogram | `ms` | **new**; tags `cache.mode`, `cache.operation`; emitted only when a factory ran in this call |

Every other instrument is untouched: `cache.hits`, `cache.misses`, `cache.errors`, `cache.sets`, `cache.removes`, `cache.evictions`, `cache.stale_served`, `cache.circuit_state_changes`, `cache.schema_drift`, `cache.payload.bytes`, `cache.stale_refresh.in_flight`, `cache.tls.validation`, `cache.serialize.duration`, `cache.deserialize.duration`. Existing hit/miss counters keep being emitted by the concrete services, unchanged.

`CacheInstruments` API changes:

- add `RecordFactoryDuration(string mode, string operation, double milliseconds)` (public, matching the style of the existing `Record*` methods)
- add `RecordDuration(string mode, string operation, double milliseconds, string? servedFrom)` overload
- keep the existing 3-arg `RecordDuration` for source/binary compatibility
- delete `MeasureDuration` / `OperationTimer` — internal, added in the previous change, superseded by `CacheCallRecorder`

### Span

- Name: `cache {operation}` — e.g. `cache get_or_create`. Low cardinality.
- Kind: `Internal`.
- Total duration is the span's own duration; it is not duplicated as a tag.

Tags:

| Tag | When |
| ---- | ---- |
| `cache.mode` | always |
| `cache.operation` | always |
| `cache.served_from` | read-shaped operations |
| `cache.factory_ms` | a factory ran in this call |
| `cache.miss_reason` | a miss reason was determined |
| `cache.hit_count`, `cache.miss_count` | batch operations |
| `cache.coalesced` | this call waited on another caller's factory |
| `cache.error_kind` | a backend error occurred, including swallowed ones |
| `cache.key_hash` | `IncludeKeyHashInTraces=true` and the operation has a single key |

Status:

- `Error` **only** for exceptions that escape to the caller.
- Fail-open swallowed failures stay `Ok` and carry `cache.error_kind`. Otherwise every Redis blip would paint consumer traces red for calls that returned successfully.
- `OperationCanceledException` is tagged `cache.error_kind=Cancelled` and left un-errored — cancellation is not a fault.

### `cache.mode` on short-circuit paths

The resolved backend mode when a backend was reached (honoring a per-call mode override). `Routing` when no backend was involved — `Enabled=false`, bypass, rejected key — matching what the counters already do today and the vocabulary already documented in `docs/TELEMETRY.md`.

## Coverage

Every `RoutingCacheService` entry point: `GetOrCreateAsync` (both overloads), `GetAsync<T>`, `GetAsync(Type)`, `GetManyAsync`, `SetAsync` (both), `SetManyAsync`, `RemoveAsync`, `RemoveManyAsync`, `RemoveByTagAsync` (both), `ExistsAsync`, `RefreshAsync`, `ClearAsync`.

Paths that record nothing today and must record after this change:

| Path | Result |
| ---- | ---- |
| `Enabled=false` | `mode=Routing`, `served_from=source`, factory timed |
| Per-call bypass | `mode=Routing`, `served_from=source`, factory timed |
| Rejected key (validator/transformer) | `mode=Routing`, `miss_reason=KeyRejected`; `served_from=source` on factory-bearing ops (`get_or_create`, `refresh`), `none` on plain reads |
| Force refresh | resolved mode, `served_from=source`, factory timed |
| Background stale refresh | own span, `operation=stale_refresh`, `served_from=source`, factory timed |
| Redis key over `MaximumKeyLength` | resolved mode, `miss_reason=KeyTooLong`; `served_from` follows the same rule as rejected keys |

The background stale-refresh span starts on the refresh task. Its parent span may already have ended; that is valid in OTel and the parent span id remains correct.

## Edge cases

- **Factory throws** — total and `cache.factory_ms` are still recorded from the `finally`; `cache.error_kind` set; span status `Error` since the exception reaches the caller.
- **Factory invoked twice in one call** (read fails, fail-open falls back to factory) — `cache.factory.duration` gets one sample carrying the accumulated time; `FactoryRan` is true.
- **Nested cache call inside a factory** — two independent recorders, two spans (inner is a natural child), two totals. The outer `factory_ms` includes the inner total.
- **Batch op with zero valid keys** — no backend work; emits a total with `served_from=none` and `hit_count=0`.
- **`Dispose` called twice** — second call is a no-op.

## Testing

### Unit (`tests/Caching.NET.Tests`)

- `MeterListener`: exactly **one** `cache.operation.duration` sample per call per mode — the regression guard for the nesting being removed.
- `cache.factory.duration` present on a miss, absent on a hit, absent for a coalesced waiter.
- `served_from` across all four values: `cache`, `source`, `mixed`, `none`; tag absent on write ops.
- Every short-circuit path emits: disabled, bypass, rejected key, force refresh, key-too-long.
- Factory that throws still produces both samples.
- Nested factory: two samples, outer total ≥ inner total.
- `ActivityListener`: span name, kind, tag set per scenario, `key_hash` present only with `IncludeKeyHashInTraces=true`, `Error` status on escaping exception vs `Ok` + `error_kind` on fail-open, cancellation not marked `Error`.

### Integration (`tests/Caching.NET.Tests.Integration`, Testcontainers Redis)

- Redis and Hybrid modes end-to-end: hit then miss, asserting `served_from` and that `factory_ms` ≈ the deliberate factory delay.
- Background stale-refresh span with `operation=stale_refresh`.

### Rewrites

`tests/Caching.NET.Tests/Telemetry/OperationDurationTests.cs` (added in the previous change, asserts per-service samples) is rewritten to assert at the Routing boundary.

## Documentation updates

- `docs/TELEMETRY.md` — rewrite the `cache.operation.duration` section (the nesting caveat disappears), add `cache.factory.duration`, add `cache.served_from` / `cache.coalesced` to the allowed-tags list, replace the "v2 does not start Activities" paragraph with the span schema, add example queries.
- `docs/features/telemetry.md` — instrument table.
- `CLAUDE.md` — Telemetry section, which currently describes instruments only.

## Migration and compatibility

Additive for consumers: no `ICacheService` change, no configuration change, existing counters and their tags unchanged. Existing dashboards keep working, with two behavior changes to call out in release notes:

1. `cache.operation.duration` no longer emits nested samples, so panels that counted `set` samples produced by `set_many` will see fewer measurements. This is the double counting being fixed.
2. `cache.operation.duration` now carries `cache.served_from` on read operations, which splits existing series. Queries that aggregate without `by (...)` are unaffected; queries pinned to an exact label set need the new label.

Traces are new output: apps that already call `AddSource(CacheInstruments.ActivitySourceName)` — as the docs have always shown — will start seeing cache spans. Worth a release-note line, since a busy app gains one span per cache call at whatever rate its sampler allows.

## Risks

| Risk | Mitigation |
| ---- | ---- |
| Span volume on hot paths | `StartActivity` returns null when unsampled; volume control is the consumer's sampler, which is where it belongs |
| Overhead added to every call | One timestamp pair, one null check, one delegate allocation per call with a factory; no ambient state, no locks |
| Hybrid tier still unanswered | Explicit non-goal; additive `cache.tier` follow-up needs no schema change |
| Coalesced waiters show total ≫ factory with no lock-wait instrument | `cache.coalesced` span tag flags them; lock-wait instrument deferred |
