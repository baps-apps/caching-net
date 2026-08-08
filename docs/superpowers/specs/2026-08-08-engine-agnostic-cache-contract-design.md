# Engine-agnostic cache contract

**Date:** 2026-08-08 (telemetry design revised 2026-08-09)
**Status:** Approved, not yet implemented
**Applies to:** Caching.NET v3.0.0 (unreleased — no tag, not on `main`)

## Problem

Caching.NET v3 exposes `ZiggyCreatures.Caching.Fusion.IFusionCache` as its cache operation contract
and `FusionCacheEntryOptions` as its per-call options type. Consuming applications therefore name the
cache engine in their own source. Two consequences:

1. **The engine cannot be replaced.** Any future change of engine — a different library, or a
   hand-rolled implementation — is a breaking change for every consumer.
2. **Branding is inconsistent.** Everything else Caching.NET emits is branded `Caching.NET`: logging
   categories, meter, activity source, metric names, configuration section. The cache API is not.

A third consequence is structural rather than cosmetic. Because there is no wrapper, two guards
cannot be enforced and one footgun cannot be closed at run time:

- Passing a caller-constructed `FusionCacheEntryOptions` **replaces** the configured defaults
  wholesale, so the call runs without the mode's skip flags and without the key-length guard
  (`docs/ARCHITECTURE.md` §3). The package ships the `CACHENET001` analyzer purely to patch this at
  build time.
- The tag guard and the key guard are documented as "application-invoked" limitations
  (`docs/ARCHITECTURE.md` §7) because enforcing them would require intercepting every call.

## Goal

**True swappability.** The cache engine must be replaceable in a later version with no consumer
source change. Concretely: no type from `ZiggyCreatures.Caching.Fusion`, `StackExchange.Redis`, or
`Microsoft.Extensions.Caching.Memory` appears anywhere in Caching.NET's public API.

This reverses API design rule #1 in `CLAUDE.md` ("never wrap the cache operation contract") and the
reasoning recorded in `docs/ARCHITECTURE.md` §3 and §7. The reversal is deliberate. The rule was
written to avoid the maintenance cost of a pass-through wrapper; the decision here accepts that cost
in exchange for swappability, and recovers part of it by closing the guard gaps the rule created.

### Non-goals

- ILRepack or assembly internalization of the engine. The engine remains an ordinary transitive
  NuGet dependency; nothing in Caching.NET's API leads a consumer to it.
- Mirroring the engine's events hub or plugin model. Both leave the public API.
- Shipping a second engine implementation to prove the seam. The seam is the deliverable.
- Any v2 compatibility shim.

### Release shape

v3.0.0 is unreleased: no git tag, not merged to `main`, only a local `nupkgs/Caching.NET.3.0.0.nupkg`.
This work **folds into v3.0.0**. The `IFusionCache` surface never ships. Consumers see one migration
(v2 → v3), not two. No deprecation cycle, no v4.

## Public surface

### `ICacheService` — the operation contract

Eight verbs, async and sync. Matches the engine's operation verbs exactly; the engine's remaining
`IFusionCache` members are setup, plugin and introspection concerns that were never appropriate on a
consumer-facing API.

```csharp
namespace Caching.NET;

public interface ICacheService
{
    string CacheName { get; }

    ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key, TValue? defaultValue = default,
        CacheEntryOverrides? options = null, CancellationToken token = default);

    ValueTask<CacheValue<TValue>> TryGetAsync<TValue>(
        string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    ValueTask SetAsync<TValue>(
        string key, TValue value, CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null, CancellationToken token = default);

    ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default);
    ValueTask ExpireAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default);
    ValueTask RemoveByTagAsync(string tag, CacheEntryOverrides? options = null, CancellationToken token = default);
    ValueTask ClearAsync(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default);

    // Eight synchronous twins with the same parameters, returning TValue / CacheValue<TValue> / void:
    // GetOrSet, GetOrDefault, TryGet, Set, Remove, Expire, RemoveByTag, Clear.
}
```

### `CacheValue<TValue>` — read result

Replaces the engine's `MaybeValue<T>`. A readonly struct so the hit path allocates nothing.

```csharp
namespace Caching.NET;

public readonly struct CacheValue<TValue>
{
    public bool HasValue { get; }
    public TValue Value { get; }                                  // throws when empty
    public TValue? GetValueOrDefault(TValue? fallback = default);
    public static CacheValue<TValue> None { get; }
    public static CacheValue<TValue> Of(TValue value);
    public void Deconstruct(out bool hasValue, out TValue? value);
}
```

### `CacheFactoryContext<TValue>` — factory execution context

Replaces the engine's `FusionCacheFactoryExecutionContext<T>`. Preserves the capabilities the v3
CHANGELOG advertises: stale value access, ETag / `NotModified`, and adaptive expiration.

```csharp
namespace Caching.NET;

public sealed class CacheFactoryContext<TValue>
{
    public bool HasStaleValue { get; }
    public CacheValue<TValue> StaleValue { get; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public CacheEntryOverrides Overrides { get; }   // adaptive expiration for this execution
    public TValue NotModified();
    public TValue Fail(string reason);              // trigger fail-safe without throwing
}
```

`Fail(reason)` lets a factory signal a soft upstream failure — a non-exceptional error response —
and have fail-safe serve the stale value, without manufacturing an exception. The engine's
per-execution `Tags` is deliberately not mirrored: tags already arrive as an argument to
`GetOrSetAsync`, and two ways to set them would need a precedence rule for no gain.

`ApplyOverrides` mutates the engine context's options in place rather than assigning a new instance,
because the engine's idiom is in-place mutation and the property may be get-only.

### `CacheEntryOverrides` — per-call options

Replaces the engine's `FusionCacheEntryOptions`. Every property is nullable; `null` means "inherit
the configured default".

```csharp
namespace Caching.NET.Options;

public sealed class CacheEntryOverrides
{
    public TimeSpan? LocalExpiration { get; set; }
    public TimeSpan? DistributedExpiration { get; set; }
    public TimeSpan? JitterMaxDuration { get; set; }
    public float?    EagerRefreshThreshold { get; set; }

    public bool?     FailSafe { get; set; }
    public TimeSpan? FailSafeMaxDuration { get; set; }
    public TimeSpan? FailSafeThrottleDuration { get; set; }

    public TimeSpan? FactorySoftTimeout { get; set; }
    public TimeSpan? FactoryHardTimeout { get; set; }
    public TimeSpan? DistributedSoftTimeout { get; set; }
    public TimeSpan? DistributedHardTimeout { get; set; }

    public bool? AllowBackgroundDistributedOperations { get; set; }
    public bool? AllowBackgroundBackplaneOperations { get; set; }
    public bool? EnableAutoClone { get; set; }

    public CacheEntryPriority? Priority { get; set; }
    public long? Size { get; set; }

    /// <summary>Suppresses the cross-instance invalidation broadcast for this write. Other
    /// instances keep serving their current L1 copy until it expires on its own.</summary>
    public bool? SkipBackplaneNotification { get; set; }
}

public enum CacheEntryPriority { Low, Normal, High, NeverRemove }
```

`SkipBackplaneNotification` is the one engine skip flag that is exposed, because it carries no mode
semantics — unlike `SkipMemoryCache` and `SkipDistributedCache`, which encode the cache mode and are
therefore deliberately unreachable per call. Without it, bulk warm-up (writing many entries at
startup without invalidating every other instance's L1 once per entry) regresses from possible to
impossible.

#### Additive semantics — the structural win

The engine's own type **replaces** the defaults. `CacheEntryOverrides` is **additive by
construction**: the adapter starts from a copy of the configured defaults (which carries the mode's
skip flags) and applies only the non-null properties.

```csharp
// Before — drops the mode skip flags and the key-length guard.
await cache.SetAsync("k", v, new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes(1) });

// After — overrides the duration, inherits everything else, guards intact.
await cache.SetAsync("k", v, new CacheEntryOverrides { DistributedExpiration = TimeSpan.FromMinutes(1) });
```

This removes the need for `cache.CreateEntryOptions()` as a documented workaround and removes the
original justification for the `CACHENET001` analyzer.

#### Field split rule

A knob appears in `CacheEntryOverrides` only if overriding it *per call* is meaningful.

| Scope | Where it lives |
|---|---|
| Per entry — expirations, jitter, eager refresh, fail-safe, factory/distributed timeouts, background ops, priority, size, auto-clone | `CachingOptions` (defaults) **and** `CacheEntryOverrides` (per call) |
| Per instance — `MemorySizeLimitMegabytes`, auto-recovery, circuit-breaker durations, serialization, backplane, security, observability, Redis | `CachingOptions` only |

`CachingOptions` and its nested groups keep their current role unchanged: registration-time
configuration for one cache instance, bound from the `CacheOptions` section. They are already
engine-neutral.

### Removed from the public surface

| Removed | Replacement |
|---|---|
| `IFusionCache` from `ICacheProvider.Default` / `GetCache` / `GetCacheOrNull` | `ICacheService` |
| `IFusionCache` / `FusionCacheEntryOptions` from `CacheExtensions` | `ICacheService` / `CacheEntryOverrides` |
| `IFusionCache` DI registrations, keyed and non-keyed | `ICacheService`, keyed and non-keyed |
| `RedisOptions.ConfigureConnection` | `Redis.Configuration` connection string plus the two typed TLS members below |
| `CachingBuilder.UseRedis(Action<ConfigurationOptions>)`, `UseHybrid(Action<ConfigurationOptions>, bool)` | Connection-string overloads plus `WithRedis(Action<RedisOptions>)` |
| `CacheEntryOptions.Priority` typed as `CacheItemPriority` | `CacheEntryPriority` |
| `CacheTelemetry.EngineActivitySourceNames`, `EngineMeterNames`, `EngineKeyAttributeName` | Removed outright — see [Telemetry](#telemetry). Caching.NET emits the equivalent signal itself |
| Plugins and the events hub | Not exposed. Observability is the Caching.NET meter, traces and logs |

### Added to the public surface

| Added | Purpose |
|---|---|
| `CacheSecurityOptions.AllowRawKeysInTelemetry` (default `false`) | Opt in to `cache.key` on spans instead of `cache.key.fingerprint` |
| `CacheTelemetryAttributes.Key` (`"cache.key"`) | Attribute name for the raw key, emitted only under that flag |
| `CacheObservabilityOptions.EnableLayerMetrics` (default `true`) | Gates the per-layer duration histogram |
| `RedisOptions.ClientCertificate` (`X509Certificate2?`) | TLS client certificate; replaces the one common `ConfigureConnection` use a connection string cannot express |
| `RedisOptions.ValidateServerCertificate` (`RemoteCertificateValidationCallback?`) | Extra server-certificate validation, run after Caching.NET's own |

Both replacement members use BCL types, so neither reintroduces a `StackExchange.Redis` reference.

#### What removing `ConfigureConnection` genuinely costs

`ConfigureConnection` is a working code-first configuration path, not a validation loophole:
`RedisConnectionProvider.BuildConfiguration` starts from `new ConfigurationOptions()` when
`Redis.Configuration` is empty, so the delegate is the only thing that could supply endpoints in that
case. Removing it therefore makes `Redis.Configuration` genuinely required for `Redis` and `Hybrid`
modes, and the validator message must stop offering the delegate as an alternative.

A StackExchange.Redis connection string already expresses endpoints, `ssl`, `sslHost`, `password`,
`user`, `abortConnect`, `allowAdmin`, `checkCertificateRevocation`, `configCheckSeconds`, `proxy`,
`serviceName` and `defaultDatabase`, and the rest of `RedisOptions` covers timeouts, retries,
keep-alive, client name and database. With the two typed TLS members added, the remaining losses are:

- Sentinel `CommandMap`
- `ReconnectRetryPolicy`, `BacklogPolicy`
- `SocketManager`, `LoggerFactory`

None has an engine-free expression. They are accepted as lost and documented; each can be added as a
typed member later if an application needs one.

## Internals

### Composition

```text
CacheEngineFactory
   ├─ builds IFusionCache exactly as today (option mapping, memory cache, Redis connection,
   │  serializer, backplane, logger adapter, event bridge — all unchanged)
   └─ final step: new FusionCacheService(inner, guard, options)

CacheInstance.Cache : ICacheService          (was IFusionCache)
   holds the engine reference only for disposal ordering

CacheProvider.Default / GetCache / GetCacheOrNull → ICacheService
DI: keyed and non-keyed ICacheService. IFusionCache is never registered.
```

`Internal/FusionCacheService` is the only type in the codebase that calls an engine *operation*.
`CacheEngineFactory` remains the only type that performs engine *setup*. Swapping engines means
adding a sibling implementation of `ICacheService` and changing one line in `CacheEngineFactory`.

The name is deliberately engine-specific so that the file that must be replaced on a swap is
obvious.

The resolution-cycle workaround recorded in `docs/ARCHITECTURE.md` §2 — registering the default
cache's non-keyed alias through `CacheInstance` rather than through the keyed service — still applies
unchanged.

### Options mapping

```csharp
// Internal/FusionCacheService.cs
private FusionCacheEntryOptions? Resolve(CacheEntryOverrides? o)
{
    if (o is null)
    {
        return null;                              // engine uses its configured defaults
    }

    var e = _inner.CreateEntryOptions();          // duplicate of the defaults; skip flags preserved
    if (o.DistributedExpiration is { } d) e.DistributedDuration = d;
    if (o.FailSafe is { } fs) e.IsFailSafeEnabled = fs;
    // ... non-null properties only
    return e;
}
```

### Guards

`Internal/KeyGuardEntryOptionsProvider` is deleted. It fires only on calls that carry no explicit
entry options, which is the hole this design closes.

No replacement arithmetic is needed. `CacheGuard.ValidateKey` already computes
`_prefixLength + key.Length`, where `_prefixLength` is `BuildKeyPrefix().Length` plus the separator —
exactly the string the engine passes to `ValidatePhysicalKey` today, because the engine's
`CacheKeyPrefix` is built from the same value. The adapter therefore calls `ValidateKey(key)` on
every call and `ValidateTags(tags)` on every call that supplies them, and `ValidatePhysicalKey` is
deleted alongside the provider.

The engine's `v2:` wire prefix and `Redis.InstancePrefix` are outside the measured length today and
stay outside it. Including them would tighten every configured limit — a behaviour change beyond
this design's goal.

Both rows in the `docs/ARCHITECTURE.md` §7 guard table currently marked "application-invoked" become
enforced.

### Disabled cache

`CacheEngineFactory` currently returns the engine's `NullFusionCache` when `Enabled` is `false`. It
returns a new `Internal/NullCacheService` instead: reads miss, writes are discarded, get-or-set
factories run on every call. No engine object is constructed at all.

### Unchanged

`PayloadCodec`, `RedisConnectionProvider`, `CachingCategoryLogger`, `CachingStartupService`, health
checks, key builders, `CachingOptionsValidator`, and the whole backplane path — `RedisBackplane` over
the shared multiplexer, channel-prefix derivation, auto-recovery, circuit breaking, and
`CacheBackplaneOptions`. The adapter sits above the engine; backplane wiring is below it.

## Telemetry

Registering the engine's activity sources and meters is what makes the engine visible to an
operator, exports raw physical cache keys, and double-counts every operation. Caching.NET stops
publishing those names and emits the equivalent signal itself. An `ActivitySource` with no listener
produces nothing, so not publishing the names removes the duplication entirely rather than
suppressing it.

```csharp
public static class CacheTelemetry
{
    public const string ActivitySourceName = "Caching.NET";
    public const string MeterName          = "Caching.NET";

    public static readonly string[] ActivitySourceNames = [ActivitySourceName];
    public static readonly string[] MeterNames          = [MeterName];
}
```

`Telemetry/EngineTelemetryNames.cs` is deleted. No `Engine*` or `Detailed*` members remain.

### Where each signal is produced

Every seam the engine instruments is reachable from code Caching.NET owns or can decorate. Three of
the six already exist.

| Layer | Instrumented by | Status |
|---|---|---|
| Operation | `Internal/FusionCacheService` | new |
| Factory | `Internal/FusionCacheService`, wrapping the caller's delegate | new |
| L1 memory | `Internal/InstrumentedMemoryCache` decorating the `MemoryCache` from `CacheEngineFactory` | new |
| L2 Redis | `Internal/InstrumentedDistributedCache` decorating `RedisCache` | new |
| Serialization | `Internal/InstrumentedCacheSerializer` | exists |
| Backplane | `Internal/InstrumentedBackplane` | exists; gains publish spans |

Span names follow the existing `CacheLayers` values: `cache.get_or_set`, `cache.set`, `cache.remove`,
`cache.expire`, `cache.remove_by_tag`, `cache.clear`, `cache.try_get`, `cache.get_or_default`,
`cache.factory`, `cache.memory.get`, `cache.memory.set`, `cache.redis.get`, `cache.redis.set`,
`cache.serialize`, `cache.deserialize`, `cache.backplane.publish`.

`cache.get_or_default` carries no `cache.result` tag. Deriving one would mean replacing the engine's
`GetOrDefault` with a `TryGet` plus a caller-side default, and the two are not known to agree on
stale-value handling under fail-safe — an unnecessary behavioural bet for a tag. The outcome is
already visible on that span's `cache.memory.get` / `cache.redis.get` children, which observe it
directly.

### One producer per signal

The decorators observe hits and misses that `CacheEventBridge` also observes. Recording both would
double-count inside a single meter, so the sources of truth are split:

| Signal | Producer |
|---|---|
| Operation result (`get_or_set` hit/miss/stale), factory execution | adapter |
| L1 and L2 hit/miss, per-layer duration | decorators |
| Fail-safe activation, eager refresh, memory eviction, circuit-breaker open, factory synthetic timeout, backplane publish/receive, serialization and deserialization errors | event bridge — the only place these are observable |

`CacheEventBridge` drops the `Hit`, `Miss`, `Set`, `Remove`, `RemoveByTag`, `Clear`, `Expire`,
`FactorySuccess` and `FactoryError` subscriptions, shrinking from 22 to about 12. Each dropped
subscription also removes one engine event-args construction and one background dispatch per
operation, so the change is a net reduction in work on the operation path.

### Correcting the Hybrid layer attribution

[`CacheEventBridge.OnHit`] currently attributes every Hybrid hit to `cache.layer=memory`, because the
engine's `Hit` event does not report which level answered. With the L1 and L2 decorators the level is
observed directly, so `caching.net.hits{cache.layer=redis}` becomes correct for a Hybrid hit served
by L2. This is a bug fix that arrives with the design rather than a separate change.

### New instrument

```csharp
internal static readonly Histogram<double> LayerDuration =
    Meter.CreateHistogram<double>("caching.net.layer.duration", "ms", "Per-layer operation duration.");
```

Dimensions `cache.layer`, `cache.operation`, `cache.result`, on top of the standard base tags. This
is the signal the engine's per-level meters provided. Gated by
`CacheObservabilityOptions.EnableLayerMetrics`, default `true`.

### Cache keys on spans

Default: `cache.key.fingerprint`, the xxHash64 already produced by `Internal/KeyFingerprint` and
exposed as `ICacheGuard.Fingerprint`. Fingerprints are not anonymization — a small, guessable key
space is trivially brute-forced — but they prevent accidental identifier capture and survive
correlation across logs, spans and tickets.

`CacheSecurityOptions.AllowRawKeysInTelemetry` (default `false`) switches the adapter to emit
`cache.key` with the **caller's** key instead. The physical key is never emitted. Enabling it exports
whatever the application puts in its cache keys — tenant ids, user ids, record ids — to the tracing
backend, where span attributes are indexed, retained under that backend's policy, and readable by
everyone with trace access. Treat enabling it as a data-flow change, not a debug toggle.

### Zero cost when instrumentation is off

`InstrumentedBackplane.Wrap` already returns the bare backplane when metrics are disabled. The two
new decorators follow the same pattern, and `CacheEngineFactory` installs neither when
`Observability.EnableMetrics` and `Observability.EnableTracing` are both `false`. A cache with
telemetry disabled runs with the bare `MemoryCache` and `RedisCache` and pays nothing beyond the
single adapter call.

### Backplane receive stays metrics-only

Backplane receive runs on the engine's subscription callback thread, outside any request context, and
its latency is the invalidation propagation delay. Spans are emitted on publish only; receive
continues to record metrics through the event bridge.

### Hot-path cost

Added:

- **Hit, telemetry off:** one extra virtual call. `CacheValue<T>` is a struct; `options: null`
  short-circuits mapping; decorators are not installed. Zero added allocations.
- **Hit, metrics on:** one decorator virtual call per L1/L2 probe, one counter and one histogram
  record. `TagList` is a stack struct, so still zero heap allocations.
- **Any config, tracing on with a sampling listener:** four to six spans per operation instead of
  one. This is deliberate — it is the per-layer detail the engine sources used to provide.
- **Miss:** one closure plus one `CacheFactoryContext<T>` per factory execution.
- **Any call passing non-null overrides:** one `FusionCacheEntryOptions`. Today the *caller*
  allocates that object, so this is net neutral.

Removed:

- Nine `CacheEventBridge` subscriptions, each of which currently costs one engine event-args
  construction and one background dispatch per operation.

The removal is plausibly larger than the additions on the metrics-on path. It is measured, not
assumed — see the benchmark gates below.

## Enforcement

`PublicApiTests` gains an assertion that `tests/Caching.NET.Tests/Api/PublicApi.approved.txt`
contains zero occurrences of `ZiggyCreatures`, `StackExchange`, and
`Microsoft.Extensions.Caching.Memory`. The approved-API diff is already the breaking-change review
gate; this makes a leak fail the build rather than depend on a reviewer noticing it.

`CACHENET001` is repurposed rather than deleted. `src/Caching.NET.Analyzers/CacheEntryOptionsAnalyzer.cs`
currently flags constructed `FusionCacheEntryOptions`, a construct consumers can no longer reach.
The same diagnostic ID is retitled *"Caching.NET engine type referenced directly"* and flags any
`ZiggyCreatures.Caching.Fusion.*` or `StackExchange.Redis.*` symbol in consumer code. Reusing the ID
is safe because v3 is unshipped. The analyzer continues to ship inside the Caching.NET package under
`analyzers/dotnet/cs`.

## Testing

### Migrated call sites

Roughly 30 files across `tests/`, `samples/`, `benchmark/`, `aot/` and `tests/Caching.NET.Tests.Pod`.
Substitutions are mechanical:

| From | To |
|---|---|
| `IFusionCache` | `ICacheService` |
| `MaybeValue<T>` | `CacheValue<T>` |
| `new FusionCacheEntryOptions { … }` | `new CacheEntryOverrides { … }` |
| `cache.CreateEntryOptions()` | deleted — the adapter does this |

### New tests

Behaviours the previous design could not express:

- **Overrides are additive.** Per mode, a call passing `CacheEntryOverrides` still carries the mode's
  skip flags. Asserted in `CacheEngineMappingTests`.
- **Guards fire with overrides present.** Key-length and tag violations are caught on calls that pass
  overrides, not only on calls that omit them.
- **Factory context round-trip.** Stale value visibility, ETag with `NotModified`, and adaptive
  expiration produce the observed TTLs — verified against real elapsed behaviour, not only mapped
  fields.
- **`NullCacheService` parity.** Same observable semantics as the engine's null object for every verb.
- **No engine types in the approved API.** The enforcement assertion described above.
- **Hybrid L2 hit is attributed to `cache.layer=redis`.** Integration test: warm Redis, clear L1,
  read, assert the metric dimension. Fails today.
- **No double counting.** One `GetOrSet` miss produces exactly one
  `caching.net.operations{cache.result=miss}` and one `caching.net.factory.executions`. Belongs in
  the `caching-net-metrics` collection and filters by cache name.
- **Spans carry a fingerprint, never a key**, and carry `cache.key` only when
  `AllowRawKeysInTelemetry` is set. `SpanKeyExposureTests` is rewritten to pin both states; its
  engine-span test is deleted along with the engine sources.
- **Backplane unchanged.** The cross-process `Caching.NET.Tests.Pod` suite and the chaos
  backplane-loss and restart tests pass unmodified except for the mechanical type substitution.
- **`SkipBackplaneNotification` suppresses the broadcast.** Cross-process test: write with the flag
  set, assert the other process keeps its L1 copy.
- **Telemetry off installs no decorators.** Assert the engine receives the bare `MemoryCache`.

### Conventions

Unchanged from `CLAUDE.md`: prefer a real in-memory Caching.NET cache over a mock; integration and
chaos tests poll for the observable outcome rather than sleeping, except where a TTL is the subject;
tests asserting the absence of metrics belong in the `caching-net-metrics` collection.

## Documentation

| File | Change |
|---|---|
| `CLAUDE.md` | API design rule #1 inverts: never *expose* the engine contract. Add the "contract is eight verbs, permanently" rule |
| `docs/ARCHITECTURE.md` | §1 layer diagram; §3 loses the skip-flag-gap paragraph; §7 guard table flips two rows to enforced; §8 feature guidance |
| `README.md` | Every example rewritten against `ICacheService` and `CacheEntryOverrides` |
| `CHANGELOG.md` | v3.0.0 entry rewritten; drop the plugins-and-events claim |
| `docs/MIGRATION-V2-TO-V3.md` | Rewritten against the new contract |
| `docs/TELEMETRY.md` | Substantial rewrite: one source, one meter, the new span catalogue, `caching.net.layer.duration`, `EnableLayerMetrics`, and the removal of every engine-source instruction |
| `docs/SECURITY.md` | Guard coverage table reflects enforced guards; new section on `AllowRawKeysInTelemetry` and what enabling it exports |
| `docs/BENCHMARKS.md` | The three telemetry tiers with absolute numbers |
| `docs/OPERATIONS.md` | Dashboard and alert guidance rewritten against branded instruments only |
| `docs/audits/2026-08-08-v3.0.0-production-readiness-review.md` | **Re-run, not edited.** It is the release gate with measured evidence against the current surface |

`docs/MIGRATION-V1-TO-V2.md` and `docs/V2.0.0-RELEASE-IMPACT.md` are historical and are not touched.

## Sequencing

Eight phases, each independently green.

1. Add `ICacheService`, `CacheValue<T>`, `CacheFactoryContext<T>`, `CacheEntryOverrides`,
   `CacheEntryPriority`, `Internal/FusionCacheService`, `Internal/NullCacheService` and the overrides
   mapper. The engine surface stays registered alongside; both paths work.
2. Flip DI, `ICacheProvider` and `CacheExtensions` to `ICacheService`. Remove the engine
   registrations. Move guard enforcement into the adapter and delete
   `Internal/KeyGuardEntryOptionsProvider`.
3. Leak scrub: `CacheEntryPriority` mapping, `RedisOptions.ConfigureConnection` and the
   `CachingBuilder` `ConfigurationOptions` overloads.
4. Telemetry, part one: strip `CacheTelemetry` to the branded source and meter, delete
   `Telemetry/EngineTelemetryNames.cs`, add `caching.net.layer.duration`,
   `AllowRawKeysInTelemetry` and `EnableLayerMetrics`. Emit operation and factory spans from the
   adapter.
5. Telemetry, part two: add `InstrumentedMemoryCache` and `InstrumentedDistributedCache` with
   conditional installation, add backplane publish spans, and shrink `CacheEventBridge` to the
   engine-only events.
6. Migrate the ~30 call-site files.
7. Repurpose `CACHENET001`, regenerate `PublicApi.approved.txt`, add the banned-namespace assertion.
8. Rewrite the documentation, record the benchmark tiers, re-run the release-gate audit.

## Risks

| Risk | Mitigation |
|---|---|
| **Factory-context fidelity.** The engine reads mutations the factory made to its context *after* the factory returns. `CacheFactoryContext.Overrides` must be applied back onto the engine context at the right moment or TTLs are silently wrong | Dedicated test class plus integration tests asserting real observed TTLs |
| **Feature drift.** The engine gains a capability the contract lacks | The contract is eight verbs, permanently. New capability lands as a `CachingOptions` knob or a `CacheEntryOverrides` field, never a ninth verb. Recorded in `CLAUDE.md` |
| **A missing knob blocks a consumer**, with no escape hatch by design | The contract covers all eight verbs and every per-entry knob on day one. A gap is a one-line addition and a patch release |
| **Internal metric double-counting** between the decorators and the event bridge | One producer per signal, table above; asserted by a test that counts instruments for a single operation |
| **L1 decorator on a hot path.** Every engine `IMemoryCache` probe gains a virtual call, and a histogram record when layer metrics are on | Decorators are not installed when both telemetry switches are off; `EnableLayerMetrics` gates the histogram separately; benchmark tiers below |
| **Span volume under tracing.** Four to six spans per operation instead of one | Deliberate — it replaces the engine sources. Measured and published rather than gated |
| **Background spans are orphaned.** Background distributed writes, auto-recovery and backplane receive run off the caller's async context, so their spans are roots | Tag `cache.background_operation=true` and document it. Replicating the engine's operation-id correlation is out of scope |
| **Native AOT** | The new generic types are reflection-free; `aot/Caching.NET.AotSmoke` covers them |

### Performance gates

Replaces a single percentage. Absolute nanoseconds are published alongside each, in
`docs/BENCHMARKS.md`. A new benchmark measures the same operation at three telemetry tiers — off,
metrics on, metrics and tracing on — so the cost of each tier is a number rather than an argument.

| Path | Gate |
|---|---|
| `GetOrSet` hit, InMemory, telemetry off | ≤2%, zero added allocations |
| `GetOrSet` hit, InMemory, metrics on | ≤10% |
| `GetOrSet` hit and miss, Redis and Hybrid | ≤2% |
| Tracing enabled, all modes | measured and published, no gate |

## Done means

- `tests/Caching.NET.Tests/Api/PublicApi.approved.txt` contains zero occurrences of
  `ZiggyCreatures`, `StackExchange`, and `Microsoft.Extensions.Caching.Memory`.
- An application registering only `CacheTelemetry.ActivitySourceNames` and
  `CacheTelemetry.MeterNames` sees cache operation spans in all three modes, per-layer spans and
  durations, and no duplicated instrument.
- A Hybrid hit served by L2 is recorded as `cache.layer=redis` in both the span and the metric.
- No span carries a cache key unless `AllowRawKeysInTelemetry` is set.
- `dotnet test` is green, including the Docker-backed integration and chaos suites and the
  cross-process `Caching.NET.Tests.Pod` backplane suite.
- `aot/Caching.NET.AotSmoke` passes.
- Every performance gate above is met, with absolute numbers recorded in `docs/BENCHMARKS.md`.
- The release-gate audit has been re-run against the new surface and is clean.
