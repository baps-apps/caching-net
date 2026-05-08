# Caching.NET v2 — Post-Release Codebase Audit

> **HISTORICAL.** Findings A3 / A4 and any other reference to `Caching.NET.Analyzers` / `Caching.NET.Tests.Analyzers` / `CN0001` describe state at audit time. The analyzer project, test project, and `CN0001` rule were **removed in v2.1.0**.

**Date:** 2026-05-06 (revalidated 2026-05-07)
**Branch:** `vpatel/v2`
**Scope:** Deep review of `src/Caching.NET` and `src/Caching.NET.Analyzers` against the v2.0.0 spec ([2026-05-05-v2-amazon-scale-design.md](../specs/2026-05-05-v2-amazon-scale-design.md)). Surfaces bugs in the shipped implementation and improvement opportunities the spec did not cover.
**Method:** Three parallel deep-read passes (Services + Internal; Resilience + Serialization + Telemetry + Health + Keys; DI + Builder + Options + Validation), cross-checked against shipped behavior and tests.
**Result:** Initial pass captured six targeted fixes plus a broader backlog; follow-up work landed many backlog items (see §2). §3 lists **remaining** triage only.

**2026-05-07 revalidation:** All §1 + §2 claims re-verified against current source on `vpatel/v2` (file:line evidence in §6). Two test regressions surfaced and fixed in the same pass:

- `PayloadEnvelope.TryRead` set the out `payload` slice **before** verifying the trailer checksum, so a checksum-mismatch caller still received a non-empty span. Fixed in [src/Caching.NET/Serialization/PayloadEnvelope.cs:113-118](../../../src/Caching.NET/Serialization/PayloadEnvelope.cs#L113-L118) — assignment moved past the `XxHash32` check; failure path leaves `payload = default`.
- `CacheOptionsValidationTests.AddCaching_WithInvalidPayloadCompressionThreshold_Throws` asserted at `services.AddCaching(...)` time, but `PayloadCompressionThresholdBytes` is enforced by `IValidateOptions<CacheOptions>` (deferred to options resolution), not by an inline registration throw. Updated to build the provider and assert `OptionsValidationException` on `IOptions<CacheOptions>.Value`.

**2026-05-07 P3 revert:** The `ValueTask` migration recorded under §2 P3 was reverted before ship. Cost/benefit re-examined: alloc savings on synchronous in-memory hits did not justify the breaking-change burden across consumer code, mocking frameworks, and decorators in mixed Hybrid/Redis production workloads. `ICacheService` ships as `Task` / `Task<T>`; `InMemoryCacheService` returns `Task.FromResult(...)` on sync hits. `IAsyncDisposable.DisposeAsync` (e.g. `RoutingCacheService`, `RedisConnectionRotator`) and Polly predicate callbacks remain `ValueTask` (interface contracts). `Testcontainers.IAsyncLifetime.DisposeAsync()` retains `.AsTask()` adapters in integration fixtures.

---

## 1. Fixes Landed in This Branch

| # | File | Severity | Change |
|---|------|----------|--------|
| 1 | `src/Caching.NET/Services/RoutingCacheService.cs` (`ScheduleBackgroundRefresh`) | BUG | Background stale-refresh replaced silent `catch {}` with structured log + `cache.errors{error_kind}` metric via new `ClassifyError`. Bounded the stripe-lock `WaitAsync` with the configured factory timeout so a stuck holder cannot pin a throttle slot indefinitely. Moved telemetry inc/dec inside `Task.Run` to avoid leak if the scheduler refuses the task. |
| 2 | `src/Caching.NET/Internal/CacheLogMessages.cs` | BUG (paired) | Added `LoggerMessage` source-gen entries 1111 (`StaleRefreshFailed`) and 1112 (`StaleRefreshLockTimeout`). |
| 3 | `src/Caching.NET/Keys/CacheKey.cs` | BUG | `CacheKey.For<T>(id)` now formats `IFormattable` ids with `CultureInfo.InvariantCulture`. Previously, locale-sensitive types (`DateTime`, `decimal`, `double`) produced different keys on `de-DE` vs `en-US` for the same logical id — split-brain cache across hosts. |
| 4 | `src/Caching.NET/Telemetry/CacheInstruments.cs` | MINOR | `Meter` and `ActivitySource` now report the runtime-resolved `AssemblyInformationalVersion` instead of the frozen `"2.0.0"` constant. Public `const Version` preserved for source/binary compatibility. |
| 5 | `src/Caching.NET/Services/RoutingCacheService.cs` (`SetManyAsync`, `RemoveManyAsync`) | MINOR (perf) | Skip the dictionary copy when `KeyPrefix` is empty; early-return on empty input/prefixed list. |
| 6 | `src/Caching.NET/Validation/CacheOptionsValidator.cs` + `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs` + `tests/Caching.NET.Tests/Builder/CachingBuilderTests.cs` | BUG (contract) | **`Enabled=false` now means no caching at any level.** Validator short-circuits to `Success`. Registration skips MemoryCache, `InMemoryCacheService`, Redis multiplexer, `RedisCacheService`, `HybridCacheService`, TLS validator, default `JsonCacheSerializer`, and the Polly resilience pipeline registry. `RoutingCacheService` is still registered and short-circuits every call to the factory. Health-check registration moved outside the gate (the check itself returns `Healthy` when disabled). Switching `Enabled` between `true` and `false` no longer requires source changes — the same registration code handles both. |

### Verification

- `dotnet build -c Release` — clean across `net8.0`, `net9.0`, `net10.0` (revalidated 2026-05-07).
- `Caching.NET.Tests` — **186 / 186** on each of `net8.0`, `net9.0`, `net10.0` (revalidated 2026-05-07; includes KeyPrefix colon rejection, `Enabled=false` wiring, hybrid value-type miss path, health-check multiplexer behavior, liveness/readiness checks, `ICacheKeyFactory` registration, payload-envelope checksum, payload-compression threshold validation, and other audit regressions).
- `Caching.NET.Tests.Analyzers` — **9 / 9** (revalidated 2026-05-07).
- `Caching.NET.Tests.Properties` — **11 / 11** on each of `net8.0`, `net9.0`, `net10.0` (revalidated 2026-05-07).
- `Caching.NET.Tests.Chaos` — **4 / 4** on each of `net8.0`, `net9.0`, `net10.0` (revalidated 2026-05-07).
- `Caching.NET.Tests.Integration` — **10 / 10** on `net10.0` when Redis (Testcontainers) is available.

### Behavioral Notes for `Enabled=false`

- `ICacheService` is resolvable; every operation short-circuits (`GetOrCreateAsync` runs the factory, all writes/removes are no-ops).
- `RedisConnectionString` is no longer required even when `Mode` is `Redis` or `Hybrid`. The toggle alone disables all backend wiring.
- Hot-reloading `Enabled` from `false` → `true` at runtime does **not** lazily register backends. Mode and connection string are startup-only (per `CLAUDE.md`); a process restart is required to bring caching online. Document or enforce this explicitly if hot-enable is desired.
- `app.Services.ValidateCacheRegistration()` succeeds with `Enabled=false`.
- Health-check endpoint reports `Healthy` with description `"Caching is disabled via configuration."` when bound and `Enabled=false`.

---

## 2. Follow-up fixes (post-audit; implemented in branch)

The backlog items below from the original audit pass have been **addressed in code** (see consumer docs: [INTERNALS.md](../../INTERNALS.md), [HEALTH-CHECKS.md](../../HEALTH-CHECKS.md), [TELEMETRY.md](../../TELEMETRY.md), [OPERATIONS.md](../../OPERATIONS.md)).

| ID | Resolution |
|----|------------|
| **B7** | `CachingHealthCheck` requires `IConnectionMultiplexer` for Redis/Hybrid, checks `IsConnected`, **PINGs** before `GetOrCreateAsync`. |
| **B8** | Probe key suffix: `Environment.MachineName` + `Environment.ProcessId`. |
| **B10** | `PayloadEnvelope.TryRead` requires declared length **exactly** `wire.Length - HeaderSize` (trailing bytes → invalid). |
| **R1** | `IsTransient` includes `SocketException`, `IOException`, `RedisServerException` (`LOADING`/`READONLY`); excludes `OperationCanceledException`. |
| **R2** | Optional `ConcurrencyLimiter` on Redis pipelines via `CacheResilienceOptions`. |
| **R3** | `cache.serialize.duration` / `cache.deserialize.duration` histograms (`cache.format` tag). |
| **R7** | `DriftLogSampler` — at most one drift log per `(driftKind, keyFingerprint)` per minute. |
| **R8** | Retry: exponential + jitter, **Delay 50 ms**, **MaxDelay 1 s**. |
| **V1** | Validator enforces prefix + `':'` + **≥32** chars remaining for user keys vs `MaximumKeyLength`. |
| **V3** | Validator parses `RedisConnectionString` with `ConfigurationOptions.Parse` when present. |
| **P4** | `RemoveManyAsync` (multiplexer path): `cache.removes` += Redis **actual** delete count. |
| **P5** | `PayloadEnvelope.Write(..., IBufferWriter<byte>)`. |
| **A7–A9** | `Enable()`, `UseDevelopmentDefaults()`, `UseProductionDefaults()`, `WithKeyValidator`, `WithKeyTransformer`. |
| **B1** | `StaleEntryTracker`: periodic prune on registration watermark + hard cap + stale-until trim. |
| **B2** | `RoutingCacheService`: implements `IAsyncDisposable` / `IDisposable` for background refresh teardown. |
| **B3** | `HybridCacheService.GetAsync`: value-type path uses `HybridValueBox<T>?` so misses are not cached as `default(T)`. |
| **B4** | `HybridCacheService.ExistsAsync`: uses `IDistributedCache` when available before heavier paths. |
| **B9** | `RedisConnectionRotator`: 100 ms drain before disposing rotated multiplexer; disposal exceptions logged. |
| **A3** | `CardinalityAnalyzer`: extended constant-key detection (e.g. `KeyValuePair.Create`). |
| **A4** | `CardinalityAnalyzer`: high-cardinality names in **string-literal** `ILogger` templates and `BeginScope(string)` (same forbidden set as metric tags). |
| **A6** | `CachingBuilder` must be configured via `AddCaching(...)` (no direct constructor path). |
| **P2** | `InMemoryCacheService` batch paths use synchronous cache operations where applicable. |
| **P1** | `InMemoryCacheService`: reusable static `PostEvictionCallbackRegistration` for eviction callbacks. |
| **A10** | `ForceRefresh` documented on `CacheCallOptions` and set-extension overloads (honored only on get paths). |
| **V2** | `MaximumKeyLength` enforced on full prefixed key after routing; docs/options XML describe prefixed semantics. |
| **B5** | `StableTypeHash` uses `Type.FullName` (+ optional `[CacheSchema]`) instead of `AssemblyQualifiedName` — stable across package bumps. |
| **B6** | `KeyPrefix` validation forbids `':'` inside the prefix; regex updated to `^[a-zA-Z0-9][a-zA-Z0-9._-]*$`. |
| **A1** | `ICacheSerializer.Deserialize<T>(ReadOnlyMemory<byte>)`; `MessagePackCacheSerializer` uses MessagePack’s ROM path (no `ToArray()`); Redis maps envelope payload via `byte[].AsMemory(...)`. |
| **A2** | **Option B (v2.0.0):** `WithResilience(Action<CacheResilienceOptions>)` — library-owned options only; `CacheResiliencePipelineBuilder` and `BuildDefaultRegistry` → `Polly.Registry.ResiliencePipelineRegistry<string>` are **internal** (not shipped public API). |
| **R6** | Optional **liveness vs readiness** registration: `CachingLivenessHealthCheck` + `CachingHealthCheck` via `WithHealthChecks(..., splitLivenessReadiness: true)` or `AddCachingHealthChecks(..., splitLivenessReadiness: true)` with tags `liveness` / `readiness`. |
| **A5** | **`CachingHealthCheck`** / **`CachingLivenessHealthCheck`** are **internal** — register via `WithHealthChecks()` / `AddCachingHealthChecks()` only; not constructible from consuming assemblies. |
| **A11** | **`ICacheKeyFactory`** + **`DefaultCacheKeyFactory`** registered via `TryAddSingleton` with `AddCaching`; consumers inject for tenant/extra segments or register a custom factory **before** `AddCaching`. |
| **P3** | **Evaluated and reverted before ship (2026-05-07).** A `ValueTask` migration of `ICacheService` + extensions + service implementations + tests was prototyped on this branch. Reverted because the alloc savings on synchronous in-memory hits did not justify the breaking-change cost across consumer code, mocks, and decorators in mixed Hybrid/Redis workloads. `ICacheService` ships as `Task` / `Task<T>`. |
| **R4** | `PayloadEnvelope` now appends a payload checksum trailer (`XxHash32`) and validates it on `TryRead`; checksum mismatches are treated as `EnvelopeInvalid` misses. |
| **R5** | Optional Redis payload compression landed via `CacheOptions.EnablePayloadCompression` + `PayloadCompressionThresholdBytes`; compressed envelopes set format-id high bit and are transparently decompressed on read. |
| **P5** | **`PayloadEnvelope.Write` → `byte[]`** uses **`GC.AllocateUninitializedArray<byte>`** so the wire buffer is not zero-filled before `Write` overwrites it. |
| **P6** | **`StableStringHash`** (`Compute` / `Compute64`): UTF-8 buffers **> 512 B** use **`ArrayPool<byte>.Shared`** instead of **`new byte[byteCount]`** (same hash values). |

---

## 3. Backlog — Remaining improvements

Severities: **BUG** (correctness), **IMPORTANT** (design risk / latent bug), **MINOR** (cleanup), **IMPROVEMENT** (nice-to-have).

### 3.1 Correctness & Safety

*(No open items in this audit as of 2026-05-06. A post-audit re-check discovered new uncommitted findings and tracking now continues in [`2026-05-07-v2-post-uncommitted-reaudit.md`](./2026-05-07-v2-post-uncommitted-reaudit.md).)*

### 3.2 API & Packaging

*(No open items.)*

### 3.3 Performance

*(No open items — P3, P5, and P6 addressed in §2.)*

### 3.4 Resilience & Observability Gaps

| # | Severity | Location | Problem | Suggested Fix |
|---|----------|----------|---------|---------------|
*(No open items.)*

### 3.5 Validation / docs clarity

*(No open items — `MaximumKeyLength` is documented as a full prefixed-key cap in code and consumer docs.)*

---

## 4. Suggested Triage

### v2.0.0 status

- Residual analyzer hardening landed: CN0001 now inspects constant string template arguments (including `const` variables) for logger message templates / `BeginScope(string)` placeholders.
- No remaining A6 public-surface footgun items tracked in this branch audit.

### Optional

- Benchmark and tune compression thresholds per workload.

---

## 5. References

- Spec: [`docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md`](../specs/2026-05-05-v2-amazon-scale-design.md)
- v2 plans: [`docs/superpowers/plans/2026-05-05-v2-p0-foundations.md`](../plans/2026-05-05-v2-p0-foundations.md), [`P2`](../plans/2026-05-06-v2-p2-api-expansion.md), [`P3`](../plans/2026-05-06-v2-p3-hardening-ops.md)
- Public API compatibility: NuGet package validation enabled on [`src/Caching.NET/Caching.NET.csproj`](../../../src/Caching.NET/Caching.NET.csproj) (`EnablePackageValidation`) — breaking API changes must be deliberate when packing/releasing
- CHANGELOG: [`CHANGELOG.md`](../../../CHANGELOG.md)

**Verification:** Re-run `dotnet build` / `dotnet test` on the target branch before release; integration suite remains environment-dependent (Testcontainers).

---

## 6. 2026-05-07 revalidation evidence

Each §1 + §2 claim re-verified against the current source tree. Status legend: ✅ landed and matches description.

| ID | Status | Evidence |
| -- | ------ | -------- |
| §1.1 `RoutingCacheService.ScheduleBackgroundRefresh` | ✅ | [src/Caching.NET/Services/RoutingCacheService.cs:493-545](../../../src/Caching.NET/Services/RoutingCacheService.cs#L493-L545) — structured log + `ClassifyError`, bounded `WaitAsync(lockTimeout)` (line 502), telemetry inc/dec inside `Task.Run` (lines 495, 540). |
| §1.2 `CacheLogMessages` 1111 / 1112 | ✅ | [src/Caching.NET/Internal/CacheLogMessages.cs:71-77](../../../src/Caching.NET/Internal/CacheLogMessages.cs#L71-L77). |
| §1.3 `CacheKey.For<T>` invariant culture | ✅ | [src/Caching.NET/Keys/CacheKey.cs:19-20](../../../src/Caching.NET/Keys/CacheKey.cs#L19-L20). |
| §1.4 Runtime-resolved `AssemblyInformationalVersion` | ✅ | [src/Caching.NET/Telemetry/CacheInstruments.cs:24-42](../../../src/Caching.NET/Telemetry/CacheInstruments.cs#L24-L42). |
| §1.5 `SetManyAsync` / `RemoveManyAsync` empty-prefix fast paths | ✅ | [src/Caching.NET/Services/RoutingCacheService.cs:446-478](../../../src/Caching.NET/Services/RoutingCacheService.cs#L446-L478). |
| §1.6 `Enabled=false` skips backend DI | ✅ | [src/Caching.NET/Extensions/ServiceCollectionExtensions.cs:182-280](../../../src/Caching.NET/Extensions/ServiceCollectionExtensions.cs#L182-L280); validator short-circuit [src/Caching.NET/Validation/CacheOptionsValidator.cs:30](../../../src/Caching.NET/Validation/CacheOptionsValidator.cs#L30). |
| B1 `StaleEntryTracker` prune | ✅ | [src/Caching.NET/Internal/StaleEntryTracker.cs:32-34](../../../src/Caching.NET/Internal/StaleEntryTracker.cs#L32-L34). |
| B2 `RoutingCacheService` `IAsyncDisposable`/`IDisposable` | ✅ | [src/Caching.NET/Services/RoutingCacheService.cs:599-623](../../../src/Caching.NET/Services/RoutingCacheService.cs#L599-L623). |
| B3 Hybrid value-type miss path | ✅ | [src/Caching.NET/Services/HybridCacheService.cs:155-171](../../../src/Caching.NET/Services/HybridCacheService.cs#L155-L171). |
| B4 Hybrid `ExistsAsync` distributed-cache fast path | ✅ | [src/Caching.NET/Services/HybridCacheService.cs:201-206](../../../src/Caching.NET/Services/HybridCacheService.cs#L201-L206). |
| B5 `StableTypeHash` on `Type.FullName` + `[CacheSchema]` | ✅ | [src/Caching.NET/Internal/StableTypeHash.cs:20-28](../../../src/Caching.NET/Internal/StableTypeHash.cs#L20-L28). |
| B6 `KeyPrefix` colon ban | ✅ | [src/Caching.NET/Validation/CacheOptionsValidator.cs:21,43-51](../../../src/Caching.NET/Validation/CacheOptionsValidator.cs#L21). |
| B7 Health probe Redis `PING` | ✅ | [src/Caching.NET/Health/CachingHealthProbe.cs:87](../../../src/Caching.NET/Health/CachingHealthProbe.cs#L87). |
| B8 Probe key suffix (`MachineName` + `ProcessId`) | ✅ | [src/Caching.NET/Health/CachingHealthProbe.cs:15-16](../../../src/Caching.NET/Health/CachingHealthProbe.cs#L15-L16). |
| B9 `RedisConnectionRotator` 100ms drain | ✅ | [src/Caching.NET/Internal/RedisConnectionRotator.cs:80](../../../src/Caching.NET/Internal/RedisConnectionRotator.cs#L80). |
| B10 `PayloadEnvelope.TryRead` rejects trailing bytes | ✅ | [src/Caching.NET/Serialization/PayloadEnvelope.cs:104](../../../src/Caching.NET/Serialization/PayloadEnvelope.cs#L104). |
| R1 `IsTransient` exception set | ✅ | [src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs:101-125](../../../src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs#L101-L125). |
| R2 Optional `ConcurrencyLimiter` | ✅ | [src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs:42-49](../../../src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs#L42-L49) + [CacheResilienceOptions.cs:31](../../../src/Caching.NET/Resilience/CacheResilienceOptions.cs#L31). |
| R3 Serialize/deserialize histograms | ✅ | [src/Caching.NET/Telemetry/CacheInstruments.cs:58-62](../../../src/Caching.NET/Telemetry/CacheInstruments.cs#L58-L62). |
| R4 Envelope `XxHash32` trailer | ✅ (out-param leak fixed) | [src/Caching.NET/Serialization/PayloadEnvelope.cs:73,86,113-118](../../../src/Caching.NET/Serialization/PayloadEnvelope.cs#L113-L118). |
| R5 Compression threshold + flag | ✅ | [src/Caching.NET/Options/CacheOptions.cs:92,98](../../../src/Caching.NET/Options/CacheOptions.cs#L92). |
| R6 Liveness/readiness split | ✅ | [src/Caching.NET/Extensions/ServiceCollectionExtensions.cs:84-93](../../../src/Caching.NET/Extensions/ServiceCollectionExtensions.cs#L84-L93) + [Health/CachingLivenessHealthCheck.cs](../../../src/Caching.NET/Health/CachingLivenessHealthCheck.cs). |
| R7 `DriftLogSampler` per-fingerprint per minute | ✅ | [src/Caching.NET/Internal/DriftLogSampler.cs:12-33](../../../src/Caching.NET/Internal/DriftLogSampler.cs#L12-L33). |
| R8 Retry 50ms / 1s + jitter | ✅ | [src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs:84-94](../../../src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs#L84-L94). |
| V1 Validator ≥32 user-key chars | ✅ | [src/Caching.NET/Validation/CacheOptionsValidator.cs:80-90](../../../src/Caching.NET/Validation/CacheOptionsValidator.cs#L80-L90). |
| V2 `MaximumKeyLength` on prefixed key | ✅ | [src/Caching.NET/Services/RoutingCacheService.cs:83-89](../../../src/Caching.NET/Services/RoutingCacheService.cs#L83-L89). |
| V3 Validator parses Redis connection string | ✅ | [src/Caching.NET/Validation/CacheOptionsValidator.cs:59-72](../../../src/Caching.NET/Validation/CacheOptionsValidator.cs#L59-L72). |
| P1 In-memory eviction-callback reuse | ✅ | [src/Caching.NET/Services/InMemoryCacheService.cs:21-22](../../../src/Caching.NET/Services/InMemoryCacheService.cs#L21-L22). |
| P2 In-memory batch sync ops | ✅ | [src/Caching.NET/Services/InMemoryCacheService.cs:88-95](../../../src/Caching.NET/Services/InMemoryCacheService.cs#L88-L95). |
| P3 `ICacheService` `ValueTask` migration | ↩️ reverted 2026-05-07 | [src/Caching.NET/Abstractions/ICacheService.cs](../../../src/Caching.NET/Abstractions/ICacheService.cs) — interface and all implementations remain `Task`/`Task<T>`. See revalidation note below. |
| P4 `RemoveManyAsync` actual delete count | ✅ | [src/Caching.NET/Services/RedisCacheService.cs:533-535](../../../src/Caching.NET/Services/RedisCacheService.cs#L533-L535). |
| P5 `IBufferWriter` overload + `AllocateUninitializedArray` | ✅ | [src/Caching.NET/Serialization/PayloadEnvelope.cs:42,48](../../../src/Caching.NET/Serialization/PayloadEnvelope.cs#L42). |
| P6 `StableStringHash` `ArrayPool` >512B | ✅ | [src/Caching.NET/Internal/StableStringHash.cs:23-40](../../../src/Caching.NET/Internal/StableStringHash.cs#L23-L40). |
| A1 `ICacheSerializer.Deserialize<T>(ReadOnlyMemory<byte>)` | ✅ | [src/Caching.NET/Serialization/ICacheSerializer.cs:16](../../../src/Caching.NET/Serialization/ICacheSerializer.cs#L16) + [MessagePackCacheSerializer.cs:36](../../../src/Caching.NET/Serialization/MessagePackCacheSerializer.cs#L36). |
| A2 `WithResilience(...)`; pipeline builder internal | ✅ | [src/Caching.NET/CachingBuilder.cs:190-199](../../../src/Caching.NET/CachingBuilder.cs#L190-L199); [CacheResiliencePipelineBuilder.cs:20](../../../src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs#L20). |
| A3 Analyzer `KeyValuePair.Create` | ✅ | [src/Caching.NET.Analyzers/CardinalityAnalyzer.cs:197-200](../../../src/Caching.NET.Analyzers/CardinalityAnalyzer.cs#L197-L200). |
| A4 Analyzer ILogger + `BeginScope` | ✅ | [src/Caching.NET.Analyzers/CardinalityAnalyzer.cs:39-42,149-152](../../../src/Caching.NET.Analyzers/CardinalityAnalyzer.cs#L39-L42). |
| A5 Health-check types internal | ✅ | [src/Caching.NET/Health/CachingHealthCheck.cs:15](../../../src/Caching.NET/Health/CachingHealthCheck.cs#L15) + [Health/CachingLivenessHealthCheck.cs:14](../../../src/Caching.NET/Health/CachingLivenessHealthCheck.cs#L14). |
| A6 `CachingBuilder` DI-only construction | ✅ | [src/Caching.NET/Extensions/ServiceCollectionExtensions.cs:139](../../../src/Caching.NET/Extensions/ServiceCollectionExtensions.cs#L139). |
| A7-A9 Builder presets + key hooks | ✅ | [src/Caching.NET/CachingBuilder.cs:246-312](../../../src/Caching.NET/CachingBuilder.cs#L246-L312). |
| A10 `ForceRefresh` doc | ✅ | [src/Caching.NET/Options/CacheCallOptions.cs:28](../../../src/Caching.NET/Options/CacheCallOptions.cs#L28). |
| A11 `ICacheKeyFactory` `TryAddSingleton` | ✅ | [src/Caching.NET/Extensions/ServiceCollectionExtensions.cs:265](../../../src/Caching.NET/Extensions/ServiceCollectionExtensions.cs#L265). |
