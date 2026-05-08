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
| `cache.operation.duration` | Histogram | `ms` | |
| `cache.serialize.duration` | Histogram | `ms` | tag `cache.format` (`json` / `msgpack` / `unknown`) — Redis encode path |
| `cache.deserialize.duration` | Histogram | `ms` | tag `cache.format` — Redis decode path |
| `cache.payload.bytes` | Histogram | `By` | |
| `cache.stale_refresh.in_flight` | UpDownCounter | `{task}` | |

`CacheInstruments.Activity` / `ActivitySourceName` (`Caching.NET`) are public for wiring `AddSource(...)`, but **v2 does not start Activities** on cache operations; metrics and logs carry the observability story. `CacheOptions.IncludeKeyHashInTraces` is reserved for future trace tags and is **not** consumed by the library today.

## Allowed tags

- `cache.mode` ∈ {`InMemory`, `Redis`, `Hybrid`, `Routing`}
- `cache.operation` ∈ {`get`, `set`, `remove`, `get_many`, `set_many`, `remove_many`, `exists`, `refresh`, `get_or_create`}
- `cache.miss_reason` — common values include `NotFound`, `SerializationFailed`, `EnvelopeInvalid`, `Disabled`, `Bypass`, `KeyRejected` (routing: validator/transformer rejected segment), `KeyTooLong` (Redis service key cap)
- `cache.error_kind` — common values include `Timeout`, `ConnectionFailed`, `Serialization`, `CircuitOpen`, `Cancelled`/`Canceled`, `Unknown`
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

On **Redis**, when `IConnectionMultiplexer` is available, `RemoveManyAsync` uses `KeyDeleteAsync` and increments `cache.removes` **once per key Redis actually deleted** (server-reported count). The per-key `RemoveAsync` path still records one remove per call. In-memory and hybrid batch paths record per key removed via their single-key implementations.

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
- `rate(cache_circuit_state_changes{cache_circuit_state="open"}[5m])` — breaker firing rate
- `rate(cache_schema_drift[5m]) by (cache_drift_kind)` — drift bursts during deploys
