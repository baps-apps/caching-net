# Changelog

All notable changes to Caching.NET are documented in this file.

The project follows [Semantic Versioning](https://semver.org/).

## 3.1.1 — 2026-08-16

**Fewer empty `cache.backplane.receive` traces. Nothing else changes.** Patch bump, no code change,
no configuration change, no API change.

### Fixed

- **A backplane message this instance published no longer emits a `cache.backplane.receive` span.**
  Redis pub/sub delivers a publish back to the connection that made it, and the engine discards those
  by source id — but that check runs *inside* the handler 3.1.0 wrapped, so the span was timing an
  early return. Every local `Set`, `Remove`, `Expire`, `RemoveByTag` and `Clear` produced one
  sub-millisecond trace that did nothing. With two replicas that was half of all receive spans.

  It also removes a disagreement between two Caching.NET signals for the same event:
  `caching.net.background.operations{cache.operation=backplane_receive}` is recorded from an engine
  event raised only *after* the source-id check, so the metric already excluded self-published
  messages while the span counted them.

  **Nothing about delivery changes** — the message still reaches the engine exactly once, and whether
  to act on it stays the engine's decision. Spans for messages from *other* instances are unchanged,
  including the local invalidation work nested under them.

## 3.1.0 — 2026-08-16

**Your traces get quieter. Nothing else changes.** Bump the package and you are done — no code
change, no configuration change, no API removal, and every metric reports exactly what it did in
3.0.0.

### What you will notice

**Your cache traces are unchanged.** A cache call still produces its operation span with the layer
work nested underneath:

```text
GET /orders/42                        ← your request span
└── cache.get_or_set                  ← unchanged
    ├── cache.memory.get              ← unchanged  (L1 miss…)
    └── cache.redis.get               ← unchanged  (…so Hybrid falls through to L2)
```

**The single-span traces beside them are gone.** Cache work the engine does on its own threads —
applying an invalidation another pod published, writing an entry after a background refresh — used to
emit one root trace per operation, each holding a single sub-millisecond span with nothing indicating
what caused it. In a multi-pod deployment those arrived in bursts, one per invalidated key:

```text
Before                          After
trace A: cache.memory.remove    trace A: cache.backplane.receive
trace B: cache.memory.remove             ├── cache.memory.remove
trace C: cache.memory.remove             ├── cache.memory.remove
… one root trace per key                 └── … one trace for the whole message
```

**Nothing stops being measured.** `caching.net.layer.duration`, `caching.net.payload.size` and every
counter record those operations exactly as before. Dashboards and alerts built on metrics are
unaffected; only the trace stream is smaller.

**If you had a dashboard or saved search keyed on those root spans**, it will go empty. One setting
restores the old behavior:

```jsonc
// appsettings.json
"CacheOptions": {
  "Observability": {
    "LayerTracing": "Always"   // "WhenParented" (default) | "Always" | "Never"
  }
}
```

```csharp
// or in code
services.AddCaching(config, cache => cache.WithLayerTracing(CacheLayerTracing.Always));
```

`Never` drops layer spans entirely, keeping operation spans — useful if you want cache calls visible
in traces but not their internals.

### Added

- **`Observability.LayerTracing`** (`Always` | `WhenParented` | `Never`) and
  `CachingBuilder.WithLayerTracing(...)` — controls when one physical layer probe emits a span. Spans
  only; layer metrics are identical at all three values.
- **`cache.backplane.receive` span**, one per backplane message this instance receives, tagged
  `cache.background_operation=true`. It is the parent in the "After" diagram above. It starts a new
  trace rather than continuing the publisher's: the message format carries no trace context, so
  cross-process correlation is not available at any setting — see
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §3.2.

### Changed

- **`LayerTracing` defaults to `WhenParented`**, which is the behavior described above. Set it to
  `Always` for the 3.0.0 behavior.
- **Cost.** A suppressed probe costs 22.5 ns and allocates nothing, against 144 ns and 600 B before —
  but read that per-probe, not as a throughput win: it only applies to background work, so the
  aggregate CPU saving is small. What you actually save is exporter batches, ingest volume and
  trace-store cardinality. The receive span costs ~126–144 ns per received message. Your request path
  is unchanged, confirmed by a paired benchmark against 3.0.0 (L1 hit 145.6 → 140.9 ns, identical
  allocations). Full numbers: [docs/BENCHMARKS.md](docs/BENCHMARKS.md).

### Security

- Pinned **SSH.NET to 2026.0.0** for `GHSA-q939-rpr3-3284` (high). **This does not affect your
  application** — SSH.NET is a test-only transitive dependency of Testcontainers and never appears in
  the published package's dependency graph. Listed for completeness of the repository's audit trail.

## 3.0.0 — 2026-08-12

**Major redesign.** Caching.NET v3 replaces three hand-written cache implementations with a single
engine, and reuses the `ICacheService` name for a new eight-verb contract Caching.NET owns and
implements over that engine — the engine itself never appears in a public signature — while keeping
registration, configuration, security, connection management and observability under Caching.NET's
own names.

There is **no compatibility shim**. Keeping the v2 surface would have preserved exactly the
limitation this release exists to remove — a four-method cache interface that hid fail-safe,
timeouts, eager refresh and factory context from applications.

Migration guide: [docs/MIGRATION-V2-TO-V3.md](docs/MIGRATION-V2-TO-V3.md). The release-gate review,
with the measured evidence behind every gate, is
[docs/audits/2026-08-12-v3.0.0-final-release-gate.md](docs/audits/2026-08-12-v3.0.0-final-release-gate.md).
The three earlier reviews in that directory are **superseded and carry banners saying so** — the
2026-08-12 gate found a release blocker all three passed over (tag and `Clear` invalidation silently
lost across instances in `Redis` mode), so their approvals cover builds that shipped it. The oldest,
the [2026-08-08 review](docs/audits/2026-08-08-v3.0.0-production-readiness-review.md), additionally
describes a design that was rejected after it — the one in which the engine's own `IFusionCache` was
the public contract — and does not describe what ships.

### Added

- **Full cache operation surface.** Fail-safe with throttling, factory soft/hard timeouts,
  distributed soft/hard timeouts, eager refresh, factory execution context (`CacheFactoryContext<T>`:
  stale value, ETag/`LastModified`, `NotModified()`/`Fail(reason)`, adaptive per-execution overrides),
  background distributed and backplane operations, auto-recovery, and per-entry options
  (`CacheEntryOverrides`) — all available to applications for the first time, through a new,
  permanently eight-verb `ICacheService` (`GetOrSet`, `GetOrDefault`, `TryGet`, `Set`, `Remove`,
  `Expire`, `RemoveByTag`, `Clear`, each async and sync) that Caching.NET owns and implements over the
  engine. The engine's own event hub and plugin system are **not** exposed to applications — they are
  consumed internally, the event hub only for telemetry (`CacheEventBridge`) — so a new engine
  capability lands as a `CachingOptions` knob or a `CacheEntryOverrides` field, never a ninth verb or
  a reference to the engine.
- **`CacheValue<T>`** — the result of a read, distinguishing a cached `null` from a miss; returned by
  `TryGet(Async)` and exposed to a factory as `CacheFactoryContext<T>.StaleValue`.
- **`CacheEntryOverrides` is additive by construction.** Every property is nullable and starts `null`
  ("use the configured value"). There is no way to build one that starts from a blank slate, so a
  per-call override can add to a cache's mode and guard behaviour but can never escape it — closing,
  by construction, the gap the equivalent caller-constructed engine options object used to leave open.
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
- **Key-length guard enforced on every call**, inside the cache adapter, whether or not the call
  supplies per-call overrides.
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
- **`CACHENET001` analyzer**, shipped inside the Caching.NET package. Warns on any direct reference in
  consumer code to a `ZiggyCreatures.Caching.Fusion` type — the internal cache engine, including its
  generics such as `MaybeValue<T>` — because a direct reference forfeits the guarantee that
  Caching.NET's whole public surface (`ICacheService`, `CacheEntryOverrides`, `CachingBuilder`,
  `CachingOptions`) exists precisely so the engine can be replaced without a source change downstream.
  Caching.NET's own assembly and its `Caching.NET.Tests*` assemblies are exempt, since they legitimately
  build and verify the adapter that hides the engine from everyone else. `StackExchange.Redis` is not
  flagged: an application may legitimately use the Redis client directly for something that is not the
  cache. Consumers take no Roslyn
  dependency.
- **One activity source, one meter, both fully owned.** `CacheTelemetry.ActivitySourceName` /
  `.MeterName` are `"Caching.NET"`; the plural `ActivitySourceNames`/`MeterNames` arrays contain that
  same single name, for API symmetry rather than a second, engine-branded detail tier. The internal
  cache engine's own diagnostics are never registered under any name — every span and metric an
  application sees originates inside Caching.NET (`FusionCacheService`, the layer decorators, and
  `CacheEventBridge` for engine-event-sourced signals). Pinned by `SpanKeyExposureTests` and
  `OperationSpanTests`.
- **Public API baseline.** `PublicApiTests` compares the reflected public surface against
  `Api/PublicApi.approved.txt` and fails on any addition, removal or signature change, listing
  removals as breaking. Approve an intended change with `CACHINGNET_APPROVE_API=1`. Companion tests
  assert that no `Internal` namespace is exported and that **no engine type appears in any public
  signature at all** (`NoEngineTypeAppearsInAPublicSignature`) — a stricter guarantee than earlier
  drafts of this release, which had planned to allow `IFusionCache` and `FusionCacheEntryOptions`
  through.
- **Chaos test suite** covering Redis unavailable at startup, runtime outage, restart, backplane
  loss, fail-safe activation, factory-timeout fallback, and log-storm suppression — plus **network
  partition** (`NetworkPartitionTests`): a warm key is still served from L1 without waiting for
  Redis, a cold key runs its factory and releases the caller, a write made during the partition
  reaches Redis once it heals, readiness degrades and recovers without a restart, and cross-instance
  invalidation resumes after the heal. A partition is distinct from an outage: the connection is not
  refused, it hangs, which is the case a timeout budget rather than a connection error has to cover.
- **Redis authentication and TLS tests** against purpose-built containers: correct password round
  trips; a wrong password surfaces to the caller when distributed errors are requested and reports
  readiness as degraded otherwise; an untrusted TLS certificate is rejected under strict validation
  **and** under permissive validation (permissive relaxes a host-name mismatch and nothing else);
  a TLS rejection degrades the cache instead of taking the application down. Ten unit tests cover
  the certificate policy directly, including combined policy errors and one-time handshake logging.
- **`CachingDefaults`** — the public constants registration and key composition are built from
  (`DefaultCacheName`, `KeyPrefixSeparator`, `MaximumCacheNameLength`).
- **`Caching.NET.Health.CachingHealthCheck` and `CachingLivenessHealthCheck` are public again.** v2.0.0
  made both `internal` (see its "Removed" section); v3 exports them, so an application can register a
  probe itself instead of going through `WithHealthChecks()`/`AddCachingHealthChecks()`.
- **Cross-process multi-pod tests.** `Caching.NET.Tests.Pod` is a console cache instance the suite
  launches as a separate OS process; seven tests exercise write visibility, L1 invalidation, remove,
  tag invalidation, clear, application isolation and pod restart across real processes rather than
  two service providers sharing one CLR.

### Changed

- **Cache API**: `ICacheService` (v2's four-method shape) → `ICacheService` (v3's eight-verb shape) —
  **the interface keeps its name**, but the shape is entirely new and Caching.NET owns the
  implementation over the engine rather than exposing the engine's own operation contract. See the
  migration guide for the call-site map.
- **Registration**: `AddCaching` and `CachingBuilder` keep their v2 names but are new types with new
  signatures — v2 call sites compile and then fail startup validation until they are updated.
- **Configuration section**: still `CacheOptions`, but restructured into `Entry`, `Resilience`,
  `Redis`, `Backplane`, `Serialization`, `Security`, `Observability`. A v2 section binds partially
  and is rejected at startup; it is not silently accepted.
- **`KeyPrefix` → `ApplicationPrefix`**, and the key layout gains optional environment, tenant and
  cache-name segments.
- **BREAKING: the memory cap is `Entry.MemorySizeLimit`, and it is no longer multiplied by
  1024 × 1024.** v2 called it `MemorySizeLimitMb` and a v3 pre-release called it
  `MemorySizeLimitMegabytes`; both scaled the configured value. Neither was ever a megabyte cap.
  The in-process memory layer's size limit
  is a ceiling on the **summed `Size` the cached entries declare**, in whatever unit the application
  charges; it cannot weigh an arbitrary object in bytes. Scaling the configured value and calling it
  megabytes made a documented `WithMemorySizeLimit(megabytes: 1)` a 1,048,576-entry cap under the
  default per-entry size of `1` — measured, 200 entries holding about 78 MB of strings all stayed
  resident under that "1 MB" cap, so a pod sized against the documented meaning would OOM.
  `CachingBuilder.WithMemorySizeLimit`'s first parameter is renamed `megabytes` → `limit`. With
  `defaultEntrySize: 1` the limit is simply a cap on the **number of entries**; to approximate bytes,
  charge each entry a size in your own unit (per call via `CacheEntryOverrides.Size`, or as the
  `Entry.Size` default) and express the limit in that same unit. A new validation rule rejects
  `Entry.Size` of zero or less alongside a limit: an entry that charges nothing can never move the
  sum, so the cap would look configured while the memory layer stayed unbounded. See
  [docs/OPERATIONS.md §4](docs/OPERATIONS.md#4-capacity).
- **Health-check registration is idempotent.** Two caches that both called `WithHealthChecks()` used
  to register the same probe name twice and fail the host at startup with
  `ArgumentException: Duplicate health checks were registered with the name(s): caching-net` — a
  message naming neither Caching.NET nor the cause. The repeat is now a no-op, on the internal path
  and through `AddCachingHealthChecks` alike; one `CachingHealthCheck` already probes every
  registered cache through `ICacheProvider.CacheNames`. The consumer's `Action<CachingBuilder>` also
  no longer runs twice per registration — it used to be replayed eagerly against a throwaway
  `CachingOptions` purely to read whether health checks had been opted into, so a delegate that
  loaded a client certificate loaded two, and one that read a secret from a vault made the call twice
  at startup.
- **Metric names**: `cache.*` → `caching.net.*`. Dashboards and alerts must be updated.
- **Telemetry accessor**: `CacheInstruments` → `CacheTelemetry`. `ActivitySourceNames`/`MeterNames`
  are plural arrays containing the same single `"Caching.NET"` name as the singular properties — for
  API symmetry, not a second detail tier.
- **Telemetry recording moved off the engine's event pump for the signals that belong on the caller's
  path.** `FusionCacheService` now records `caching.net.hits`, `caching.net.misses`,
  `caching.net.operations` and `caching.net.invalidations` synchronously, one producer per
  signal; only the signals the engine's own code path must attribute (factory executions, fail-safe,
  eager refresh, backplane publish/receive, evictions) still come through the event bridge.
  Engine-side evictions have their own counter, **`caching.net.evictions`** — they are engine-
  initiated, not caller-requested, so counting them on `caching.net.operations` or
  `caching.net.invalidations` booked every removal, overwrite and expiry twice. This also
  fixed the hit ratio: the old event-hub subscription could fire more than once per logical read (tag
  lookups, lock double-checks), inflating `caching.net.hits`/`.misses` by a call-mix-dependent factor.
- **Key and tag guards are enforced on every call**, inside `FusionCacheService`, not only for calls
  that fall back to the configured default entry options.
- **`cache.layer` is omitted from the `cache.get_or_set` span and from `caching.net.operations` on a
  Hybrid hit**, corrected from an earlier draft that would have tagged every Hybrid hit `memory`. A
  Hybrid hit can be answered by L1 *or* L2 (L2 after an L1 miss — exactly the case an operator
  investigates), and the engine's hit signal carries no level information on the common path;
  reporting `memory` when Redis actually answered would be worse than reporting nothing. `InMemory`
  and `Redis` modes still get a tautological `cache.layer`, since each has exactly one layer.
  Per-layer truth for Hybrid lives on `caching.net.layer.duration` and the `cache.memory.*`/
  `cache.redis.*` child spans instead.
- **Redis mode no longer keeps entries in local memory.** Reads always consult Redis, so no instance
  can serve a value Redis has not confirmed. The in-process stampede locker is unaffected.
- **Jitter keeps v2's proportional model under a new name, plus an absolute ceiling.**
  `TtlJitterPercentage` (fraction) → `Entry.JitterFraction` (fraction, default `0.1` — the same
  default), capped by `Entry.JitterMaxDuration` (default 2 s). The applied window is
  `min(duration × JitterFraction, JitterMaxDuration)`. Setting `JitterFraction` to `null` makes
  `JitterMaxDuration` a flat absolute window instead. See the Fixed entry below for why the
  fraction exists: an interim v3 draft shipped only the flat window, and a flat window does not
  scale down to short-lived entries.
- **Fail-safe is on by default**; a failing factory now returns a stale value where one exists.
- **`Enabled` is no longer hot-reloadable.** It is read once at registration.
- **Health checks**: `AddCachingHealthChecks` keeps its v2 name; readiness now reports
  Degraded rather than Unhealthy when only the distributed layer is down, and reports exception
  types rather than messages.
- **`ValidateCacheRegistration` → `ValidateCachingRegistration`**, which now resolves every
  registered cache.
- **Engine log output is re-categorised** under `Caching.NET`, so log filters never name the engine.
- **`Logging:LogLevel:Caching.NET` guidance is `Warning`**, but `Information` is safe as a standing
  setting too: the engine's per-operation lines are rewritten to
  `Observability.EngineOperationLogLevel` (`Debug` by default), so `Information` on that category
  costs zero engine lines per operation — see the `EngineOperationLogLevel` entry under Fixed.
  Everything an operator needs during an incident is `Warning` or above regardless.
- **`CacheKey.For<T>` puts generic arguments in the type segment.** In v2, `List<int>` and
  `List<string>` both produced `` List`1 ``, so two different types shared one key — a
  type-confusion bug, not just a collision. They now produce `List_Int32` and `List_String`. Keys for
  non-generic types are unchanged; keys for closed generics change, so those entries cold-start.

### Removed

- **`net8.0` and `net9.0` target frameworks.** The package targets `net10.0` only. Applications on
  .NET 8 or .NET 9 must stay on 2.2.0 or upgrade their target framework.
- v2's `ICacheService` shape (`GetAsync`/`SetAsync`/`GetOrSetAsync`/`RemoveAsync`, `GetOrCreateAsync`,
  runtime-typed `GetAsync`, and the rest). The **name** `ICacheService` is reused by v3 for a new,
  unrelated eight-verb contract — see Changed above; this is not the same interface with new methods,
  it is a breaking replacement that happens to share a name.
- `CacheOptions` (the class), `CacheCallOptions`, `CacheSerializerOptions`, `CacheServiceCallExtensions`,
  `CacheSchemaAttribute`.
- The v2 `CachingBuilder` and all four v2 `AddCaching` overloads. Both names are reused by v3 with
  different members and signatures.
- `ValidateCacheRegistration`.
- `ICacheSerializer`, `JsonCacheSerializer`, `MessagePackCacheSerializer`, `PayloadEnvelope`,
  `PayloadEnvelopeReadResult`, schema-drift detection.
- **Every member of `Caching.NET.Resilience.CacheResilienceOptions`.** v3 reuses the type *name* in
  `Caching.NET.Options` with a completely different member set, and
  `CachingBuilder.WithResilience(Action<CacheResilienceOptions>)` exists in both versions — so a v2
  call site keeps binding a same-named type and fails only on the member. Gone: `Timeout`,
  `FailureRatio`, `MinimumThroughput`, `SamplingDuration`, `BreakDuration`, `RetryCount`,
  `EnableRedisConcurrencyLimiter`, `RedisConcurrencyPermitLimit`, `RedisConcurrencyQueueLimit`.
- `CacheResiliencePipelineBuilder`, `ResiliencePipelineNames`, and the whole Polly layer.
- `CacheInstruments` and every `cache.*` metric.
- Per-call `Mode` override, `BypassCache`, `ForceRefresh` and `CoalesceConcurrent` (use a named cache
  or `RefreshAsync`; there is no per-call layer-skip override in v3 — `CacheEntryOverrides`
  deliberately has no equivalent of `BypassCache`, since that is exactly the kind of mode-encoding
  flag the additive-overrides design keeps out of per-call reach, and the engine owns stampede
  protection with no per-call opt-out).
- Runtime-typed `GetAsync(string, Type, …)`.
- `RequireTagSupport`, `StripeLockCount`, `StaleRefreshConcurrency`, `KeyValidator`, `KeyTransformer`.
- **v2-internal types**, listed because a v2 stack trace, log line or internal fork may still name
  them — none of them can break a consumer compile: `RoutingCacheService`, `InMemoryCacheService`,
  `RedisCacheService`, `HybridCacheService`, `StripedLockManager`, `StaleEntryTracker`,
  `StaleRefreshThrottle`, `TtlJitter`, `RuntimeTypedCacheInvoker`, `DriftLogSampler`,
  `RedisConnectionRotator`, `StableTypeHash`, `StableStringHash`, `PayloadCompression`.
- NuGet dependencies: `Microsoft.Extensions.Caching.Hybrid`, `Polly`, `Polly.Extensions`,
  `Polly.RateLimiting`, `Microsoft.Extensions.Options.DataAnnotations`, and the explicit
  `System.Diagnostics.DiagnosticSource` reference — the last is part of the `net10.0` shared
  framework, so referencing it explicitly is now rejected by NU1510.

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
- `MessagePack` pinned to 3.1.8, above the 3.1.4 the serializer resolves transitively (three
  high-severity advisories). `Microsoft.OpenApi` pinned to 2.11.0 in the sample, above the
  vulnerable 2.0.0 resolved transitively by `Microsoft.AspNetCore.OpenApi`.

### Fixed

- **Tag and `Clear` invalidation now reaches instances that already read the key.** The engine
  implements `RemoveByTag` and `Clear` as *marker* entries that every read compares itself against, and
  markers are configured separately from ordinary entries. Caching.NET applied the cache mode only to
  ordinary entries, so markers kept the engine's defaults — **ten days, memory layer included**. In
  `Redis` mode, entries bypassed the memory layer but markers did not, so an instance's first read
  cached "no marker exists" locally for ten days; Redis mode registers no backplane, so nothing could
  ever evict it, and an invalidation was invisible to every instance that had already served the key
  once — which under real traffic is every instance. Measured: a warm reader still served a
  tag-invalidated value after 45 seconds. In `Hybrid` without a backplane, markers ignored
  `Entry.LocalExpiration`, so the documented bound held for an overwrite and not for a tag
  invalidation. The mode is now applied to markers as well: `Redis` keeps them out of the memory layer,
  `Hybrid` bounds their in-process copy by `Entry.LocalExpiration ?? DefaultExpiration`, and `InMemory`
  keeps them out of the distributed layer. Their logical and distributed lifetimes keep the engine's
  long defaults so an invalidation stays durable for an instance that was offline when it happened.
  **This costs Redis mode read latency:** a read that *hits* now takes `3 + n` Redis commands for an
  entry with `n` tags, re-baselined at ×3.10 for an untagged read (109 µs → 338 µs) and ×4.92 with two
  tags (537 µs), with per-read allocation roughly doubling. A read that *misses* is unchanged at one
  command, so the amplification tracks the hit ratio rather than the request rate:
  `commands ≈ requests × (1 + hitRatio × (2 + n))`. `Hybrid` and `InMemory` are
  unaffected. See [docs/BENCHMARKS.md](docs/BENCHMARKS.md) for the numbers,
  [docs/OPERATIONS.md §4](docs/OPERATIONS.md#4-capacity) for capacity planning, and
  [ARCHITECTURE §3.1](docs/ARCHITECTURE.md#31-the-mode-also-has-to-reach-the-tag-markers) for why. The
  earlier tests passed because they asserted from a *cold* reader, which fetches the marker from Redis;
  the regression tests now assert from a warm one.
- **The public-API baseline can now see a method's generic parameters.** `PublicApiSurface` rendered a
  method by name only, so `CacheKey.For<T>(object)` was recorded as `For(System.Object id)` and
  `CacheExtensions.ExistsAsync<TValue>` lost `<TValue>` entirely. Changing `For<T>` to `For<T, TId>`, or
  dropping a type parameter, produced an identical baseline and passed the gate — both source-breaking
  for every caller. No shipped API changed; the regenerated baseline differs only by the `<T>`
  annotations it was previously blind to.
- **Jitter is proportional again.** v2 expressed jitter as `TtlJitterPercentage`, a fraction of the
  entry's lifetime; the first v3 draft replaced it with a flat `Entry.JitterMaxDuration` defaulting to
  2 seconds. A flat window does not scale: 2 s against a 10-minute entry is a rounding error, but
  against a 300 ms entry it is **seven times** the requested lifetime, so an entry could be served
  long after the duration its caller asked for — measured, an entry configured for 300 ms was still
  returned 900 ms later. `Entry.JitterFraction` (default `0.1`) restores the v2 model, and
  `Entry.JitterMaxDuration` becomes its ceiling: the applied window is
  `min(duration × JitterFraction, JitterMaxDuration)`. A 10-minute entry still gets 2 s (60 s
  proposed, capped), so long-lived entries are bit-for-bit unchanged; a 300 ms entry now gets 30 ms.
  The base duration is the **shortest** lifetime governing the entry — `Entry.LocalExpiration` or
  `Entry.DistributedExpiration` when either is set and shorter — because an entry with a 200 ms memory
  duration is short-lived in the layer that will actually expire it. Per-call and adaptive overrides
  that shorten an entry recompute its jitter with it, so shortening an entry for one call no longer
  leaves a window sized for the configured default. `CacheEntryOverrides.JitterFraction` overrides the
  fraction per call; an explicit `CacheEntryOverrides.JitterMaxDuration` is still honoured as a flat
  window; `JitterFraction = null` restores the flat behaviour; `JitterMaxDuration = TimeSpan.Zero`
  still disables jitter entirely and no fraction can reintroduce it. New
  `CachingBuilder.WithProportionalJitter(fraction, maximum)`, and a validator rule rejecting a
  fraction outside `(0.0, 1.0]`.
- **The shipped analyzer no longer breaks every consuming build.** `Caching.NET.Analyzers` was built
  against `Microsoft.CodeAnalysis.CSharp` 5.6.0, whose assembly version (`5.6.0.0`) is newer than the
  Roslyn in the .NET 10.0.100 SDK this repository itself pins (`5.0.0.0`). Because the analyzer ships
  inside the Caching.NET package, a consumer's `csc` loaded it and refused: `error CS9057: Analyzer
  assembly ... references version '5.6.0.0' of the compiler, which is newer than the currently
  running version '5.0.0.0'`. The package was therefore uninstallable, while every in-repo build
  stayed green — nothing in the repository loads the analyzer through the compiler. Pinned to
  `4.14.0`, which loads in the .NET 8, 9 and 10 SDK compilers alike, and guarded by
  `EngineTypeAnalyzerTests.ShippedAnalyzer_TargetsARoslynOldEnoughForConsumerCompilers` so raising it
  again is a deliberate decision to raise the minimum consumer SDK.
- **Caller cancellation is no longer recorded as a cache error.** `cache.get_or_set`, `cache.factory`
  and every other operation span were marked `ActivityStatusCode.Error` with
  `cache.error.type=OperationCanceledException` when the caller's own token cancelled the call. In
  ASP.NET Core the ambient token is `HttpContext.RequestAborted`, so every client that navigated away
  mid-request produced error spans, making ordinary user behaviour indistinguishable from a Redis
  outage on an error-rate dashboard. Such a span is now tagged `cache.result=canceled` and left
  `Unset`; the `factory` layer of `caching.net.layer.duration` uses the same value, so a cancellation
  is never booked as a factory error. A cancellation Caching.NET did not ask for — an internal
  timeout, a disposed connection — still reports an error, because that is what it is.
- **The engine's per-operation log lines no longer flood production logs.** The internal engine logs
  every cache call, and every cache result, at `Information` — the level production runs at —
  measured at **2.04 lines per `GetOrSet`**, each carrying a full dump of the entry's resolved
  options. The engine exposes level knobs for its error categories but none for these, and the
  documentation's answer was to tell operators to suppress the whole `Caching.NET` category. Those
  lines are now rewritten to the new `Observability.EngineOperationLogLevel` (default `Debug`):
  measured 0 lines per 100 `GetOrSet` calls at `Information`, and all of them back at `Debug`. The
  rewrite also answers the engine's `IsEnabled(Information)` probe with the rewritten level, so a
  suppressed line is never formatted at all rather than formatted and dropped. Warnings and errors are
  never downgraded, and `CachingOptionsValidator` rejects setting any `Observability.*LogLevel`
  diagnostic property to `Information` while the rewrite is active, so a level an operator
  deliberately raised cannot be silently lowered again.
- **The readiness health check now detects a Redis outage in Hybrid mode.** Its probe read was
  served by L1 — the value its own write had just placed there — so it reported `Healthy` with Redis
  stopped, and never contacted the distributed layer even when Redis was up. The probe read now
  forces `SkipMemoryCacheRead`, and the probe write awaits the L2 operation with
  `ReThrowDistributedCacheExceptions`, when the cache has a distributed layer. Both are engine-only
  settings reached through `FusionCacheService`'s internal `ProbeSetAsync`/`ProbeTryGetAsync` helpers
  — deliberately not on the public `CacheEntryOverrides`, since `SkipMemoryCacheRead` is exactly the
  kind of mode-encoding flag the per-call override surface excludes by design — and both are skipped
  in InMemory mode, where bypassing memory would make every probe a miss. See
  [docs/HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md).
- **Probe entries no longer inherit the configured expirations.** The probe builds its
  `CacheEntryOverrides` explicitly (`LocalExpiration`/`DistributedExpiration` both pinned to 10
  seconds) instead of starting from the cache's configured defaults, so a configured
  `Entry.DistributedExpiration` of, say, 6 hours can no longer override the probe's own duration and
  leave the probe key in Redis for 6 hours. `JitterMaxDuration` and (on the read) `EagerRefreshThreshold`
  are cleared the same way, so the probe's TTL is at most 10 seconds in every layer regardless of
  configuration.
- **Packaging.** The README is now inside the NuGet package, and `PackageReleaseNotes` no longer
  points at a repository path that does not resolve from a package page. The symbol package is no
  longer produced: `DebugType=embedded` already ships the PDB inside the assembly, so `IncludeSymbols`
  built a `.snupkg` containing no symbols.

### Known behavior

- **Accepted performance trade-off: the telemetry-off in-memory hit path is ~15% slower than the
  pre-release `IFusionCache`-exposing baseline.** Measured against `329d8f4` (the commit immediately
  before this plan's first task, the last point where the engine's own contract was still public):
  **116.0 ns → 133.2 ns (+14.9%)** on a telemetry-off `GetOrSet` hit in InMemory mode, against a ≤2%
  spec gate; allocations are unchanged at 192 B both sides, so the cost is pure CPU —
  `FusionCacheService` now runs key/tag guard validation and `CacheEntryOverrides` resolution on every
  call, where the old surface called the engine directly. **Accepted**, not fixed in this release,
  because (1) it buys enforced key/tag guards on every call, which the exposed-engine-contract design
  could not enforce at all, and (2) the shipping default is faster in absolute terms than before this
  plan: with metrics on — the default — the same hit went **304.4 ns → 153.4 ns (−49.6%)**, because
  the old "metrics on" cost included the engine's own event-bridge dispatch, which this release
  replaced with direct, single-producer `RecordHit`/`RecordMiss` calls. Redis miss (−7.0%) and Hybrid
  full miss + factory (−6.5%) are also faster than the baseline. **The Redis *hit* row of that
  comparison no longer holds**: it read −5.8% when measured, but that was before the tag-marker fix
  above, i.e. while Redis-mode invalidation could be silently lost. Re-measured against the same
  baseline it is now **+192%** (115,827 ns → 338,110 ns) — but that figure is a composite of two
  independent changes with opposite signs, and should not be read as the contract's cost:

  | Step | Reading | Δ |
  |---|---:|---:|
  | Baseline `329d8f4` — engine exposed, markers local | 115,827 ns | — |
  | …after the engine-agnostic contract | 109,120 ns | **−5.8%** |
  | …after the tag-marker fix | 338,110 ns | **×3.10** |
  | Net vs. baseline | | **+192%** |

  The contract change made this row *faster*; the marker fix is what multiplied it, and `3 + n`
  commands per hit is the reason. The controlled measurement of the fix is therefore the ×3.10, not
  the +192%. The cross-session comparison is trustworthy because two rows untouched by the fix act as
  controls on the same machine — Redis miss moved +1.6% and Hybrid full miss + factory +4.5% between
  the two sessions, so the hit row's jump is not session drift. Full
  numbers and methodology: [docs/BENCHMARKS.md](docs/BENCHMARKS.md#baseline-comparison-the-engine-agnostic-contracts-cost).
- **Unobserved task exceptions during a Redis outage.** With background distributed operations on
  (the default) the engine schedules distributed reads, writes and backplane publishes as background
  tasks; when Redis goes away some fault with nothing awaiting them and reach
  `TaskScheduler.UnobservedTaskException`. Measured at three per 50 operations across a full outage,
  plus one `SocketClosed` on `UNSUBSCRIBE` when a cache is disposed mid-outage. It does not crash the
  process and the same failures are already on `caching.net.redis.errors`, but a host that subscribes
  to that event will see cache-layer noise during an incident. OPERATIONS.md carries the filtering
  guidance; it is not fixable without the delegating wrapper v3 exists to remove.
- **Physical Redis TTL is `Duration + FailSafeMaxDuration`** when fail-safe is on — a one-minute entry
  occupies Redis for two hours and one minute under the defaults, not the longer of the two. Verified
  directly against `TTL` on a live key; see OPERATIONS.md's capacity guidance.
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

