# Changelog

## Unreleased (v2.1.0 target)

- Hardened stale-refresh disposal/race handling in `RoutingCacheService`.
- Added decompression output safety cap for compressed Redis payloads.
- Reduced Brotli payload allocations using pooled-buffer compression/decompression helpers.
- Made `RedisConnectionRotator.Dispose()` synchronous and deterministic.
- Bounded `DriftLogSampler` dictionary growth to avoid unbounded process-lifetime memory.
- Improved health probe behavior: liveness cancellation symmetry and readiness warm/read split.
- Expanded sample coverage for v2 APIs: key hooks, custom key factory, `[CacheSchema]`, resilience tuning, split health checks, and payload compression options.
- New public API surface ships under NuGet package validation (`EnablePackageValidation` on `Caching.NET.csproj`); breaking or additive changes require an intentional baseline/package-version decision for the next tag (v2.1.0 target).

## 2.0.0 — 2026-05-06

Major release. Breaking changes from v1.x. See [docs/MIGRATION-V1-TO-V2.md](docs/MIGRATION-V1-TO-V2.md).

### Highlights

- Multi-target `net8.0`, `net9.0`, `net10.0` (single package).
- `KeyPrefix` mandatory across all modes (replaces `RedisInstanceName`).
- Striped lock manager with stable hashing — no per-key allocation, no leak.
- Polly v8 resilience pipelines (timeout + circuit breaker + retry) per backend.
- OpenTelemetry-native via static `CacheInstruments`. `ICacheTelemetry` removed.
- `PayloadEnvelope` wire format with schema-drift detection.
- `LoggerMessage` source-gen for hot-path logs.
- `Caching.NET.Analyzers` ships in the main package — compile-time `CN0001` blocks high-cardinality OTel tags.
- New API surface: `GetAsync`, `ExistsAsync`, `RefreshAsync`, `GetManyAsync`, `SetManyAsync`, `RemoveManyAsync`.
- `CacheCallOptions`: `AbsoluteExpiration`, `SlidingExpiration`, `AllowStaleFor`, `Tags`, `JitterPercentage`, `FactoryTimeout`.
- `CacheKey.For<T>(id).WithVariant(...).Build()` canonical key builder.
- `MessagePackCacheSerializer` opt-in via `WithMessagePackSerializer()`.
- Stale-while-revalidate orchestrator (in-process registry; bounded background refresh).
- TTL jitter (`WithTtlJitter(0.10)` default).
- TLS certificate audit logging + `cache.tls.validation` counter.
- Credential rotation hook (`RedisConnectionRotator` reloads multiplexer on options change).
- Server-side Redis MGET/MSET/KeyDelete pipelining (when `IConnectionMultiplexer` is registered).
- AOT/trim verified via `Caching.NET.AotSmoke` smoke project.
- Testcontainers Redis integration suite, Polly chaos suite, FsCheck property suite.
- BenchmarkDotNet perf-gate via `scripts/dev.ps1 bench:gate` (10% regression threshold).
- SPDX 2.2 SBOM emitted with the nupkg.

### Post-audit hardening (v2.0.0)

- `Enabled=false`: skip backend DI (memory/Redis/hybrid, serializer, Polly); options validation skipped; routing still resolves and short-circuits.
- Health probe: Redis/Hybrid uses multiplexer `PING` + per-process probe key suffix; avoids false-healthy when `FailOpen` masks cache errors.
- Resilience: broader transient classification, tighter retry backoff defaults, optional Redis concurrency limiter.
- Telemetry: `cache.serialize.duration` / `cache.deserialize.duration`; drift warning logs sampled per key fingerprint.
- Validation: Redis connection string parse; prefix + user-key budget vs `MaximumKeyLength`; full **prefixed** key length enforced at routing.
- Correctness: `StaleEntryTracker` cap/prune; `RoutingCacheService` async disposal; Hybrid value-type `GetAsync` miss path; stricter `PayloadEnvelope` length check; safer multiplexer rotation disposal.
- Analyzer **CN0001**: constant tag keys + string-literal logger templates / `BeginScope`.
- Builder: `Enable()`, environment presets, `WithKeyValidator` / `WithKeyTransformer`; `CachingBuilder` is configured via `AddCaching(...)`.
- **Resilience public surface:** configure timeouts/breaker/retry/concurrency via `CachingBuilder.WithResilience(Action<CacheResilienceOptions>)` only. **`CacheResiliencePipelineBuilder` is not public** — Polly registry types are not part of the shipped contract (Option B).
- **Health checks:** optional Kubernetes-style split — `WithHealthChecks(splitLivenessReadiness: true)` registers `CachingLivenessHealthCheck` + `CachingHealthCheck` as `{name}-liveness` / `{name}-readiness` with tags `liveness` / `readiness`.
- **`ICacheKeyFactory` / `DefaultCacheKeyFactory`:** DI-resolvable key builder (mirrors `CacheKey.For`); register a custom `ICacheKeyFactory` **before** `AddCaching` to inject tenant/segment logic.
- **Performance (audit §3.3):** `PayloadEnvelope.Write` allocates the wire `byte[]` with **`GC.AllocateUninitializedArray`**; **`StableStringHash`** uses **`ArrayPool<byte>`** for large UTF-8 encodings (>512 B). **`ICacheService`** stays **`Task` / `Task<T>`** — a `ValueTask` migration was prototyped and reverted before ship: the alloc savings on synchronous in-memory hits did not justify the breaking-change cost across consumer code, mocking frameworks, and decorators in mixed Hybrid/Redis production workloads.
- **Schema hash (B5):** envelope schema hash uses `Type.FullName` + optional `[CacheSchema]` so **library/package version bumps do not invalidate Redis entries**; existing entries written with the older assembly-qualified hash may schema-drift **once** after upgrade.
- **KeyPrefix (B6):** **`':'` is no longer allowed inside `KeyPrefix`** — avoids ambiguous physical keys when routing inserts `':'` between prefix and user segment. Prefer `serviceName-environment` naming (e.g. `asm-api-dev`).
- **Breaking:** `ICacheSerializer.Deserialize<T>` now takes **`ReadOnlyMemory<byte>`** (was `ReadOnlySpan<byte>`). Custom serializers must update; `MessagePackCacheSerializer` no longer allocates via `ToArray()` on deserialize when paired with Redis envelope payloads (zero-copy path).

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
