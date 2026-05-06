# Caching.NET v2 — Post-Release Codebase Audit

**Date:** 2026-05-06
**Branch:** `vpatel/v2`
**Scope:** Deep review of `src/Caching.NET` and `src/Caching.NET.Analyzers` against the v2.0.0 spec ([2026-05-05-v2-amazon-scale-design.md](../specs/2026-05-05-v2-amazon-scale-design.md)). Surfaces bugs in the shipped implementation and improvement opportunities the spec did not cover.
**Method:** Three parallel deep-read passes (Services + Internal; Resilience + Serialization + Telemetry + Health + Keys; DI + Builder + Options + Validation), cross-checked against shipped behavior and tests.
**Result:** 6 fixes landed in this branch. 33 backlog items captured below.

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

- `dotnet build -c Release` — clean across `net8.0`, `net9.0`, `net10.0`.
- `Caching.NET.Tests` — 164 / 164 (added `AddCaching_TogglingEnabledWithSameSampleConfig_RoundTrips` which mirrors the sample's Hybrid + tags + health-check setup with no Redis available).
- `Caching.NET.Tests.Analyzers` — 4 / 4.
- `Caching.NET.Tests.Properties` — 11 / 11.
- `Caching.NET.Tests.Chaos` — 4 / 4.
- Integration suite (`Caching.NET.Tests.Integration`) not exercised — Testcontainers Redis required.

### Behavioral Notes for `Enabled=false`

- `ICacheService` is resolvable; every operation short-circuits (`GetOrCreateAsync` runs the factory, all writes/removes are no-ops).
- `RedisConnectionString` is no longer required even when `Mode` is `Redis` or `Hybrid`. The toggle alone disables all backend wiring.
- Hot-reloading `Enabled` from `false` → `true` at runtime does **not** lazily register backends. Mode and connection string are startup-only (per `CLAUDE.md`); a process restart is required to bring caching online. Document or enforce this explicitly if hot-enable is desired.
- `app.Services.ValidateCacheRegistration()` succeeds with `Enabled=false`.
- Health-check endpoint reports `Healthy` with description `"Caching is disabled via configuration."` when bound and `Enabled=false`.

---

## 2. Backlog — Improvements Not Covered by the Spec

Severities: **BUG** (correctness), **IMPORTANT** (design risk / latent bug), **MINOR** (cleanup), **IMPROVEMENT** (nice-to-have).

### 2.1 Correctness & Safety

| # | Severity | Location | Problem | Suggested Fix |
|---|----------|----------|---------|---------------|
| B1 | IMPORTANT | `Internal/StaleEntryTracker.cs` | Unbounded `ConcurrentDictionary`. Entries are pruned only on lazy reads; workloads that register stale metadata for many one-shot keys leak unbounded. | Timer-driven sweep, or piggyback prune when register count crosses a watermark. |
| B2 | IMPORTANT | `Services/RoutingCacheService.cs` | Singleton is not `IAsyncDisposable`. Background `Task.Run` refreshes may still touch `_redis`/`_inMemory` after host disposal — use-after-dispose risk. | Hold internal CTS, link into background factories, await in-flight on `DisposeAsync`. |
| B3 | IMPORTANT | `Services/HybridCacheService.cs` (`GetAsync`) | Uses `HybridCache.GetOrCreateAsync` with `default(T)!` factory. For value types this caches `default(T)` (e.g. `0`) on miss, polluting the cache with sentinel values. | Use a non-caching read path or sentinel-aware wrapper. |
| B4 | IMPORTANT | `Services/HybridCacheService.cs` (`ExistsAsync`) | Probes presence via full `GetAsync<object>` deserialization. | Native exists path, or document the cost. |
| B5 | IMPORTANT | `Internal/StableTypeHash.cs:14` | Hash uses `AssemblyQualifiedName`, which embeds the assembly version. Every package bump invalidates the entire cache as `cache.schema_drift`. | Hash `Type.FullName` only; expose explicit schema versioning via `[CacheSchema("v3")]` opt-in. |
| B6 | IMPORTANT | `Services/RoutingCacheService.cs` (`PrependPrefix`) | Single-`:` separator allows keyspace collision: `KeyPrefix="foo"` + key `"bar:x"` collides with `KeyPrefix="foo:bar"` + key `"x"`. | Reject `:` in `KeyPrefix` (validator), or use a non-printable separator. |
| B7 | BUG | `Health/CachingHealthCheck.cs` | Probe writes a real entry through `RoutingCacheService`. With `FailOpen=true` a dead Redis returns Healthy because routing swallows the failure and runs the factory. False-negative on outage; pollutes prod cache. | Use `CacheCallOptions.BypassCache=true`, or call multiplexer `IsConnected` / `PING` directly when mode is Redis or Hybrid. |
| B8 | IMPORTANT | `Health/CachingHealthCheck.cs` | Probe key is fixed (`caching-net:health:probe`). Multiple replicas thrash the same key, inflating breaker windows. | Suffix with `Environment.MachineName` or per-process Guid. |
| B9 | IMPORTANT | `Internal/RedisConnectionRotator.cs:78` | `_ = ad.DisposeAsync().AsTask()` — old multiplexer disposal exception is unobserved, and disposal is not awaited before rotation returns. In-flight commands on the old multiplexer may still be processing. | Configurable drain delay before disposal; observe exceptions. |
| B10 | MINOR | `Serialization/PayloadEnvelope.cs:59` | Length check uses `>` instead of `!=`. A trailing-garbage entry passes; a truncated entry can silently parse a partial payload. | Tighten to `len != (uint)(wire.Length - HeaderSize)` once we are sure no caller appends framing bytes. |

### 2.2 API & Packaging

| # | Severity | Location | Problem | Suggested Fix |
|---|----------|----------|---------|---------------|
| A1 | IMPORTANT | `Serialization/MessagePackCacheSerializer.cs:37` | `bytes.ToArray()` is required because `ICacheSerializer.Deserialize` takes `ReadOnlySpan<byte>` while MessagePack consumes `ReadOnlySequence`/`ReadOnlyMemory`. Hot-path heap allocation per read. | Migrate `ICacheSerializer` to `ReadOnlyMemory<byte>` in v3; pass a pinned-array slice from `PayloadEnvelope`. |
| A2 | IMPORTANT | `CachingBuilder.cs` (`WithResilience`) | Public surface exposes raw Polly v8 types (`ResiliencePipelineRegistry<string>`, `RetryStrategyOptions`). Locks our public API to a Polly major. | Wrap behind a `CacheResilienceOptions` POCO; map internally. |
| A3 | IMPORTANT | `Caching.NET.Analyzers/CardinalityAnalyzer.cs` | Only matches `new KeyValuePair<…>("key", …)` literal syntax. Misses `KeyValuePair.Create(...)`, tuple syntax, collection-expression syntax, and variable-bound keys — easy to bypass. | Use `SemanticModel.GetConstantValue` on the first KVP argument. |
| A4 | IMPORTANT | `Caching.NET.Analyzers/CardinalityAnalyzer.cs` | Only enforces metric tags. Forbidden keys (`key`, `tenant`, `user_id`) leak through `ILogger.BeginScope` and structured-log placeholders. | Extend rule to log scopes / message templates. |
| A5 | MINOR | `PublicAPI.Shipped.txt` | `Caching.NET.Health.CachingHealthCheck` is shipped public. Users may instantiate directly, bypassing DI. | Mark `internal` in v3 or add factory only. |
| A6 | MINOR | `CachingBuilder.cs` | Public parameterless ctor (`PublicAPI.Shipped.txt:15`) leaves `_services` null; most fluent methods (`WithSerializer`, `WithResilience`, etc.) throw at runtime. Footgun. | `[EditorBrowsable(Never)]` now; remove in v3. |
| A7 | IMPROVEMENT | `CachingBuilder.cs` | No `Enable()` companion to `Disable()`. Once `Enabled=false` is in config, no fluent way to re-enable for tests/overrides. | Add `Enable()`. |
| A8 | IMPROVEMENT | `CachingBuilder.cs` | No environment-aware preset. | Add `UseDevelopmentDefaults()` / `UseProductionDefaults()`. |
| A9 | IMPROVEMENT | `CachingBuilder.cs` | No custom key validation hook. | `WithKeyValidator(Func<string,bool>)` / `WithKeyTransformer`. |
| A10 | IMPROVEMENT | `Options/CacheCallOptions.cs` | `ForceRefresh` is silently ignored when applied to `SetAsync` extension overloads. | XML-doc the constraint, or split into two derived options structs. |
| A11 | IMPROVEMENT | `Keys/CacheKey.cs` | Static factory only. Consumers needing tenant-injected prefixes must wrap. | Provide `ICacheKeyFactory` DI service alongside the static API. |

### 2.3 Performance

| # | Severity | Location | Problem | Suggested Fix |
|---|----------|----------|---------|---------------|
| P1 | MINOR | `Services/InMemoryCacheService.cs` (`SetAsync`) | Allocates `MemoryCacheEntryOptions` + `PostEvictionCallbackRegistration` per call. Registration wrapper is stateless and reusable. | Cache static singleton registration. |
| P2 | MINOR | `Services/InMemoryCacheService.cs` | `GetMany`/`SetMany`/`RemoveMany` await per iteration over a sync `IMemoryCache`. | Tight sync loop with `TryGetValue` / `Set`. |
| P3 | IMPROVEMENT | `Abstractions/ICacheService.cs` | All methods return `Task<T>` instead of `ValueTask<T>`. InMemory hits are synchronous and allocate a `Task` per call. | v3 surface change to `ValueTask`. |
| P4 | BUG | `Services/RedisCacheService.cs:430-432` | `RemoveManyAsync` records `N` increments regardless of how many keys actually existed. `KeyDeleteAsync` returns the count of keys actually removed. | Use the return value as the increment. |
| P5 | MINOR | `Serialization/PayloadEnvelope.cs:32-42` | Always allocates `byte[]`. | `Write(IBufferWriter<byte>, …)` overload using `BinaryPrimitives` span writes. |
| P6 | MINOR | `Internal/StableStringHash.cs` | `stackalloc byte[256]` even when actual byteCount is smaller; UTF-8 encoding can hit `3 × char-len` (~768 bytes) on long strings — possible deep-stack overflow risk. | Gate stack alloc by `Encoding.UTF8.GetByteCount` or cap at a safer ceiling. |

### 2.4 Resilience & Observability Gaps

| # | Severity | Location | Problem | Suggested Fix |
|---|----------|----------|---------|---------------|
| R1 | IMPORTANT | `Resilience/CacheResiliencePipelineBuilder.cs:82-89` | `IsTransient` predicate too narrow. Misses `SocketException`, `IOException`, `RedisServerException` (LOADING/READONLY during failover). Silent fail-open without resilience kicking in. | Add the missing types; explicitly exclude `OperationCanceledException` whose token is the user's CT. |
| R2 | IMPORTANT | `Resilience/CacheResiliencePipelineBuilder.cs` | No bulkhead / `RateLimiterStrategy`. Under Redis brownout, request threads queue indefinitely. | Add configurable `RateLimiterStrategy`. |
| R3 | IMPORTANT | `Telemetry/CacheInstruments.cs` | No `cache.serialize.duration` / `cache.deserialize.duration` histograms. Slow serialization is hidden inside `OperationDuration`. | Add two histograms keyed by `cache.format`. |
| R4 | IMPORTANT | `Serialization/PayloadEnvelope.cs` | No CRC / HMAC. A bit-flipped Redis entry deserializes to a corrupted object silently. | Append xxHash32 of payload after the payload bytes; verify on read. |
| R5 | IMPROVEMENT | `Serialization/ICacheSerializer.cs` | No compression hook. Large payloads (>16 KiB) waste network and Redis memory. | Optional decorator (`LZ4`/`Brotli`) with FormatId high bit indicating compression. |
| R6 | IMPROVEMENT | `Health/CachingHealthCheck.cs` | Single check covers Enabled + reachability. Kubernetes typically wants liveness vs readiness split. | Two `IHealthCheck` types or tag-based filter (`caching-readiness`, `caching-liveness`). |
| R7 | MINOR | `Internal/CacheLogMessages.cs` | Drift logs (1106-1108) are at Warning per-key. High-volume drift floods logs. | Sample (first occurrence per `(typeHash, driftKind)` per minute). |
| R8 | MINOR | `Resilience/CacheResiliencePipelineBuilder.cs:69-75` | Retry uses `BackoffType=Exponential, UseJitter=true` with default delay (~2 s). Single Redis blip can stall a request 4–12 s — exceeding many caller HTTP budgets. | Set `Delay=TimeSpan.FromMilliseconds(50)`, `MaxDelay=TimeSpan.FromSeconds(1)`. |

### 2.5 Validation Gaps

| # | Severity | Location | Problem | Suggested Fix |
|---|----------|----------|---------|---------------|
| V1 | IMPORTANT | `Validation/CacheOptionsValidator.cs` | No `KeyPrefix.Length + 1 < MaximumKeyLength` budget check. A long prefix plus a valid user key always overflows the per-key limit. | Add a combined-budget check; suggest at least 32 chars left for user keys. |
| V2 | IMPORTANT | `Services/RedisCacheService.cs:451-457` | `MaximumKeyLength` is enforced after `KeyPrefix` is prepended. Spec/docs treat it as a user-key cap; behavior caps prefix + key. | Either rename in docs (`MaximumPrefixedKeyLength`) or check at the routing layer before prepending. |
| V3 | IMPORTANT | `Validation/CacheOptionsValidator.cs` | `RedisConnectionString` checked only for presence, not validity. Malformed strings fail late at multiplexer connect with a worse error. | `try { ConfigurationOptions.Parse(...) }` with redacted exception surface. |

---

## 3. Suggested Triage

### Land in v2.1.0 (no API break)

- **B1** `StaleEntryTracker` sweep — straight bug; latent leak.
- **B2** `RoutingCacheService` `IAsyncDisposable` — graceful shutdown story.
- **B7** Health check Redis bypass — false-positive on outage is a real ops hazard.
- **B8** Probe key per-instance suffix.
- **R1** Polly `IsTransient` widening.
- **R8** Polly retry delay.
- **V1**, **V2**, **V3** validation tightening.

### Plan for v2.2.0 (small additive surface)

- **A7** `Enable()`, **A8** environment presets, **A9** key validator/transformer.
- **R2** rate limiter, **R3** ser/deser histograms, **R6** health-check split.
- **A3**, **A4** analyzer coverage extensions.

### Defer to v3.0.0 (breaking)

- **A1** `ICacheSerializer` switch to `ReadOnlyMemory<byte>`.
- **A2** Polly type wrapping in public surface.
- **A5**, **A6** public-surface footgun cleanup.
- **B5** `StableTypeHash` migration to `FullName` + explicit schema attribute (cache invalidation event — needs a coordinated cutover).
- **P3** `ValueTask` everywhere.

### Optional

- **B3**, **B4** Hybrid mode improvements — wait for `Microsoft.Extensions.Caching.Hybrid` API to mature.
- **R4** envelope CRC, **R5** compression — gated by benchmark evidence.
- **B6** `KeyPrefix` separator collision — document first; only rework if a real incident materializes.
- **B10** envelope strict length — defense-in-depth, low practical risk.

---

## 4. References

- Spec: [`docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md`](../specs/2026-05-05-v2-amazon-scale-design.md)
- v2 plans: [`docs/superpowers/plans/2026-05-05-v2-p0-foundations.md`](../plans/2026-05-05-v2-p0-foundations.md), [`P2`](../plans/2026-05-06-v2-p2-api-expansion.md), [`P3`](../plans/2026-05-06-v2-p3-hardening-ops.md)
- Public API surface: [`src/Caching.NET/PublicAPI.Shipped.txt`](../../../src/Caching.NET/PublicAPI.Shipped.txt)
- CHANGELOG: [`CHANGELOG.md`](../../../CHANGELOG.md)
