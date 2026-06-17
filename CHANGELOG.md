# Changelog

All notable changes to Caching.NET are documented in this file.

The project follows [Semantic Versioning](https://semver.org/). See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for versioning policy.

## 2.1.0 — 2026-06-16

Additive minor release. No breaking changes.

### Added

- **Runtime-typed read overload:** `ICacheService.GetAsync(string key, Type type, CancellationToken)` — a non-generic counterpart to `GetAsync<T>` for callers that only know the target type at runtime (e.g. a settings cache keyed by `typeof(T).Name`). Returns `object?`; `null` on miss / envelope-invalid / format drift / schema drift; throws `ArgumentNullException` for a null `type`. It shares the **identical** envelope, format, and schema-hash validation as the generic path, so values are cross-readable between `SetAsync<T>` / `GetAsync<T>` and the runtime-typed overload. Prefer `GetAsync<T>` when the type is known at compile time.
  - Shipped as a **default interface method** so existing third-party `ICacheService` implementations keep compiling; the default reflects onto `GetAsync<T>`. Built-in `RedisCacheService`, `InMemoryCacheService`, `HybridCacheService`, and `RoutingCacheService` override it with a direct path.
  - Disabled mode (`Enabled=false`) short-circuits to `null`, mirroring the generic path.
- **`ICacheSerializer.Deserialize(ReadOnlyMemory<byte> bytes, Type type)`** — non-generic deserialize, added as a default interface method (reflects onto `Deserialize<T>` for custom serializers). `JsonCacheSerializer` and `MessagePackCacheSerializer` override it with their native non-generic APIs, preserving AOT/trim behavior.
- **`StableTypeHash.Compute(Type)`** (internal) — runtime-typed schema hash; `Compute<T>()` and `Compute(typeof(T))` are guaranteed to produce the same value.
- **`GetOrCreateAsync` no longer caches `null` factory results.** When the factory returns `null` (reference types / empty `Nullable<T>`), the value is returned to the caller but is **not** written to any tier, so the next call re-runs the factory. Applies to all modes (InMemory, Redis, Hybrid). Value-type defaults (`0`, `false`, `default(Guid)`, empty struct) are unaffected and continue to be cached. Explicit `SetAsync(key, value)` is unchanged. Previously a `null` factory result was stored in InMemory/Hybrid (served as a hit) and written to the Redis backend.

## 2.0.0 — 2026-05-09

Major release. Breaking changes from v1.x. See [docs/MIGRATION-V1-TO-V2.md](docs/MIGRATION-V1-TO-V2.md).

### Highlights

- Multi-target `net8.0`, `net9.0`, `net10.0` (single package).
- `KeyPrefix` mandatory across all modes (replaces `RedisInstanceName`).
- Striped lock manager with stable hashing — no per-key allocation, no leak.
- Polly v8 resilience pipelines (timeout + circuit breaker + retry) per backend.
- OpenTelemetry-native via static `CacheInstruments`. `ICacheTelemetry` removed.
- `PayloadEnvelope` wire format with schema-drift detection.
- `LoggerMessage` source-gen for hot-path logs.
- New API surface: `GetAsync`, `ExistsAsync`, `RefreshAsync`, `GetManyAsync`, `SetManyAsync`, `RemoveManyAsync`.
- `CacheCallOptions`: `AbsoluteExpiration`, `SlidingExpiration`, `AllowStaleFor`, `Tags`, `JitterPercentage`, `FactoryTimeout`.
- `CacheKey.For<T>(id).WithVariant(...).Build()` canonical key builder.
- `MessagePackCacheSerializer` opt-in via `WithMessagePackSerializer()`.
- Stale-while-revalidate orchestrator (in-process registry; bounded background refresh).
- TTL jitter (`WithTtlJitter(0.10)` default).
- TLS certificate audit logging + `cache.tls.validation` counter.
- Credential rotation hook (`RedisConnectionRotator` reloads multiplexer on options change).
- Server-side Redis MGET/MSET/KeyDelete pipelining (when `IConnectionMultiplexer` is registered).
- Brotli payload compression for Redis with pooled-buffer encode/decode helpers and a decompression output safety cap.
- AOT/trim verified via `Caching.NET.AotSmoke` smoke project.
- Testcontainers Redis integration suite, Polly chaos suite, FsCheck property suite.
- BenchmarkDotNet perf-gate via `scripts/dev.ps1 bench:gate` (10% regression threshold).
- SPDX 2.2 SBOM emitted with the nupkg.
- New public API surface ships under NuGet package validation (`EnablePackageValidation` on `Caching.NET.csproj`); breaking or additive changes require an intentional baseline/package-version decision for the next tag.

### Post-audit hardening

- `Enabled=false`: skip backend DI (memory/Redis/hybrid, serializer, Polly); options validation skipped; routing still resolves and short-circuits.
- Health probe: Redis/Hybrid uses multiplexer `PING` + per-process probe key suffix; avoids false-healthy when `FailOpen` masks cache errors. Liveness cancellation symmetry and readiness warm/read split.
- Resilience: broader transient classification, tighter retry backoff defaults, optional Redis concurrency limiter.
- Telemetry: `cache.serialize.duration` / `cache.deserialize.duration`; drift warning logs sampled per key fingerprint via `DriftLogSampler` with bounded dictionary growth.
- Validation: Redis connection string parse; prefix + user-key budget vs `MaximumKeyLength`; full **prefixed** key length enforced at routing.
- Correctness: `StaleEntryTracker` cap/prune; `RoutingCacheService` async disposal with hardened stale-refresh disposal/race handling; Hybrid value-type `GetAsync` miss path; stricter `PayloadEnvelope` length check; safer multiplexer rotation disposal; `RedisConnectionRotator.Dispose()` synchronous and deterministic.
- Builder: `Enable()`, environment presets, `WithKeyValidator` / `WithKeyTransformer`; `CachingBuilder` is configured via `AddCaching(...)`.
- **Resilience public surface:** configure timeouts/breaker/retry/concurrency via `CachingBuilder.WithResilience(Action<CacheResilienceOptions>)` only. **`CacheResiliencePipelineBuilder` is not public** — Polly registry types are not part of the shipped contract (Option B).
- **Health checks:** optional Kubernetes-style split — `WithHealthChecks(splitLivenessReadiness: true)` registers `CachingLivenessHealthCheck` + `CachingHealthCheck` as `{name}-liveness` / `{name}-readiness` with tags `liveness` / `readiness`.
- **`ICacheKeyFactory` / `DefaultCacheKeyFactory`:** DI-resolvable key builder (mirrors `CacheKey.For`); register a custom `ICacheKeyFactory` **before** `AddCaching` to inject tenant/segment logic.
- **Performance (audit §3.3):** `PayloadEnvelope.Write` allocates the wire `byte[]` with **`GC.AllocateUninitializedArray`**; **`StableStringHash`** uses **`ArrayPool<byte>`** for large UTF-8 encodings (>512 B). **`ICacheService`** stays **`Task` / `Task<T>`** — a `ValueTask` migration was prototyped and reverted before ship: the alloc savings on synchronous in-memory hits did not justify the breaking-change cost across consumer code, mocking frameworks, and decorators in mixed Hybrid/Redis production workloads.
- **Schema hash (B5):** envelope schema hash uses `Type.FullName` + optional `[CacheSchema]` so **library/package version bumps do not invalidate Redis entries**; existing entries written with the older assembly-qualified hash may schema-drift **once** after upgrade.
- **KeyPrefix (B6):** **`':'` is no longer allowed inside `KeyPrefix`** — avoids ambiguous physical keys when routing inserts `':'` between prefix and user segment. Prefer `serviceName-environment` naming (e.g. `asm-api-dev`).
- **Breaking:** `ICacheSerializer.Deserialize<T>` now takes **`ReadOnlyMemory<byte>`** (was `ReadOnlySpan<byte>`). Custom serializers must update; `MessagePackCacheSerializer` no longer allocates via `ToArray()` on deserialize when paired with Redis envelope payloads (zero-copy path).
- **Configuration section naming:** Documentation and samples use the JSON section **`CacheOptions`** and environment prefix **`CacheOptions__`** (matches `CacheConfigurationKeys.CacheOptions`). Configurations copied from older snippets that used `"Caching"` must be renamed.
- **`CachingBuilder` TLS controls:** `WithStrictCertificateValidation()` and **`WithPermissiveRedisTls()`** set `CacheOptions.StrictRedisCertificateValidation`; fluent intent overrides configuration when either method is used (nullable builder state replaces the previous always-true strict flag).
- **`CacheSerializerOptions`:** When the host does not call `Configure<CacheSerializerOptions>`, registration now initializes **`JsonSerializerOptions`** to **`JsonSerializerDefaults.Web`** so `[Required]` / `ValidateDataAnnotations` + `ValidateOnStart()` succeed.

### Samples

- Expanded `Caching.NET.Sample` coverage for v2 APIs: key hooks, custom key factory, `[CacheSchema]`, resilience tuning, split health checks, payload compression options, optional **`POST`** Redis round-trip probe (`redis/validate`) with **`CacheCallOptions`** mode override, Makefile **`sample-redis-validate`**, permissive TLS example for custom-host Redis alongside strict library defaults.

### Removed

- Public surface for **`CachingHealthCheck`** and **`CachingLivenessHealthCheck`** — types are **internal**; use `WithHealthChecks()` / `AddCachingHealthChecks()` only (instantiation from app code is unsupported).
- `ICacheTelemetry`, `NoopCacheTelemetry`, `OpenTelemetryCacheTelemetry`.
- `CacheOptions.RedisInstanceName`, `CachingBuilder.WithRedisInstanceName`.
- `RemoveAsync(IEnumerable<string>)` (renamed to `RemoveManyAsync`).
- All synchronous overloads (v2 is async-only).

### Defaults changed

- `Mode`: `Hybrid` → `InMemory` (zero-config friendlier).
- `StrictRedisCertificateValidation`: `false` → `true`.
- `MaximumKeyLength`: `null` → `512`.
- `TtlJitterPercentage`: `0.0` → `0.10`.

## [1.0.0](https://github.com/baps-apps/caching-net/releases/tag/v1.0.0) - Initial release

### Added

- **ICacheService** abstraction for shared caching across .NET applications.
- **Three cache modes:**
  - **InMemory** – in-process memory cache only.
  - **Redis** – distributed Redis via `Microsoft.Extensions.Caching.StackExchangeRedis`.
  - **Hybrid** – in-memory + optional Redis with stampede protection via `Microsoft.Extensions.Caching.Hybrid`.
- **CacheOptions** configuration bound from `CacheOptions` section:
  - `Enabled` (default: `false`, opt-in) – when false, registers `NoOpCacheService`; invalid option values do not fail startup.
  - `Mode` – InMemory, Redis, or Hybrid.
  - `DefaultExpiration` / `DefaultLocalExpiration` (TimeSpan format).
  - `RedisConnectionString`, `RedisInstanceName`, `MaximumPayloadBytes`, `MaximumKeyLength`, `MemorySizeLimitMb`.
  - `FailOpen`, `ThrowOnFailure`, `FactoryTimeout`, `StrictRedisCertificateValidation`.
- **CacheCallOptions** for per-call overrides: `OverrideMode`, `BypassCache`, `ForceRefresh`, `CoalesceConcurrent`.
- **CacheSerializerOptions** for custom JSON serialization (Redis/Hybrid).
- **AddCaching(IConfiguration)** extension – binds options, validates when enabled, registers mode-specific services and `RoutingCacheService` as `ICacheService`.
- **AddCachingHealthChecks** for lightweight pipeline health checks.
- **ValidateCacheRegistration** for fail-fast DI validation after host build.
- **ICacheTelemetry** abstraction and optional **OpenTelemetryCacheTelemetry** for metrics and spans.
- Data annotations and conditional validation on `CacheOptions` (validated only when `Enabled` is true).
- Target framework: **.NET 10** (`net10.0`).

### Documentation

- [README.md](README.md) – quick start, configuration, per-call options, telemetry, security.
- [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) – implementation details, modes, configuration, telemetry.
- [docs/OPERATIONS.md](docs/OPERATIONS.md) – production runbooks (when present).

---

