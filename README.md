# Caching.NET

Shared .NET caching for BAPS applications. One package, one registration call, three modes:
**InMemory**, **Redis**, and **Hybrid** (L1 memory + L2 Redis with cross-instance invalidation).

**v3.0.0 is a major redesign.** See [Migration from v2](#24-migration-from-v2) and
[docs/MIGRATION-V2-TO-V3.md](docs/MIGRATION-V2-TO-V3.md).

---

## Contents

1. [Overview](#1-overview) · 2. [Supported modes](#2-supported-cache-modes) · 3. [Architecture](#3-architecture) ·
4. [Installation](#4-installation) · 5. [In-Memory configuration](#5-in-memory-configuration) ·
6. [Redis configuration](#6-redis-configuration) · 7. [Hybrid configuration](#7-hybrid-configuration) ·
8. [Registration](#8-dependency-injection-registration) · 9. [Cache API usage](#9-cache-api-usage) ·
10. [Named caches](#10-named-caches) · 11. [Factories](#11-factory-usage) ·
12. [Per-entry options](#12-per-entry-options) · 13. [Fail-safe](#13-fail-safe) · 14. [Timeouts](#14-timeouts) ·
15. [Tagging](#15-tagging) · 16. [Invalidation](#16-invalidation) · 17. [Tracing](#17-opentelemetry-tracing) ·
18. [Metrics](#18-opentelemetry-metrics) · 19. [Logging](#19-structured-logging) ·
20. [Redis outage behaviour](#20-redis-outage-behaviour) · 21. [Kubernetes](#21-kubernetes-guidance) ·
22. [Security](#22-security-guidance) · 23. [Performance](#23-performance-guidance) ·
24. [Migration from v2](#24-migration-from-v2) · 25. [Breaking changes](#25-breaking-changes) ·
26. [Troubleshooting](#26-troubleshooting) · 27. [Feature matrix](#27-feature-matrix) ·
28. [Known limitations](#28-known-limitations)

---

## 1. Overview

Caching.NET v3 gives applications a full-featured cache without asking them to assemble one.

- **One package.** `Caching.NET` brings everything: memory layer, Redis client, serializers,
  backplane, telemetry.
- **One registration call.** `services.AddCaching(configuration)`. No cache engine to register,
  no serializer to pick, no backplane to wire, no distributed-cache adapter to configure.
- **One configuration section.** `CacheOptions`, strongly typed and validated at startup.
- **A complete cache API.** Not a four-method wrapper: get-or-set with factory context, fail-safe,
  soft and hard timeouts, eager refresh, tags, remove-by-tag, clear, per-entry options, background
  operations, auto-recovery, events, and cancellation are all available.
- **Branded observability.** Everything Caching.NET emits is named `Caching.NET`: logging
  categories, meter, activity source, metric names.

### What it is built on

Caching.NET v3 uses **[FusionCache](https://github.com/ZiggyCreatures/FusionCache)** as its internal
cache engine, and exposes FusionCache's `IFusionCache` as the cache **operation contract**.
Applications never register, configure, or reference FusionCache themselves — Caching.NET owns
registration, configuration, lifecycle, connection management, security limits, and observability.

Why the operation contract is exposed rather than wrapped is explained in
[§3.2 Public API decision](#32-public-api-decision).

---

## 2. Supported cache modes

| Mode | L1 (memory) | L2 (Redis) | Backplane | Use it when |
|---|---|---|---|---|
| `InMemory` | authoritative | — | — | Single instance, or per-instance data that does not need sharing |
| `Redis` | not used for entries | authoritative | not applicable | Every instance must read exactly what Redis holds |
| `Hybrid` | read-through cache of L2 | authoritative | recommended | Multi-pod services that want L1 latency with L2 sharing |

### Hybrid request flow

```text
Request
   |
   v
Check L1 memory
   |
   |-- Hit --> Return
   |
   |-- Miss
          |
          v
      Check L2 Redis
          |
          |-- Hit --> Populate L1 --> Return
          |
          |-- Miss
                 |
                 v
             Execute factory  (one caller per key; the rest wait)
                 |
                 +--> Store in L2
                 +--> Store in L1
                 +--> Return
```

### How Redis mode really works

FusionCache is designed around an L1 memory layer with an optional L2. Caching.NET's `Redis` mode is
implemented as **"L2 only"**: the memory layer is still allocated, but every entry read and write
sets `SkipMemoryCacheRead` / `SkipMemoryCacheWrite`, so no value is ever served from local memory.

What that means concretely:

- **Redis is authoritative.** A value deleted directly in Redis disappears from the application on
  the very next read. (Verified by an integration test that deletes the key behind the cache's back.)
- **Stampede protection still works, minus one execution per cold key.** The in-process *memory
  locker* is independent of the memory *cache*, so concurrent callers for one key are still
  serialised on a single lock. But the engine's post-lock re-check reads the memory *cache*, which
  this mode bypasses, so it never hits: **50 concurrent callers on a cold key run the factory twice,
  not once and not fifty times.** `InMemory` and `Hybrid` run it once. Measured and pinned by
  `StampedeScopeTests`.
- **Fail-safe still works.** Stale entries live in Redis with their logical-expiration metadata.
- **Every read costs a Redis round trip.** That is the point of the mode; if you want L1 latency,
  use `Hybrid`.
- **A backplane is rejected in this mode** at startup validation: there are no local entries to
  invalidate, so enabling it would only add traffic.
- **The skip flags live on the cache's default entry options.** A call that supplies its own entry
  options replaces them wholesale, and the engine has no cache-wide switch that could reimpose them
  (the only alternative would be the delegating wrapper this release exists to remove). Build
  per-call options with `cache.CreateEntryOptions(o => ...)`, which starts from the configured
  defaults and keeps the flags; `new FusionCacheEntryOptions()` does not, and re-enables L1 for that
  call. Pinned by `RedisModeEntryOptionsTests`, and **the package ships an analyzer** —
  `CACHENET001` warns wherever entry options are constructed instead of derived, so the gap is
  caught at build time rather than in production.

---

## 3. Architecture

```text
Application code
        │  injects IFusionCache  /  ICacheProvider  /  ICacheGuard
        ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Caching.NET                                                          │
│                                                                      │
│  AddCaching(...)  ──►  CachingOptions  ──►  validation         │
│          │                                                           │
│          ▼                                                           │
│  CacheEngineFactory  (the only place engine setup happens)           │
│     ├─ memory layer         (owned MemoryCache, optional size limit) │
│     ├─ distributed layer    (RedisCache over a shared multiplexer)   │
│     ├─ serializer           (JSON or MessagePack + framing/limits)   │
│     ├─ backplane            (Redis pub/sub over the same connection) │
│     ├─ key guard            (per-operation key-length enforcement)   │
│     ├─ logger adapter       (re-categorises output as "Caching.NET") │
│     └─ event bridge         (engine events ─► Caching.NET metrics)   │
│                                                                      │
│  CacheInstance  ── owns and disposes the whole graph                 │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.1 What Caching.NET owns

| Concern | Owner |
|---|---|
| Package and branding | Caching.NET |
| Registration and DI lifetimes | Caching.NET |
| Configuration schema and validation | Caching.NET |
| Redis connection lifecycle and TLS policy | Caching.NET |
| Serializer selection, payload framing, size limits, compression | Caching.NET |
| Backplane setup and channel naming | Caching.NET |
| Key namespacing and key/tag limits | Caching.NET |
| Logging categories, meter, metric names, activity source | Caching.NET |
| Cache **operations** (get, set, get-or-set, remove, tags, …) | the engine, surfaced as `IFusionCache` |

### 3.2 Public API decision

**Decision: expose `IFusionCache` as the operation contract; own everything else.**

The alternatives were weighed as follows.

| Option | Verdict |
|---|---|
| Small custom interface (`GetAsync`/`SetAsync`/`GetOrSetAsync`/`RemoveAsync`) | **Rejected.** This is what v2 did. It costs applications fail-safe, timeouts, eager refresh, factory context, ETags, adaptive expiration and per-entry options, and it is exactly what this redesign set out to remove. |
| Full wrapper reproducing the engine's method surface | **Rejected.** Roughly 80 overloads to mirror, and every engine release becomes a Caching.NET release. Pure maintenance cost, zero capability gained. |
| `interface ICacheNet : IFusionCache` | **Rejected.** Inheriting the interface still requires an implementation, and the only way to implement it is a full delegating wrapper — the option above with extra steps. |
| C# type aliases / `TypeForwardedTo` | **Rejected.** `using` aliases do not cross assembly boundaries, so every consumer would have to declare their own. Type-forwarding types the assembly never owned is an abuse of the mechanism and confuses tooling. |
| **Expose `IFusionCache`; Caching.NET owns registration, configuration, lifecycle, security, observability** | **Chosen.** Full capability, zero duplication, nothing to re-implement when the engine ships a new overload. |

**What this means in practice.** `IFusionCache` is the *operational* API contract — the type you
inject and call. It is not a configuration surface: an application never calls `AddFusionCache`,
never constructs `FusionCacheOptions`, never registers a serializer or backplane, and never
references a FusionCache package. The one visible consequence is that `IFusionCache` and
`FusionCacheEntryOptions` appear in method signatures and `using` directives.

Caching.NET adds, in its own namespaces:

- `ICacheProvider` — named-cache resolution (`Default`, `GetCache(name)`, `CacheNames`).
- `ICacheGuard` — key/tag limit checks and non-reversible key fingerprints.
- `CacheExtensions` — batch reads/writes/removals, existence probing, forced refresh. Only methods
  that add something the operation contract does not already have; nothing is renamed.
- `CachingOptions` and friends, `CachingBuilder`, `CacheTelemetry`, `CacheKey`, health checks.

---

## 4. Installation

```bash
dotnet add package Caching.NET --version 3.0.0
```

That is the only package an application installs. Redis client, serializers, backplane and memory
cache arrive transitively.

**Target framework: `net10.0` only.** v2 multi-targeted `net8.0`/`net9.0`/`net10.0`; v3 does not. An
application on .NET 8 or .NET 9 stays on 2.2.0 until it moves to .NET 10.

> **Why one package rather than `Caching.NET` + `Caching.NET.Redis` + `Caching.NET.OpenTelemetry`?**
> A split was evaluated. It would save memory-only applications one transitive Redis dependency, but
> it would also mean `Mode: "Redis"` failing at runtime with "install another package", a second
> publish pipeline, and version-skew between the two. Caching.NET is an internal package consumed by
> many services that mostly *do* use Redis, and v2 already shipped as one package. Single package
> wins on consumer simplicity. Revisit if memory-only services report a real dependency problem.
> Note that OpenTelemetry is **not** a dependency at all — see [§17](#17-opentelemetry-tracing).

---

## 5. In-Memory configuration

No Redis server, no Redis connection, no distributed components registered.

```json
{
  "CacheOptions": {
    "Mode": "InMemory",
    "ApplicationPrefix": "orders-api",
    "DefaultExpiration": "00:10:00"
  }
}
```

```csharp
builder.Services.AddCaching(builder.Configuration);
```

Or entirely in code:

```csharp
builder.Services.AddCaching(cache => cache
    .UseInMemory()
    .WithApplicationPrefix("orders-api")
    .WithDefaultExpiration(TimeSpan.FromMinutes(10)));
```

Optional memory cap (requires a per-entry size so the limit can be enforced):

```csharp
.WithMemorySizeLimit(megabytes: 256, defaultEntrySize: 1)
```

## 6. Redis configuration

```json
{
  "CacheOptions": {
    "Mode": "Redis",
    "ApplicationPrefix": "orders-api",
    "EnvironmentPrefix": "prod",
    "Redis": {
      "Configuration": "redis-0.cache.svc:6379,abortConnect=false",
      "Database": 0,
      "UseTls": true,
      "StrictCertificateValidation": true,
      "ConnectTimeout": "00:00:05",
      "CommandTimeout": "00:00:02"
    }
  }
}
```

Credentials belong in the connection string supplied by a secret, never in a checked-in file.
Caching.NET redacts `password=` and `user=` before anything reaches a log.

## 7. Hybrid configuration

```json
{
  "CacheOptions": {
    "Mode": "Hybrid",
    "ApplicationPrefix": "orders-api",
    "EnvironmentPrefix": "prod",
    "DefaultExpiration": "00:10:00",
    "Entry": {
      "LocalExpiration": "00:01:00",
      "DistributedExpiration": "00:30:00",
      "EagerRefreshThreshold": 0.8
    },
    "Redis": { "Configuration": "redis-0.cache.svc:6379,abortConnect=false" },
    "Backplane": { "Enabled": true }
  }
}
```

`UseHybrid(...)` in the fluent builder enables the backplane by default. **A cache bound from
configuration does not** — `Backplane.Enabled` defaults to `false`, so the block above sets it
explicitly. Keep it on for multi-pod deployments: without it, a pod keeps serving its own L1 copy
until `Entry.LocalExpiration` elapses. Hybrid without a backplane is allowed (a single replica has
nothing to invalidate) but logs a startup warning naming the stale window you are accepting.

`Entry.LocalExpiration` must not exceed `Entry.DistributedExpiration` in Hybrid mode; startup
validation rejects it. A longer local lifetime means the in-process copy outlives the authoritative
Redis entry, so the instance answers with data every other instance has already refetched — a split
view that no single-instance test can show.

## 8. Dependency-injection registration

Four entry points, all in `Caching.NET.Extensions`:

```csharp
using Caching.NET.Extensions;

// 1. From configuration (also registers anything under CacheOptions:NamedCaches).
builder.Services.AddCaching(builder.Configuration);

// 2. From configuration, with code-first overrides layered on top (fluent wins).
builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithEnvironmentPrefix(builder.Environment.EnvironmentName)
    .WithHealthChecks(splitLivenessReadiness: true));

// 3. Entirely from code.
builder.Services.AddCaching(cache => cache
    .UseHybrid(redisConnectionString)
    .WithApplicationPrefix("orders-api"));

// 4. Strongly typed options delegate.
builder.Services.AddCachingOptions(options =>
{
    options.Mode = CacheMode.Hybrid;
    options.ApplicationPrefix = "orders-api";
    options.DefaultExpiration = TimeSpan.FromMinutes(10);
    options.Redis.Configuration = redisConnectionString;
});
```

Fail fast on a wiring mistake:

```csharp
var app = builder.Build();
app.Services.ValidateCachingRegistration();
```

### Lifetimes

| Service | Lifetime | Notes |
|---|---|---|
| `IFusionCache` (default cache) | Singleton | Resolves the same instance from any scope |
| `IFusionCache` keyed by cache name | Singleton | `[FromKeyedServices("name")]` |
| `ICacheProvider` | Singleton | Frozen lookup table, no mutable state |
| `ICacheGuard` (default + keyed) | Singleton | |
| `ICacheKeyFactory` | Singleton | `TryAdd`; register your own **before** `AddCaching` to replace it |

No scoped service is captured, no root provider is stored in a static, and no static mutable
dictionary is used for cache resolution. Registering the same cache name twice throws at
registration time with the offending name in the message.

### Startup validation

`IValidateOptions<CachingOptions>` runs with `ValidateOnStart`, so a misconfigured cache fails
the host at boot. Every failure is reported at once, scoped to the cache name, and names the fix.
Covered: empty/invalid `ApplicationPrefix` and `CacheName`, `':'` in a prefix, Redis or Hybrid
without Redis settings, Redis settings in `InMemory` mode, backplane without Redis, backplane in
Redis mode, zero/negative expirations, soft timeout > hard timeout, fail-safe max duration below the
expiration, invalid jitter, invalid eager-refresh threshold, memory size limit without an entry
size, non-positive payload/key/tag limits, key limit shorter than the prefix, decompression ceiling
below the payload limit, negative Redis database, non-positive Redis timeouts and retry count, and
permissive TLS without TLS.

Disabling the cache (`Enabled: false`) skips validation entirely, so a service can ship with caching
off and no Redis settings.

## 9. Cache API usage

```csharp
using ZiggyCreatures.Caching.Fusion;   // the operation contract

public sealed class ProductService(IFusionCache cache, IProductRepository repository)
{
    public async Task<Product?> GetAsync(string sku, CancellationToken cancellationToken)
        => await cache.GetOrSetAsync<Product?>(
            $"products:{sku}",
            async token => await repository.LoadAsync(sku, token),
            token: cancellationToken);
}
```

Core operations: `GetOrSetAsync`, `GetOrDefaultAsync`, `TryGetAsync`, `SetAsync`, `RemoveAsync`,
`ExpireAsync`, `RemoveByTagAsync`, `ClearAsync`, `CreateEntryOptions` — each with a synchronous
counterpart, per-entry options, tags and a `CancellationToken`.

Caching.NET adds batch and convenience operations in `Caching.NET.Extensions`:

```csharp
using Caching.NET.Extensions;

var hits    = await cache.GetManyAsync<Order>(keys, token: ct);
await cache.SetManyAsync(items, tags: ["batch"], token: ct);
await cache.RemoveManyAsync(keys, token: ct);
var exists  = await cache.ExistsAsync<Order>(key, token: ct);
var updated = await cache.RefreshAsync(key, ct => LoadAsync(ct), token: ct);
```

Build keys with the guarded builder rather than string concatenation — it rejects `':'`, whitespace
and control characters in caller-supplied segments, so a hostile id cannot forge an extra key
segment:

```csharp
using Caching.NET.Keys;

var key = CacheKey.For<Product>(sku).WithVariant("v2").Build();   // "Product:ABC-1:v2"
```

## 10. Named caches

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid(redisConnectionString)
    .WithApplicationPrefix("orders-api"));

builder.Services.AddCaching("short-lived", cache => cache
    .UseInMemory()
    .WithApplicationPrefix("orders-api")
    .WithDefaultExpiration(TimeSpan.FromSeconds(30)));
```

Or from configuration:

```json
{
  "CacheOptions": {
    "ApplicationPrefix": "orders-api",
    "NamedCaches": {
      "short-lived":    { "ApplicationPrefix": "orders-api", "DefaultExpiration": "00:00:30" },
      "reference-data": { "ApplicationPrefix": "orders-api", "DefaultExpiration": "01:00:00" }
    }
  }
}
```

Resolve them by keyed injection or through the branded provider:

```csharp
public sealed class QuotaService(
    [FromKeyedServices("short-lived")] IFusionCache shortLived,
    ICacheProvider caches)
{
    private readonly IFusionCache _reference = caches.GetCache("reference-data");
}
```

**Isolation.** A named cache appends its name to the key prefix
(`orders-api:prod:short-lived:…`), so two caches in one application never share a Redis key space.
The default cache is left unsuffixed. `CacheName` in configuration cannot retarget a registration —
the registered name always wins.

## 11. Factory usage

```csharp
// Simple factory.
await cache.GetOrSetAsync<Order>(key, async token => await Load(token), token: ct);

// Factory with context: conditional refresh, adaptive expiration, ETags, tags.
await cache.GetOrSetAsync<Order>(key, async (ctx, token) =>
{
    var result = await LoadIfChangedAsync(ctx.ETag, token);
    if (result.NotModified)
    {
        return ctx.NotModified();          // keep the cached value, restart its lifetime
    }

    ctx.Options.SetDuration(result.Volatile ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(30));
    return ctx.Modified(result.Value, result.ETag);
}, token: ct);
```

The factory receives the caller's `CancellationToken`. Cancelling the caller cancels the factory and
caches nothing.

## 12. Per-entry options

```csharp
await cache.SetAsync(key, value, options => options
    .SetDuration(TimeSpan.FromMinutes(5))
    .SetFailSafe(true, maxDuration: TimeSpan.FromHours(2))
    .SetFactoryTimeouts(softTimeout: TimeSpan.FromSeconds(1))
    .SetPriority(CacheItemPriority.High));
```

Global defaults live in `CacheOptions:DefaultExpiration` and `CacheOptions:Entry`.

> Per-entry options bypass the engine-level key-length guard, which only runs for calls that use the
> configured defaults. Call `ICacheGuard.ValidateKey` yourself on those paths if the key is built
> from untrusted input.

## 13. Fail-safe

When a factory fails or times out, an expired entry can still be served instead of surfacing the
error. On by default.

```json
"Resilience": {
  "FailSafeEnabled": true,
  "FailSafeMaxDuration": "02:00:00",
  "FailSafeThrottleDuration": "00:00:30"
}
```

`FailSafeMaxDuration` must be at least `DefaultExpiration` — startup validation rejects the
combination that would make an entry unusable as a fallback before it even expires.
`FailSafeThrottleDuration` stops a failing dependency from being retried once per request.

## 14. Timeouts

| Setting | Default | Effect |
|---|---|---|
| `Resilience.FactorySoftTimeout` | infinite | Return a stale value this fast, keep the factory running in the background |
| `Resilience.FactoryHardTimeout` | 30s | Absolute ceiling on factory execution |
| `Resilience.DistributedSoftTimeout` | 500ms | Stop waiting on Redis, use a stale value if there is one |
| `Resilience.DistributedHardTimeout` | 2s | Absolute ceiling on one Redis operation |
| `Resilience.AllowTimedOutFactoryBackgroundCompletion` | `true` | Store the late factory result when it finally arrives |

`UseProductionDefaults()` sets a 1s soft / 10s hard factory timeout and 500ms/2s for Redis.

## 15. Tagging

```csharp
await cache.SetAsync(key, product, tags: [$"category:{categoryId}", $"tenant:{tenantId}"]);
```

Tags work in **all three modes**. Limits (`Security.MaximumTagCount`, `MaximumTagLength`) are
enforced through `ICacheGuard.ValidateTags`. Tag values are kept out of logs, traces and metrics
unless `Security.AllowTagsInTelemetry` is set — they are frequently tenant- or user-scoped, which
makes them both sensitive and high-cardinality.

## 16. Invalidation

```csharp
await cache.RemoveAsync(key);                        // one entry
await cache.RemoveByTagAsync("category:42");         // a tag group
await cache.RemoveByTagAsync(["a", "b"]);            // several tag groups
await cache.ExpireAsync(key);                        // expire logically, keep the fail-safe copy
await cache.ClearAsync();                            // this cache, this application prefix only
```

In Hybrid mode with the backplane on, every one of these propagates to other pods. `ClearAsync`
never issues `FLUSHDB` — it is scoped to the application's own prefix, so it is safe on a shared
Redis database.

## 17. OpenTelemetry tracing

Caching.NET owns an `ActivitySource` named **`Caching.NET`**.

```csharp
using Caching.NET.Telemetry;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceName))   // branded spans, no cache keys
    .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterName));            // branded metrics, no overlap
```

The plural forms (`ActivitySourceNames`, `MeterNames`) add the engine's own sources and meters. They
buy operation-level span detail and per-layer instruments, and they cost you **exported cache keys**
and **double-counted metrics**. Read "Engine spans export the raw cache key" below before using them.

Caching.NET takes **no dependency on OpenTelemetry**. It publishes `System.Diagnostics` primitives
and hands you the names.

### The tracing decision, stated plainly

Four approaches were considered. Caching.NET uses a mix of two of them:

1. **Caching.NET emits its own spans for what it owns** — `cache.serialize` and `cache.deserialize`,
   from the serializer decorator, tagged `cache.system`, `cache.mode`, `cache.name`,
   `cache.operation`, `cache.layer`, `cache.payload.bytes`. Created only when a listener is attached.
2. **Operation-level spans are delegated to the engine's activity sources**, whose names are
   surfaced through `CacheTelemetry.ActivitySourceNames` so application code never types them.

Why not translate engine spans into Caching.NET-branded ones? Because renaming a span requires
intercepting the operation, which means the delegating wrapper this redesign explicitly rejects,
and re-emitting them alongside would duplicate every span.

**Honest consequence:** operation-level spans arrive in your tracing backend under
`ZiggyCreatures.Caching.Fusion*` source names. Metrics and logs are fully Caching.NET-branded;
low-level trace *sources* are not. If that matters more than span fidelity, register only
`CacheTelemetry.ActivitySourceName` and you get Caching.NET spans exclusively — you simply see
less detail.

### Engine spans export the raw cache key

Every engine operation span carries the full physical cache key, prefix included, as
`fusioncache.operation.key`:

```text
source: ZiggyCreatures.Caching.Fusion
span:   get or set from cache
tags:   fusioncache.operation.key=orders-api:prod:Order:user-4815162342   <-- raw key
```

The engine offers no switch to suppress it, and stripping it would mean wrapping every cache call.
So:

| You register | Cache keys reach your tracing backend? |
|---|---|
| `CacheTelemetry.ActivitySourceName` | **No.** Caching.NET spans never carry a key. |
| `CacheTelemetry.EngineActivitySourceNames` | **Yes.** |
| `CacheTelemetry.ActivitySourceNames` | **Yes.** |

That is fine when keys are opaque identifiers, and a data-protection decision when a key embeds a
user id, tenant id, email or token. To keep the span detail without the keys, drop the attribute in
an OpenTelemetry processor — the name is published as `CacheTelemetry.EngineKeyAttributeName`:

```csharp
sealed class DropCacheKeyProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
        => activity.SetTag(CacheTelemetry.EngineKeyAttributeName, null);
}
```

Both halves of this are pinned by tests (`SpanKeyExposureTests`), so the guarantee and the warning
stay accurate against future engine versions.

### Never recorded on a Caching.NET span

Cache values, serialized payloads, cache keys, Redis connection strings, credentials, tokens,
user identifiers, tenant identifiers, PII. Metrics and logs carry none of these either, engine
sources included.

Caching.NET does not attach a key fingerprint to spans: the engine resolves entry options before it
starts an operation span, so there is no hook that could add a key attribute to that span without
wrapping every call. Applications that want the correlation can add it themselves —
`ICacheGuard.Fingerprint(key)` produces the non-reversible xxHash64 hex value, and
`CacheTelemetryAttributes.KeyFingerprint` is the attribute name to use.

## 18. OpenTelemetry metrics

Meter name: **`Caching.NET`**. Metrics are produced by subscribing to the engine's event stream, so
they cost nothing on the caller's path and duplicate nothing.

| Instrument | Type | Meaning |
|---|---|---|
| `caching.net.operations` | Counter | Operations by result |
| `caching.net.hits` | Counter | Reads served from a cached value |
| `caching.net.misses` | Counter | Reads with no usable value |
| `caching.net.errors` | Counter | Errors by layer |
| `caching.net.factory.executions` | Counter | Factory runs (foreground and background) |
| `caching.net.fail_safe.served` | Counter | Stale values served |
| `caching.net.invalidations` | Counter | Removals, tag invalidations, clears, evictions |
| `caching.net.redis.errors` | Counter | Distributed-layer errors |
| `caching.net.backplane.errors` | Counter | Backplane errors |
| `caching.net.background.operations` | Counter | Eager refresh, backplane publish/receive |
| `caching.net.guard.violations` | Counter | Key/tag/payload limit breaches |
| `caching.net.redis.tls.validations` | Counter | TLS handshake outcomes |
| `caching.net.serialization.duration` | Histogram (ms) | Serialize/deserialize duration |
| `caching.net.payload.size` | Histogram (bytes) | Serialized payload size |

**Dimensions**, all low-cardinality: `cache.system`, `cache.mode`, `cache.name`, `cache.operation`,
`cache.result`, `cache.layer`, `cache.error.type`, `cache.background_operation`. Never keys, tag
values, tenant or user ids, URLs, request ids, exception messages, or Redis endpoints. Set
`Observability.IncludeCacheNameDimension: false` if an application registers many named caches.

A unit test asserts that no dimension outside the allow-list is ever emitted and that no key
fragment reaches a tag value.

## 19. Structured logging

Categories: `Caching.NET`, `Caching.NET.Redis`, `Caching.NET.Backplane`, `Caching.NET.Security`,
`Caching.NET.Configuration`. Engine output is re-categorised under `Caching.NET` through a logger
adapter, so log filters never mention the engine.

```json
"Logging": { "LogLevel": { "Caching.NET": "Warning", "Caching.NET.Redis": "Information" } }
```

`Information` on the root category costs about **one engine log line per cache operation**, which is
what you want while reproducing a problem and not as a standing production setting. Everything an
operator needs during an incident is `Warning` or above; `Caching.NET.Redis` at `Information` keeps
the low-volume connection-lifecycle lines. See
[TELEMETRY.md](docs/TELEMETRY.md#choosing-a-level-for-the-cachingnet-category-in-production).

Hot paths use source-generated logging. Levels are configurable per event class through
`Observability.*LogLevel`.

### Startup summary

```text
Caching.NET initialized. CacheName: default Mode: Hybrid MemoryLayer: Enabled RedisLayer: Enabled
Backplane: Enabled FailSafe: Enabled Serializer: SystemTextJson Compression: Enabled
Tracing: Enabled Metrics: Enabled
```

No endpoint, no connection string, no credential. Turn it off with
`Observability.LogStartupSummary: false`.

### Never logged

Cached values, full serialized payloads, raw cache keys (a fingerprint is logged instead unless
`Security.AllowRawKeysInLogs` is set for development), connection strings, credentials, tokens,
secrets, PII.

This holds for engine log lines too, not only Caching.NET's own messages: the engine puts the
physical key in a structured `CacheKey` property, and the logger adapter substitutes the fingerprint
before the line reaches a provider. Engine **trace spans** are the one exception and do carry the
raw key — they are opt-in, and covered in [§17](#17-opentelemetry-tracing).

## 20. Redis outage behaviour

| Condition | Behaviour | Telemetry |
|---|---|---|
| Redis unreachable at startup | Host starts. L1 and factories work. Connection retried in the background. | `caching.net.errors{layer=redis}`, Critical log on hard connect failure |
| Redis unavailable at runtime | Hybrid degrades to L1 + factory; Redis mode falls through to the factory. No exception unless `ThrowOnDistributedCacheErrors`. | error counter, Warning log |
| Redis timeout | Soft timeout → stale value if available; hard timeout → treated as a miss | `caching.net.errors`, Debug log (synthetic timeout) |
| Redis restart | Shared multiplexer reconnects. Queued writes/notifications replay via auto-recovery. | `Redis connection restored` (Information) |
| Network partition | Circuit breaker opens for `DistributedCircuitBreakerDuration`, suppressing retry and log storms | `CircuitBreakerOpen` error counter |
| Auth or TLS failure | Connection rejected, error logged with the policy error only (never the certificate chain contents or credentials) | `caching.net.redis.tls.validations` |
| Corrupt Redis payload | Rejected before deserialization, treated as a miss, overwritten by the next factory result | `caching.net.errors{error.type=CorruptPayloadException}`, Warning log |
| Oversized value, background distributed writes on (default) | Not written to Redis; the caller still receives its value | `caching.net.guard.violations`, Warning log |
| Oversized value, background distributed writes **off** | **Throws `InvalidOperationException` to the caller**, even with `ThrowOnSerializationErrors: false` — the foreground write does not honour it. Warned about at startup; see [OPERATIONS.md](docs/OPERATIONS.md#foreground-writes-surface-serialization-failures) | `caching.net.guard.violations`, Warning log |
| Backplane unavailable | Cache operations continue; notifications queue for auto-recovery | `caching.net.backplane.errors` |
| Factory exception | Stale value if fail-safe has one, otherwise the original exception is rethrown | `caching.net.factory.executions{result=error}` |
| Caller cancellation | `OperationCanceledException`, nothing cached, **not** counted as an internal error | — |

Nothing is silently swallowed: every degraded path increments a counter and writes a log entry.

## 21. Kubernetes guidance

- Keep `Redis.AbortOnConnectFail: false` (the default) so a pod can start before Redis is ready.
- Use `Hybrid` with the backplane on for multi-pod services.
- Set `Entry.LocalExpiration` shorter than `DefaultExpiration`; it bounds staleness if the backplane
  is ever unavailable.
- Wire the split health checks: liveness performs no I/O (a Redis outage must not restart every pod),
  readiness performs a real round trip and reports **Degraded**, not Unhealthy, when only the
  distributed layer is down.

```csharp
builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithHealthChecks(splitLivenessReadiness: true));

app.MapHealthChecks("/health/live",  new() { Predicate = r => r.Tags.Contains("liveness") });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("readiness") });
```

- Give every application a distinct `ApplicationPrefix`, and every environment a distinct
  `EnvironmentPrefix`, when they share a Redis database.

## 22. Security guidance

Implemented and tested:

- **Namespace isolation** — application, environment, tenant and cache-name prefixes on every key.
- **Key validation** — `CacheKey`/`CacheKeyBuilder` reject `':'`, whitespace and control characters,
  so a caller-supplied id cannot forge a key segment. Key length is enforced inside the cache on
  every operation that uses the configured defaults.
- **Tag limits** — count and length, via `ICacheGuard.ValidateTags`.
- **Payload limits** — oversized writes refused, oversized reads rejected as corrupt.
- **Corrupt-payload handling** — a one-byte format header is validated before any deserialization,
  so a poisoned or truncated Redis value becomes a miss, not a parse of attacker-controlled bytes.
- **Bounded decompression** — Brotli output is capped by `Compression.MaximumDecompressedBytes`.
- **Safe deserialization** — System.Text.Json with no type-name handling; MessagePack contractless.
  No `BinaryFormatter`, no `NetDataContractSerializer`, no polymorphic type resolution from Redis.
- **TLS** — strict certificate validation by default; the permissive mode is rejected at startup
  unless TLS is actually enabled.
- **Redaction** — connection strings redacted, only exception *types* in health output, no values in
  metrics, key fingerprints instead of keys.

**Do not cache**: passwords, access or refresh tokens, API keys, encryption keys, payment-card data,
banking data, health data, or sensitive personal information. Caching.NET cannot detect these; cache
an identifier and re-resolve the secret from its vault. If a value must be user-scoped, put the user
in the key and consider whether an in-memory-only named cache is more appropriate than Redis.

## 23. Performance guidance

- `Hybrid` for read-heavy multi-pod workloads: L1 answers without a network hop, L2 shares.
- Set `Entry.EagerRefreshThreshold` (e.g. `0.8`) on hot keys so refreshes happen off the request path.
- Keep `Entry.JitterMaxDuration` non-zero so entries created together do not expire together.
- Leave `AllowBackgroundDistributedOperations` on in production; turn it off only when a caller must
  observe its own write in Redis immediately.
- Enable compression only for genuinely large payloads (`ThresholdBytes` ≥ 16 KiB); it costs CPU and
  incompressible payloads are stored uncompressed anyway.
- Turn off `Observability.IncludeCacheNameDimension` if you register many named caches.

Measured numbers: [docs/BENCHMARKS.md](docs/BENCHMARKS.md). No performance claim in this repository
is made without a benchmark behind it.

## 24. Migration from v2

Full guide: **[docs/MIGRATION-V2-TO-V3.md](docs/MIGRATION-V2-TO-V3.md)**. The short version:

```csharp
// v2
services.AddCaching(configuration);

public class OrderService(ICacheService cache)
{
    public Task<Order> GetAsync(int id, CancellationToken ct) =>
        cache.GetOrCreateAsync($"Order:{id}", t => Load(id, t), TimeSpan.FromMinutes(5), cancellationToken: ct);
}
```

```csharp
// v3
services.AddCaching(configuration);

public class OrderService(IFusionCache cache)
{
    public ValueTask<Order> GetAsync(int id, CancellationToken ct) =>
        cache.GetOrSetAsync($"Order:{id}", t => Load(id, t), TimeSpan.FromMinutes(5), token: ct);
}
```

The section name is the same in both versions; only its shape changed.

```jsonc
// v2 shape                      // v3 shape
"CacheOptions": {                "CacheOptions": {
  "KeyPrefix": "orders-api",       "ApplicationPrefix": "orders-api",
  "Mode": "Hybrid",                "Mode": "Hybrid",
  "RedisConnectionString": "…",    "Redis": { "Configuration": "…" },
  "DefaultExpiration": "00:10:00"  "DefaultExpiration": "00:10:00"
}                                }
```

## 25. Breaking changes

Everything below is removed in v3.0.0. There is no compatibility shim.

Three names survive the rewrite with different meanings — `AddCaching`, `CachingBuilder` and the
`CacheOptions` configuration section. v2 code and configuration using them still compiles and binds,
so the compiler will not find these for you. Startup validation will.

| Removed | Replacement |
|---|---|
| `ICacheService` and every method on it | `IFusionCache` |
| v2 `AddCaching(...)` (4 overloads) | v3 `AddCaching(...)` — **same name**, new overloads and semantics |
| v2 `CachingBuilder` | v3 `CachingBuilder` — **same name**, different methods |
| v2 `CacheOptions` section shape | v3 `CacheOptions` section — **same name**, regrouped into `Entry`, `Resilience`, `Redis`, `Backplane`, `Serialization`, `Security`, `Observability` |
| `CacheOptions`, `CacheCallOptions`, `CacheSerializerOptions` (the classes) | `CachingOptions` and nested option classes |
| `KeyPrefix` | `ApplicationPrefix` (+ `EnvironmentPrefix`, `TenantPrefix`) |
| `RedisConnectionString` | `Redis.Configuration` |
| `FailOpen` / `ThrowOnFailure` | `Resilience.ThrowOnDistributedCacheErrors` and friends |
| `TtlJitterPercentage` (fraction) | `Entry.JitterMaxDuration` (absolute) |
| `StripeLockCount`, `StaleRefreshConcurrency` | Removed — the engine owns stampede protection |
| `ICacheSerializer`, `JsonCacheSerializer`, `MessagePackCacheSerializer`, `PayloadEnvelope` | `Serialization.Format` + `Serialization.JsonSerializerOptions` |
| `CacheSchemaAttribute`, schema-drift detection | Payload framing + corrupt-payload rejection |
| Polly resilience pipeline, `CacheResiliencePipelineBuilder`, `ResiliencePipelineNames` | `Resilience.*` (engine timeouts, circuit breakers, auto-recovery) |
| `CacheInstruments` | `CacheTelemetry` |
| `cache.*` metric names | `caching.net.*` |
| `ValidateCacheRegistration` | `ValidateCachingRegistration` |
| Microsoft `HybridCache` implementation | Single engine |

Also changed: `Enabled` is no longer hot-reloadable (it is read once at registration); metric names,
Redis physical key layout and the L2 wire format all change, so upgrading starts with a cold cache.

## 26. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `OptionsValidationException` at startup | Configuration rejected | Read the message — each failure names the property and the fix |
| `Caching[…]: ApplicationPrefix is required` | No prefix set | Set `CacheOptions:ApplicationPrefix` |
| `Mode is Hybrid but Redis.Configuration is not set` | Missing connection string | Set it, or switch to `InMemory` |
| `Backplane.Enabled is true but Mode is Redis` | Backplane has nothing to invalidate | Disable it, or use `Hybrid` |
| `A Caching.NET cache named 'x' is already registered` | Duplicate registration | Remove the duplicate or rename |
| `No Caching.NET cache is registered with the name 'x'` | Typo, or the cache was never registered | The message lists the registered names |
| `Cache key length (…) exceeds Security.MaximumKeyLength` | Key too long | Shorten or hash the variable part, or raise the limit deliberately |
| `Caching.NET refused to cache a … byte payload` | Value over `Serialization.MaximumPayloadBytes` | Cache a smaller projection, or raise the limit |
| Everything is a miss in Redis mode | Working as designed — Redis mode never serves from L1 | Use `Hybrid` if you want L1 |
| `Entry.LocalExpiration (…) is longer than Entry.DistributedExpiration` | L1 would outlive the authoritative L2 entry | Lower `Entry.LocalExpiration`, or raise `Entry.DistributedExpiration` |
| Startup warning `is in Hybrid mode with the backplane disabled` | `Backplane.Enabled` defaults to `false` when bound from configuration | Set it to `true` for any deployment with more than one replica |
| One pod serves stale data | Backplane off | Enable it, or lower `Entry.LocalExpiration` |
| Cold cache after deploying v3 | Key layout and wire format changed | Expected once; entries repopulate on demand |
| No traces appear | Only the Caching.NET source registered | Use `CacheTelemetry.ActivitySourceNames` (plural) |
| Redis `WRONGTYPE` or garbage keys | Another application shares the prefix | Give each application a unique `ApplicationPrefix` |

## 27. Feature matrix

| Feature | InMemory | Redis | Hybrid | Notes |
|---|---|---|---|---|
| Get / Set / Get-or-set / Remove | ✅ | ✅ | ✅ | |
| Expire (logical) | ✅ | ✅ | ✅ | Keeps the fail-safe copy |
| Clear | ✅ | ✅ | ✅ | Scoped to the application prefix; never `FLUSHDB` |
| Default expiration | ✅ | ✅ | ✅ | |
| Distributed expiration | ➖ | ✅ | ✅ | `Entry.DistributedExpiration`; no L2 in InMemory |
| Local (L1) expiration | ✅ | ➖ | ✅ | Redis mode keeps no entries in memory |
| Factory delegates + factory context | ✅ | ✅ | ✅ | ETag, `NotModified`, adaptive expiration |
| Stampede protection | ✅ | ⚠️ | ✅ | In-process only, never distributed. Redis mode runs the factory **twice** per cold key |
| Fail-safe | ✅ | ✅ | ✅ | |
| Fail-safe throttle | ✅ | ✅ | ✅ | |
| Factory soft / hard timeout | ✅ | ✅ | ✅ | |
| Distributed soft / hard timeout | ➖ | ✅ | ✅ | |
| Eager refresh | ✅ | ✅ | ✅ | |
| Jittered expiration | ✅ | ✅ | ✅ | |
| Background distributed operations | ➖ | ✅ | ✅ | |
| Background backplane operations | ➖ | ➖ | ✅ | |
| Auto-recovery | ➖ | ✅ | ✅ | Replays failed L2/backplane operations |
| Null-value / negative caching | ✅ | ✅ | ✅ | |
| Tags and remove-by-tag | ✅ | ✅ | ✅ | |
| Backplane invalidation | ➖ | ⛔ | ✅ | Rejected at startup in Redis mode |
| Named caches | ✅ | ✅ | ✅ | Isolated by a cache-name key segment |
| Per-entry options | ✅ | ✅ | ✅ | |
| CancellationToken propagation | ✅ | ✅ | ✅ | |
| Serializer selection (JSON / MessagePack) | ➖ | ✅ | ✅ | The memory layer stores objects, not bytes |
| Payload compression | ➖ | ✅ | ✅ | |
| Payload size limit | ➖ | ✅ | ✅ | Enforced at the wire boundary |
| Corrupt-payload rejection | ➖ | ✅ | ✅ | |
| Key-length guard | ✅ | ✅ | ✅ | Bypassed by calls passing explicit entry options |
| Events | ✅ | ✅ | ✅ | `IFusionCache.Events` |
| Plugins | ✅ | ✅ | ✅ | Via `IFusionCache.AddPlugin` after resolution |
| Caching.NET metrics | ✅ | ✅ | ✅ | |
| Caching.NET serialize/deserialize spans | ➖ | ✅ | ✅ | |
| Operation-level spans | ✅ | ✅ | ✅ | Engine-named sources — see §17 |
| Health checks | ✅ | ✅ | ✅ | |

✅ supported · ➖ not applicable in this mode · ⛔ rejected at startup

## 28. Known limitations

1. **Operation-level trace sources are engine-branded.** See [§17](#17-opentelemetry-tracing).
   Metrics and logs are fully Caching.NET-branded; low-level span source names are not.
2. **The key-length guard does not cover calls that pass explicit entry options.** The engine hook it
   uses is only consulted for calls that fall back to the configured defaults. Use
   `ICacheGuard.ValidateKey` on those paths.
3. **Tag limits are advisory.** Nothing in the operation contract lets Caching.NET intercept tags, so
   `ICacheGuard.ValidateTags` must be called by the application. Enforcing it would require the
   wrapper this design rejects.
4. **`Enabled` is not hot-reloadable.** It is read once at registration. Flipping it at runtime has no
   effect; restart the process.
5. **Mode and Redis settings are startup-only.** Same reason.
6. **Upgrading from v2 starts with a cold cache.** Key layout and L2 wire format both changed.
7. **`ICacheProvider.Default` throws if only named caches are registered.** Register an unnamed cache
   or use `GetCache(name)`.
8. **Redis-mode reads always cost a round trip.** By design; use `Hybrid` for L1 latency.
9. **Trim/AOT** needs a source-generated `JsonSerializerContext` supplied through
   `Serialization.JsonSerializerOptions.TypeInfoResolver`. MessagePack and configuration binding are
   reflection-based.

---

## Repository layout

```text
src/Caching.NET                     the package
src/Caching.NET.Analyzers           the CACHENET001 analyzer, shipped inside the package
samples/Caching.NET.Sample          ASP.NET sample: registration, controllers, health checks
tests/Caching.NET.Tests             unit tests
tests/Caching.NET.Tests.Properties  property-based tests
tests/Caching.NET.Tests.Integration integration tests (requires Docker)
tests/Caching.NET.Tests.Chaos       outage/restart tests (requires Docker)
tests/Caching.NET.Tests.Pod         console cache instance the integration suite launches as a
                                    separate OS process for cross-process backplane tests
benchmark/Caching.NET.Benchmark     BenchmarkDotNet suites
aot/Caching.NET.AotSmoke            native-AOT smoke test
docs/                               architecture, security, telemetry, operations, migration
docs/audits/                        dated release-gate reviews with measured evidence
```

The v3.0.0 release sign-off, including the measured evidence behind every claim in this README, is
[docs/audits/2026-08-08-v3.0.0-production-readiness-review.md](docs/audits/2026-08-08-v3.0.0-production-readiness-review.md).

```bash
dotnet build
dotnet test                                     # Docker required for integration and chaos suites
dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs
```

## Acknowledgements

The cache engine inside Caching.NET is
[FusionCache](https://github.com/ZiggyCreatures/FusionCache) by Jody Donetti (MIT). Caching.NET
wraps its setup, not its API.

## Licence

MIT.
