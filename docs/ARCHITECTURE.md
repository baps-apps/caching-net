# Caching.NET v3 architecture

How the package is put together, and why. Consumer-facing documentation lives in the
[README](../README.md); this file is for people changing Caching.NET itself.

## 1. Layers

```text
Application
   │ IFusionCache · ICacheProvider · ICacheGuard · CacheExtensions · CacheKey
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
   ├─ InstrumentedCacheSerializer ─► PayloadCodec ─► JSON | MessagePack
   ├─ RedisBackplane            over the same multiplexer
   ├─ KeyGuardEntryOptionsProvider ─► CacheGuard
   ├─ CachingCategoryLogger<T>  ─► "Caching.NET" logging categories
   └─ CacheEventBridge          engine events ─► CacheTelemetryContext ─► Meter "Caching.NET"
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
4. `AddKeyedSingleton<IFusionCache>(name, …)` and `AddKeyedSingleton<ICacheGuard>(name, …)` —
   projections of the instance.
5. For the default cache, non-keyed `IFusionCache` and `ICacheGuard` aliases, resolved through
   `CacheInstance` rather than through the keyed `IFusionCache`.
6. `AddSingleton(new CacheRegistration(...))` — the enumerable `CacheProvider` is built from.
7. `TryAddSingleton<ICacheProvider, CacheProvider>()`, `TryAddSingleton<ICacheKeyFactory, …>()`,
   `TryAddEnumerable(IHostedService → CachingStartupService)`.

### Why the default cache is not registered through the engine's own DI helpers

The engine's `AddFusionCache(name)` registers a `LazyNamedCache` for names other than its own
default, and its provider takes `IEnumerable<IFusionCache>` in its constructor. Adding a non-keyed
`IFusionCache` alias that resolves through that provider creates a resolution cycle that deadlocks
`ServiceProvider` rather than throwing. Registering with `AddFusionCache()` (no name) avoids the
cycle but hard-codes the engine's own cache name — which would surface as `IFusionCache.CacheName`
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

The flags are set on `DefaultEntryOptions`, which is the only lever the engine offers: per-call
entry options replace the defaults entirely, and `FusionCacheEntryOptionsProvider` is consulted only
for calls that pass none. A call carrying a caller-constructed `FusionCacheEntryOptions` therefore
runs without the mode's skip flags *and* without the key-length guard. `cache.CreateEntryOptions`
duplicates the defaults and keeps the skip flags, so it is the documented way to build per-call
options. Closing the remaining gap at run time would require intercepting every cache method — the
wrapper this design exists to avoid — so it is closed at **build** time instead: the package ships
`Caching.NET.Analyzers`, whose `CACHENET001` diagnostic warns on any constructed
`FusionCacheEntryOptions`. Documented, tested, and caught by the compiler.

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

- **Metrics**: `CacheEventBridge` subscribes to the engine's event hub at construction and records
  through a per-cache `CacheTelemetryContext`, which holds pre-resolved tags and builds a stack-only
  `TagList` per measurement. Handlers run on the engine's event pump, off the caller's path.
- **Spans**: `InstrumentedCacheSerializer` emits `cache.serialize` / `cache.deserialize` from the
  Caching.NET activity source, only when a listener is attached. Operation-level spans come from the
  engine's own sources, surfaced through `CacheTelemetry.ActivitySourceNames`.
- **Logs**: `CachingCategoryLogger<T>` re-categorises engine output under `Caching.NET`.
  Caching.NET's own messages are source-generated in `CacheLogMessages`.

The event bridge captures the events hub at attach time rather than reading it on dispose — the
container may already have disposed the cache when the bridge is torn down, and unsubscribing must
never throw during shutdown.

## 7. Guards

| Guard | Where | Coverage |
|---|---|---|
| Key length | `KeyGuardEntryOptionsProvider`, invoked by the engine per operation | Every call that uses the configured default entry options |
| Key characters | `CacheKeyBuilder` | Keys built through `CacheKey` |
| Tag count/length | `ICacheGuard.ValidateTags` | Application-invoked |
| Payload size (write) | `InstrumentedCacheSerializer` | Every distributed write |
| Payload size (read) | `InstrumentedCacheSerializer` | Every distributed read |
| Payload framing | `PayloadCodec` | Every distributed read |
| Decompression ceiling | `PayloadCodec` | Every compressed read |

The two application-invoked guards are the price of exposing the engine's operation contract
directly; enforcing them would require intercepting every call, which is the wrapper this design
rejects. Both are documented as limitations in the README.

## 8. Adding a feature

- **A new knob** → add it to the matching `CachingOptions` group, map it in
  `CacheEngineFactory`, validate it in `CachingOptionsValidator`, add a builder method in the
  matching group of `CachingBuilder`, and add a mapping assertion to `CacheEngineMappingTests`.
- **A new operation** → first check whether `IFusionCache` already has it. Only add to
  `CacheExtensions` if it does something the contract does not (batching, a different default,
  a guard). Never add a pass-through rename.
- **A new metric** → add the instrument to `CacheTelemetry`, a recorder to `CacheTelemetryContext`,
  and a subscription in `CacheEventBridge` if it is event-driven. Keep dimensions inside the
  allow-list asserted by `CacheTelemetryTests`.

## 9. Test layout

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
