# Telemetry

OpenTelemetry-native. Static `CacheInstruments` exposes a Meter and ActivitySource both named `Caching.NET`. No `WithOpenTelemetry()` plumbing required — consumers wire the OTel pipeline directly.

## Quick wire-up

```csharp
using Caching.NET.Telemetry;

services.AddOpenTelemetry()
    .WithMetrics(b => b.AddMeter(CacheInstruments.MeterName))
    .WithTracing(b => b.AddSource(CacheInstruments.ActivitySourceName));
```

`MeterName` and `ActivitySourceName` are both the string `"Caching.NET"`.

## When to use what

- **Metrics** — dashboards, SLO alerts, capacity planning. Always-on, cheap.
- **Traces** — diagnosing slow requests, cache-miss reasons, factory latency. Sample-based; turn on per-environment.
- **Logs** — hashed key + reason fields included automatically; raw keys gated by `IncludeRawKeyInLogs` (dev only).

## Instruments (summary)

Defined in [src/Caching.NET/Telemetry/CacheInstruments.cs](../../src/Caching.NET/Telemetry/CacheInstruments.cs). All emitted with `cache.mode` and (where relevant) `cache.operation` tags.

| Instrument | Type | Purpose |
| --- | --- | --- |
| `cache.hits` / `cache.misses` | counter | Hit ratio numerator/denominator |
| `cache.errors` | counter | Backend errors (tagged by `cache.error_kind`) |
| `cache.sets` / `cache.removes` | counter | Write/remove volume |
| `cache.evictions` | counter | Entry evictions (tagged by `cache.eviction_reason`) |
| `cache.stale_served` | counter | Stale entries served while a background refresh ran |
| `cache.stale_refresh.in_flight` | up-down counter | Background refresh tasks currently running |
| `cache.operation.duration` | histogram (ms) | End-to-end op latency |
| `cache.serialize.duration` / `cache.deserialize.duration` | histogram (ms) | Serializer encode/decode latency (Redis wire path) |
| `cache.payload.bytes` | histogram (bytes) | Serialized payload size |
| `cache.circuit_state_changes` | counter | Polly circuit-breaker transitions |
| `cache.schema_drift` | counter | Envelope/format/schema drift on read |
| `cache.tls.validation` | counter | Redis TLS validation outcomes |

Full taxonomy, tag dimensions, and Grafana dashboard JSON: [../TELEMETRY.md](../TELEMETRY.md).

## Health checks

`WithHealthChecks()` registers an ASP.NET Core health check (PING + probe). Split into liveness/readiness:

```csharp
services.AddCaching(b => b
    .UseHybrid("rediss://...")
    .WithKeyPrefix("asm-api-prod")
    .WithHealthChecks(name: "caching-net", splitLivenessReadiness: true));
```

Routes wire up with the standard `MapHealthChecks("/health/live", new { Predicate = r => r.Tags.Contains("liveness") })` pattern.

Full guide: [../HEALTH-CHECKS.md](../HEALTH-CHECKS.md).

## Key redaction in logs

By default raw keys are **never** logged — only stable hashes. Set `IncludeRawKeyInLogs = true` for local debugging only. `IncludeKeyHashInTraces = true` enables hash tags on spans for correlation without PII leakage.

## Related

- [../TELEMETRY.md](../TELEMETRY.md) — full instrument list, Grafana dashboard, Prometheus rules
- [../HEALTH-CHECKS.md](../HEALTH-CHECKS.md) — health check wiring
- [../OPERATIONS.md](../OPERATIONS.md) — operational runbooks
