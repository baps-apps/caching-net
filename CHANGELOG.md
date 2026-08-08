# Changelog

All notable changes to Caching.NET are documented in this file.

The project follows [Semantic Versioning](https://semver.org/).

## 3.0.0 — 2026-08-08

**Major redesign.** Caching.NET v3 replaces three hand-written cache implementations with a single
engine, exposes that engine's full operation contract as the cache API, and keeps registration,
configuration, security, connection management and observability under Caching.NET's own names.

There is **no compatibility shim**. Keeping the v2 surface would have preserved exactly the
limitation this release exists to remove — a four-method cache interface that hid fail-safe,
timeouts, eager refresh and factory context from applications.

Migration guide: [docs/MIGRATION-V2-TO-V3.md](docs/MIGRATION-V2-TO-V3.md). The release-gate review,
with the measured evidence behind every gate, is
[docs/audits/2026-08-08-v3.0.0-production-readiness-review.md](docs/audits/2026-08-08-v3.0.0-production-readiness-review.md).

### Added

- **Full cache operation surface.** Fail-safe with throttling, factory soft/hard timeouts,
  distributed soft/hard timeouts, eager refresh, factory execution context (ETag, `NotModified`,
  adaptive expiration), background distributed and backplane operations, auto-recovery, plugins,
  events, and per-entry options — all available to applications for the first time.
- **`ICacheProvider`** — Caching.NET-owned named-cache resolution (`Default`, `GetCache`,
  `GetCacheOrNull`, `CacheNames`, `GetGuard`). Built from an enumerable of registrations; no service
  locator, no static dictionary.
- **Named caches** — `AddCaching("name", …)`, resolvable by keyed injection or through
  `ICacheProvider`. Isolated by a cache-name key segment. Also declarable under
  `CacheOptions:NamedCaches`.
- **`ICacheGuard`** — key/tag limit validation and non-reversible key fingerprints.
- **Redis backplane** — cross-instance L1 invalidation in Hybrid mode, on by default via
  `UseHybrid(...)`, channel-scoped to the application prefix.
- **Tags in every mode.** v2 supported them only in Hybrid.
- **Environment and tenant key prefixes** alongside the application prefix.
- **Payload framing** — a one-byte format header validated before deserialization, so a poisoned or
  truncated Redis value becomes a miss rather than a parse of attacker-controlled bytes.
- **Bounded decompression** — `Serialization.Compression.MaximumDecompressedBytes`.
- **Payload size enforcement on read** as well as on write.
- **Engine-level key-length guard**, invoked per operation for calls using the configured defaults.
- **`CacheExtensions`** — `GetManyAsync`, `SetManyAsync`, `RemoveManyAsync`, `ExistsAsync`,
  `RefreshAsync`. Only operations the contract does not already provide; nothing renamed.
- **Startup summary log line** describing the resolved topology, with no endpoint or credential.
- **`CachingOptionsValidator`** — every failure reported at once, scoped to the cache name, each
  naming the property and the fix. Two of its rules are worth calling out:
  - `Entry.LocalExpiration` longer than `Entry.DistributedExpiration` is **rejected at startup** in
    Hybrid mode, comparing effective values so an unset duration is measured against
    `DefaultExpiration` rather than skipped. A longer local lifetime means the in-process copy
    outlives the authoritative Redis entry, so the instance keeps answering with data every other
    instance has already refetched.
  - Hybrid mode with the backplane disabled logs a **startup warning (event 3051)** naming the stale
    window being accepted. `UseHybrid(...)` enables the backplane; a cache bound from configuration
    does not. It stays a warning rather than a failure because a single-replica deployment has
    nothing to invalidate.
- **`CACHENET001` analyzer**, shipped inside the Caching.NET package. Warns wherever per-call entry
  options are constructed (`new FusionCacheEntryOptions()`) instead of derived from the cache
  (`cache.CreateEntryOptions(...)`). This is the one guarantee the library cannot enforce at run
  time — per-call options replace the cache's defaults, and in Redis mode that silently re-enables
  the local memory layer — so it is caught at build time. Consumers take no Roslyn dependency.
- **`CacheTelemetry.EngineMeterNames`, `CacheTelemetry.EngineActivitySourceNames` and
  `CacheTelemetry.EngineKeyAttributeName`.** Registering the engine's instrumentation is now a
  deliberate, separately named choice, because both halves of it cost something: the engine meters
  overlap the Caching.NET meter (one hit increments `caching.net.hits` *and* `fusioncache.cache.hit`),
  and the engine activity sources attach the **raw physical cache key** to every operation span as
  `fusioncache.operation.key`, which the engine offers no way to suppress. `MeterName` and
  `ActivitySourceName` alone are the recommended defaults; `EngineKeyAttributeName` is published so a
  collector can drop the key attribute when the extra span detail is wanted anyway. Pinned by
  `SpanKeyExposureTests`.
- **Public API baseline.** `PublicApiTests` compares the reflected public surface against
  `Api/PublicApi.approved.txt` and fails on any addition, removal or signature change, listing
  removals as breaking. Approve an intended change with `CACHINGNET_APPROVE_API=1`. Two companion
  tests assert that no `Internal` namespace is exported and that the only engine types in public
  signatures are `IFusionCache` and `FusionCacheEntryOptions`.
- **Chaos test suite** covering Redis unavailable at startup, runtime outage, restart, backplane
  loss, fail-safe activation, factory-timeout fallback, and log-storm suppression.
- **Redis authentication and TLS tests** against purpose-built containers: correct password round
  trips; a wrong password surfaces to the caller when distributed errors are requested and reports
  readiness as degraded otherwise; an untrusted TLS certificate is rejected under strict validation
  **and** under permissive validation (permissive relaxes a host-name mismatch and nothing else);
  a TLS rejection degrades the cache instead of taking the application down. Eight unit tests cover
  the certificate policy directly, including combined policy errors and one-time handshake logging.
- **Cross-process multi-pod tests.** `Caching.NET.Tests.Pod` is a console cache instance the suite
  launches as a separate OS process; seven tests exercise write visibility, L1 invalidation, remove,
  tag invalidation, clear, application isolation and pod restart across real processes rather than
  two service providers sharing one CLR.

### Changed

- **Cache API**: `ICacheService` → `IFusionCache`. See the migration guide for the call-site map.
- **Registration**: `AddCaching` and `CachingBuilder` keep their v2 names but are new types with new
  signatures — v2 call sites compile and then fail startup validation until they are updated.
- **Configuration section**: still `CacheOptions`, but restructured into `Entry`, `Resilience`,
  `Redis`, `Backplane`, `Serialization`, `Security`, `Observability`. A v2 section binds partially
  and is rejected at startup; it is not silently accepted.
- **`KeyPrefix` → `ApplicationPrefix`**, and the key layout gains optional environment, tenant and
  cache-name segments.
- **Metric names**: `cache.*` → `caching.net.*`. Dashboards and alerts must be updated.
- **Telemetry accessor**: `CacheInstruments` → `CacheTelemetry`, with `ActivitySourceNames` and
  `MeterNames` arrays that carry every source an application needs to register.
- **Redis mode no longer keeps entries in local memory.** Reads always consult Redis, so no instance
  can serve a value Redis has not confirmed. The in-process stampede locker is unaffected.
- **Jitter is absolute** (`Entry.JitterMaxDuration`) rather than proportional
  (`TtlJitterPercentage`).
- **Fail-safe is on by default**; a failing factory now returns a stale value where one exists.
- **`Enabled` is no longer hot-reloadable.** It is read once at registration.
- **Health checks**: `AddCachingHealthChecks` keeps its v2 name; readiness now reports
  Degraded rather than Unhealthy when only the distributed layer is down, and reports exception
  types rather than messages.
- **`ValidateCacheRegistration` → `ValidateCachingRegistration`**, which now resolves every
  registered cache.
- **Engine log output is re-categorised** under `Caching.NET`, so log filters never name the engine.
- **`Logging:LogLevel:Caching.NET` guidance is `Warning`.** `Information` on that category costs
  roughly one engine log line per cache operation; everything an operator needs during an incident is
  `Warning` or above.
- **`CacheKey.For<T>` puts generic arguments in the type segment.** In v2, `List<int>` and
  `List<string>` both produced `` List`1 ``, so two different types shared one key — a
  type-confusion bug, not just a collision. They now produce `List_Int32` and `List_String`. Keys for
  non-generic types are unchanged; keys for closed generics change, so those entries cold-start.

### Removed

- **`net8.0` and `net9.0` target frameworks.** The package targets `net10.0` only. Applications on
  .NET 8 or .NET 9 must stay on 2.2.0 or upgrade their target framework.
- `ICacheService`, `CacheOptions` (the class), `CacheCallOptions`, `CacheSerializerOptions`,
  `CacheServiceCallExtensions`, `CacheSchemaAttribute`.
- The v2 `CachingBuilder` and all four v2 `AddCaching` overloads. Both names are reused by v3 with
  different members and signatures.
- `ValidateCacheRegistration`.
- `RoutingCacheService`, `InMemoryCacheService`, `RedisCacheService`, `HybridCacheService`.
- `ICacheSerializer`, `JsonCacheSerializer`, `MessagePackCacheSerializer`, `PayloadEnvelope`,
  `PayloadEnvelopeReadResult`, `PayloadCompression`, schema-drift detection.
- `StripedLockManager`, `StaleEntryTracker`, `StaleRefreshThrottle`, `TtlJitter`,
  `RuntimeTypedCacheInvoker`, `DriftLogSampler`, `RedisConnectionRotator`, `StableTypeHash`,
  `StableStringHash`.
- `CacheResiliencePipelineBuilder`, `ResiliencePipelineNames`, and the whole Polly layer.
- `CacheInstruments` and every `cache.*` metric.
- Per-call `Mode` override, `Bypass` and `ForceRefresh` (use a named cache, explicit skip options, or
  `RefreshAsync`).
- Runtime-typed `GetAsync(string, Type, …)`.
- `RequireTagSupport`, `StripeLockCount`, `StaleRefreshConcurrency`, `KeyValidator`, `KeyTransformer`.
- NuGet dependencies: `Microsoft.Extensions.Caching.Hybrid`, `Polly`, `Polly.Extensions`,
  `Polly.RateLimiting`, `Microsoft.Extensions.Options.DataAnnotations`.

### Security

- System.Text.Json with no type-name handling, and MessagePack with the contractless resolver, are
  the only wire formats. No `BinaryFormatter`, no `NetDataContractSerializer`, no polymorphic type
  resolution from Redis payloads, and no switch that enables them.
- Corrupt-payload rejection before deserialization; bounded Brotli decompression.
- Payload size enforced in both directions.
- Permissive Redis TLS is rejected at startup unless TLS is actually enabled.
- Per-cache TLS validators replace the process-wide mutable validator.
- Tag values are excluded from logs, traces and metrics unless explicitly opted in.
- **No raw cache key reaches the logs unless `Security.AllowRawKeysInLogs` is enabled.** A cache key
  routinely embeds a user id, an email address or a tenant id. Caching.NET's own messages never carry
  one, and the logger adapter substitutes the `ICacheGuard.Fingerprint` digest into both the rendered
  message and the structured `CacheKey` property of the engine's per-operation lines — which an
  application could not otherwise filter out, since engine output is re-categorised under
  `Caching.NET`. Pinned by `EngineKeyRedactionTests`.
- Health output reports exception types only.
- `MessagePack` pinned to 3.1.7, above the 3.1.4 the serializer resolves transitively (three
  high-severity advisories). `Microsoft.OpenApi` pinned to 2.11.0 in the sample, above the
  vulnerable 2.0.0 resolved transitively by `Microsoft.AspNetCore.OpenApi`.

### Fixed

- **The readiness health check now detects a Redis outage in Hybrid mode.** Its probe read was
  served by L1 — the value its own write had just placed there — so it reported `Healthy` with Redis
  stopped, and never contacted the distributed layer even when Redis was up. The probe read now sets
  `SkipMemoryCacheRead`, and the probe write awaits the L2 operation with
  `ReThrowDistributedCacheExceptions`, when the cache has a distributed layer. Both are skipped in
  InMemory mode, where bypassing memory would make every probe a miss.
- **Probe entries no longer inherit the configured expirations.** `CreateEntryOptions` duplicates the
  cache defaults, so `Entry.DistributedExpiration` overrode the probe's own 10-second duration: a
  6-hour setting left the probe key in Redis for 6 hours. The probe now clears
  `DistributedCacheDuration`, `MemoryCacheDuration`, `JitterMaxDuration` and `EagerRefreshThreshold`,
  so its TTL is at most 10 seconds in every layer regardless of configuration.
- **Packaging.** The README is now inside the NuGet package, and `PackageReleaseNotes` no longer
  points at a repository path that does not resolve from a package page. The symbol package is no
  longer produced: `DebugType=embedded` already ships the PDB inside the assembly, so `IncludeSymbols`
  built a `.snupkg` containing no symbols.

### Known behavior

- **Redis mode's no-local-memory guarantee has a boundary.** The layer-skip flags live on the cache's
  default entry options, so a call passing a caller-constructed `FusionCacheEntryOptions` re-enables
  L1 for that call. `cache.CreateEntryOptions(...)` preserves them, and `CACHENET001` flags the other
  form at build time. Pinned by integration tests.
- **Unobserved task exceptions during a Redis outage.** With background distributed operations on
  (the default) the engine schedules distributed reads, writes and backplane publishes as background
  tasks; when Redis goes away some fault with nothing awaiting them and reach
  `TaskScheduler.UnobservedTaskException`. Measured at three per 50 operations across a full outage,
  plus one `SocketClosed` on `UNSUBSCRIBE` when a cache is disposed mid-outage. It does not crash the
  process and the same failures are already on `caching.net.redis.errors`, but a host that subscribes
  to that event will see cache-layer noise during an incident. OPERATIONS.md carries the filtering
  guidance; it is not fixable without the delegating wrapper v3 exists to remove.
- **Physical Redis TTL is `Duration + FailSafeMaxDuration`** when fail-safe is on — a one-minute entry
  occupies Redis for two hours under the defaults. Verified directly against `TTL` on a live key; see
  OPERATIONS.md's memory guidance.
- **`Observability.EnableMetrics: false` skips the event-hub subscription entirely** rather than
  installing handlers that return immediately.

## 2.2.0 — 2026-06-30

Additive minor release. No public API breaks. One operational behavior change for Hybrid L2 key naming (see Changed) that cold-starts the Hybrid distributed cache on upgrade.

### Added

- **`ClearAsync()`** (extension method on `ICacheService`) — clears all of this application's cache entries, scoped to the configured `KeyPrefix`. Per mode:
  - **InMemory** — clears the process memory cache (`MemoryCache.Clear()`).
  - **Redis** — cursor-based `SCAN {KeyPrefix}:*` + batched delete (never `FLUSHDB`; safe on a shared database).
  - **Hybrid** — logical invalidation via the reserved wildcard tag `"*"`; entries expire naturally.
  - No-op when caching is disabled or the backend cannot clear. New per-call surface lives on the extension/`IRoutingCacheService`, not on `ICacheService` (API-stability contract).
- **Hybrid tag-write wiring:** `CacheCallOptions.Tags` is now applied to the underlying `HybridCache` on `SetAsync` and `GetOrCreateAsync`. Previously tags were accepted by the API but dropped before reaching `HybridCache`, so `RemoveByTagAsync` had nothing to match. Tags now function end-to-end in Hybrid mode.

### Changed

- **`RemoveManyAsync` (Redis) uses `UNLINK` when the server supports it** (Redis 4.0+), falling back to `DEL` otherwise. `UNLINK` reclaims memory on a background thread (non-blocking), improving large-batch deletes. Server support is probed once and cached.
- **Hybrid L2 per-app isolation:** `KeyPrefix` is now applied as the Hybrid L2 Redis adapter `InstanceName` rather than at the routing layer. This namespaces **all** Hybrid L2 keys — entries *and* HybridCache's tag/wildcard invalidation markers — so one app's `ClearAsync`/`RemoveByTagAsync` no longer invalidates another app's entries on a shared Redis database. **Requires a unique `KeyPrefix` per application.**
  - **Migration / impact:** Hybrid L2 physical key names change (markers were previously unprefixed). On upgrade, existing Hybrid L2 entries become unreachable and repopulate on demand (one-time cold cache); orphans expire by TTL. InMemory and Redis modes are unchanged. Hybrid L1 (in-process) keys are now unprefixed (harmless — L1 is per-process); a per-call `Mode = InMemory` override in a Hybrid app writes an unprefixed entry.

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
- `docs/IMPLEMENTATION.md` – implementation details, modes, configuration, telemetry. (Superseded in v3 by `docs/ARCHITECTURE.md`.)
- [docs/OPERATIONS.md](docs/OPERATIONS.md) – production runbooks (when present).

---

