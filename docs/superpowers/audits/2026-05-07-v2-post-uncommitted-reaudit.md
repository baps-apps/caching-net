# Caching.NET v2 — Post-Uncommitted Reaudit

**Date:** 2026-05-07
**Branch:** `vpatel/v2`
**Scope:** Re-review of `src/Caching.NET` after the 2026-05-06 audit, focused on the **uncommitted working tree** and **untracked new files** introduced post-v2.0.0 ship commit (`515ac3b`).
**Method:** Read-pass on `RoutingCacheService`, `RedisConnectionRotator`, `PayloadEnvelope`, `PayloadCompression`, `StaleEntryTracker`, `DriftLogSampler`, `CachingHealthProbe`, `ServiceCollectionExtensions`, `RedisCacheService`. Cross-checked with `git diff HEAD`, `git status -u`, `dotnet build` (clean: 0 warnings, 0 errors), and prior audit at [`2026-05-06-v2-codebase-audit.md`](./2026-05-06-v2-codebase-audit.md).
**Result:** Prior backlog stays cleared. Uncommitted work introduced **4 P0s** (correctness / resource), **7 P1s** (design / perf), **7 P2s** (tests / docs / release hygiene).
**Implementation status (this branch, 2026-05-07):** #1 through #15, #17, and #18 addressed in code/docs; #16 decision recorded as v2.1 promotion target.

---

## 1. P0 — Correctness & Resource

| # | Location | Problem | Fix |
|---|----------|---------|-----|
| 1 | [src/Caching.NET/Services/RoutingCacheService.cs:482-545](../../../src/Caching.NET/Services/RoutingCacheService.cs#L482-L545) | **Race: `ScheduleBackgroundRefresh` vs `DisposeAsync`.** `Volatile.Read(ref _disposed)` checked at line 489, then `Task.Run` posts a body that touches `_shutdown.Token` at line 502. `DisposeAsync` (line 599) can interleave between the check and `Task.Run`, dispose `_shutdown` (line 617), and the background body throws `ObjectDisposedException` on `_shutdown.Token` access. | Capture `_shutdown.Token` into a local **before** `Task.Run`, **or** re-check `_disposed` inside the `Task.Run` body before touching `_shutdown`, **or** gate the schedule path with a lock that `DisposeAsync` also takes. |
| 2 | [src/Caching.NET/Serialization/PayloadCompression.cs:18-36](../../../src/Caching.NET/Serialization/PayloadCompression.cs#L18-L36) | **No decompression-bomb cap.** `DecompressBrotli` reads compressed bytes into an unbounded `MemoryStream`. Adversarial Redis payload (or corruption) can OOM the process. | Bound output to `CacheOptions.MaximumPayloadBytes` (or new `MaxDecompressedBytes`). Use `BrotliDecoder.TryDecompress` with explicit size check, or wrap output in a length-limited stream. |
| 3 | [src/Caching.NET/Internal/RedisConnectionRotator.cs:104](../../../src/Caching.NET/Internal/RedisConnectionRotator.cs#L104) | **`Dispose() => _ = DisposeAsync().AsTask();` is fire-and-forget.** Caller expects sync teardown to complete; old multiplexer can outlive container shutdown, holding sockets/threads. | `DisposeAsync().AsTask().GetAwaiter().GetResult();` (matches `RoutingCacheService.Dispose` pattern at line 622). |
| 4 | [src/Caching.NET/Internal/DriftLogSampler.cs:11](../../../src/Caching.NET/Internal/DriftLogSampler.cs#L11) | **Unbounded static `s_lastLogTicks`.** Distinct `(driftKind, keyHash)` fingerprints accumulate forever; high-cardinality drift events leak memory across process lifetime. | Cap dict size (e.g. 4096) and evict oldest, **or** sweep entries with `now - ticks > 2 * s_intervalMs` on insert past threshold. |

---

## 2. P1 — Design & Performance

| # | Location | Problem | Fix |
|---|----------|---------|-----|
| 5 | [src/Caching.NET/Serialization/PayloadCompression.cs:29-36](../../../src/Caching.NET/Serialization/PayloadCompression.cs#L29-L36) | `DecompressBrotli` does `payload.ToArray()` on input **and** `output.ToArray()` on output — two heap allocations per compressed read. Hot path on every Redis hit when compression enabled. | Use `ArrayPool<byte>` for input copy (or accept `byte[]` directly), and `BrotliDecoder.TryDecompress` into a pooled output buffer. |
| 6 | [src/Caching.NET/Serialization/PayloadCompression.cs:18-27](../../../src/Caching.NET/Serialization/PayloadCompression.cs#L18-L27) | `CompressBrotli` does `output.ToArray()` final copy. Mirrors P5 in prior audit but for compression path. | Add `IBufferWriter<byte>` overload mirroring `PayloadEnvelope.Write(..., IBufferWriter<byte>)`. |
| 7 | [src/Caching.NET/Serialization/PayloadEnvelope.cs:48-60](../../../src/Caching.NET/Serialization/PayloadEnvelope.cs#L48-L60) | `IBufferWriter` overload's `if (span.Length < needed) WriteSlow(...)` branch is dead code. `IBufferWriter<T>.GetSpan(sizeHint)` contract guarantees length **≥** `sizeHint`. | Drop `WriteSlow` + branch, **or** add comment explaining defensive-fallback intent. |
| 8 | [src/Caching.NET/Internal/StaleEntryTracker.cs:54-60](../../../src/Caching.NET/Internal/StaleEntryTracker.cs#L54-L60) | `OrderBy(...).Take(...)` materializes full `_entries` snapshot + sort. At `HardEntryLimit=50_000`, that is 50k allocations + O(N log N) under contention inside `Prune`. | Two-pass: compute time threshold via reservoir/quickselect, then remove keys where `StaleUntilUtcTicks < threshold`. **Or** approximate (drop random 20% over limit). |
| 9 | [src/Caching.NET/Services/RoutingCacheService.cs:620-623](../../../src/Caching.NET/Services/RoutingCacheService.cs#L620-L623) | Sync `Dispose() => DisposeAsync().GetAwaiter().GetResult();` — deadlock risk under sync context (legacy ASP.NET, some test runners). | Drop `IDisposable` (DI prefers `IAsyncDisposable`), **or** guard sync path with `Task.Run(...).GetAwaiter().GetResult()` to detach context. |
| 10 | [src/Caching.NET/Services/RoutingCacheService.cs:275-301](../../../src/Caching.NET/Services/RoutingCacheService.cs#L275-L301) | Coalesce-vs-no-coalesce SWR paths duplicate the extended-TTL + `factoryRan` + `Register` logic. ~25 LOC duplication; divergence risk. | Extract `ExecuteWithStaleWindow(...)` helper used by both branches. |
| 11 | [src/Caching.NET/Health/CachingHealthProbe.cs:90-95](../../../src/Caching.NET/Health/CachingHealthProbe.cs#L90-L95) | Readiness probe routes through full `GetOrCreateAsync` (coalesce + stale machinery) every probe tick. Probe traffic shows up in `cache.gets` / `cache.sets` metrics, polluting baseline. | Tag probe ops with `operation="health"` or use a dedicated bypass path. After first warm, prefer read-only `ExistsAsync`. |

---

## 3. P2 — Tests, Docs, Release Hygiene

| # | Location | Problem | Fix |
|---|----------|---------|-----|
| 12 | `src/Caching.NET/Internal/DriftLogSampler.cs` | No unit tests. CAS loop is concurrency-sensitive. | Add tests: same fingerprint within window blocks; different fingerprints independent; window-expiry releases; concurrent producers do not deadlock. |
| 13 | `src/Caching.NET/Serialization/PayloadCompression.cs` | No direct unit tests; covered indirectly via [tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs:162](../../../tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs#L162). | Add round-trip, small-payload-skip, threshold-boundary, and `WithCompression` / `BaseFormatId` / `IsCompressed` bit-manipulation tests. |
| 14 | [src/Caching.NET/Health/CachingHealthProbe.cs:24](../../../src/Caching.NET/Health/CachingHealthProbe.cs#L24) | `CheckLivenessAsync` calls `cancellationToken.ThrowIfCancellationRequested()` — surfaces `OperationCanceledException` to host. `CheckReadinessAsync` catches all exceptions — asymmetric. | Return `HealthCheckResult.Unhealthy` on cancellation, **or** remove the throw (host-level CT handling already covers). |
| 15 | [src/Caching.NET/Extensions/ServiceCollectionExtensions.cs:303](../../../src/Caching.NET/Extensions/ServiceCollectionExtensions.cs#L303) | `services.Any(d => d.ServiceType == typeof(IConfigureOptions<CacheSerializerOptions>))` — O(N) scan over the service collection. Brittle: false negative if registered via different descriptor type. | `services.AddOptions<CacheSerializerOptions>()` is idempotent — call directly without the guard. |
| 16 | `Caching.NET.csproj` (`EnablePackageValidation`) | Six new public surface areas (`CacheSchemaAttribute`, `ICacheKeyFactory`, `DefaultCacheKeyFactory`, `CacheResilienceOptions`, `EnablePayloadCompression`, `PayloadCompressionThresholdBytes`) landed after v2.0.0 baseline. | Decide ship target (v2.1.0): update package-validation baseline / version policy when tagging; keep [CHANGELOG](../../../CHANGELOG.md) accurate. *(Historical: `PublicAPI.*.txt` files were removed in favor of SDK package validation.)* |
| 17 | [samples/Caching.NET.Sample/Program.cs](../../../samples/Caching.NET.Sample/Program.cs) | Sample diff is 1 line. Does not exercise `WithKeyValidator` / `WithKeyTransformer`, `[CacheSchema]`, `EnablePayloadCompression`, `WithHealthChecks(splitLivenessReadiness:true)`, custom `ICacheKeyFactory`, or `WithResilience`. | Extend sample (or add a feature-tour sample) covering new v2 surface; otherwise consumers cannot see how to wire the additions. |
| 18 | [docs/superpowers/audits/2026-05-06-v2-codebase-audit.md](./2026-05-06-v2-codebase-audit.md) | Prior audit §3 declares backlog empty. This reaudit found 4 P0 + 7 P1 + 7 P2 introduced by uncommitted work after the prior pass. | Cross-link this doc from the prior audit (or fold findings into §3). |

---

## 4. Suggested Triage

### Block before next ship
- **#1** RoutingCacheService dispose race
- **#2** Decompression-bomb cap
- **#3** RedisConnectionRotator sync Dispose
- **#4** DriftLogSampler unbounded dict

### Before next package tag
- **#12, #13** Direct tests for `DriftLogSampler`, `PayloadCompression`
- **#16** Reconcile package-validation baseline / versioning with new public API (see `EnablePackageValidation` on `Caching.NET.csproj`)
- **#17** Sample app coverage of new surface

### Cleanup / nice-to-have
- **#5–#11** Allocation reduction, dead-code drop, SWR path dedup, probe metrics tagging
- **#14, #15** Liveness symmetry, registration guard simplification
- **#18** Cross-link prior audit

---

## 5. Verification

- `dotnet build` — **clean** across `net8.0` / `net9.0` / `net10.0` (0 warnings, 0 errors as of 2026-05-07).
- Test suite untouched by this reaudit; prior counts remain (186 unit / 9 analyzer / 11 properties / 4 chaos / 10 integration).
- No code changes proposed inline — this is a triage doc only.

---

## 6. References

- Prior audit: [`2026-05-06-v2-codebase-audit.md`](./2026-05-06-v2-codebase-audit.md)
- Spec: [`docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md`](../specs/2026-05-05-v2-amazon-scale-design.md)
- Public API: NuGet package validation — [`src/Caching.NET/Caching.NET.csproj`](../../../src/Caching.NET/Caching.NET.csproj) (`EnablePackageValidation`)
- v2.0.0 ship commit: `515ac3b`
- Branch HEAD at audit time: `e6b2865`

---

## 7. Implementation Tracking (2026-05-07)

- **Implemented P0:** `RoutingCacheService` shutdown-token race hardening, decompression output cap, synchronous `RedisConnectionRotator.Dispose` teardown, bounded `DriftLogSampler` dictionary growth.
- **Implemented P1/P2 follow-through:** stale-window path dedup in `RoutingCacheService`, dead branch removal in `PayloadEnvelope.Write(IBufferWriter)`, `StaleEntryTracker` prune sort removal, health probe liveness cancellation symmetry + readiness warm/read split, idempotent serializer options registration, pooled-buffer Brotli compression/decompression path in `PayloadCompression`, direct tests for `DriftLogSampler` and `PayloadCompression`.
- **Sample/docs updates:** sample now demonstrates `WithKeyValidator`, `WithKeyTransformer`, `WithResilience`, split liveness/readiness checks, custom `ICacheKeyFactory`, `[CacheSchema]`, and payload compression configuration. Prior audit now cross-links this reaudit.
- **Release planning note (#16):** treat additive APIs as the next semver tag (v2.1 target); align `dotnet pack` / package-validation baseline with that release. v2.0.0 baseline remains historical.
