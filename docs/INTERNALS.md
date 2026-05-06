# Internals

Reference for maintainers and contributors. Consumer docs live in [README.md](../README.md) and [OPERATIONS.md](OPERATIONS.md).

## Architecture

```
Consumer code
    │
    ▼  ICacheService
RoutingCacheService     KeyPrefix injection · mode dispatch · per-call options
    │                   stale-while-revalidate orchestrator · TTL jitter
    │
    ├── StripedLockManager        — coalesce on InMemory/Redis (1024 stripes)
    ├── ResiliencePipelineRegistry — Polly v8 (timeout + CB + retry per backend)
    ├── ICacheSerializer           — JSON (default) | MessagePack (opt-in) | custom
    ├── PayloadEnvelope            — magic + format + schema-hash + length wrapper
    │
    ▼
   InMemoryCacheService    RedisCacheService    HybridCacheService
   (IMemoryCache +         (IDistributedCache + (Microsoft HybridCache —
    PostEvictionCallbacks) Polly pipeline)       coalesce delegated to it)
```

## StripedLockManager

Fixed array of `SemaphoreSlim(1, 1)`, length rounded up to a power of two.
xxHash32 over UTF-8 bytes selects a stripe via `hash & (length − 1)`.
Zero per-op allocation, zero leak (locks live for the app lifetime).

Default 1024 stripes (~64 KiB). Collision rate at 1 M unique hot keys ≈ 0.1 %.

## PayloadEnvelope wire format

Redis-only wrapper. InMemory stores raw `T`.

| Offset | Size | Purpose |
|-------:|-----:|---------|
| 0      | 4    | Magic `CN20` (ASCII) |
| 4      | 1    | FormatId (`0x01` json, `0x02` msgpack, `0xFF` custom) |
| 5      | 8    | xxHash64 of `typeof(T).AssemblyQualifiedName` (little-endian) |
| 13     | 4    | PayloadLen, uint32 little-endian |
| 17     | N    | Payload bytes |

Decode rules:
- Buffer < 17 bytes OR magic mismatch → `EnvelopeInvalid` → miss.
- FormatId mismatch with configured serializer → `FormatDrift` → miss.
- SchemaHash mismatch → `SchemaDrift` → miss (DTO changed since cached).

All decode failures emit `cache.schema_drift` and `cache.misses`. Decoder never throws.

## Resilience pipeline

Three named pipelines per backend:
- `cache.redis.read`
- `cache.redis.write`
- `cache.redis.delete`

Each: `AddTimeout(2s) → AddCircuitBreaker → AddRetry(2)`. The breaker is independent per pipeline so write storms can't trip the read path.

Circuit transitions emit `cache.circuit_state_changes` (tags: `cache.pipeline`, `cache.circuit_state` ∈ open|half-open|closed).

## Stale-while-revalidate

In-process registry (`StaleEntryTracker`, `ConcurrentDictionary<string, StaleMetadata>`) tracks `(absExpiresAtUtcTicks, staleUntilUtcTicks)` per prefixed key. Underlying TTL = `AbsoluteExpiration + AllowStaleFor`. On read inside the stale window:
1. Return cached value, emit `cache.stale_served`.
2. If `StaleRefreshThrottle.TryAcquire()`, schedule a background `Task.Run` that takes the stripe lock for the same key, runs the factory, writes the fresh entry, updates the registry, then releases throttle + lock.
3. `cache.stale_refresh.in_flight` UpDownCounter increments before factory and decrements in `finally`.

Hybrid mode bypasses the orchestrator: `HybridCache` manages its own L1/L2 lifecycle.

## Hot-reload matrix

| Option | Hot-reloadable? |
|--------|:---------------:|
| `Enabled`, `FailOpen`, `DefaultExpiration`, `TtlJitterPercentage` | ✅ |
| `MaximumPayloadBytes`, `MaximumKeyLength`, `IncludeRawKeyInLogs` | ✅ |
| `FactoryTimeout`, `RedisOperationTimeout`, `StaleRefreshConcurrency` | ✅ |
| `KeyPrefix`, `Mode`, `RedisConnectionString`*, `StrictRedisCertificateValidation` | ❌ |
| `StripeLockCount`, `MemorySizeLimitMb`, `HybridLocalCacheExpiration` | ❌ |

*`RedisConnectionString` is a special case: the `RedisConnectionRotator` hosted service reloads the multiplexer on change, so credential rotation works without a restart.
