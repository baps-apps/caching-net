# Migrating from Caching.NET v2 to v3.0.0

v3.0.0 is a major redesign. The public API, the configuration section and the on-the-wire format all
change. There is no compatibility shim: the v2 surface is gone, deliberately, because keeping it
would have preserved exactly the limitation this release exists to remove — a four-method cache
interface that hid fail-safe, timeouts, eager refresh and factory context from applications.

Expect a **mechanical but non-trivial** migration: roughly one afternoon for a typical service.
Nothing about the internal cache engine needs to be understood or configured to complete it.

**Prerequisite: .NET 10.** v2 multi-targeted `net8.0`, `net9.0` and `net10.0`; v3 targets `net10.0`
only. An application on .NET 8 or .NET 9 cannot reference 3.0.0 at all — retarget first, or stay on
2.2.0.

---

## 1. What changed, in one paragraph

v2 shipped a custom `ICacheService` over three hand-written implementations (in-memory, Redis,
Microsoft `HybridCache`), with custom striped locks, a custom payload envelope, custom serializers
and a Polly resilience pipeline. v3 replaces all of it with one engine, exposes that engine's
operation contract (`IFusionCache`) as the cache API, and keeps registration, configuration,
security, connection management and observability under Caching.NET's own names. Applications gain
fail-safe, soft/hard timeouts, eager refresh, factory context, auto-recovery, backplane invalidation
and named caches; they lose a wrapper that did none of that.

---

## 2. Removed

### Interfaces and types

| Removed | Replacement |
|---|---|
| `Caching.NET.Abstractions.ICacheService` | `ZiggyCreatures.Caching.Fusion.IFusionCache` |
| `Caching.NET.CachingBuilder` (v2) | `Caching.NET.CachingBuilder` (v3) — same name, different members |
| `Caching.NET.Options.CacheOptions` | `Caching.NET.Options.CachingOptions` |
| `Caching.NET.Options.CacheCallOptions` | `FusionCacheEntryOptions` (per call) |
| `Caching.NET.Options.CacheSerializerOptions` | `CachingOptions.Serialization` |
| `Caching.NET.Extensions.CacheServiceCallExtensions` | `Caching.NET.Extensions.CacheExtensions` |
| `Caching.NET.Serialization.*` (`ICacheSerializer`, `JsonCacheSerializer`, `MessagePackCacheSerializer`, `PayloadEnvelope`, `PayloadCompression`) | `CachingOptions.Serialization.Format` and `.Compression` |
| `Caching.NET.Resilience.*` (`CacheResiliencePipelineBuilder`, `ResiliencePipelineNames`) | `CachingOptions.Resilience` |
| `Caching.NET.Telemetry.CacheInstruments` | `Caching.NET.Telemetry.CacheTelemetry` |
| `Caching.NET.CacheSchemaAttribute` | Payload framing + corrupt-payload rejection |
| `Caching.NET.Services.*` (`RoutingCacheService`, `InMemoryCacheService`, `RedisCacheService`, `HybridCacheService`) | Single engine |
| `StripedLockManager`, `StaleEntryTracker`, `StaleRefreshThrottle`, `TtlJitter`, `RuntimeTypedCacheInvoker`, `DriftLogSampler`, `RedisConnectionRotator`, `StableTypeHash` | Engine-provided or no longer needed |

### Registration methods

v3 keeps the v2 method names. Most v2 call sites therefore still **compile**, but they now bind a
different options shape, so an unmodified v2 configuration fails startup validation rather than
running with wrong values.

| v2 | v3 | Source-compatible? |
|---|---|---|
| `AddCaching()` | `AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("…"))` | **No** — the parameterless overload is gone; a prefix is required |
| `AddCaching(IConfiguration)` | `AddCaching(IConfiguration)` | Yes — but the `CacheOptions` section must be restructured (§3) |
| `AddCaching(Action<CachingBuilder>)` | `AddCaching(Action<CachingBuilder>)` | Signature only — the builder's methods changed (§4) |
| `AddCaching(IConfiguration, Action<CachingBuilder>)` | `AddCaching(IConfiguration, Action<CachingBuilder>)` | Signature only — as above |
| — | `AddCachingOptions(Action<CachingOptions>)`, `AddCaching(string cacheName, …)` | New in v3 |
| `AddCachingHealthChecks(...)` | `AddCachingHealthChecks(...)` | Yes |
| `ValidateCacheRegistration()` | `ValidateCachingRegistration()` | **No** — renamed |

### NuGet packages

Removed from the package's dependency graph: `Microsoft.Extensions.Caching.Hybrid`, `Polly`,
`Polly.Extensions`, `Polly.RateLimiting`, `Microsoft.Extensions.Options.DataAnnotations`.

Added: `ZiggyCreatures.FusionCache`, `ZiggyCreatures.FusionCache.Backplane.StackExchangeRedis`,
`ZiggyCreatures.FusionCache.Serialization.SystemTextJson`,
`ZiggyCreatures.FusionCache.Serialization.NeueccMessagePack`,
`Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Configuration.Binder`, and a
direct `StackExchange.Redis` reference. Applications still install only `Caching.NET`.

---

## 3. Configuration

The section name stays `CacheOptions`. Its contents are restructured into groups, so a v2 section
binds only partially and is rejected by startup validation — it will not run with silently wrong
values, but it will not run unchanged either.

### Property map

| v2 | v3 | Note |
|---|---|---|
| `Enabled` | `Enabled` | No longer hot-reloadable |
| `Mode` | `Mode` | Same three values |
| `KeyPrefix` | `ApplicationPrefix` | Plus `EnvironmentPrefix`, `TenantPrefix` |
| `RedisConnectionString` | `Redis.Configuration` | |
| `StrictRedisCertificateValidation` | `Redis.StrictCertificateValidation` | Now rejected at startup if TLS is off |
| `RedisOperationTimeout` | `Redis.CommandTimeout` and `Resilience.DistributedHardTimeout` | Split: client timeout vs cache timeout |
| `DefaultExpiration` | `DefaultExpiration` | |
| `HybridLocalCacheExpiration` | `Entry.LocalExpiration` | |
| — | `Entry.DistributedExpiration` | New |
| `TtlJitterPercentage` (0.10 = ±10%) | `Entry.JitterMaxDuration` (`00:00:02`) | Fraction → absolute duration |
| `MaximumKeyLength` | `Security.MaximumKeyLength` | |
| `MaximumPayloadBytes` | `Serialization.MaximumPayloadBytes` | |
| `EnablePayloadCompression` | `Serialization.Compression.Enabled` | |
| `PayloadCompressionThresholdBytes` | `Serialization.Compression.ThresholdBytes` | |
| — | `Serialization.Compression.MaximumDecompressedBytes` | New: decompression-bomb ceiling |
| `MemorySizeLimitMb` | `Entry.MemorySizeLimitMegabytes` | Now requires `Entry.Size` |
| `FailOpen`, `ThrowOnFailure` | `Resilience.ThrowOnDistributedCacheErrors`, `ThrowOnSerializationErrors`, `ThrowOnBackplaneErrors`, `ThrowOriginalExceptions` | Split by failure class |
| `FactoryTimeout` | `Resilience.FactoryHardTimeout` | Plus a new `FactorySoftTimeout` |
| `StripeLockCount` | — | Engine owns stampede protection |
| `StaleRefreshConcurrency` | — | Engine owns background refresh |
| `RequireTagSupport` | — | Tags work in every mode now |
| `IncludeRawKeyInLogs` | `Security.AllowRawKeysInLogs` | |
| `IncludeKeyHashInTraces` | — | No equivalent: the engine starts an operation span after entry options are resolved, so nothing can attach a key attribute to it without wrapping every call. Use `ICacheGuard.Fingerprint(key)` and tag your own span |
| `KeyValidator`, `KeyTransformer` | — | Use `CacheKey`/`ICacheKeyFactory` before the call |
| — | `Backplane.*`, `Resilience.AutoRecovery*`, `Resilience.EagerRefresh*`, `Observability.*` | New |

### Before

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "Hybrid",
    "KeyPrefix": "orders-api",
    "RedisConnectionString": "redis:6379,abortConnect=false",
    "DefaultExpiration": "00:10:00",
    "HybridLocalCacheExpiration": "00:03:00",
    "TtlJitterPercentage": 0.10,
    "MaximumKeyLength": 512,
    "MaximumPayloadBytes": 1048576,
    "FactoryTimeout": "00:00:30",
    "RedisOperationTimeout": "00:00:02"
  }
}
```

### After

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "Hybrid",
    "ApplicationPrefix": "orders-api",
    "EnvironmentPrefix": "prod",
    "DefaultExpiration": "00:10:00",
    "Entry": {
      "LocalExpiration": "00:03:00",
      "JitterMaxDuration": "00:00:02",
      "EagerRefreshThreshold": 0.8
    },
    "Resilience": {
      "FailSafeEnabled": true,
      "FactorySoftTimeout": "00:00:01",
      "FactoryHardTimeout": "00:00:30",
      "DistributedSoftTimeout": "00:00:00.500",
      "DistributedHardTimeout": "00:00:02"
    },
    "Redis": {
      "Configuration": "redis:6379,abortConnect=false",
      "CommandTimeout": "00:00:02"
    },
    "Backplane": { "Enabled": true },
    "Serialization": { "MaximumPayloadBytes": 1048576 },
    "Security": { "MaximumKeyLength": 512 }
  }
}
```

---

## 4. Registration

```csharp
// v2
services.AddCaching(builder.Configuration);

services.AddCaching(b => b
    .UseHybrid(redisConnectionString)
    .WithKeyPrefix("orders-api")
    .UseProductionDefaults()
    .WithTtlJitter(0.10)
    .WithHealthChecks());
```

```csharp
// v3
services.AddCaching(builder.Configuration);

services.AddCaching(cache => cache
    .UseHybrid(redisConnectionString)
    .WithApplicationPrefix("orders-api")
    .UseProductionDefaults()
    .WithJitter(TimeSpan.FromSeconds(2))
    .WithHealthChecks());
```

---

## 5. Call-site migration

| v2 | v3 |
|---|---|
| `cache.GetOrCreateAsync(key, factory, expiration, cancellationToken: ct)` | `cache.GetOrSetAsync(key, factory, duration, token: ct)` |
| `cache.SetAsync(key, value, expiration, cancellationToken: ct)` | `cache.SetAsync(key, value, duration, token: ct)` |
| `cache.GetAsync<T>(key, ct)` | `cache.GetOrDefaultAsync<T>(key, token: ct)` |
| `cache.GetAsync(key, type, ct)` (runtime-typed) | Removed — call the generic overload, or resolve the type at the call site |
| `cache.ExistsAsync(key, ct)` | `cache.ExistsAsync<T>(key, token: ct)` (extension) or `TryGetAsync<T>` |
| `cache.RemoveAsync(key, ct)` | `cache.RemoveAsync(key, token: ct)` |
| `cache.RemoveByTagAsync(tag, ct)` | `cache.RemoveByTagAsync(tag, token: ct)` |
| `cache.RefreshAsync(key, factory, …)` | `cache.RefreshAsync(key, factory, token: ct)` (extension) |
| `cache.GetManyAsync<T>(keys, ct)` | `cache.GetManyAsync<T>(keys, token: ct)` (extension) |
| `cache.SetManyAsync(items, …)` | `cache.SetManyAsync(items, token: ct)` (extension) |
| `cache.RemoveManyAsync(keys, ct)` | `cache.RemoveManyAsync(keys, token: ct)` (extension) |
| `cache.ClearAsync(ct)` | `cache.ClearAsync(token: ct)` |
| `new CacheCallOptions { Tags = [...] }` | `tags:` parameter on the call |
| `new CacheCallOptions { Mode = ... }` (per-call mode override) | Removed — register a named cache instead |
| `new CacheCallOptions { Bypass = true }` | `options => options.SetSkipMemoryCache(true).SetSkipDistributedCache(true, true)` |
| `new CacheCallOptions { ForceRefresh = true }` | `cache.RefreshAsync(...)` (extension) |

Notes:

- v3 methods return `ValueTask`/`ValueTask<T>`, not `Task`. `await` is unchanged; assigning to a
  `Task` variable needs `.AsTask()`.
- The parameter is `token:`, not `cancellationToken:`.
- Named parameter `expiration:` becomes a positional `TimeSpan duration` or
  `options => options.SetDuration(...)`.

### Before and after

```csharp
// v2
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;

public sealed class ProductService(ICacheService cache, IProductRepository repository)
{
    public Task<Product> GetAsync(string sku, CancellationToken ct) =>
        cache.GetOrCreateAsync(
            $"Product:{sku}",
            token => repository.LoadAsync(sku, token),
            expiration: TimeSpan.FromMinutes(10),
            localExpiration: TimeSpan.FromMinutes(1),
            cancellationToken: ct);

    public Task InvalidateCategoryAsync(int categoryId, CancellationToken ct) =>
        cache.RemoveByTagAsync($"category:{categoryId}", ct);
}
```

```csharp
// v3
using ZiggyCreatures.Caching.Fusion;

public sealed class ProductService(IFusionCache cache, IProductRepository repository)
{
    public ValueTask<Product> GetAsync(string sku, CancellationToken ct) =>
        cache.GetOrSetAsync(
            $"Product:{sku}",
            async token => await repository.LoadAsync(sku, token),
            options => options
                .SetDuration(TimeSpan.FromMinutes(10))
                .SetMemoryCacheDuration(TimeSpan.FromMinutes(1)),
            token: ct);

    public ValueTask InvalidateCategoryAsync(int categoryId, CancellationToken ct) =>
        cache.RemoveByTagAsync($"category:{categoryId}", token: ct);
}
```

Custom `ICacheService` mocks in tests should become real in-memory caches — a Caching.NET cache with
`Mode: InMemory` is cheap to build and exercises the real code path:

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddCaching(c => c.UseInMemory().WithApplicationPrefix("tests"));
var cache = services.BuildServiceProvider().GetRequiredService<IFusionCache>();
```

---

## 6. Observability

| v2 | v3 |
|---|---|
| `CacheInstruments.MeterName` / `.ActivitySourceName` (`"Caching.NET"`) | `CacheTelemetry.MeterName` / `.ActivitySourceName` (`"Caching.NET"`, unchanged) |
| `AddMeter(CacheInstruments.MeterName)` | `AddMeter(CacheTelemetry.MeterName)` — the singular name is the recommended wiring |
| `AddSource(CacheInstruments.ActivitySourceName)` | `AddSource(CacheTelemetry.ActivitySourceName)` — the singular name is the recommended wiring |
| — | `CacheTelemetry.MeterNames` / `.ActivitySourceNames` add the engine's own meters and sources: more detail, but the meters **double-count** every operation and the sources **export the raw cache key**. Opt in deliberately — see [TELEMETRY.md §2](TELEMETRY.md#2-the-integration-decision) |
| `cache.hits`, `cache.misses`, `cache.errors`, `cache.sets`, `cache.removes`, `cache.operation.duration`, `cache.payload.bytes`, … | `caching.net.hits`, `caching.net.misses`, `caching.net.errors`, `caching.net.operations`, `caching.net.payload.size`, … |
| `cache.miss_reason`, `cache.error_kind`, `cache.drift_kind`, `cache.pipeline`, `cache.circuit_state` dimensions | `cache.result`, `cache.layer`, `cache.error.type` |
| `WithOpenTelemetry()` builder hook (no-op) | Removed — use `CacheTelemetry` names directly |

**Dashboards and alerts referencing `cache.*` metric names must be updated to `caching.net.*`.**

---

## 7. Behaviour changes to plan for

1. **Cold cache on deploy.** The physical Redis key layout (prefix composition) and the L2 wire
   format (one-byte framing header) both change. Existing v2 entries are unreachable and expire by
   TTL; v3 repopulates on demand. Deploy during a period when a cache miss storm is acceptable, or
   pre-warm.
2. **`Enabled` is read once.** Flipping it at runtime no longer changes behaviour.
3. **Redis mode no longer serves from local memory.** v2's Redis mode kept an in-process layer; v3's
   does not. Read latency in Redis mode goes up, correctness goes up with it. Use `Hybrid` if the old
   behaviour is what you wanted.
4. **Jitter is absolute, not proportional.** `TtlJitterPercentage: 0.10` on a 10-minute TTL was ±60s;
   `Entry.JitterMaxDuration` default is +2s. Set it explicitly if you relied on wide spread.
5. **Fail-safe is on by default.** A failing factory now returns a stale value where one exists,
   instead of throwing. Set `Resilience.FailSafeEnabled: false` to keep the old behaviour.
6. **Validation is stricter.** Configurations v2 accepted may now fail at startup — for example a
   `DefaultExpiration` longer than `FailSafeMaxDuration`, or `Redis.Configuration` set while
   `Mode: InMemory`. The message names the fix.
7. **A Hybrid local lifetime may not exceed the distributed one.** `Entry.LocalExpiration` longer
   than `Entry.DistributedExpiration` is rejected at startup, comparing effective values (an unset
   duration falls back to `DefaultExpiration`). v2's `HybridLocalCacheExpiration` had no such rule,
   so a v2 configuration that set it above the shared TTL now fails the host until it is lowered.
8. **Hybrid without a backplane logs a startup warning** (event 3051) naming the stale window.
   `Backplane.Enabled` defaults to `false` when the cache is bound from configuration — only
   `UseHybrid(...)` turns it on — so a v2 Hybrid section migrated verbatim runs without cross-pod
   invalidation. Set it to `true` for anything with more than one replica.
9. **Named caches change key layout.** A named cache adds its name as a key segment.
10. **`ClearAsync` semantics.** Still scoped to the application prefix, still never `FLUSHDB`, but
    implemented via the engine's tagging rather than a Redis `SCAN` sweep.

---

## 8. Checklist

- [ ] Retarget the application to `net10.0` — 3.0.0 does not ship `net8.0` or `net9.0`.
- [ ] Bump the package reference to `3.0.0`.
- [ ] Restructure the `CacheOptions` section into groups (§3). The section name is unchanged, so a
      stale v2 section will not be flagged as missing — compare it against §3 key by key.
- [ ] Rename `KeyPrefix` → `ApplicationPrefix`; add `EnvironmentPrefix`.
- [ ] Recheck every `AddCaching(...)` call: the name is unchanged but the overloads and the builder
      are not (§2).
- [ ] Replace `ICacheService` injections with `IFusionCache`.
- [ ] Rewrite call sites per §5.
- [ ] Replace `ICacheService` mocks with a real in-memory Caching.NET cache.
- [ ] Update OpenTelemetry wiring to `CacheTelemetry.MeterName` / `.ActivitySourceName` (add the
      plural forms only after reading their cost in §6).
- [ ] Set `Logging:LogLevel:Caching.NET` to `Warning` in production — `Information` on that category
      is roughly one engine log line per cache operation.
- [ ] Update dashboards and alerts from `cache.*` to `caching.net.*`.
- [ ] Rename `ValidateCacheRegistration` → `ValidateCachingRegistration`.
- [ ] Decide on `Backplane.Enabled` for multi-pod Hybrid deployments.
- [ ] Review `Resilience.FailSafeEnabled` — on by default now.
- [ ] Review `Entry.JitterMaxDuration` — absolute, not proportional.
- [ ] Run the app in a non-production environment and read the startup summary line.
- [ ] Plan for a cold cache on the first deploy.
