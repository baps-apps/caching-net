# Caching.NET v3 architecture

How the package is put together, and why. Consumer-facing documentation lives in the
[README](../README.md); this file is for people changing Caching.NET itself.

## 1. Layers

```text
Application
   │ ICacheService · ICacheProvider · ICacheGuard · CacheExtensions · CacheKey
   ▼
Caching.NET registration
   ServiceCollectionExtensions ─► CachingOptions ─► CachingOptionsValidator
   │
   ▼
CacheEngineFactory                      ← the only place engine setup happens
   ├─ FusionCacheOptions  (mapped from CachingOptions)
   ├─ FusionCacheEntryOptions (mapped, mode-adjusted)
   ├─ MemoryCache               owned, optional size limit
   ├─ RedisCache                over RedisConnectionProvider's shared multiplexer
   ├─ InstrumentedMemoryCache / InstrumentedDistributedCache   layer decorators ─► per-layer spans + caching.net.layer.duration
   ├─ InstrumentedCacheSerializer ─► PayloadCodec ─► JSON | MessagePack
   ├─ RedisBackplane            over the same multiplexer, wrapped by InstrumentedBackplane
   │                            (subscribe/unsubscribe/publish failures ─► caching.net.backplane.errors)
   ├─ CacheGuard                key/tag limits, invoked by FusionCacheService on every call
   ├─ CachingCategoryLogger<T>  ─► "Caching.NET" logging categories
   └─ CacheEventBridge          engine events (fail-safe, eager refresh, background factory, eviction) ─► CacheTelemetryContext ─► Meter "Caching.NET"
   │
   ▼
FusionCacheService : ICacheService   the only type that calls an engine operation; owns every
   cache.* span, and records hits/misses/operations/foreground invalidations synchronously
   │
   ▼
CacheInstance   owns and disposes: event bridge → cache → distributed adapter → memory → connection
```

## 2. Registration flow

`AddCaching(...)` per cache:

1. `CacheRegistrationTracker.ForServices(services).Claim(name, isDefault)` — duplicate names and a
   second default registration throw immediately. The tracker instance lives in the service
   collection, not in a static, so independent containers never interfere.
2. Named options: `AddOptions<CachingOptions>(name)` → `Bind(section)` → `Configure` (cache name
   is forced from the registration, never from configuration) → `PostConfigure` (fluent builder, so
   code wins over configuration) → `ValidateOnStart`.
3. `AddKeyedSingleton<CacheInstance>(name, …)` — the owning graph.
4. `AddKeyedSingleton<ICacheService>(name, …)` and `AddKeyedSingleton<ICacheGuard>(name, …)` —
   projections of the instance. There is no `IFusionCache` registration anywhere in the container;
   `FusionCacheService` (Caching.NET's implementation of `ICacheService`) holds the engine instance
   privately.
5. For the default cache, non-keyed `ICacheService` and `ICacheGuard` aliases, resolved through
   `CacheInstance` rather than through the keyed `ICacheService`.
6. `AddSingleton(new CacheRegistration(...))` — the enumerable `CacheProvider` is built from.
7. `TryAddSingleton<ICacheProvider, CacheProvider>()`, `TryAddSingleton<ICacheKeyFactory, …>()`,
   `TryAddEnumerable(IHostedService → CachingStartupService)`.

### Why the default cache is not registered through the engine's own DI helpers

The engine's `AddFusionCache(name)` registers a `LazyNamedCache` for names other than its own
default, and its provider takes `IEnumerable<IFusionCache>` in its constructor. Adding a non-keyed
`ICacheService` alias that resolves through that provider creates a resolution cycle that deadlocks
`ServiceProvider` rather than throwing. Registering with `AddFusionCache()` (no name) avoids the
cycle but hard-codes the engine's own cache name — which would surface as `ICacheService.CacheName`
and in the `cache.name` telemetry dimension.

Caching.NET therefore composes the cache itself in `CacheEngineFactory` and registers it directly.
That keeps `CacheName` under Caching.NET's control, removes the cycle, and removes any dependency on
the engine's DI conventions. Cost: about forty lines of composition.

## 3. Mode mapping

| Caching.NET | Engine entry options |
|---|---|
| `InMemory` | `SetSkipDistributedCache(true, skipBackplaneNotifications: true)`; no Redis components created |
| `Redis` | `SetSkipMemoryCache(true)` — memory locker still active, memory *cache* bypassed for entries |
| `Hybrid` | Neither skip flag; both layers active |

`SkipMemoryCache` is what makes Redis mode authoritative. The memory *locker* is a separate
component from the memory *cache*, so concurrent callers are still serialised on one lock per key.

That is not the same as single-flight, and the difference is measurable. After taking the lock the
engine re-checks the *memory cache* before running the factory; that re-check is the step that turns
"one lock holder at a time" into "one factory execution". Redis mode bypasses the memory cache, so
the re-check can never hit, and one extra caller runs the factory before the value becomes visible
through the distributed layer.

| Mode | Factory executions for N concurrent callers on one cold key |
|---|---|
| `InMemory` | 1 |
| `Hybrid` | 1 |
| `Redis` | 2 — one extra per cold key, not per caller |

Measured at N = 50 and pinned by `StampedeScopeTests`. No mode is distributed single-flight: two
processes racing the same cold key each run their own factory, because the locker is a per-process
object with no distributed lease behind it. Anything that must happen exactly once globally (an
increment, a send, a charge) does not belong in a cache factory in any mode.

### 3.1 The mode also has to reach the tag markers

`RemoveByTag` and `Clear` are not implemented as a sweep over keys. The engine writes one *marker*
entry per tag — `Clear` uses two reserved tags — and every read compares its own entry's timestamp
against the marker. A marker is therefore an ordinary cache entry, with its own lifetime and its own
layer placement, configured by `FusionCacheOptions.TagsDefaultEntryOptions` rather than by
`DefaultEntryOptions`. The engine's defaults for it are deliberately long-lived: **ten days, memory
layer included.**

Applying the mode only to `DefaultEntryOptions` therefore left invalidation outside the mode, and that
was not a tuning detail — it silently broke `RemoveByTag` and `Clear`:

- In `Redis` mode, entries skipped the memory layer but markers did not, so an instance's first read
  cached "no marker exists" locally for ten days. Redis mode registers no backplane, so nothing could
  ever evict that copy: an invalidation was invisible to every instance that had already served the
  key once — which, under real traffic, is every instance. A *cold* reader saw it, which is why a test
  that read only through the instance performing the removal passed.
- In `Hybrid` mode without a backplane, `Entry.LocalExpiration` bounds how long a stale local copy can
  be served, but markers ignored that bound and kept their own ten-day memory lifetime — so the
  documented guarantee held for an overwrite and not for a tag invalidation.

`CacheEngineFactory.MapTagsEntryOptions` brings markers under the mode:

| Mode | Marker placement |
|---|---|
| `InMemory` | `SetSkipDistributedCache(true, skipBackplaneNotifications: true)` — mirrors the entry mapping |
| `Redis` | `SetSkipMemoryCache(true)` — a marker is as authoritative as an entry, at the cost of extra Redis round trips per read (see README, "Redis mode") |
| `Hybrid` | `MemoryCacheDuration = Entry.LocalExpiration ?? DefaultExpiration` — may live in L1, may not outlive the bound every other local copy obeys |

The marker's *logical* and *distributed* lifetimes keep the engine's long defaults on purpose: that is
what makes an invalidation durable in Redis, so an instance that was offline when it happened still
observes it on startup. Only the marker's placement in, and lifetime within, the in-process layer is
brought under the mode. Pinned by `CacheEngineMappingTests`, and end-to-end from a *warm* reader by
`RedisModeTests.TagInvalidation_IsSeenByAnInstanceThatAlreadyReadTheKey`,
`RedisModeTests.Clear_IsSeenByAnInstanceThatAlreadyReadTheKey` and
`HybridModeTests.WithoutABackplane_TagInvalidationIsAlsoBoundedByTheLocalExpiration`.

The entry flags are set on `DefaultEntryOptions` and the marker flags on `TagsDefaultEntryOptions`,
which are the only levers the engine offers: per-call entry options replace the defaults entirely at
the engine boundary.

That boundary is exactly why `CacheEntryOverrides` — the public per-call options type — is designed
to be **additive by construction** rather than a caller-visible `FusionCacheEntryOptions`. Every
property on it is nullable and starts `null`; `null` means "use the configured value". `FusionCacheService`
resolves a caller's `CacheEntryOverrides` by taking a fresh copy of the cache's own default entry
options (`inner.CreateEntryOptions()` — mode skip flags and all already applied) and mutating onto it
only the properties the caller actually set — see `CacheEntryOverridesMapper.Resolve`. The key and tag
guards do not ride on entry options at all: `FusionCacheService` runs them itself before the engine is
called, so no per-call options path can skip them. There is no way to
construct a `CacheEntryOverrides` that starts from a blank slate: the type has no equivalent of
`new FusionCacheEntryOptions()`, so a per-call override can add to the cache's mode and guard
behaviour but can never replace or escape it. This is also why the engine-only settings a health
probe needs — `SkipMemoryCacheRead`, `ReThrowDistributedCacheExceptions` — are deliberately absent
from `CacheEntryOverrides`: exposing them would let an application-level call reach past the mode
boundary the same way a raw `FusionCacheEntryOptions` used to. `Health.CachingHealthCheck` reaches
them instead through `FusionCacheService`'s internal `ProbeSetAsync`/`ProbeTryGetAsync`, which operate
below the public contract on purpose (see docs/HEALTH-CHECKS.md).

## 4. Key layout

The logical key Caching.NET builds:

```text
{ApplicationPrefix}[:{EnvironmentPrefix}][:{TenantPrefix}][:{CacheName}]:{caller key}
```

`CacheName` is appended only for non-default caches — that is what keeps two named caches in one
application from sharing a Redis key space.

The **physical Redis key** is not that string. The engine prepends its wire-format segment, and the
Redis adapter prepends `Redis.InstancePrefix` outside everything:

```text
[{Redis.InstancePrefix}]v2:{ApplicationPrefix}[:{EnvironmentPrefix}][:{TenantPrefix}][:{CacheName}]:{caller key}
```

```text
orders-api, prod, key "Order:1"     ->  v2:orders-api:prod:Order:1
named cache "hot" on orders-api     ->  v2:orders-api:hot:Order:1
InstancePrefix "legacy::"           ->  legacy::v2:orders-api:Order:1
```

`v2` is the engine's wire-format version, not something Caching.NET picks. An engine release that
bumps it changes every key and therefore cold-starts the cache, which is why
`PhysicalKeyLayoutTests` asserts the literal string rather than a wildcard: operators write eviction
policies, key scans and runbooks against it.

The backplane channel prefix defaults to the same prefix (without the trailing separator), so
applications sharing a Redis instance never receive each other's invalidations.

## 5. Wire format

```text
byte 0 : 0x00 = raw, 0x01 = Brotli
bytes 1..n : serialized payload
```

The header is written unconditionally, so toggling compression does not orphan existing entries. An
unrecognised header is rejected as corrupt rather than guessed at — a poisoned or truncated Redis
value becomes a miss, not a deserialization of attacker-controlled bytes. Brotli decompression is
read in bounded chunks against `Compression.MaximumDecompressedBytes`.

## 6. Telemetry pipeline

Spans and the synchronous metrics come from the adapter and the layer decorators now, not from the
engine's own diagnostics — the engine's activity sources and meters are never registered, so every
signal a consumer sees originates inside Caching.NET. Each signal has exactly one producer; recording
the same outcome from two places double-counts it.

- **Spans**: `FusionCacheService` owns every `cache.*` operation span (`cache.get_or_set`,
  `cache.get_or_default`, `cache.try_get`, `cache.set`, `cache.remove`, `cache.expire`,
  `cache.remove_by_tag`, `cache.clear`, and the nested `cache.factory` span created only when the
  factory actually runs). The layer decorators add child spans for their own layer
  (`InstrumentedMemoryCache` → `cache.memory.get`/`set`/`remove`; `InstrumentedDistributedCache` →
  `cache.redis.get`/`set`/`refresh`/`remove`). `InstrumentedCacheSerializer` emits `cache.serialize` /
  `cache.deserialize`. All of it comes from the single `Caching.NET` activity source, only when a
  listener is attached.
- **Metrics, synchronous producer**: `FusionCacheService` records `caching.net.hits`,
  `caching.net.misses`, `caching.net.operations` and `caching.net.invalidations`
  (`remove`/`expire`/`remove_by_tag`/`clear`) directly, once per logical call, on the caller's own path — no engine event round trip.
  `InstrumentedMemoryCache`/`InstrumentedDistributedCache` record `caching.net.layer.duration`
  the same way, once per physical probe of that layer.
- **Metrics, event-pump producer**: `CacheEventBridge` subscribes to the engine's event hub at
  construction and records through the same per-cache `CacheTelemetryContext`. It owns
  `caching.net.factory.executions` (foreground and background), the factory part of
  `caching.net.errors`, `caching.net.fail_safe.served`, `caching.net.evictions`, and
  `caching.net.background.operations` (eager refresh, backplane
  publish/receive) — signals only the engine can attribute correctly, because it alone knows which of
  its own code paths triggered a given factory invocation (see `CacheEventBridge`'s remarks for the
  double-counting measurement that motivated the split). These handlers run on the engine's
  background event pump, off the caller's path.
- **Logs**: `CachingCategoryLogger<T>` re-categorises engine output under `Caching.NET`.
  Caching.NET's own messages are source-generated in `CacheLogMessages`.

The event bridge captures the events hub at attach time rather than reading it on dispose — the
container may already have disposed the cache when the bridge is torn down, and unsubscribing must
never throw during shutdown.

## 7. Guards

| Guard | Where | Coverage |
|---|---|---|
| Key length | `ICacheGuard.ValidateKey`, invoked by `FusionCacheService` at the start of every call | Every call, in `FusionCacheService` |
| Key characters | `CacheKeyBuilder` | Keys built through `CacheKey` |
| Tag count/length | `ICacheGuard.ValidateTags`, invoked by `FusionCacheService` whenever a call supplies tags | Every call, in `FusionCacheService` |
| Payload size (write) | `InstrumentedCacheSerializer` | Every distributed write |
| Payload size (read) | `InstrumentedCacheSerializer` | Every distributed read |
| Payload framing | `PayloadCodec` | Every distributed read |
| Decompression ceiling | `PayloadCodec` | Every compressed read |

Both key and tag guards now run inside `FusionCacheService` itself, ahead of every call to the
engine — not only for calls that fall back to the engine's own configured-default hook. This is a
direct consequence of owning the adapter: `ICacheService` no longer hands the caller's key or tags to
the engine before Caching.NET has seen them, so there is no per-call options path that bypasses the
guard the way a caller-constructed `FusionCacheEntryOptions` used to.

## 8. Adding a feature

- **A new knob** → add it to the matching `CachingOptions` group, map it in
  `CacheEngineFactory`, add a matching field to `CacheEntryOverrides` and
  `CacheEntryOverridesMapper` when it is per-call, validate it in `CachingOptionsValidator`, add a
  builder method in the matching group of `CachingBuilder`, and add a mapping assertion to
  `CacheEngineMappingTests`.
- **A new operation** → first check whether the eight `ICacheService` verbs already cover it. Only add
  to `CacheExtensions` if it does something the contract does not (batching, a different default,
  a guard). Never add a pass-through rename, and never add a ninth verb to `ICacheService` itself.
- **A new metric** → add the instrument to `CacheTelemetry`, a recorder to `CacheTelemetryContext`,
  and pick its producer from §6's split: `FusionCacheService` if it belongs on the caller's
  synchronous path, `CacheEventBridge` if only the engine's event pump can attribute it correctly, or
  a layer decorator if it is per-layer. Keep dimensions inside the allow-list asserted by
  `CacheTelemetryTests`.

## 9. Accepted exception: `IFusionCacheSerializer` in test/benchmark code

`InstrumentedCacheSerializer`'s constructor takes an `IFusionCacheSerializer` — the engine's own
serializer abstraction — because that is genuinely what it decorates. Two files outside `src/`
construct one directly to exercise it: `tests/Caching.NET.Tests/Internal/SerializerAndRedactionTests.cs`
and `benchmark/Caching.NET.Benchmark/SerializationBenchmarks.cs`. This was reviewed and accepted
rather than hidden behind a Caching.NET-owned wrapper interface: `IFusionCacheSerializer` is
`internal` to `InstrumentedCacheSerializer`'s constructor and never reaches a public signature, so
`PublicApiTests.NoEngineTypeAppearsInAPublicSignature` passes with no allow-list entry needed, and
`Caching.NET.Analyzers`' `CACHENET001` exempts the `Caching.NET` and `Caching.NET.Tests*` assemblies
by design (see `EngineTypeAnalyzer`) precisely so the code that builds and verifies the adapter can
still name the engine. Adding a wrapper abstraction here would duplicate the engine's serializer
contract for no capability gained — the mistake API design rule 1 already rejects for the operation
contract itself.

## 10. Test layout

| Project | Needs Docker | Covers |
|---|---|---|
| `Caching.NET.Tests` | no | Registration, DI lifetimes, configuration binding, validation, option→engine mapping, in-memory behaviour, serializer/codec, guards, telemetry, extensions, health checks |
| `Caching.NET.Tests.Properties` | no | Payload codec round-trip invariants, key-builder invariants, fingerprint determinism |
| `Caching.NET.Tests.Integration` | yes | Redis and Hybrid modes end to end, multi-instance, backplane, tags, isolation, corrupt/oversized payloads, named caches |
| `Caching.NET.Tests.Chaos` | yes | Redis unavailable at startup, outage, restart, backplane loss, fail-safe, timeout fallback, log-storm suppression |

`Caching.NET.Tests.Pod` is not a test project: it is a console cache instance that the integration
suite launches as a separate OS process, so cross-process behavior (write visibility, L1
invalidation, remove, tag invalidation, clear, application isolation, pod restart) is exercised
against real processes rather than two service providers sharing one CLR.

Integration and chaos tests poll for observable outcomes instead of sleeping for a fixed duration,
except where a TTL is the thing under test.
