# Caching.NET

Production-grade caching for high-throughput .NET services. One `ICacheService` abstraction. Three modes: **InMemory**, **Redis**, **Hybrid**. Stampede protection, Polly resilience, OpenTelemetry-native, AOT-friendly.

## Install

```bash
dotnet add package Caching.NET
```

Targets: `net8.0`, `net9.0`, `net10.0`. AOT/trim compatible when consumer supplies a `JsonSerializerContext`.

### Target Framework decision rule

Use this rule for all future TFM decisions:

- Keep modern runtime targets only (`net8+`) by default.
- Add a new target **only** when a real consumer app requires it.
- Do **not** target `netstandard` unless you must support legacy runtimes that cannot consume `net8+`.
- Prefer rolling forward (`net11`, `net12`, ...) over broadening backward compatibility.
- For each new target, require CI build/test coverage before release.

## Quickstart (config-first)

```csharp
services.AddCaching(configuration);
```

```json
{
  "Caching": {
    "Enabled": true,
    "Mode": "InMemory",
    "KeyPrefix": "asm-api-dev"
  }
}
```

Key prefix guideline: use `serviceName-environment` (for example, `asm-api-dev`).

### Fluent API (alternate option)

```csharp
services.AddCaching(b => b
    .UseInMemory()
    .WithKeyPrefix("asm-api-dev"));
```

Inject `ICacheService`:

```csharp
public class OrderService(ICacheService cache)
{
    public Task<Order> Get(int id) => cache.GetOrCreateAsync(
        $"Order:{id}",
        ct => LoadFromDb(id, ct),
        expiration: TimeSpan.FromMinutes(5));
}
```

## Three modes

### InMemory

Config-first (`appsettings.json`):

```json
{
  "Caching": {
    "Enabled": true,
    "Mode": "InMemory",
    "KeyPrefix": "asm-api-dev"
  }
}
```

```csharp
services.AddCaching(configuration);
```

Alternate: fluent API:

```csharp
services.AddCaching(b => b.UseInMemory().WithKeyPrefix("asm-api-dev"));
```

### Redis

Config-first (`appsettings.json`):

```json
{
  "Caching": {
    "Enabled": true,
    "Mode": "Redis",
    "KeyPrefix": "asm-api-dev",
    "RedisConnectionString": "localhost:6379"
  }
}
```

```csharp
services.AddCaching(configuration);
```

Alternate: fluent API:

```csharp
services.AddCaching(b => b.UseRedis("localhost:6379").WithKeyPrefix("asm-api-dev"));
```

### Hybrid

Config-first (`appsettings.json`):

```json
{
  "Caching": {
    "Enabled": true,
    "Mode": "Hybrid",
    "KeyPrefix": "asm-api-dev",
    "RedisConnectionString": "localhost:6379"
  }
}
```

```csharp
services.AddCaching(configuration);
```

Alternate: fluent API:

```csharp
services.AddCaching(b => b.UseHybrid("localhost:6379").WithKeyPrefix("asm-api-dev"));
```

## Production config (Amazon-scale)

```csharp
services.AddCaching(configuration);
```

```json
{
  "Caching": {
    "Enabled": true,
    "Mode": "Hybrid",
    "KeyPrefix": "asm-api-prod",
    "RedisConnectionString": "rediss://elasticache.amzn.example:6380",
    "IncludeRawKeyInLogs": false,
    "StrictRedisCertificateValidation": true,
    "DefaultExpiration": "00:05:00",
    "TtlJitterPercentage": 0.10,
    "StaleRefreshConcurrency": 512
  }
}
```

### Alternate: fluent production setup

```csharp
services.AddCaching(b => b
    .UseHybrid("rediss://elasticache.amzn.example:6380")
    .WithKeyPrefix("asm-api-prod")
    .UseProductionDefaults()
    .WithSerializer(new JsonCacheSerializer(MyJsonContext.Default)) // AOT/trim
    .WithTtlJitter(0.10)
    .WithStaleRefreshConcurrency(512)
    .WithHealthChecks());
// Wire OpenTelemetry in host startup: AddMeter(CacheInstruments.MeterName).
// WithOpenTelemetry() on the builder is a v1-compat flag only; it does not register OTel for you.
```

### `Enabled = false` (ops toggle)

When `CacheOptions.Enabled` is false after options merge, **no cache backends are registered** (no `IMemoryCache`, Redis multiplexer, hybrid stack, default serializer, Polly registry, or TLS validator). `IValidateOptions<CacheOptions>` **skips validation** so you are not forced to keep a live Redis connection string for a disabled cache. `ICacheService` still resolves: `GetOrCreateAsync` runs the factory; writes/removes are no-ops. Toggling `Enabled` from false to true at runtime does **not** register backends; restart the process to turn caching on. See [INTERNALS.md](docs/INTERNALS.md) and [HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md).

### Config members explained

| Section | Field Name | Default Value | Use | Effect |
| --- | --- | --- | --- | --- |
| Core behavior | `Enabled` | `true` | Ops kill-switch for caching. | `false` bypasses caching calls; factories still run. Good for incident mitigation. |
| Core behavior | `Mode` (`InMemory`, `Redis`, `Hybrid`) | `InMemory` | Pick cache topology. | `InMemory`: fastest local, per-instance only. `Redis`: shared distributed cache, network hop cost. `Hybrid`: local L1 + Redis L2, best latency/scale mix. |
| Core behavior | `KeyPrefix` | Empty string (`""`), but required when enabled | Namespace keys by app/env (for example `catalog-prod`). | Prevents collisions across services/environments and makes invalidation safer. |
| Core behavior | `RedisConnectionString` | Not specified | Redis endpoint for `Redis`/`Hybrid`. | Enables distributed tier; required for those modes. |
| Reliability and failure policy | `FailOpen` | `true` | Decide outage behavior for cache failures. | `true` serves from source instead of throwing on cache errors (higher availability). |
| Reliability and failure policy | `ThrowOnFailure` | `false` | Strict failure mode for sensitive paths/tests. | When `FailOpen=false`, cache failures bubble as exceptions. |
| Reliability and failure policy | `RedisOperationTimeout` | `00:00:02` | Bound single Redis call latency. | Prevents long Redis stalls from dragging request latency. |
| Reliability and failure policy | `FactoryTimeout` | `00:00:30` | Bound source/factory execution in `GetOrCreateAsync`. | Limits long-running backend fetch impact. |
| Freshness and stampede control | `DefaultExpiration` | `00:10:00` | Default TTL when per-call TTL not provided. | Governs staleness window and miss rate. |
| Freshness and stampede control | `TtlJitterPercentage` | `0.10` | Add random TTL spread (for example `0.10`). | Reduces synchronized expirations / thundering herd. |
| Freshness and stampede control | `StripeLockCount` | `1024` | Tuning for stampede lock striping. | Higher = less key contention, slightly more overhead. |
| Freshness and stampede control | `StaleRefreshConcurrency` | `256` | Cap background stale refresh parallelism. | Protects backend from refresh storms. |
| Freshness and stampede control | `HybridLocalCacheExpiration` | Not specified | L1 memory TTL override in hybrid mode. | Smaller = fresher, larger = lower latency/higher hit ratio. |
| Payload and memory limits | `MaximumPayloadBytes` | `1048576` (1 MiB) | Guardrail on value size. | Avoids huge entries hurting memory/network. |
| Payload and memory limits | `EnablePayloadCompression` | `false` | Compress larger payloads for Redis paths. | Saves bandwidth/storage, adds CPU cost. |
| Payload and memory limits | `PayloadCompressionThresholdBytes` | `16384` (16 KiB) | Minimum size before compression starts. | Avoids wasting CPU on small payloads. |
| Payload and memory limits | `MemorySizeLimitMb` | Not specified | Cap in-memory cache footprint. | Enables bounded memory growth via eviction pressure. |
| Payload and memory limits | `MaximumKeyLength` | `512` | Safety limit for final physical key length. | Prevents pathological/oversized keys. |
| Security and observability | `StrictRedisCertificateValidation` | `true` | TLS posture for Redis. | `true` enforces strict cert checks; safer production default. |
| Security and observability | `IncludeRawKeyInLogs` | `false` | Debug convenience toggle. | Logs full keys (use cautiously; can leak sensitive key material). |
| Security and observability | `IncludeKeyHashInTraces` | `false` | Correlate cache behavior in tracing without raw keys. | Better observability with lower PII risk. |
| Advanced / fluent-only hooks | `KeyValidator` (fluent-only) | Not specified | Skip caching for disallowed key shapes. | Prevents low-value/risky keys from entering cache. |
| Advanced / fluent-only hooks | `KeyTransformer` (fluent-only) | Not specified | Normalize keys (case, format, partitions). | Better hit ratio and consistent keying. |
| Advanced / fluent-only hooks | `RequireTagSupport` (set by builder API) | `false` | Enforce tag-capable mode at startup. | Fails fast if mode does not support required tag behavior. |

## Docs

- [INTERNALS.md](docs/INTERNALS.md) — striped locks, payload envelope, resilience, stale-while-revalidate
- [OPERATIONS.md](docs/OPERATIONS.md) — K8s/ElastiCache deployment, sharding, cred rotation, circuit-breaker tuning
- [TELEMETRY.md](docs/TELEMETRY.md) — OTel instruments, tag taxonomy, Grafana dashboard, Prometheus rules
- [SECURITY.md](docs/SECURITY.md) — TLS posture, secret redaction, PII handling, supply-chain
- [HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md) — health-check wiring
- [MIGRATION-V1-TO-V2.md](docs/MIGRATION-V1-TO-V2.md) — v1 → v2 breaking changes
- [BENCHMARKS.md](docs/BENCHMARKS.md) — perf numbers per mode / payload

## License

MIT
