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
  "CacheOptions": {
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

**Null results are not cached.** When the `factory` returns `null` (reference types / empty `Nullable<T>`), `GetOrCreateAsync` returns that `null` to the caller but does **not** store it in any tier — the next call re-runs the factory. This holds across all modes (InMemory, Redis, Hybrid). Value-type defaults (`0`, `false`, `default(Guid)`, empty struct) are real values and **are** cached normally. Only the `GetOrCreateAsync` factory path is guarded; an explicit `SetAsync(key, value)` writes whatever you pass.

## Three modes

### InMemory

Config-first (`appsettings.json`):

```json
{
  "CacheOptions": {
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
  "CacheOptions": {
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
  "CacheOptions": {
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

## Production config (high throughput scale)

```csharp
services.AddCaching(configuration);
```

```json
{
  "CacheOptions": {
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

### Per-call overrides (`CacheCallOptions`)

App-level `Mode` sets the default backend. Individual calls can override it via `CacheCallOptions.Mode` — useful when most data belongs in Redis/Hybrid but a specific entry should stay process-local (large objects, per-instance state, secrets you do not want crossing the wire).

```csharp
using Caching.NET.Extensions;
using Caching.NET.Options;

// App configured for Hybrid; this single call writes/reads only the local L1.
var localOnly = new CacheCallOptions { Mode = CacheMode.InMemory };

var profile = await cache.GetOrCreateAsync(
    key: $"User:{userId}:Profile",
    factory: ct => LoadProfileAsync(userId, ct),
    expiration: TimeSpan.FromMinutes(5),
    callOptions: localOnly,
    cancellationToken: ct);
```

Other `CacheCallOptions` knobs honoured per call: `BypassCache`, `ForceRefresh`, `CoalesceConcurrent`, `FactoryTimeout`, `AbsoluteExpiration`, `SlidingExpiration`, `AllowStaleFor`, `Tags`, `JitterPercentage`. Set/Remove overloads accept `callOptions` too — `ForceRefresh` is honoured only by `GetOrCreateAsync`.

**Backend availability.** `InMemoryCacheService` and `IMemoryCache` are registered for **every** mode (`InMemory`, `Redis`, `Hybrid`) when `Enabled=true`, so an `InMemory` per-call override always has a backend. A `Redis`/`Hybrid` override only resolves when the corresponding service was registered (i.e. the app started in `Redis` or `Hybrid` mode); otherwise the call throws.

**Cross-mode caveats.**

- **Key namespace is shared.** All modes use the same `KeyPrefix`. A local-only entry will not be visible to other instances; pick the override consistently per logical key (do not mix Redis and InMemory writes for the same key on the same instance).
- **Set/Remove asymmetry.** A `SetAsync` with `Mode=InMemory` writes only the local cache; a later read without override goes to the configured backend and misses. If you mark a key local, mark all reads/writes for that key local.
- **Hybrid-only features off.** `Tags`, `RequireTagSupport`, `SlidingExpiration`, and `AllowStaleFor` are not honoured under a `Hybrid → InMemory` per-call override (semantics differ per backend).

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

## Feature Matrix

Discovery table. Pick a feature, jump to its builder method / per-call knob / recipe.

| Feature | Modes | Builder API | Per-call (`CacheCallOptions`) | Recipe |
| --- | --- | --- | --- | --- |
| Get-or-create with factory | All | — | — | [#get-or-create](#get-or-create) |
| Force refresh | All | — | `ForceRefresh = true` | [#force-refresh](#force-refresh) |
| Bypass cache | All | — | `BypassCache = true` | [#bypass-cache](#bypass-cache) |
| Per-call mode override | All | — | `Mode = CacheMode.InMemory` | [#per-call-mode-override](#per-call-mode-override) |
| Per-call expiration | All | `WithDefaultExpiration` | `AbsoluteExpiration` | [#per-call-expiration](#per-call-expiration) |
| Sliding expiration | InMemory, Redis | — | `SlidingExpiration` | [#sliding-expiration](#sliding-expiration) |
| Stale-while-revalidate | InMemory, Redis | — | `AllowStaleFor` | [#stale-while-revalidate](#stale-while-revalidate) |
| Stampede coalescing | All | `WithStripedLocks` | `CoalesceConcurrent = false` (opt-out) | [#stampede-coalescing](#stampede-coalescing) — see [features/stampede.md](docs/features/stampede.md) |
| TTL jitter | All | `WithTtlJitter` | `JitterPercentage` | [#ttl-jitter](#ttl-jitter) |
| Tag invalidation | Hybrid | `RequireTagSupport` | `Tags` | [#tag-invalidation](#tag-invalidation) — see [features/hybrid.md](docs/features/hybrid.md) |
| Factory timeout | All | `WithFactoryTimeout` | `FactoryTimeout` | [#factory-timeout](#factory-timeout) |
| Payload size cap | All | `WithMaximumPayloadBytes` | — | [#payload-size-cap](#payload-size-cap) |
| In-memory size cap | InMemory, Hybrid | `WithMemorySizeLimit` | — | [#in-memory-size-cap](#in-memory-size-cap) |
| Hybrid L1 TTL | Hybrid | `WithDefaultLocalExpiration` | — | [features/hybrid.md](docs/features/hybrid.md) |
| Key namespace prefix | All | `WithKeyPrefix` | — | [#key-prefix](#key-prefix) |
| Key validation | All | `WithKeyValidator` | — | [#key-validation](#key-validation) |
| Key normalization | All | `WithKeyTransformer` | — | [#key-normalization](#key-normalization) |
| Custom serializer | Redis, Hybrid | `WithSerializer<T>()` / `WithMessagePackSerializer` | — | [#custom-serializer](#custom-serializer) |
| Polly resilience | Redis, Hybrid | `WithResilience` | — | [#polly-resilience](#polly-resilience) |
| Redis op timeout | Redis, Hybrid | `WithRedisOperationTimeout` | — | [#redis-operation-timeout](#redis-operation-timeout) |
| Strict TLS | Redis, Hybrid | `WithStrictCertificateValidation` | — | [#strict-tls](#strict-tls) |
| Permissive TLS (custom DNS) | Redis, Hybrid | `WithPermissiveRedisTls` | — | [#permissive-tls](#permissive-tls) |
| Health checks | All | `WithHealthChecks` | — | [#health-checks](#health-checks) — see [HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md) |
| OTel telemetry | All | (auto via `CacheInstruments`) | — | [features/telemetry.md](docs/features/telemetry.md) |
| Stale refresh throttle | All | `WithStaleRefreshConcurrency` | — | [features/stampede.md](docs/features/stampede.md) |
| Ops kill-switch | All | `Disable` / `Enable` (or `Enabled` config) | — | [#ops-kill-switch](#ops-kill-switch) |
| Dev presets | All | `UseDevelopmentDefaults` | — | [#presets](#presets) |
| Prod presets | All | `UseProductionDefaults` | — | [#presets](#presets) |

## Cookbook

Three-line recipes. **When** = use case, **Code** = copy-paste, **Why** = mechanism.

### Get-or-create

**When:** Cache the result of an expensive backend call.

```csharp
var order = await cache.GetOrCreateAsync(
    $"Order:{id}",
    ct => LoadOrderAsync(id, ct),
    expiration: TimeSpan.FromMinutes(5));
```

**Why:** Factory runs only on miss; concurrent callers coalesce on the striped lock.

### Runtime-typed read (`GetAsync(key, Type)`)

**When:** You only have a `System.Type` at runtime (e.g. a settings cache keyed by type) and cannot call the generic `GetAsync<T>`.

```csharp
object? cached = await cache.GetAsync(key, type, ct);
if (cached is null)
{
    // miss — load from source, then SetAsync(key, value)
}
```

**Why:** Non-generic counterpart to `GetAsync<T>`; returns `null` on miss/drift, shares the same envelope + schema-hash validation as the generic path (cross-readable with `SetAsync<T>`). Prefer `GetAsync<T>` whenever the type is known at compile time — this overload is not a replacement for it. Not trim/AOT-safe for custom `ICacheService`/`ICacheSerializer` implementations that rely on the reflection fallback; the built-in services and JSON/MessagePack serializers override it.

### Force refresh

**When:** Invalidate-and-replace a key (e.g. after a write).

```csharp
var opts = new CacheCallOptions { ForceRefresh = true };
var fresh = await cache.GetOrCreateAsync("User:42:Profile", ct => LoadProfileAsync(ct), opts);
```

**Why:** Skips the cache read; runs factory; writes result back. Honoured only by `GetOrCreateAsync`.

### Bypass cache

**When:** One-off probe, admin endpoint, or sensitive call that should never be cached.

```csharp
var opts = new CacheCallOptions { BypassCache = true };
var value = await cache.GetOrCreateAsync("Diag:LiveProbe", ct => ProbeAsync(ct), opts);
```

**Why:** Factory runs, result is returned without read or write.

### Per-call mode override

**When:** App is Hybrid but one entry should stay process-local (large object, per-instance state).

```csharp
var opts = new CacheCallOptions { Mode = CacheMode.InMemory };
var local = await cache.GetOrCreateAsync($"User:{id}:Profile", ct => LoadProfileAsync(ct), opts);
```

**Why:** Routes to `InMemoryCacheService`; skips Redis. Apply consistently to all reads/writes for that key.

### Per-call expiration

**When:** Most entries use the default TTL but a specific key needs a longer/shorter one.

```csharp
var value = await cache.GetOrCreateAsync(
    "Config:Snapshot",
    ct => LoadConfigAsync(ct),
    expiration: TimeSpan.FromHours(1));
```

**Why:** Overrides `DefaultExpiration` for this call only.

### Sliding expiration

**When:** Session-style entries that should stay hot as long as accessed.

```csharp
var opts = new CacheCallOptions { SlidingExpiration = TimeSpan.FromMinutes(15) };
var session = await cache.GetOrCreateAsync($"Session:{id}", ct => LoadSessionAsync(ct), opts);
```

**Why:** TTL resets on each access. InMemory + Redis only — Hybrid ignores.

### Stale-while-revalidate

**When:** Tolerate slightly-stale data to absorb backend latency on expiry.

```csharp
var opts = new CacheCallOptions { AllowStaleFor = TimeSpan.FromSeconds(30) };
var top10 = await cache.GetOrCreateAsync("Leaderboard:Top10", ct => ComputeAsync(ct), opts);
```

**Why:** After absolute expiry, stale value serves up to 30s while one background refresh runs. InMemory + Redis only.

### Stampede coalescing

**When:** Hot keys + expensive factory. Active by default — only knob is opt-out.

```csharp
var opts = new CacheCallOptions { CoalesceConcurrent = false };
var v = await cache.GetOrCreateAsync(key, ct => CheapIdempotentAsync(ct), opts);
```

**Why:** Default coalescing serializes factory through striped lock. Opt out only when factory is cheap and concurrent runs are acceptable. Deep dive: [features/stampede.md](docs/features/stampede.md).

### TTL jitter

**When:** Avoid synchronized expirations after a deploy or batch warm-up.

```csharp
services.AddCaching(b => b.UseHybrid("rediss://...").WithKeyPrefix("svc-prod").WithTtlJitter(0.20));
```

**Why:** ±20% random spread per entry. Clamped to 0–0.5. Per-call override via `JitterPercentage`.

### Tag invalidation

**When:** Bulk-invalidate every entry related to a domain object (e.g. all entries for a category).

```csharp
var opts = new CacheCallOptions { Tags = new[] { $"category:{categoryId}" } };
await cache.SetAsync($"Product:{id}", product, opts, expiration: TimeSpan.FromMinutes(5));
await cache.RemoveByTagAsync($"category:{categoryId}");
```

**Why:** Hybrid only. Tags are applied on write (`SetAsync`/`GetOrCreateAsync`) and matched by `RemoveByTagAsync`. Call `RequireTagSupport()` at startup to fail fast on misconfig. Deep dive: [features/hybrid.md](docs/features/hybrid.md).

### Clear all (this app)

**When:** Flush everything this application owns (e.g. after a bulk reseed or schema bump).

```csharp
await cache.ClearAsync();
```

**Why:** Scoped to your `KeyPrefix`. InMemory clears the process cache; Redis `SCAN`s and removes `{KeyPrefix}:*` (never `FLUSHDB`); Hybrid logically invalidates via the reserved `"*"` tag. On a shared Redis database, apps do not clear each other **as long as each app uses a unique `KeyPrefix`** — for Hybrid, `KeyPrefix` is applied as the L2 `InstanceName`, so even tag/wildcard markers are namespaced per app.

### Factory timeout

**When:** A slow downstream should not hold the lock or starve callers.

```csharp
var opts = new CacheCallOptions { FactoryTimeout = TimeSpan.FromSeconds(2) };
var v = await cache.GetOrCreateAsync("ExternalApi:Result", ct => CallSlowApiAsync(ct), opts);
```

**Why:** Cancels the factory CT after 2s. Defaults to `CacheOptions.FactoryTimeout` (30s).

### Payload size cap

**When:** Defend memory/network from accidental large entries.

```csharp
services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").WithMaximumPayloadBytes(512 * 1024));
```

**Why:** Entries above the cap are not written and a warning is logged.

### In-memory size cap

**When:** Bounded memory footprint on the L1 / InMemory cache.

```csharp
services.AddCaching(b => b.UseInMemory().WithKeyPrefix("svc-prod").WithMemorySizeLimit(256)); // MB
```

**Why:** Sets `IMemoryCache.SizeLimit` to N×1 MiB. Enables eviction pressure.

### Key prefix

**When:** Isolate keys per service/environment to prevent cross-namespace collisions.

```csharp
services.AddCaching(b => b.UseRedis("...").WithKeyPrefix("catalog-prod"));
```

**Why:** Required when `Enabled=true`. Must not contain `':'`. Convention: `serviceName-environment`.

### Key validation

**When:** Skip caching for low-value/sensitive key shapes (e.g. anonymous user keys).

```csharp
services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    .WithKeyValidator(k => !k.StartsWith("Anon:")));
```

**Why:** Returns `false` → reads miss, writes no-op for that key. Fluent-only.

### Key normalization

**When:** Trim, lower-case, or partition keys before caching.

```csharp
services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    .WithKeyTransformer(k => k.Trim().ToLowerInvariant()));
```

**Why:** Improves hit ratio when callers use inconsistent casing/whitespace.

### Custom serializer

**When:** Smaller payloads or AOT/trim compatibility.

```csharp
services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod")
    .WithSerializer(new JsonCacheSerializer(MyJsonContext.Default))); // AOT-safe
// or
services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").WithMessagePackSerializer());
```

**Why:** Default is reflection-based `System.Text.Json`. MessagePack is ~2-3× smaller; source-gen JSON is AOT/trim safe.

### Polly resilience

**When:** Tune Redis timeout, circuit breaker, retry count for your latency budget.

```csharp
services.AddCaching(b => b.UseRedis("...").WithKeyPrefix("svc-prod").WithResilience(r =>
{
    r.Timeout = TimeSpan.FromMilliseconds(500);
    r.RetryCount = 1;
    r.FailureRatio = 0.5;
}));
```

**Why:** Builds the Polly pipeline used by `RedisCacheService`. Library-owned `CacheResilienceOptions` — Polly types are not on the public API.

### Redis operation timeout

**When:** Bound any single Redis op to prevent latency drag.

```csharp
services.AddCaching(b => b.UseRedis("...").WithKeyPrefix("svc-prod")
    .WithRedisOperationTimeout(TimeSpan.FromSeconds(1)));
```

**Why:** Per-op cap (default 2s). Independent of factory timeout.

### Strict TLS

**When:** Production Redis (ElastiCache) where hostname must match cert.

```csharp
services.AddCaching(b => b.UseRedis("rediss://...").WithKeyPrefix("svc-prod").WithStrictCertificateValidation());
```

**Why:** Sets `StrictRedisCertificateValidation=true`. Recommended for prod (also set by `UseProductionDefaults`).

### Permissive TLS

**When:** ElastiCache accessed through a custom DNS alias that does not match the certificate CN/SAN.

```csharp
services.AddCaching(b => b.UseRedis("rediss://my-alias:6380").WithKeyPrefix("svc-prod").WithPermissiveRedisTls());
```

**Why:** Allows host-mismatch only; chain-of-trust still validated. Untrusted certs are still rejected.

### Health checks

**When:** ASP.NET Core `/health` endpoint should reflect cache reachability.

```csharp
services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").WithHealthChecks(splitLivenessReadiness: true));
```

**Why:** Registers liveness (connection-only) + readiness (PING + probe). Deep dive: [HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md).

### Ops kill-switch

**When:** Incident mitigation — disable caching without redeploy.

```json
{ "CacheOptions": { "Enabled": false, "KeyPrefix": "svc-prod" } }
```

**Why:** When `Enabled=false`, `GetOrCreateAsync` runs the factory directly; writes no-op. Hot-reload flip to `false` takes effect immediately; flipping to `true` at runtime only works if the process started with `Enabled=true` (otherwise restart needed).

### Presets

**When:** Bundle environment-specific defaults.

```csharp
services.AddCaching(b => b.UseHybrid("...").WithKeyPrefix("svc-prod").UseProductionDefaults());
// dev: services.AddCaching(b => b.UseInMemory().WithKeyPrefix("svc-dev").UseDevelopmentDefaults());
```

**Why:** Prod = hashed keys in logs + strict TLS. Dev = raw keys in logs for debugging.

## Docs

- [features/](docs/features/) — per-feature deep dives (hybrid, stampede, telemetry)
- [INTERNALS.md](docs/INTERNALS.md) — striped locks, payload envelope, resilience, stale-while-revalidate
- [OPERATIONS.md](docs/OPERATIONS.md) — K8s/ElastiCache deployment, sharding, cred rotation, circuit-breaker tuning
- [TELEMETRY.md](docs/TELEMETRY.md) — OTel instruments, tag taxonomy, Grafana dashboard, Prometheus rules
- [SECURITY.md](docs/SECURITY.md) — TLS posture, secret redaction, PII handling, supply-chain
- [HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md) — health-check wiring
- [MIGRATION-V1-TO-V2.md](docs/MIGRATION-V1-TO-V2.md) — v1 → v2 breaking changes
- [BENCHMARKS.md](docs/BENCHMARKS.md) — perf numbers per mode / payload

## License

MIT
