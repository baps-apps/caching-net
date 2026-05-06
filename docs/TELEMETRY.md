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
| `cache.payload.bytes` | Histogram | `By` | |
| `cache.stale_refresh.in_flight` | UpDownCounter | `{task}` | |

Activity source name is `Caching.NET`. Current implementation focuses on metrics/logging; activity emission is limited.

## Allowed tags

- `cache.mode` ∈ {`InMemory`, `Redis`, `Hybrid`, `Routing`}
- `cache.operation` ∈ {`get`, `set`, `remove`, `get_many`, `set_many`, `remove_many`, `exists`, `refresh`, `get_or_create`}
- `cache.miss_reason` ∈ {`NotFound`, `Expired`, `Stale`, `SerializationFailed`, `EnvelopeInvalid`, `CircuitOpen`, `Disabled`, `Bypass`}
- `cache.error_kind` ∈ {`Timeout`, `ConnectionFailed`, `Serialization`, `CircuitOpen`, `Cancelled`, `Unknown`}
- `cache.circuit_state` ∈ {`closed`, `open`, `half-open`}
- `cache.drift_kind` ∈ {`envelope_invalid`, `format_drift`, `schema_drift`}
- `cache.tls_result` ∈ {`ok`, `name_mismatch`, `chain_error`, `untrusted`}

## Forbidden tags (compile-time enforced via CN0001)

- `key`, `cache.key` — cardinality bomb
- `tenant`, `cache.tenant`
- `user_id`, `cache.user_id`

## Logging

`LoggerMessage` source-gen, zero-allocation. Stable EventId ranges:
- 1000–1099 = info/debug
- 1100–1199 = warn
- 1200–1299 = error

Default redaction: 64-bit xxHash hex of the key. Toggle `Options.IncludeRawKeyInLogs=true` for dev only.

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
