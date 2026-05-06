# Caching.NET v2.0.0 — Amazon-Scale Microservice Cache: Design Spec

**Date:** 2026-05-05
**Status:** Approved (brainstorming complete; ready for implementation planning)
**Target version:** v2.0.0 (major release; breaking changes from v1.x; single ship)
**Audience:** Library maintainers, downstream consumers planning v1→v2 migration

---

## 1. Goals & Non-Goals

### Goals

1. Make Caching.NET production-grade for high-throughput microservice-to-microservice communication at Amazon scale (10⁵+ rps per service, multi-region, multi-tenant).
2. Eliminate correctness bugs surfaced by the v1 audit (lock leak, telemetry miscount, silent failures, cardinality risk, hard-coded JSON).
3. Provide first-class resilience (circuit breaker, timeout, retry, FailOpen), pluggable serialization, and OTel-native observability.
4. Lock the public API surface and ship deterministic, AOT/trim-compatible NuGet artifacts with source-link and SBOM.
5. Cover .NET 8 LTS, .NET 9, .NET 10 targets in a single multi-targeted package.

### Non-Goals

- Cross-region cache invalidation (consumer responsibility — pattern documented, not built in).
- Custom Redis cluster sharding strategies (documented; library uses `IDistributedCache` as-is).
- Backwards compatibility with v1 API. v2 is a clean break.
- Synchronous API surface. Async-only.
- Built-in persistence beyond Redis L2.

---

## 2. Decisions Locked During Brainstorming

| # | Decision | Choice |
|---|----------|--------|
| 1 | Scope strategy | Phased mega-spec, 4 phases (P0–P3) |
| 2 | Versioning | v2.0.0 major bump; break freely; no shims |
| 3 | Target frameworks | `net8.0; net9.0; net10.0` multi-target |
| 4 | Serialization | Pluggable `ICacheSerializer`; default JSON STJ source-gen; opt-in MessagePack |
| 5 | Resilience | Polly v8 baked into core; configurable via builder |
| 6 | Telemetry | Drop `ICacheTelemetry`; OTel-first via static `CacheInstruments` |
| 7 | Multi-tenancy | `KeyPrefix` mandatory at routing layer; `RedisInstanceName` removed |
| 8 | API surface | Full expansion: batch + Get/Refresh/Exists + sliding + tags + stale-while-revalidate + jitter |
| 9 | Stampede protection | Striped locks (1024 default); Hybrid mode delegates coalescing to `HybridCache` |
| 10 | Phase ordering | P0 → P1 → P2 → P3, single v2.0.0 ship after all four merge |
| 11 | Testing | Unit + Testcontainers Redis + Polly chaos + BenchmarkDotNet perf-gate + FsCheck property tests |
| 12 | Packaging | Single `Caching.NET` NuGet (no split packages) |

---

## 3. Architecture Overview

```
Consumer code
   │
   ▼  ICacheService (stable v2 contract: stamped + batch + tag + refresh + exists)
RoutingCacheService          ← KeyPrefix injection, mode dispatch, per-call options,
   │                            stale-while-revalidate orchestrator, telemetry emit
   │
   ├── StripedLockManager (1024 stripes, configurable) — coalesce in InMemory/Redis modes
   ├── ResiliencePipelineProvider (Polly v8) — circuit breaker + timeout + retry per backend
   ├── ICacheSerializer (default JSON/STJ source-gen; opt-in MessagePack via builder)
   ├── PayloadEnvelope ({format, version, schema-hash, bytes}) — drift-safe
   │
   ▼
   InMemoryCacheService    RedisCacheService    HybridCacheService
   (IMemoryCache +         (IDistributedCache +  (Microsoft HybridCache —
    eviction listener)      Polly pipeline)       coalesce delegated to it)
   │
   ▼
   CacheInstruments (OTel Meter + ActivitySource — no interface; consumer opts into OTel pipeline)
```

### Key shifts vs v1

- `ICacheTelemetry` deleted. Direct OTel via static `CacheInstruments`.
- `RedisInstanceName` deleted → `KeyPrefix` mandatory at routing layer (covers all modes uniformly, not just Redis).
- Stampede coalescing moved out of `RoutingCacheService` into `StripedLockManager` (no per-key lock allocation, no lock leak).
- All Redis ops wrapped in Polly `ResiliencePipeline` with circuit breaker + timeout + retry.
- All Redis payloads wrapped in `PayloadEnvelope` for schema-drift safety.
- Hybrid mode delegates coalescing to `HybridCache` (no double-locking).

---

## 4. Public API Surface

### `ICacheService` (stable v2 contract)

```csharp
public interface ICacheService
{
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null, CancellationToken ct = default);

    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken ct = default);
    Task RefreshAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);

    // Batch
    Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken ct = default);
    Task SetManyAsync<T>(IReadOnlyDictionary<string, T> items, TimeSpan? expiration = null, CancellationToken ct = default);
    Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken ct = default);

    // Tags (no-op on non-Hybrid; throws NotSupportedException only when builder.RequireTagSupport() is set)
    Task RemoveByTagAsync(string tag, CancellationToken ct = default);
}
```

### `CacheCallOptions`

```csharp
public sealed class CacheCallOptions
{
    public CacheMode? Mode { get; init; }
    public bool BypassCache { get; init; }
    public bool ForceRefresh { get; init; }
    public bool CoalesceConcurrent { get; init; } = true;
    public TimeSpan? FactoryTimeout { get; init; }
    public TimeSpan? AbsoluteExpiration { get; init; }
    public TimeSpan? SlidingExpiration { get; init; }
    public TimeSpan? AllowStaleFor { get; init; }       // stale-while-revalidate window
    public IReadOnlyList<string>? Tags { get; init; }
    public double? JitterPercentage { get; init; }       // 0.0–0.5; overrides global
}
```

### `CachingBuilder` additions

```csharp
builder
    .UseHybrid()
    .WithKeyPrefix("orders-svc:v1")              // mandatory; non-empty in v2
    .WithSerializer<MessagePackCacheSerializer>() // pluggable; default JSON STJ source-gen
    .WithResilience(r => r.CircuitBreaker(...).Timeout(2.Seconds()).Retry(3))
    .WithTtlJitter(0.10)                          // ±10 % default for all entries
    .WithStripedLocks(2048)                       // override default 1024
    .WithStaleRefreshConcurrency(512)
    .WithOpenTelemetry()                          // wires Meter + ActivitySource into host pipeline
    .WithHealthChecks()
    .RequireTagSupport();                         // throws if Mode != Hybrid
```

### `CacheKeyBuilder` helper

```csharp
var key = CacheKey.For<Order>(orderId).WithVariant("v2").Build();
// → "Order:12345:v2"
// Routing layer prepends KeyPrefix → "orders-svc:v1:Order:12345:v2"
```

### Removed v1 surface

- `ICacheTelemetry`, `NoopCacheTelemetry`, `OpenTelemetryCacheTelemetry` — gone.
- `CacheOptions.RedisInstanceName` and `CachingBuilder.WithRedisInstanceName()` — gone.
- `RedisCertificateValidation` callback prop — replaced by `CertificateValidationCallback` in resilience builder with mandatory audit logging.

---

## 5. Stampede Protection & Lock Manager

### `StripedLockManager`

```csharp
internal sealed class StripedLockManager
{
    private readonly SemaphoreSlim[] _stripes;
    private readonly int _mask;  // _stripes.Length - 1; must be power of 2

    public StripedLockManager(int stripeCount = 1024)
    {
        int n = RoundUpPow2(stripeCount);
        _stripes = new SemaphoreSlim[n];
        for (int i = 0; i < n; i++) _stripes[i] = new SemaphoreSlim(1, 1);
        _mask = n - 1;
    }

    public SemaphoreSlim GetLock(string key)
    {
        uint h = StableStringHash(key);   // xxHash64 truncated; NOT String.GetHashCode (randomized)
        return _stripes[h & _mask];
    }
}
```

### Properties

- Fixed memory: `stripeCount × SemaphoreSlim` (~64 B each) ≈ 64 KiB at default 1024.
- Zero allocation per op.
- Zero leak (locks live for app lifetime).
- Collision rate at 1024 stripes / 1 M unique hot keys: ~0.1 %; configurable upward.
- Stable hash so same key → same stripe across process restarts (matters for diagnostics only).

### Coalescing flow in `RoutingCacheService.GetOrCreateAsync`

1. Prepend `KeyPrefix` → `fullKey`.
2. If `Mode == Hybrid` → delegate to `HybridCacheService` (HybridCache coalesces internally).
3. Otherwise (InMemory / Redis):
   1. Read attempt (no lock); hit + `!ForceRefresh` → return.
   2. Acquire stripe lock for `fullKey`.
   3. Read attempt #2 (double-checked locking); hit + `!ForceRefresh` → release, return.
   4. Run factory under Polly resilience pipeline + `FactoryTimeout`.
   5. Write to backend.
   6. Release stripe lock.
   7. Emit miss telemetry with `miss_reason`.

### `ForceRefresh` correctness

v1 bug fixed: under `CoalesceConcurrent=true`, all coalesced waiters see same fresh value because step 3.iii double-check is skipped when the lock-holder set `ForceRefresh=true`. Late arrivers after release see freshly written value.

### `BypassCache`

Skips lock entirely; runs factory directly; no read/write to cache.

### `AllowStaleFor` (stale-while-revalidate)

- Each entry stores `expiresAt` and `staleUntil = expiresAt + AllowStaleFor`.
- Read between `expiresAt` and `staleUntil`: return stale value, schedule background refresh on `Task.Run` with bounded concurrency (default 256, configurable via `WithStaleRefreshConcurrency`).
- Background refresh acquires same stripe lock → only one refresh per key.
- Telemetry tag: `cache.served_stale=true`; counter `cache.stale_served`; UpDownCounter `cache.stale_refresh.in_flight`.

---

## 6. Resilience, Serialization, Payload Envelope

### Polly Resilience Pipeline

`Caching.NET.Resilience.CacheResiliencePipelineBuilder` produces named `ResiliencePipeline` per backend:

```csharp
new ResiliencePipelineBuilder()
    .AddTimeout(TimeSpan.FromSeconds(2))         // per-op
    .AddCircuitBreaker(new()
    {
        FailureRatio        = 0.5,
        MinimumThroughput   = 20,
        SamplingDuration    = TimeSpan.FromSeconds(30),
        BreakDuration       = TimeSpan.FromSeconds(15)
    })
    .AddRetry(new()
    {
        MaxRetryAttempts = 2,
        BackoffType      = DelayBackoffType.Exponential,
        UseJitter        = true,
        ShouldHandle     = static args => ValueTask.FromResult(args.Outcome.Exception is RedisConnectionException
                                                                                       or TimeoutException)
    })
    .Build();
```

- Pipeline names: `cache.redis.read`, `cache.redis.write`, `cache.redis.delete`. Distinct so write-path breaker never trips read path.
- InMemory mode: no pipeline.
- Hybrid mode: pipeline applied only to L2 (Redis) calls; L1 path bypasses Polly.
- Circuit-open behavior respects `FailOpen`:
  - `FailOpen=true` + breaker open + `GetOrCreateAsync` → run factory directly, no cache read/write, telemetry tag `cache.circuit_open=true`.
  - `FailOpen=false` + breaker open → throw `BrokenCircuitException`.
- All circuit transitions emit `cache.circuit_state_changes` counter and structured log.

### `ICacheSerializer` Abstraction

```csharp
public interface ICacheSerializer
{
    string FormatId { get; }                  // "json", "msgpack", custom
    byte[] Serialize<T>(T value);
    T? Deserialize<T>(ReadOnlySpan<byte> bytes);
}
```

Built-in implementations:

- `JsonCacheSerializer` (default) — uses consumer-supplied `JsonSerializerContext` for AOT/trim. Registered via `WithSerializer(new JsonCacheSerializer(MyContext.Default))`. If no context supplied, falls back to reflection-based STJ with a build warning emitted under `<IsAotCompatible>true`.
- `MessagePackCacheSerializer` — opt-in via `WithSerializer<MessagePackCacheSerializer>()`. `MessagePack` is a hard dep of the single package per Q12 (always shipped, only loaded when consumer wires it up).

### `PayloadEnvelope` (Redis only — InMemory stores raw `T`)

Wire format:

```
Magic     : 4 bytes  ASCII "CN20" (CN + version)
FormatId  : 1 byte   0x01=json, 0x02=msgpack, 0xFF=custom (length-prefixed string follows)
SchemaHash: 8 bytes  xxHash64 of full type name (assembly-qualified)
PayloadLen: 4 bytes  uint32 little-endian
Payload   : N bytes
```

- Read errors:
  - Magic mismatch → WARN log, treat as miss, emit `cache.envelope_invalid`.
  - FormatId ≠ currently-configured serializer → miss, emit `cache.format_drift`.
  - SchemaHash mismatch → miss, emit `cache.schema_drift` (DTO changed since cached).
  - All cases respect `FailOpen`; never throw on envelope errors.
- Write: ~17 B overhead per entry. Acceptable.

### Per-Op Timeouts (Defense in Depth)

```csharp
using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
linked.CancelAfter(options.RedisOperationTimeout);  // default 2 s
await db.StringGetAsync(key).WaitAsync(linked.Token);
```

Polly timeout fires too. Both layers catch slow backend.

---

## 7. Observability (OTel-First)

### `CacheInstruments`

```csharp
public static class CacheInstruments
{
    public const string MeterName = "Caching.NET";
    public const string ActivitySourceName = "Caching.NET";

    internal static readonly Meter Meter = new(MeterName, "2.0.0");
    internal static readonly ActivitySource Activity = new(ActivitySourceName, "2.0.0");

    // Counters
    internal static readonly Counter<long> Hits                = Meter.CreateCounter<long>("cache.hits", unit: "{op}");
    internal static readonly Counter<long> Misses              = Meter.CreateCounter<long>("cache.misses", unit: "{op}");
    internal static readonly Counter<long> Errors              = Meter.CreateCounter<long>("cache.errors", unit: "{op}");
    internal static readonly Counter<long> Evictions           = Meter.CreateCounter<long>("cache.evictions", unit: "{entry}");
    internal static readonly Counter<long> StaleServed         = Meter.CreateCounter<long>("cache.stale_served", unit: "{op}");
    internal static readonly Counter<long> CircuitStateChanges = Meter.CreateCounter<long>("cache.circuit_state_changes", unit: "{event}");
    internal static readonly Counter<long> SchemaDrift         = Meter.CreateCounter<long>("cache.schema_drift", unit: "{event}");

    // Histograms
    internal static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("cache.operation.duration", unit: "ms");
    internal static readonly Histogram<long>   PayloadBytes      = Meter.CreateHistogram<long>("cache.payload.bytes", unit: "By");

    internal static readonly UpDownCounter<long> StaleRefreshInFlight = Meter.CreateUpDownCounter<long>("cache.stale_refresh.in_flight", unit: "{task}");
}
```

### Tag Discipline

**Allowed metric tags (low cardinality, ≤ ~50 unique values total):**

- `cache.mode` ∈ `{InMemory, Redis, Hybrid}`
- `cache.operation` ∈ `{get, set, remove, get_many, set_many, remove_many, exists, refresh, get_or_create}`
- `cache.layer` ∈ `{l1, l2}` (Hybrid only)
- `cache.miss_reason` ∈ `{NotFound, Expired, Stale, SerializationFailed, EnvelopeInvalid, CircuitOpen, Disabled, Bypass}`
- `cache.eviction_reason` ∈ `{Expired, Capacity, Replaced, Removed, TokenExpired}`
- `cache.error_kind` ∈ `{Timeout, ConnectionFailed, Serialization, CircuitOpen, Unknown}`
- `cache.served_stale` ∈ `{true, false}`
- `cache.circuit_state` ∈ `{closed, open, half-open}`

**Forbidden metric tags (analyzer-enforced):**

- `key` — cardinality bomb. Never tag.
- `tenant`, `user_id` — same.
- `payload_size` — use the histogram.

### Tracing

- One Activity per public op (`cache.get_or_create`, `cache.get`, `cache.set`, …).
- Standard tags + optional `cache.key_hash` (xxHash64 hex, never the raw key) when `Options.IncludeKeyHashInTraces=true` (default `false`).
- Activity events: `lock.acquired`, `factory.invoked`, `stale.refresh.scheduled`, `circuit.opened`.
- Raw key never appears in traces (PII safety).

### Logging

`LoggerMessage` source-gen, zero-alloc hot path. Stable EventId ranges:

- 1000–1099 = info
- 1100–1199 = warn
- 1200–1299 = error

All log messages use `KeyHash` (xxHash64 hex) by default. Toggle `Options.IncludeRawKeyInLogs=true` for dev only.

### IMemoryCache Eviction Hook

`PostEvictionCallbacks` increments `Evictions` counter tagged with `eviction_reason` so InMemory mode is no longer silent on capacity-driven evictions.

### Cardinality Enforcement (compile-time)

Roslyn analyzer (`Caching.NET.Analyzers`, ships in main package as analyzer-only ref):

- Flags `tags.Add("key", ...)` or `tags.Add("tenant", ...)` calls on internal `TagList` instances.
- Severity: error.

---

## 8. Configuration, Validation, Hot-Reload, Multi-Tenant

### `CacheOptions` (v2 — breaking)

```csharp
public sealed class CacheOptions
{
    [Required] public string KeyPrefix { get; set; } = string.Empty;     // empty → ValidationException

    public CacheMode Mode { get; set; } = CacheMode.Hybrid;
    public string? RedisConnectionString { get; set; }
    public bool StrictRedisCertificateValidation { get; set; } = true;    // default flipped

    public bool Enabled { get; set; } = true;                              // hot-reloadable
    public bool FailOpen { get; set; } = true;
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(10);
    public double TtlJitterPercentage { get; set; } = 0.10;

    public int MaximumKeyLength { get; set; } = 512;
    public long MaximumPayloadBytes { get; set; } = 1_048_576;             // 1 MiB

    public int StripeLockCount { get; set; } = 1024;                       // power of 2; rounded up
    public int StaleRefreshConcurrency { get; set; } = 256;
    public TimeSpan FactoryTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RedisOperationTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public long? MemorySizeLimitMb { get; set; } = 256;

    public bool IncludeRawKeyInLogs { get; set; } = false;
    public bool IncludeKeyHashInTraces { get; set; } = false;

    public TimeSpan? HybridLocalCacheExpiration { get; set; }
}
```

### Startup Validation (`IValidateOptions<CacheOptions>` + `ValidateOnStart()`)

Hard fails at host startup:

- `KeyPrefix` non-empty, ≤ 64 chars, regex `^[a-zA-Z0-9][a-zA-Z0-9._:-]*$` (no whitespace, no `*` or `?` to prevent KEYS-pattern injection).
- `Mode == Redis || Mode == Hybrid` ⇒ `RedisConnectionString` non-empty.
- `MaximumKeyLength >= 64 && <= 8192`.
- `MaximumPayloadBytes >= 1024 && <= 100 * 1024 * 1024`.
- `StripeLockCount >= 16 && <= 65536` (rounded up to power of 2).
- `TtlJitterPercentage >= 0 && <= 0.5`.
- `FactoryTimeout` between 100 ms and 30 min.
- `RedisOperationTimeout` between 50 ms and 30 s.
- If `RedisConnectionString` non-null, never include in any exception/log message; redact via `RedisConnectionStringRedactor`.

### Hot-Reload (`IOptionsMonitor<CacheOptions>`)

**Hot-reloadable (read on every call):**

`Enabled`, `FailOpen`, `DefaultExpiration`, `TtlJitterPercentage`, `MaximumPayloadBytes`, `MaximumKeyLength`, `IncludeRawKeyInLogs`, `IncludeKeyHashInTraces`, `FactoryTimeout`, `RedisOperationTimeout`, `StaleRefreshConcurrency`.

**Startup-only (read once in constructor; emits WARN log if changed at runtime):**

`KeyPrefix`, `Mode`, `RedisConnectionString`, `StrictRedisCertificateValidation`, `StripeLockCount`, `MemorySizeLimitMb`, `HybridLocalCacheExpiration`.

`OptionsChangeTokenSource` subscribes and logs at WARN: `"Caching option {Name} changed but is startup-only. Restart required."` for the gated set.

### Multi-Tenant Key Layout

All keys produced by `RoutingCacheService` follow:

```
{KeyPrefix}:{type-or-domain}:{id}[:{variant}]
```

Examples (with `KeyPrefix="orders-svc:v1"`):

- `orders-svc:v1:Order:12345`
- `orders-svc:v1:Order:12345:fulfilled`
- `orders-svc:v1:_tag:premium-customer` (tag-association keys, internal)

### `CacheKeyBuilder`

```csharp
public static class CacheKey
{
    public static CacheKeyBuilder For<T>(object id) => new(typeof(T).Name, id.ToString()!);
}

public sealed class CacheKeyBuilder
{
    public CacheKeyBuilder WithVariant(string variant);
    public CacheKeyBuilder WithSegment(string seg);
    public string Build();   // does NOT include KeyPrefix; routing layer prepends
}
```

`Build()` validation: max 256 chars (post-prefix limit checked at routing layer), no `:` or whitespace inside individual segments (auto-escaped via URL-safe encoding).

### Secret Redaction

`RedisConnectionStringRedactor`:

- Strips `password=`, `user=`, `name=` segments before any logging.
- Used in `IValidateOptions` failure messages and in any log/exception touching options.
- Unit tested.

### TLS / Certificate Audit

`Caching.NET.Internal.RedisCertificateValidator`:

- Wraps consumer's optional callback.
- Always logs at INFO on first validation per process: subject, issuer, thumbprint, expiry.
- Logs at WARN on any chain error even when `StrictRedisCertificateValidation=false`.
- Emits `cache.tls.validation` counter tagged with `result` ∈ `{ok, name_mismatch, chain_error, expired, untrusted}`.

---

## 9. Testing Strategy (Q11 = D — full matrix)

Test projects under `tests/`:

1. **`Caching.NET.Tests`** (xUnit + Moq) — unit tests for options validation, key builder, envelope encode/decode, striped lock manager, routing decisions, telemetry tags, secret redactor, cert validator. Target ≥ 90 % line coverage on internal logic.
2. **`Caching.NET.Tests.Integration`** (Testcontainers.Redis) — real Redis 7.x container per test class. Covers Redis read/write, MGET/MSET batch, TLS handshake (stunnel sidecar container), connection drop mid-call, key prefix isolation, schema-drift on real wire, eviction observability.
3. **`Caching.NET.Tests.Chaos`** (`Microsoft.Extensions.Resilience.Testing` + `Polly.Testing`) — fault injection: slow Redis, drop responses, return corrupt bytes, intermittent disconnects. Asserts: circuit opens at threshold, FailOpen executes factory, telemetry `circuit_state_changes` fires, no thread exhaustion under sustained errors.
4. **`Caching.NET.Tests.Properties`** (FsCheck) — generative properties:
   - Serializer round-trip: `∀ T, ∀ v ∈ T : deserialize(serialize(v)) ≡ v` (excluding NaN / cycles).
   - `StripedLockManager` determinism: `∀ k1, k2 : k1 == k2 ⇒ GetLock(k1) == GetLock(k2)`.
   - `PayloadEnvelope`: any random bytes either decode cleanly or produce `envelope_invalid` telemetry — never throw.
   - `GetOrCreateAsync` coalescing: under N concurrent waiters with same key, factory invoked exactly once.
5. **`Caching.NET.Bench`** (BenchmarkDotNet):
   - `GetOrCreateAsync` cold/warm by mode.
   - Serializer comparison (JSON vs MessagePack) at 100 B / 10 KB / 1 MB payloads.
   - Striped lock contention at 1 / 10 / 100 / 1000 concurrent threads.
   - Batch ops: GetMany 10 / 100 / 1000 keys.
   - Allocations per op (`[MemoryDiagnoser]`).

   **CI perf gate:** baseline stored in `bench-baseline.json`. CI fails if mean p99 latency or `Allocated` regresses > 10 % vs baseline.

### Local Build & Test Tooling

No remote CI service is used. All build, test, AOT smoke, bench, perf-gate, and pack work runs through cross-platform PowerShell Core (`pwsh`) scripts under `scripts/`. Single entrypoint `scripts/dev.ps1` exposes subcommands:

- `build` — restore + build (warnings-as-errors).
- `test` — unit tests across `[net8.0, net9.0, net10.0]`.
- `test:integration` — Testcontainers Redis suite (requires Docker).
- `test:chaos` — Polly fault-injection suite.
- `test:property` — FsCheck property suite.
- `aot` — `Caching.NET.AotSmoke` publish + run.
- `bench` — BenchmarkDotNet run, JSON output to `bench/Caching.NET.Bench/BenchmarkDotNet.Artifacts`.
- `bench:gate` — compare current bench output against `bench-baseline.json`; fail on > 10 % regression.
- `pack` — produce signed nupkg + snupkg into `nupkgs/`.
- `all` — runs the full local equivalent of the former CI matrix in dependency order.

Runs on Windows / Linux / macOS via `pwsh`. Developers and reviewers execute the same scripts; no green-checkmark badge is sourced from a remote runner.

---

## 10. Packaging (Q12 = A — single NuGet)

`Caching.NET.csproj`:

```xml
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
<IsAotCompatible>true</IsAotCompatible>
<IsTrimmable>true</IsTrimmable>
<PublishRepositoryUrl>true</PublishRepositoryUrl>
<EmbedUntrackedSources>true</EmbedUntrackedSources>
<IncludeSymbols>true</IncludeSymbols>
<SymbolPackageFormat>snupkg</SymbolPackageFormat>
<DeterministicSourcePaths>true</DeterministicSourcePaths>
<ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<EnablePackageValidation>true</EnablePackageValidation>
<PackageValidationBaselineVersion>2.0.0</PackageValidationBaselineVersion>
<EnableSourceLink>true</EnableSourceLink>
```

**Hard dependencies:**

- `Microsoft.Extensions.Caching.Memory`
- `Microsoft.Extensions.Caching.StackExchangeRedis`
- `Microsoft.Extensions.Caching.Hybrid`
- `Microsoft.Extensions.Http.Resilience` / `Polly` v8
- `MessagePack` (always shipped; opt-in via builder)
- `System.Text.Json` (source-gen for AOT)

**SBOM:** `Microsoft.Sbom.Targets` generates SPDX 2.2 alongside `.nupkg`.

**Public API surface lock:** `Microsoft.CodeAnalysis.PublicApiAnalyzers` with `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt`. Any unintended API change fails the build.

---

## 11. Documentation Deliverables

Under `docs/`:

- `README.md` — quickstart, installation, three-mode example, Amazon-style production config example.
- `MIGRATION-V1-TO-V2.md` — full breaking-change list with sed-friendly find/replace table.
- `OPERATIONS.md` (rewritten) — sections: Hot-reload matrix, Sharding strategy, Multi-tenant key design, K8s deployment manifest, AWS ElastiCache setup (TLS / VPC / IAM auth), Cred rotation runbook, Circuit-breaker tuning.
- `TELEMETRY.md` (rewritten) — Meter / ActivitySource names, full tag taxonomy, **cardinality warnings**, OTel collector config snippet, Grafana dashboard JSON, Prometheus recording rules.
- `INTERNALS.md` (rewritten) — striped locks, payload envelope wire format, resilience pipeline composition, stale-while-revalidate flow.
- `SECURITY.md` (new) — TLS posture, secret redaction guarantees, PII handling, supply-chain (signed pkgs, source-link, SBOM).
- `BENCHMARKS.md` — published BenchmarkDotNet results per mode / payload size.

### Migration Path (v1 → v2)

`MIGRATION-V1-TO-V2.md` find/replace table (excerpt):

| v1 | v2 |
|----|----|
| `services.AddCaching(b => b.UseRedis(cs).WithRedisInstanceName("foo"))` | `services.AddCaching(b => b.UseRedis(cs).WithKeyPrefix("foo"))` |
| `ICacheTelemetry` impl | Subscribe to `Meter("Caching.NET")` + `ActivitySource("Caching.NET")` via OTel |
| `services.AddSingleton<ICacheTelemetry, MyTelemetry>()` | Configure OTel `Meter` / `ActivitySource` provider |
| `CacheOptions.RedisInstanceName` | `CacheOptions.KeyPrefix` (now required) |
| `MaximumKeyLength = null` | `MaximumKeyLength = 512` (default) |
| `StrictRedisCertificateValidation = false` | flip to `true` for prod |

No back-compat shims. Major-version bump = clean break.

---

## 12. Implementation Phases (single v2.0.0 ship)

All four phases land on `main` before tagging `v2.0.0`. Each phase has its own implementation plan generated by the `writing-plans` skill.

**P0 — Foundations & Critical**
TFM multi-target · `KeyPrefix` mandatory · `StripedLockManager` · Polly pipeline · per-op timeouts · `ICacheSerializer` + `JsonCacheSerializer` (source-gen) · drop `RedisInstanceName` · drop `ICacheTelemetry` → `CacheInstruments` · default tightening (`MaximumKeyLength=512`, `StrictRedisCertificateValidation=true`).

**P1 — Observability & Envelope**
All OTel instruments · miss-reason · eviction listener · payload histogram · `PayloadEnvelope` · schema-drift counter · `LoggerMessage` source-gen · cardinality analyzer.

**P2 — API Expansion**
`GetMany` / `SetMany` / `RemoveMany` (with Redis pipelining) · `GetAsync` / `RefreshAsync` / `ExistsAsync` · sliding expiration · tag overloads · `MessagePackCacheSerializer` · `CacheKeyBuilder` · stale-while-revalidate · TTL jitter.

**P3 — Hardening & Ops**
AOT/trim verified · Testcontainers integration suite · Polly chaos suite · BenchmarkDotNet + perf gate · K8s/ElastiCache runbook · sharding guide · deterministic build + source-link + SBOM · cred rotation hooks · cert validation audit logging · local cross-platform build/test scripts (`scripts/dev.ps1`).

Single tag: `v2.0.0` after all four phases land on `main` and `scripts/dev.ps1 all` is green on every supported host (matrix + chaos + perf gate).

---

## 13. Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Polly v8 dep drag for consumers already on Polly v7 | Document min Polly version; v8 is stable since 2024-Q1 — adoption mature by 2026 |
| MessagePack hard dep increases install size for JSON-only users | Trim eliminates unused types; install size delta ≤ 200 KiB |
| Striped-lock false collisions cause unexpected serialization at extreme contention | Default 1024 stripes raises lift; user can `WithStripedLocks(8192)`; benchmark documents trade-off |
| Removing `ICacheTelemetry` breaks consumers with custom telemetry sinks | Migration doc shows OTel-only path; OTel is industry standard |
| Multi-tenant `KeyPrefix` mandatory may surprise single-tenant consumers | Default to service name (e.g. `Assembly.GetEntryAssembly().GetName().Name`) suggested in docs; still required to be set explicitly |
| Multi-TFM matrix run-time on a single dev host | `scripts/dev.ps1 all` runs all three TFMs in series; full pass < 15 min on a modern laptop. Engineers can scope down via `scripts/dev.ps1 test -Tfm net10.0` while iterating |
| Schema-drift counter spam during gradual deploys | Counter is per-event, not per-key; rate-limited by miss rate; log de-dup via first-occurrence cache |

---

## 14. Out-of-Scope (Deferred Beyond v2.0.0)

- Cross-region cache invalidation bus (consumer responsibility; pattern documented).
- Built-in DynamoDB / Memcached backends.
- gRPC streaming cache server.
- Distributed lock manager beyond per-process striped locks.
- Cache warming / pre-population utilities.
- Per-tenant rate limiting.

These may land in v2.x minor releases after community feedback.

---

## 15. Acceptance Criteria for v2.0.0 Tag

1. All four phases (P0–P3) merged on `main`.
2. `scripts/dev.ps1 all` green locally on at least one Windows host and one Linux/macOS host across `[net8.0, net9.0, net10.0]` (no remote CI runner).
3. Testcontainers integration suite green via `scripts/dev.ps1 test:integration`.
4. Polly chaos suite green via `scripts/dev.ps1 test:chaos`.
5. BenchmarkDotNet perf gate green via `scripts/dev.ps1 bench:gate` vs `bench-baseline.json`.
6. `PublicAPI.Shipped.txt` matches generated public surface; no Unshipped diffs.
7. `MIGRATION-V1-TO-V2.md` complete; reviewed by maintainer.
8. SBOM generated; source-link verified; package signed.
9. Smoke deploy of `samples/Caching.NET.Sample` against AWS ElastiCache (TLS) successful.
10. README quickstart copy-paste reproduces a working hybrid setup in a clean .NET 8 project.

---
