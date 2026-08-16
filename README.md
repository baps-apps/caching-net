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
cache engine, and exposes its own **`ICacheService`** as the cache **operation contract** — the
engine is never named in a public signature. Applications never register, configure, or reference
FusionCache themselves — Caching.NET owns registration, configuration, lifecycle, connection
management, security limits, and observability, and `Internal/FusionCacheService` is the only type
that calls an engine operation.

Why the operation contract is Caching.NET's own eight-verb interface rather than the engine's, and
rather than a full delegating wrapper, is explained in
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
- **Tag and `Clear` invalidation is authoritative too, and it is not free.** The engine implements
  `RemoveByTag` and `Clear` as *marker* entries that every read compares itself against, and a marker
  is an ordinary cache entry. Redis mode therefore applies the same skip-the-memory-layer rule to
  markers as to entries, so an invalidation is visible to every instance on its next read. The cost is
  extra Redis round trips per read, because a marker can no longer be answered locally: a read that
  **hits** costs **3** Redis commands for an untagged entry (the entry, plus the two reserved `Clear`
  markers) and **3 + *n*** for an entry with *n* tags. A read that **misses** still costs 1 — with no
  entry there is nothing a marker could invalidate — so the amplification tracks the *hit* ratio, not
  the request rate. Re-baselined on a local Redis: **109 µs → 338 µs mean for an untagged read (×3.10)
  and 537 µs with two tags (×4.92)**, with per-read allocation roughly doubling. `Hybrid` and
  `InMemory` are unaffected (Hybrid L1 hit unchanged at 422 ns, 741 ns with two tags). Full numbers and
  capacity formula: [BENCHMARKS](docs/BENCHMARKS.md#redis-mode-the-cost-of-authoritative-tag-markers)
  and [OPERATIONS §4](docs/OPERATIONS.md#4-capacity).
  If you are in Redis mode for consistency this is the consistency you asked for; if you want L1
  latency, use `Hybrid`, which keeps markers in L1 and evicts them over the backplane.
- **Every read costs a Redis round trip.** That is the point of the mode; if you want L1 latency,
  use `Hybrid`.
- **A backplane is rejected in this mode** at startup validation: with markers bypassing the memory
  layer as well, there really are no local entries to invalidate, so enabling it would only add
  traffic.
- **The skip flags live on the cache's default entry options, and a per-call `CacheEntryOverrides`
  cannot remove them.** `CacheEntryOverrides` is additive by construction (see
  [§3.2](#32-public-api-decision) and docs/ARCHITECTURE.md §3): every property starts `null`, meaning
  "use the configured value," and there is no way to build one that starts from a blank slate the way
  a caller-constructed engine options object could in earlier designs. Passing
  `new CacheEntryOverrides { DistributedExpiration = ... }` changes only that property; the mode's
  skip flags, the key guard and everything else you did not set are preserved automatically. Pinned
  by `RedisModeEntryOptionsTests`.

---

## 3. Architecture

```text
Application code
        │  injects ICacheService  /  ICacheProvider  /  ICacheGuard
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
│     ├─ layer decorators     (per-layer spans + caching.net.layer.duration) │
│     ├─ serializer           (JSON or MessagePack + framing/limits)   │
│     ├─ backplane            (Redis pub/sub over the same connection) │
│     ├─ key guard            (per-operation key-length enforcement)   │
│     ├─ logger adapter       (re-categorises output as "Caching.NET") │
│     └─ event bridge         (fail-safe/eager-refresh/background factory events ─► Caching.NET metrics) │
│          │                                                           │
│          ▼                                                           │
│  FusionCacheService : ICacheService                                  │
│     the only type that calls an engine operation; owns every cache.* span and │
│     records hits/misses/operations/foreground invalidations synchronously     │
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
| Key namespacing and key/tag limits | Caching.NET, enforced on every call |
| Logging categories, meter, metric names, activity source | Caching.NET |
| Cache **operations** (get, set, get-or-set, remove, tags, …) | Caching.NET's own `ICacheService`, implemented over the engine by `Internal/FusionCacheService` |

### 3.2 Public API decision

**Decision: own a small, permanently-frozen operation contract (`ICacheService`) instead of exposing
the engine's; own everything else too.**

The alternatives were weighed as follows.

| Option | Verdict |
|---|---|
| Small custom interface reproducing only v2's four methods | **Rejected.** This is what v2 did. It costs applications fail-safe, timeouts, eager refresh, factory context, ETags, adaptive expiration and per-entry options, and it is exactly what this redesign set out to remove. |
| Full wrapper reproducing the engine's entire method surface | **Rejected.** Roughly 80 overloads to mirror, and every engine release becomes a Caching.NET release. Pure maintenance cost, zero capability gained. |
| Expose the engine's own operation contract directly (`IFusionCache`) | **Rejected**, after being the working design for most of this release. It ties every consumer's compiled surface to the engine's own type, means an engine-only capability (`SkipMemoryCacheRead`, an engine-internal probe setting) either leaks into application reach or needs its own escape hatch, and the engine's telemetry sources would have had to be registered under their own names for full detail, undermining the "everything is branded `Caching.NET`" rule. |
| **A small, purpose-designed `ICacheService` — eight verbs, each async/sync — implemented over the engine by `FusionCacheService`; a new engine capability lands as a `CachingOptions` knob or a `CacheEntryOverrides` field, never a ninth verb** | **Chosen.** Applications get fail-safe, timeouts, eager refresh, factory context, ETags, adaptive expiration and per-entry options — everything the four-method design cost them — without the engine ever appearing in a public signature. `CacheEntryOverrides` is additive by construction (§2), so it cannot reintroduce the escape hatch a caller-constructed engine options object used to be. |

**What this means in practice.** `ICacheService` is the *operational* API contract — the type you
inject and call. `CacheValue<T>`, `CacheFactoryContext<T>` and `CacheEntryOverrides` are Caching.NET's
own types, not the engine's. An application never calls `AddFusionCache`, never constructs
`FusionCacheOptions`, never registers a serializer or backplane, and never references a FusionCache
package — and now also never types an engine name to get the full operation surface, because
`ICacheService` **is** the full surface. `Caching.NET.Analyzers`' `CACHENET001` diagnostic enforces
this at build time: it warns on any direct reference to a `ZiggyCreatures.Caching.Fusion` type in
consumer code (Caching.NET's own assembly and its test assemblies are exempt, since they build and
verify the adapter that hides the engine from everyone else). `StackExchange.Redis` is deliberately
not flagged: it arrives transitively, but an application may legitimately use it directly — a rate
limiter, a distributed lock, pub/sub — on code Caching.NET has no claim over.

Caching.NET adds, in its own namespaces:

- `ICacheService` — the operation contract: `GetOrSet(Async)`, `GetOrDefault(Async)`, `TryGet(Async)`,
  `Set(Async)`, `Remove(Async)`, `Expire(Async)`, `RemoveByTag(Async)`, `Clear(Async)`, each with an
  async and a synchronous form.
- `CacheValue<T>` — the result of a read, distinguishing a cached `null` from a miss.
- `CacheFactoryContext<T>` — passed to a context-taking factory: stale value, ETag/`LastModified`,
  `NotModified()`/`Fail(reason)`, and adaptive per-execution `Overrides`.
- `CacheEntryOverrides` — per-call overrides, additive by construction (§2).
- `ICacheProvider` — named-cache resolution (`Default`, `GetCache(name)`, `CacheNames`).
- `ICacheGuard` — key/tag limit checks and non-reversible key fingerprints.
- `CacheExtensions` — batch reads/writes/removals, existence probing, forced refresh. Only methods
  that add something the operation contract does not already have; nothing is renamed.
- `CachingOptions` and friends, `CachingBuilder`, `CacheTelemetry`, `CacheKey`, health checks.

---

## 4. Installation

```bash
dotnet add package Caching.NET --version 3.1.0
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

Optional memory cap (requires a per-entry size so the limit can be enforced). The limit is a
ceiling on the **summed size of the cached entries**, not a byte or megabyte budget — with the
default size of `1` per entry it caps the **number of entries** held in memory:

```csharp
.WithMemorySizeLimit(limit: 10_000, defaultEntrySize: 1)   // at most 10 000 entries in memory
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

Four shapes, all in `Caching.NET.Extensions`. There are six `AddCaching*` overloads in total: the
four below, plus the two `AddCaching(string cacheName, …)` forms that register a named cache (§10).

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
| `ICacheService` (default cache) | Singleton | Resolves the same instance from any scope |
| `ICacheService` keyed by cache name | Singleton | `[FromKeyedServices("name")]` |
| `ICacheProvider` | Singleton | Frozen lookup table, no mutable state |
| `ICacheGuard` (default + keyed) | Singleton | |
| `ICacheKeyFactory` | Singleton | `TryAdd`; register your own **before** `AddCaching` to replace it |

There is no `IFusionCache` registration anywhere in the container.

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
size or with a non-positive one, non-positive payload/key/tag limits, key limit shorter than the
prefix, decompression ceiling below the payload limit, negative Redis database, non-positive Redis
timeouts and retry count, permissive TLS without TLS, a client or server certificate callback
without TLS, whitespace in `Redis.InstancePrefix` or `Backplane.ChannelPrefix`, a `CacheName` that
does not match the registered name, and an `Observability.*LogLevel` set to `Information` while the
engine-operation rewrite is active.

One rule is worth knowing before it fires: a **URI-form Redis connection string**
(`redis://user:password@host:6379`) is rejected at startup. StackExchange.Redis does not parse that
form — it treats the whole string as a host name, so the connection always fails *and* echoes the
credentials back inside the connection exception, which then reaches the log. Redaction cannot fix a
secret embedded in a third-party exception string, so this fails before a connection is attempted.
Use the comma-delimited form: `host:port,password=…,user=…,ssl=true`.

Disabling the cache (`Enabled: false`) skips validation entirely, so a service can ship with caching
off and no Redis settings.

## 9. Cache API usage

```csharp
using Caching.NET;   // ICacheService — the operation contract

public sealed class ProductService(ICacheService cache, IProductRepository repository)
{
    public async Task<Product?> GetAsync(string sku, CancellationToken cancellationToken)
        => await cache.GetOrSetAsync(
            $"products:{sku}",
            async token => await repository.LoadAsync(sku, token),
            token: cancellationToken);
}
```

That is the **context-free** overload — the factory takes only a `CancellationToken`, so the compiler
infers `TValue` from its return type and no explicit type argument is needed. Reach for the
**context-taking** overload (`Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>>`,
§11) only when the factory needs the stale value, `NotModified()`/`Fail(reason)`, or adaptive
per-execution `Overrides` — that overload's context parameter is a lambda parameter type, not a
return type, so the compiler cannot infer `TValue` and every call must name it explicitly
(`GetOrSetAsync<Order>(...)`).

Core operations: `GetOrSetAsync`, `GetOrDefaultAsync`, `TryGetAsync`, `SetAsync`, `RemoveAsync`,
`ExpireAsync`, `RemoveByTagAsync`, `ClearAsync` — each with a synchronous counterpart, per-call
`CacheEntryOverrides`, tags and a `CancellationToken`. That is the whole contract, permanently: a new
engine capability lands as a `CachingOptions` knob or a `CacheEntryOverrides` field, never a ninth
verb.

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
    [FromKeyedServices("short-lived")] ICacheService shortLived,
    ICacheProvider caches)
{
    private readonly ICacheService _reference = caches.GetCache("reference-data");
}
```

**Isolation.** A named cache appends its name to the key prefix
(`orders-api:prod:short-lived:…`), so two caches in one application never share a Redis key space.
The default cache is left unsuffixed. `CacheName` in configuration cannot retarget a registration —
the registered name always wins.

## 11. Factory usage

```csharp
// Context-free: the default. TValue is inferred; no explicit type argument needed.
await cache.GetOrSetAsync(key, async token => await Load(token), token: ct);

// Context-taking: conditional refresh, adaptive expiration, ETags. Explicit type argument required
// (see §9) because the context type is a lambda parameter, not the return type.
await cache.GetOrSetAsync<Order>(key, async (ctx, token) =>
{
    var response = await LoadIfChangedAsync(ctx.ETag, token);
    if (response.NotModified)
    {
        return ctx.NotModified();          // keep the cached value, restart its lifetime
    }

    ctx.ETag = response.ETag;
    ctx.Overrides.DistributedExpiration = response.Volatile
        ? TimeSpan.FromSeconds(30)
        : TimeSpan.FromMinutes(30);
    return response.Value;
}, token: ct);
```

The factory receives the caller's `CancellationToken`. Cancelling the caller cancels the factory and
caches nothing.

> **A factory may run more than once for the same key.** Stampede protection is per cache instance,
> so `N` pods racing the same cold key run the factory up to `N` times — and in `Redis` mode a single
> instance runs it twice. **Anything that must happen exactly once globally — an increment, a send, a
> charge, a job enqueue — does not belong in a cache factory in any mode.** A factory should read a
> value, nothing more. See [§28](#28-known-limitations).

## 12. Per-entry options

```csharp
await cache.SetAsync(key, value, new CacheEntryOverrides
{
    LocalExpiration = TimeSpan.FromMinutes(5),
    FailSafe = true,
    FailSafeMaxDuration = TimeSpan.FromHours(2),
    FactorySoftTimeout = TimeSpan.FromSeconds(1),
    Priority = CacheEntryPriority.High
});
```

Global defaults live in `CacheOptions:DefaultExpiration` and `CacheOptions:Entry`.

The key-length guard runs inside `FusionCacheService` at the start of every call, whether or not that
call supplies `CacheEntryOverrides` — unlike the engine's own configured-default-only hook, there is
no per-entry-options path that skips it. `ICacheGuard.ValidateKey`/`ValidateTags` remain useful to
call directly at a boundary where a key or tags are built from untrusted input, ahead of other work
in the same request — see [§22](#22-security-guidance).

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
await cache.ExpireAsync(key);                        // expire logically, keep the fail-safe copy
await cache.ClearAsync();                            // this cache, this application prefix only

// Several tag groups: RemoveByTagAsync takes one tag, so loop.
foreach (var tag in new[] { "category:42", "tenant:7" })
{
    await cache.RemoveByTagAsync(tag);
}
```

In Hybrid mode with the backplane on, every one of these propagates to other pods. `ClearAsync`
never issues `FLUSHDB` — it is scoped to the application's own prefix, so it is safe on a shared
Redis database.

## 17. OpenTelemetry tracing

Caching.NET owns an `ActivitySource` named **`Caching.NET`** — the only tracing source it ever emits
from. The internal cache engine's own diagnostics are never registered, under any name.

```csharp
using Caching.NET.Telemetry;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceName))   // branded spans, no cache keys by default
    .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterName));            // branded metrics
```

`CacheTelemetry.ActivitySourceNames`/`MeterNames` are plural arrays containing that same single name
— they exist for API symmetry (code that wants "every source/meter Caching.NET owns" has something
to iterate), not because there is a second, engine-branded detail tier to opt into. There is exactly
one source and one meter.

Caching.NET takes **no dependency on OpenTelemetry**. It publishes `System.Diagnostics` primitives
and hands you the names.

### Span catalogue

Every `cache.*` operation span (`cache.get_or_set`, `cache.get_or_default`, `cache.try_get`,
`cache.set`, `cache.remove`, `cache.expire`, `cache.remove_by_tag`, `cache.clear`, and the nested
`cache.factory` span created only on a miss) comes from `FusionCacheService` itself — the type that
implements `ICacheService`. The layer decorators add child spans for their own layer
(`cache.memory.*`, `cache.redis.*`); `cache.serialize`/`cache.deserialize` come from the serializer
decorator. All of it is tagged `cache.system`, `cache.mode` and `cache.name`; operation spans also
carry a key attribute (§22) — `cache.clear` excepted, since it has no key — and `cache.result`, with
`cache.get_or_default` the one span that deliberately omits the result tag on a successful read. The
operation itself is the span *name*, not a tag: `cache.operation` is a metric dimension, and appears
as a span tag only on `cache.serialize`/`cache.deserialize`. Created only when a listener is
attached to the `Caching.NET` source. Full catalogue and worked InMemory/Redis/Hybrid trace examples:
[docs/TELEMETRY.md §3](docs/TELEMETRY.md#3-tracing).

### Never recorded on a Caching.NET span

Cache values, serialized payloads, Redis connection strings, credentials, tokens, user identifiers,
tenant identifiers, PII. Metrics and logs carry none of these either. Exceptions are recorded with
type only, never a message.

The one attribute that is opt-in rather than always-absent is the cache key itself — see §22.

## 18. OpenTelemetry metrics

Meter name: **`Caching.NET`** — the only meter Caching.NET emits from. Metrics have two producers:
`FusionCacheService` and the layer decorators record hits, misses, operations, foreground
invalidations and per-layer duration synchronously, on the caller's own thread, at essentially no
cost when nothing is listening; `CacheEventBridge` subscribes to the engine's internal event hub for
the handful of signals only the engine's own code path can attribute correctly (factory executions,
fail-safe, eager refresh, backplane publish/receive, evictions) — and is never constructed at all
when `Observability.EnableMetrics` is `false`. Full split and per-instrument producer:
[docs/TELEMETRY.md §2](docs/TELEMETRY.md#2-metrics).

| Instrument | Type | Meaning |
|---|---|---|
| `caching.net.operations` | Counter | Operations by result |
| `caching.net.hits` | Counter | Reads served from a cached value |
| `caching.net.misses` | Counter | Reads with no usable value |
| `caching.net.errors` | Counter | Errors by layer |
| `caching.net.factory.executions` | Counter | Factory runs (foreground and background) |
| `caching.net.fail_safe.served` | Counter | Stale values served |
| `caching.net.invalidations` | Counter | Removals, tag invalidations and clears the application requested |
| `caching.net.evictions` | Counter | Entries dropped from the in-process memory layer (expired, size-limited, replaced, removed) |
| `caching.net.redis.errors` | Counter | Distributed-layer errors |
| `caching.net.backplane.errors` | Counter | Backplane errors |
| `caching.net.background.operations` | Counter | Eager refresh, backplane publish/receive |
| `caching.net.guard.violations` | Counter | Key/tag/payload limit breaches |
| `caching.net.redis.tls.validations` | Counter | TLS handshake outcomes |
| `caching.net.serialization.duration` | Histogram (ms) | Serialize/deserialize duration |
| `caching.net.payload.size` | Histogram (bytes) | Serialized payload size |
| `caching.net.layer.duration` | Histogram (ms) | Per-layer duration, gated on `Observability.EnableLayerMetrics` |

**Dimensions**, all low-cardinality: `cache.system`, `cache.mode`, `cache.name`, `cache.operation`,
`cache.result`, `cache.layer`, `cache.error.type`, `cache.background_operation`. Never keys, tag
values, tenant or user ids, URLs, request ids, exception messages, or Redis endpoints. Set
`Observability.IncludeCacheNameDimension: false` if an application registers many named caches.

A unit test asserts that no dimension outside the allow-list is ever emitted and that no key
fragment reaches a tag value.

**Layer spans.** A probe of the memory or Redis layer produces a span only when it runs under a span
that is still running — `Observability.LayerTracing`, default `WhenParented`. Cache calls are
unaffected, because Caching.NET's own operation span parents their probes; what it drops is the
single-span root traces the engine's own threads produced when applying a backplane invalidation or
writing after a background refresh. An *ended* span does not count as a parent, which matters because
ambient trace context flows into background work from the request that scheduled it. Set it to
`Always` for the pre-3.1 behaviour, or `Never` to drop layer spans entirely. Metrics are recorded
identically at every setting — see [docs/TELEMETRY.md](docs/TELEMETRY.md) §3.

## 19. Structured logging

Categories: `Caching.NET`, `Caching.NET.Redis`, `Caching.NET.Backplane`, `Caching.NET.Security`,
`Caching.NET.Configuration`. Engine output is re-categorised under `Caching.NET` through a logger
adapter, so log filters never mention the engine.

```json
"Logging": { "LogLevel": { "Caching.NET": "Warning", "Caching.NET.Redis": "Information" } }
```

The engine logs every cache call at `Information` — measured at **2.04 lines per `GetOrSet`**, each
with a full options dump. Caching.NET rewrites those to `Observability.EngineOperationLogLevel`
(`Debug` by default), so `Information` on the root category costs **zero** engine lines per operation
and is safe as a standing production setting. Drop the category to `Debug` to get them back while
reproducing a problem. Everything an operator needs during an incident is `Warning` or above;
`Caching.NET.Redis` at `Information` keeps the low-volume connection-lifecycle lines. See
[TELEMETRY.md](docs/TELEMETRY.md#engine-per-operation-lines-and-observabilityengineoperationloglevel).

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
before the line reaches a provider. Caching.NET's own trace spans carry a key attribute too — a
fingerprint by default, or the literal key when `Security.AllowRawKeysInTelemetry` is set — see
[§22](#22-security-guidance). That is an independent setting from `Security.AllowRawKeysInLogs` above:
one governs log lines, the other governs spans, and either can be set without the other.

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
  so a caller-supplied id cannot forge a key segment. Key length is enforced on every call, inside
  the cache adapter, whether or not the call supplies per-call overrides.
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
  metrics, key fingerprints instead of keys on spans by default. `Security.AllowRawKeysInTelemetry`
  (default `false`) opts a cache instance's spans into the literal key instead of the fingerprint —
  off by default, because a key routinely embeds a tenant, user or order identifier, and span
  attributes are indexed and retained under the tracing backend's own policy. This is a separate
  setting from `Security.AllowRawKeysInLogs`, which controls log lines instead. See
  [docs/SECURITY.md §9](docs/SECURITY.md#9-raw-keys-in-telemetry-allowrawkeysintelemetry).
- **Mutual TLS and extra certificate checks** — `Redis.ClientCertificate` for client certificates on
  the handshake; `Redis.ValidateServerCertificate` for an additional check that can only tighten
  Caching.NET's own validation, never loosen it.

**Do not cache**: passwords, access or refresh tokens, API keys, encryption keys, payment-card data,
banking data, health data, or sensitive personal information. Caching.NET cannot detect these; cache
an identifier and re-resolve the secret from its vault. If a value must be user-scoped, put the user
in the key and consider whether an in-memory-only named cache is more appropriate than Redis.

## 23. Performance guidance

- `Hybrid` for read-heavy multi-pod workloads: L1 answers without a network hop, L2 shares.
- Set `Entry.EagerRefreshThreshold` (e.g. `0.8`) on hot keys so refreshes happen off the request path.
- Keep jitter on (`Entry.JitterMaxDuration` non-zero) so entries created together do not expire
  together. Jitter is proportional — `min(duration x Entry.JitterFraction, Entry.JitterMaxDuration)`
  — so it scales with short TTLs instead of swamping them.
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

public class OrderService(ICacheService cache)
{
    public ValueTask<Order?> GetAsync(int id, CancellationToken ct) =>
        cache.GetOrSetAsync(
            $"Order:{id}",
            async t => await Load(id, t),
            new CacheEntryOverrides { LocalExpiration = TimeSpan.FromMinutes(5) },
            token: ct);
}
```

`ICacheService` is the one name that survives the rewrite with a **different shape**: v2's
`ICacheService` had four methods; v3's has the eight verbs in [§9](#9-cache-api-usage). They are not
source-compatible — a v2 call site still compiles against the v3 interface only by coincidence of
matching parameter shapes, and the section above shows the actual v3 signature.

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
`CacheOptions` configuration section — **plus `ICacheService` itself**, which is not removed but
reshaped: v2's four-method interface is gone, and v3's own eight-verb `ICacheService` takes its name
and its place as the cache operation contract. v2 code and configuration using any of these four
names still compiles and binds, so the compiler will not find these for you. Startup validation will,
and for `ICacheService` call sites, a changed method signature will.

| Removed | Replacement |
|---|---|
| v2 `ICacheService` (`GetAsync`/`SetAsync`/`GetOrSetAsync`/`RemoveAsync`, `GetOrCreateAsync`, …) | v3 `ICacheService` — **same name**, a different eight-verb shape (§9); not source-compatible |
| v2 `AddCaching(...)` (4 overloads) | v3 `AddCaching(...)` — **same name**, new overloads and semantics |
| v2 `CachingBuilder` | v3 `CachingBuilder` — **same name**, different methods |
| v2 `CacheOptions` section shape | v3 `CacheOptions` section — **same name**, regrouped into `Entry`, `Resilience`, `Redis`, `Backplane`, `Serialization`, `Security`, `Observability` |
| `CacheOptions`, `CacheCallOptions`, `CacheSerializerOptions` (the classes) | `CachingOptions` and nested option classes, including `CacheEntryOverrides` for per-call options |
| `KeyPrefix` | `ApplicationPrefix` (+ `EnvironmentPrefix`, `TenantPrefix`) |
| `RedisConnectionString` | `Redis.Configuration` |
| `FailOpen` / `ThrowOnFailure` | `Resilience.ThrowOnDistributedCacheErrors` and friends |
| `TtlJitterPercentage` (fraction) | `Entry.JitterFraction` (fraction, default `0.1`) + `Entry.JitterMaxDuration` (absolute ceiling, default 2 s) |
| `StripeLockCount`, `StaleRefreshConcurrency` | Removed — the engine owns stampede protection |
| `ICacheSerializer`, `JsonCacheSerializer`, `MessagePackCacheSerializer`, `PayloadEnvelope` | `Serialization.Format` + `Serialization.JsonSerializerOptions` |
| `CacheSchemaAttribute`, schema-drift detection | Payload framing + corrupt-payload rejection |
| Polly resilience pipeline, `CacheResiliencePipelineBuilder`, `ResiliencePipelineNames` | `Resilience.*` (engine timeouts, circuit breakers, auto-recovery) |
| `CacheInstruments` | `CacheTelemetry` |
| `cache.*` metric names | `caching.net.*` |
| `ValidateCacheRegistration` | `ValidateCachingRegistration` |
| Microsoft `HybridCache` implementation | Single engine, wrapped by Caching.NET's own `ICacheService` — the engine itself never appears in a public signature |

Also changed: `Enabled` is no longer hot-reloadable (it is read once at registration); metric names,
Redis physical key layout and the L2 wire format all change, so upgrading starts with a cold cache;
key and tag guards are now enforced on every call, not only calls using the configured defaults.

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
| No traces appear | `CacheTelemetry.ActivitySourceName` not registered, `Observability.EnableTracing: false`, or no listener attached | Register `CacheTelemetry.ActivitySourceName` and confirm `Observability.EnableTracing` is `true` (the default); spans are created only when a listener is attached |
| `cache.memory.*` / `cache.redis.*` spans disappeared after upgrading to 3.1.0 | `Observability.LayerTracing` defaults to `WhenParented`, which drops layer spans that are not running under a live span — backplane and background-refresh probes | Expected. Spans inside a cache call are unaffected. Set `LayerTracing: Always` for the pre-3.1 behaviour; `caching.net.layer.duration` records those probes either way |
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
| Key-length guard | ✅ | ✅ | ✅ | Enforced on every call, inside `FusionCacheService`, not only calls using the configured defaults |
| Tag-limit guard | ✅ | ✅ | ✅ | Same — enforced on every call that supplies tags |
| Events | ➖ | ➖ | ➖ | Not part of `ICacheService`. The engine's event hub is consumed internally by `CacheEventBridge` for telemetry only, never exposed to applications |
| Plugins | ➖ | ➖ | ➖ | Not part of `ICacheService`; the engine is never reachable to add one |
| Caching.NET metrics | ✅ | ✅ | ✅ | |
| Caching.NET serialize/deserialize spans | ➖ | ✅ | ✅ | |
| Operation-level spans | ✅ | ✅ | ✅ | `Caching.NET`-branded (`cache.get_or_set`, `cache.set`, …) — see §17. Never gated by `LayerTracing` |
| Layer-level spans | ✅ | ✅ | ✅ | `cache.memory.*` (InMemory, Hybrid), `cache.redis.*` (Redis, Hybrid). Emitted only under a live span by default — `Observability.LayerTracing`, see §17 |
| Backplane spans | ➖ | ➖ | ✅ | `cache.backplane.publish` and `cache.backplane.receive`, both tagged `cache.background_operation=true`. Hybrid only, since it is the only mode that runs a backplane. A message this instance published gets no receive span — Redis delivers it back to the publisher and the engine discards it |
| Health checks | ✅ | ✅ | ✅ | |

✅ supported · ➖ not applicable in this mode · ⛔ rejected at startup

## 28. Known limitations

1. **Engine capabilities beyond the eight `ICacheService` verbs are not reachable at all.** The
   engine's event hub and plugin system are consumed internally — the event hub only by
   `CacheEventBridge`, for telemetry — and never exposed. A new engine capability an application needs
   lands as a `CachingOptions` knob or a `CacheEntryOverrides` field (see
   [§3.2](#32-public-api-decision)), not as a way to reach the engine directly.
2. **`Enabled` is not hot-reloadable.** It is read once at registration. Flipping it at runtime has no
   effect; restart the process.
3. **Mode and Redis settings are startup-only.** Same reason.
4. **Upgrading from v2 starts with a cold cache.** Key layout and L2 wire format both changed.
5. **`ICacheProvider.Default` throws if only named caches are registered.** Register an unnamed cache
   or use `GetCache(name)`.
6. **Redis-mode reads always cost a round trip.** By design; use `Hybrid` for L1 latency.
7. **A write is not immediately readable by another instance.** `Resilience.AllowBackgroundDistributedOperations`
   is `true` by default, so `SetAsync` releases the caller as soon as L1 is updated and completes the
   Redis write in the background. Another pod — or, in `Redis` mode, the *same* pod, which keeps no L1
   — can therefore still read the previous value (or a miss) for a short window after `SetAsync`
   returns. This is the single easiest thing to get wrong with this package: it was hit four separate
   times while writing tests for it, each time looking like a cache bug and each time being this.
   When a caller must observe its own write, pass
   `new CacheEntryOverrides { AllowBackgroundDistributedOperations = false }`, which awaits the Redis
   write. Leave the default on for ordinary traffic — it is what keeps a slow or unhealthy Redis off
   the request path.
8. **Trim/AOT** needs a source-generated `JsonSerializerContext` supplied through
   `Serialization.JsonSerializerOptions.TypeInfoResolver`. MessagePack and configuration binding are
   reflection-based. A native-AOT publish also emits `IL3053` for the `MessagePack` and
   `ZiggyCreatures.FusionCache.Serialization.SystemTextJson` assemblies — third-party analysis
   warnings, none from `Caching.NET` itself — which an application building with
   `TreatWarningsAsErrors` must `NoWarn`. `aot/Caching.NET.AotSmoke` publishes and runs as a native
   binary on this basis.
9. **Stampede protection is never distributed.** The locker is a per-instance object with no
   distributed lease behind it, so `N` pods racing the same cold key run the factory up to `N` times.
   Within one instance it holds: 50 concurrent callers run the factory once (`InMemory`, `Hybrid`) or
   twice (`Redis`). Measured and pinned by `StampedeScopeTests`, including the cross-instance case.

   The exposure is bounded by **one factory duration**, not by traffic: as soon as the first pod's
   factory completes and writes L2, later readers hit L2 instead of running their own. Only pods that
   entered `GetOrSet` before that write are affected.

   If the origin cannot absorb that: **jitter** (on by default) desynchronises expiry so pods stop
   expiring the same key at the same instant; **eager refresh** refreshes a hot key before it expires,
   so the cold-key event mostly stops happening; **fail-safe** serves the stale value while one
   refresh runs. Reach for those before considering a distributed lock — a lock puts a Redis
   round trip on the read path and introduces a stall whenever a lock holder dies. If exactly-once
   execution is genuinely required, it belongs at the origin, not in a cache factory (see
   [§11](#11-factory-usage)).
10. **A cold pod briefly serves misses.** Reads issued while the Redis connection is still being
   established return a miss rather than an error, so a starting pod runs its factory for everything
   it is asked for — measured at ~150&#160;ms on loopback. Nothing above `Debug` is logged and no error
   metric is incremented, so the burst is visible only as misses correlated with pod age. There is no
   regression test: the window is a timing artifact that disappears once the connection is warm, so
   any test for it would pass or fail depending on execution order. Measured by hand across two cold
   processes; runbook guidance in [docs/OPERATIONS.md](docs/OPERATIONS.md).

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
[docs/audits/2026-08-12-v3.0.0-final-release-gate.md](docs/audits/2026-08-12-v3.0.0-final-release-gate.md).
The three earlier reviews in that directory are superseded and banner themselves as such: the
2026-08-12 gate found a release blocker all three passed over. The oldest, the
[2026-08-08 review](docs/audits/2026-08-08-v3.0.0-production-readiness-review.md), is additionally a
historical record only — it examined the design in which the engine's own `IFusionCache` was the
public contract, and that design was rejected after it.

```bash
dotnet build
dotnet test                                     # Docker required for integration and chaos suites
dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs
```

## Acknowledgements

The cache engine inside Caching.NET is
[FusionCache](https://github.com/ZiggyCreatures/FusionCache) by Jody Donetti (MIT). Caching.NET owns
its setup and its own API — the engine itself is never named in a public signature.

## Licence

MIT.
