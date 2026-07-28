# Telemetry

OpenTelemetry-native. No `ICacheTelemetry` interface in v2. Subscribe to the standard providers:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(b => b.AddMeter(CacheInstruments.MeterName))
    .WithTracing(b => b.AddSource(CacheInstruments.ActivitySourceName));
```

## Instruments

| Name | Type | Unit | Notes |
|------|------|------|-------|
| `cache.hits` | Counter | `{op}` | per operation |
| `cache.misses` | Counter | `{op}` | tag `cache.miss_reason` |
| `cache.errors` | Counter | `{op}` | tag `cache.error_kind` |
| `cache.sets` | Counter | `{op}` | |
| `cache.removes` | Counter | `{op}` | |
| `cache.evictions` | Counter | `{entry}` | tag `cache.eviction_reason` |
| `cache.stale_served` | Counter | `{op}` | |
| `cache.circuit_state_changes` | Counter | `{event}` | tag `cache.circuit_state`, `cache.pipeline` |
| `cache.schema_drift` | Counter | `{event}` | tag `cache.drift_kind` |
| `cache.tls.validation` | Counter | `{event}` | tag `cache.tls_result` |
| `cache.operation.duration` | Histogram | `ms` | total call latency, one sample per call — tags `cache.mode`, `cache.operation`, `cache.served_from` (see [below](#cacheoperationduration-and-cachefactoryduration)) |
| `cache.factory.duration` | Histogram | `ms` | factory (source) retrieval time; only when a factory ran |
| `cache.serialize.duration` | Histogram | `ms` | tag `cache.format` (`json` / `msgpack` / `unknown`) — Redis encode path |
| `cache.deserialize.duration` | Histogram | `ms` | tag `cache.format` — Redis decode path |
| `cache.payload.bytes` | Histogram | `By` | |
| `cache.stale_refresh.in_flight` | UpDownCounter | `{task}` | |

### `cache.operation.duration` and `cache.factory.duration`

Both are recorded once per call by the routing layer, in a `finally` (via `using var recorder = CacheCallRecorder.Start(...)`), so failed and timed-out calls are timed too. Wall time in milliseconds, measured with `Stopwatch.GetTimestamp()`.

- `cache.operation.duration` — the whole call, tagged `cache.mode`, `cache.operation`, and `cache.served_from` on read-shaped operations.
- `cache.factory.duration` — time inside the caller's factory (source retrieval), tagged `cache.mode` and `cache.operation`. Emitted only when a factory actually ran in that call.

Cache-side cost on a miss is `cache.operation.duration` − `cache.factory.duration` for the same call, exact in the span. The two histograms do not give you this by quantile arithmetic — p99(total) − p99(factory) is not p99(total − factory), since the slowest total calls and the slowest factory calls are not necessarily the same calls. To see cache-side cost as a distribution, compute the subtraction per call (from the span, or in a trace pipeline) rather than subtracting aggregated histogram quantiles.

There is exactly **one** sample per call: no nesting, so summing across operations does not double count. A call made directly against a backend service rather than through `ICacheService` records nothing — dependency injection always registers the routing layer, so this affects tests only.

Argument validation is the one gap in "per call": `GetOrCreateAsync`, `SetAsync`, `GetAsync` (both overloads), `ExistsAsync`, `RefreshAsync`, `GetManyAsync`, and `SetManyAsync` call `ArgumentException.ThrowIfNullOrWhiteSpace` / `ArgumentNullException.ThrowIfNull` *before* the recorder is created, so a null/blank key or a null collection throws with no sample and no span — the caller gets an exception, not silence. `RemoveAsync` (blank/whitespace/null key) and `RemoveManyAsync` (null key collection) don't validate that way: the recorder starts first, so those calls still emit a record even though they reach no backend.

`cache.served_from` values:

| Value | Meaning |
| ---- | ---- |
| `cache` | served from the cache without running a factory |
| `source` | a factory ran (normal miss, force refresh, bypass, caching disabled, backend error fallback) |
| `mixed` | batch read where some keys hit and some missed |
| `none` | nothing was served (a `get` miss, `exists` false) |

Presence comes from the backend, not from a null check on the returned value. `ICacheService.GetAsync<T>` returns `T?`, which cannot express "missing" when `T` is a value type — a cached `0` and a missing `int` are the same value — so the routing layer reads through an internal presence-aware probe. Without it, every value-type miss would report `served_from=cache` and every value-type batch would report a full house of hits.

Write-shaped operations (`set`, `set_many`, `remove`, `remove_many`, `remove_by_tag`, `clear`, `refresh`) omit the tag rather than carry a meaningless value. `refresh` counts as write-shaped despite reading: it always runs the factory and writes the result, so the tag could only ever say `source`.

Paths that reach no backend — caching disabled, per-call bypass, a key rejected by the validator/transformer, a Redis key over `MaximumKeyLength` — are still recorded, tagged `cache.mode=Routing`. Only `get_or_create` still runs (and times) the factory on these paths, since a value must still be produced; `refresh`'s disabled/rejected-key short-circuits return without invoking the factory at all, so no `cache.factory.duration` is emitted for those calls. Background stale refreshes record under `cache.operation=stale_refresh`. Their span is a **root span with a link** back to the `get_or_create` that triggered it, not a child of it: the refresh outlives the request that scheduled it, and parenting it there would hang a long-running child off an already-ended span. Follow the link (or the shared `cache.key_hash`) to correlate the two.

A caller that waits on a stripe lock another call holds is tagged `cache.coalesced=true` on the span, and normally reports `served_from=cache`: it performs a real cache read of the value the winner wrote, and runs no factory of its own. Normally, not always — if the winner's factory threw, nothing was written, and the waiter runs its own factory and reports `served_from=source` with `cache.coalesced=true`. This routing-level striped lock coalesces every mode, including Hybrid. `HybridCache` also runs its own internal stampede handling beneath it; that inner layer is invisible to the library and adds no tag of its own.

Not measured: L1-vs-L2 attribution inside Hybrid mode. `HybridCache` never tells the caller which tier served a value, so a cache-served Hybrid call does not say whether it came from local memory or Redis. In **Redis** mode this is not a gap — a cache-served call does nothing but talk to Redis, so the total *is* Redis latency.

## Tracing

One `Activity` per cache call, from `ActivitySource` `Caching.NET` (`CacheInstruments.ActivitySourceName`). Span name is `cache {operation}` — e.g. `cache get_or_create` — with `ActivityKind.Internal`. The call's total duration is the span's own duration.

| Tag | When |
| ---- | ---- |
| `cache.mode`, `cache.operation` | always |
| `cache.served_from` | read-shaped operations |
| `cache.factory_ms` | a factory ran in this call |
| `cache.miss_reason` | a miss reason was determined |
| `cache.hit_count`, `cache.miss_count` | batch operations |
| `cache.coalesced` | the call waited on another caller's stripe lock |
| `cache.error_kind` | a cache-side failure reached the routing layer |
| `cache.factory_failed` | the caller's own factory threw |
| `cache.key_hash` | `CacheOptions.IncludeKeyHashInTraces=true`, single-key operations |

Span status is `Error` when an exception escapes to the caller, and when the caller's factory threw. Cancellation by the caller's own token is tagged `cache.error_kind=Canceled` and never marked `Error` — it is a caller decision, not a fault. A blown `CacheOptions.FactoryTimeout` is *not* caller cancellation: the caller's token is still live, so it is tagged `Timeout` and marked `Error`.

`cache.error_kind` vs `cache.factory_failed`: a caller-supplied factory that throws means the *source* failed, not the cache. That span is marked `Error` and tagged `cache.factory_failed=true`, with no `cache.error_kind` — a cache-error dashboard that counts every flaky upstream reads backwards.

**Scope of `cache.error_kind`:** it covers failures the routing layer sees — exceptions that escape to the caller, and background `stale_refresh` failures the library swallows. Backend fail-open failures do **not** reach it: `RedisCacheService` and `HybridCacheService` catch, record `cache.errors` themselves, and return a miss, so routing sees a normal miss. A Redis outage therefore shows up on the span as `served_from=source` (the factory ran) with no `cache.error_kind`; the `cache.errors` counter, tagged `cache.mode=Redis`, is the signal for those. Do not build a Redis-outage alert on span status.

`cache.key_hash` is `StableStringHash.Compute64(key)` as 16 hex characters. Raw keys never appear on a span, regardless of `IncludeRawKeyInLogs`.

Span volume is the consumer's sampler's business: `StartActivity` returns null when nothing is listening, so the cost with tracing off is a null check.

## Allowed tags

- `cache.mode` ∈ {`InMemory`, `Redis`, `Hybrid`, `Routing`}
- `cache.operation` ∈ {`get`, `set`, `remove`, `get_many`, `set_many`, `remove_many`, `exists`, `refresh`, `get_or_create`, `remove_by_tag`, `clear`, `stale_refresh`} — the operations `CacheCallRecorder` times (every span, plus `cache.operation.duration` / `cache.factory.duration`). `cache.errors` alone can also carry `operation=serialize` for a Redis payload-encode failure that happens before the value ever reaches Redis; that failure has no per-call recorder, so `serialize` never appears on a span or in the duration histograms.
- `cache.served_from` ∈ {`cache`, `source`, `mixed`, `none`} — read-shaped operations only
- `cache.coalesced` — `true` when the call waited on another caller's stripe lock (span only)
- `cache.miss_reason` — common values include `NotFound`, `SerializationFailed`, `EnvelopeInvalid`, `Disabled`, `Bypass`, `KeyRejected` (routing: validator/transformer rejected segment), `KeyTooLong` (Redis service key cap)
- `cache.error_kind` — common values include `Timeout` (includes a blown `FactoryTimeout`), `ConnectionFailed`, `Serialization`, `CircuitOpen`, `Canceled`, `Unknown`
- `cache.factory_failed` — `true` when the caller's factory threw (span only)
- `cache.circuit_state` ∈ {`closed`, `open`, `half-open`}
- `cache.drift_kind` ∈ {`envelope_invalid`, `format_drift`, `schema_drift`}
- `cache.tls_result` ∈ {`ok`, `name_mismatch`, `chain_error`, `untrusted`}

## Forbidden tags (convention)

The library never tags metrics or logs with these names, and consumers should follow the same rule on `Counter` / `Histogram` / `UpDownCounter` `.Add` / `.Record` calls and on `ILogger` message templates / `BeginScope`:

- `key`, `cache.key` — cardinality bomb
- `tenant`, `cache.tenant`
- `user_id`, `cache.user_id`

## Logging

`LoggerMessage` source-gen, zero-allocation. Stable EventId ranges:
- 1000–1099 = info/debug
- 1100–1199 = warn
- 1200–1299 = error

Default redaction: 64-bit xxHash64 hex of the key (`StableStringHash.Compute64`). Toggle `Options.IncludeRawKeyInLogs=true` for dev only. Schema/envelope drift warning logs are **rate-limited** per drift kind and key fingerprint (see `DriftLogSampler` in [INTERNALS.md](INTERNALS.md)).

### `cache.removes` and batch delete

On **Redis**, when `IConnectionMultiplexer` is available, `RemoveManyAsync` deletes via `UNLINK` (non-blocking background reclaim) when the server supports it (Redis 4.0+), falling back to `DEL` otherwise, and increments `cache.removes` **once per key Redis actually deleted** (server-reported count). The per-key `RemoveAsync` path still records one remove per call. In-memory and hybrid batch paths record per key removed via their single-key implementations. `ClearAsync` records `cache.removes` with `operation="clear"`.

## OTel collector + Prometheus

Sample collector pipeline:

```yaml
receivers:
  otlp:
    protocols: { grpc: {}, http: {} }
processors:
  batch: {}
exporters:
  prometheusremotewrite:
    endpoint: https://prom.example/api/v1/write
service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheusremotewrite]
```

## Grafana dashboard hints

Useful panels:
- `rate(cache_hits[1m])` vs `rate(cache_misses[1m])` — hit rate
- `histogram_quantile(0.99, sum(rate(cache_operation_duration_bucket[5m])) by (le, cache_mode, cache_operation))` — p99 latency
- `histogram_quantile(0.99, sum(rate(cache_operation_duration_bucket[5m])) by (le, cache_mode, cache_operation, cache_served_from))` — p99 latency split by `cache_served_from`, to separate cache-served calls from calls that fell through to the factory
- `histogram_quantile(0.99, sum(rate(cache_factory_duration_bucket[5m])) by (le, cache_mode, cache_operation))` — p99 factory (source) retrieval time on a miss
- `rate(cache_circuit_state_changes{cache_circuit_state="open"}[5m])` — breaker firing rate
- `rate(cache_schema_drift[5m]) by (cache_drift_kind)` — drift bursts during deploys
