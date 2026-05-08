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
    PostEvictionCallbacks) Polly pipeline)       used for Hybrid mode operations)
```

## StripedLockManager

Fixed array of `SemaphoreSlim(1, 1)`, length rounded up to a power of two.
**xxHash32** (`StableStringHash.Compute`) over the UTF-8 encoding of the **prefixed** cache key selects a stripe via `hash & (length − 1)` — stable across processes and machines (unlike `string.GetHashCode()`). UTF-8 buffers larger than 512 bytes use **`ArrayPool<byte>`** for the encode scratch space (same hash as a contiguous allocation).
`StableStringHash.Compute64` (XxHash64) is used elsewhere for log redaction fingerprints and drift sampling keys, not for stripe selection.
Zero per-op allocation on the hot path, zero leak (locks live for the app lifetime).

Default 1024 stripes (~64 KiB). Collision rate at 1 M unique hot keys ≈ 0.1 %.

## PayloadEnvelope wire format

Redis-only wrapper. InMemory stores raw `T`.

| Offset | Size | Purpose |
|-------:|-----:|---------|
| 0      | 4    | Magic `CN20` (ASCII) |
| 4      | 1    | FormatId (`0x01` json, `0x02` msgpack, `0xFF` custom) |
| 5      | 8    | xxHash64 of `typeof(T).FullName` (+ optional `[CacheSchema]` version); **not** assembly-qualified name, so library bumps do not change the hash (little-endian) |
| 13     | 4    | PayloadLen, uint32 little-endian |
| 17     | N    | Payload bytes |
| 17+N   | 4    | Payload checksum (xxHash32), uint32 little-endian |

Decode rules:
- Buffer < 17 bytes OR magic mismatch → `EnvelopeInvalid` → miss.
- Declared payload length must **exactly** match `wire.Length - HeaderSize - TrailerSize`; trailing bytes or short buffers → `EnvelopeInvalid` (defense in depth).
- FormatId mismatch with configured serializer → `FormatDrift` → miss.
- SchemaHash mismatch → `SchemaDrift` → miss (DTO changed since cached).
- Payload checksum mismatch → `EnvelopeInvalid` → miss.

All decode failures emit `cache.schema_drift` and `cache.misses`. Decoder never throws.

`PayloadEnvelope.Write` has a `byte[]` overload (wire buffer allocated with **`GC.AllocateUninitializedArray<byte>`** — filled entirely by `Write`) and an **`IBufferWriter<byte>`** overload to avoid an intermediate wire `byte[]` on hot paths.

High-volume drift **logs** (not metrics) are **sampled** by `DriftLogSampler`: at most one warning per `(drift kind, xxHash64 key fingerprint)` per minute.

## Resilience pipeline

Three named pipelines:
- `cache.redis.read`
- `cache.redis.write`
- `cache.redis.delete`

Default build order per pipeline (Polly v8): optional outer **`ConcurrencyLimiter`** when `CacheResilienceOptions.EnableRedisConcurrencyLimiter` is true (permit + queue limits; default off), then **`AddTimeout`** (default 2s), **`AddCircuitBreaker`**, **`AddRetry`** (default 2 attempts when `RetryCount > 0`).

- **Transient classification** (breaker + retry): `RedisConnectionException`, `RedisTimeoutException`, `TimeoutRejectedException`, `TimeoutException`, `SocketException`, `IOException`, and `RedisServerException` messages containing `LOADING` or `READONLY` (failover scenarios). **`OperationCanceledException` is not retried.**
- **Retry backoff:** exponential with jitter, **base delay 50 ms**, **max delay 1 s** (tighter than Polly defaults so Redis blips stay within typical HTTP budgets).

The breaker is independent per pipeline so write storms can't trip the read path.

Circuit transitions emit `cache.circuit_state_changes` (tags: `cache.pipeline`, `cache.circuit_state` ∈ open|half-open|closed).

Configure knobs with `CachingBuilder.WithResilience(Action<CacheResilienceOptions>)` (requires `AddCaching` with a builder). Polly pipeline construction is internal — `Polly.Registry.ResiliencePipelineRegistry<string>` is not on the public surface.

## `Enabled = false` registration

When merged `CacheOptions.Enabled` is false at startup: **no** `IMemoryCache`, Redis/Hybrid services, `ICacheSerializer` registration, `ResiliencePipelineRegistry<string>`, or `RedisCertificateValidator` singleton. **`RoutingCacheService`**, `StripedLockManager`, `StaleEntryTracker`, and `StaleRefreshThrottle` still register so `ICacheService` resolves. Health checks may still be registered via `WithHealthChecks`; the check returns healthy without probing backends.

## StaleEntryTracker

`StaleEntryTracker` holds `(absExpiresAtUtcTicks, staleUntilUtcTicks)` per prefixed key. To avoid unbounded growth on churny keyspaces, **`Register` triggers `Prune` every 256 registrations or when the dictionary size reaches 50,000**: expired entries (by `StaleUntilUtcTicks`) are removed, and if still over limit additional entries are trimmed toward a 40,000-entry target.

## RoutingCacheService shutdown

`RoutingCacheService` implements **`IAsyncDisposable` and `IDisposable`**. `DisposeAsync` cancels an internal `CancellationTokenSource` shared with background work, **awaits in-flight stale-refresh tasks** scheduled from `ScheduleBackgroundRefresh`, then disposes the CTS — reducing use-after-dispose races on the inner cache services during host shutdown.

## Stale-while-revalidate

The tracker above stores `(absExpiresAtUtcTicks, staleUntilUtcTicks)` per prefixed key. Underlying TTL = `AbsoluteExpiration + AllowStaleFor`. On read inside the stale window:
1. Return cached value, emit `cache.stale_served`.
2. If `StaleRefreshThrottle.TryAcquire()`, schedule a background `Task.Run` that takes the stripe lock for the same key, runs the factory, writes the fresh entry, updates the registry, then releases throttle + lock.
3. `cache.stale_refresh.in_flight` UpDownCounter increments before factory and decrements in `finally`.

Hybrid mode still flows through routing/coalescing. `HybridCacheService` delegates storage lifecycle to `HybridCache`.

## Hot-reload matrix

| Option | Hot-reloadable? |
|--------|:---------------:|
| `Enabled` (behavior per call), `DefaultExpiration`, `TtlJitterPercentage` | ✅ |
| `MaximumPayloadBytes`, `MaximumKeyLength`, `IncludeRawKeyInLogs` | partial (service-dependent) |
| `FactoryTimeout`, `RedisOperationTimeout`, `StaleRefreshConcurrency`, `FailOpen` | partial (not uniformly re-read across all services) |
| `KeyPrefix`, `Mode`, `RedisConnectionString`*, `StrictRedisCertificateValidation` | ❌ |
| `StripeLockCount`, `MemorySizeLimitMb`, `HybridLocalCacheExpiration` | ❌ |

*`RedisConnectionString` is a special case: the `RedisConnectionRotator` hosted service reloads the multiplexer on change, so credential rotation works without a restart.
