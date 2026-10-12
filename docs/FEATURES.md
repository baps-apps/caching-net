# Feature guide

What each Caching.NET behaviour does, how it is configured, what it costs, and when to reach for it.
Every feature section has the same shape — **what it does**, **how it works**, **configure**,
**advantages**, **trade-offs**, **use it / avoid it**, **watch** — so you can jump straight to the one
you need.

This page describes current behaviour only. History is in the [CHANGELOG](../CHANGELOG.md); the
engine-level details behind these behaviours, and where each lives in the code, are in
[ARCHITECTURE](ARCHITECTURE.md) — §3.1 for tag markers, §11 for the rest. Defaults are the values in
[`src/Caching.NET/Options`](../src/Caching.NET/Options): if this page and the code disagree, the code
is right and this page is the bug.

Related: the [README](../README.md) for the API tour, [OPERATIONS](OPERATIONS.md) for failure
behaviour and runbooks, [TELEMETRY](TELEMETRY.md) for every metric and span, and
[SECURITY](SECURITY.md).

## Contents

**Orientation** — 1. [How settings are layered](#1-how-settings-are-layered) ·
2. [Quick reference](#2-quick-reference) · 3. [Entry lifetime at a glance](#3-entry-lifetime-at-a-glance)

**Expiration and freshness** — 4. [Layered expiration](#4-layered-expiration) ·
5. [Jitter](#5-jitter) · 6. [Eager refresh](#6-eager-refresh) ·
7. [Adaptive caching and conditional refresh](#7-adaptive-caching-and-conditional-refresh)

**Resilience** — 8. [Fail-safe](#8-fail-safe) · 9. [Factory timeouts](#9-factory-timeouts) ·
10. [Distributed timeouts](#10-distributed-timeouts) ·
11. [Background operations](#11-background-distributed-and-backplane-operations) ·
12. [Circuit breakers](#12-circuit-breakers) · 13. [Auto-recovery](#13-auto-recovery) ·
14. [Error surfacing](#14-error-surfacing) · 15. [Stampede protection](#15-stampede-protection)

**Invalidation and consistency** — 16. [Remove, Expire, RemoveByTag, Clear](#16-remove-expire-removebytag-clear) ·
17. [Tags](#17-tags) · 18. [Backplane](#18-backplane)

**Memory layer** — 19. [Memory size limit and priority](#19-memory-size-limit-and-priority) ·
20. [Auto-clone](#20-auto-clone)

**Distributed payload** — 21. [Serialization format](#21-serialization-format) ·
22. [Compression](#22-compression) · 23. [Payload size limit](#23-payload-size-limit) ·
24. [Key version placement](#24-key-version-placement)

**Keys and isolation** — 25. [Key prefixes and named caches](#25-key-prefixes-and-named-caches) ·
26. [Key and tag guards](#26-key-and-tag-guards)

**Switches** — 27. [Cached nulls and `CacheValue<T>`](#27-cached-nulls-and-cachevaluet) ·
28. [Disabling the cache](#28-disabling-the-cache) · 29. [Presets](#29-presets)

**Putting it together** — 30. [Recipes](#30-recipes) · 31. [Gotchas checklist](#31-gotchas-checklist)

---

## 1. How settings are layered

A setting can come from four places. Later layers win over earlier ones.

| Layer | Where | Validated at startup | Applies to |
|---|---|---|---|
| Configuration | `CacheOptions` section; named caches under `CacheOptions:NamedCaches:<name>` | Yes | Every call on that cache |
| Fluent builder | `AddCaching(…, cache => cache.WithFailSafe(…))` | Yes — same validator | Every call on that cache |
| Per call | `CacheEntryOverrides` passed to any verb | **No** | That call |
| Per factory run | `ctx.Overrides` inside a context-taking `GetOrSet` factory | **No** | The entry that factory run produces |

- **Overrides are additive.** A `CacheEntryOverrides` property left `null` keeps the configured value.
  The cache mode, the key guard and everything you did not set survive; no override can escape the
  configured defaults. On `RemoveByTag` and `Clear` only the operational overrides apply
  ([§17](#17-tags)).
- **Overrides skip validation.** The validator runs once, at startup, so a negative expiration or an
  out-of-range threshold passed per call is accepted silently. Put defaults in configuration, where
  they are checked.

## 2. Quick reference

| Feature | Default | Main setting | Modes | § |
|---|---|---|---|---|
| Layered expiration | 10 min, every layer | `DefaultExpiration`, `Entry.LocalExpiration`, `Entry.DistributedExpiration` | all | [4](#4-layered-expiration) |
| Jitter | **on** — 10% of lifetime, capped at 2 s | `Entry.JitterFraction`, `Entry.JitterMaxDuration` | all | [5](#5-jitter) |
| Eager refresh | off | `Entry.EagerRefreshThreshold` | all | [6](#6-eager-refresh) |
| Adaptive caching | available | `CacheFactoryContext<T>` | all | [7](#7-adaptive-caching-and-conditional-refresh) |
| Fail-safe | **on** — 2 h window, 30 s throttle | `Resilience.FailSafe*` | all | [8](#8-fail-safe) |
| Factory timeouts | soft off, hard 30 s | `Resilience.FactorySoftTimeout`, `FactoryHardTimeout` | all | [9](#9-factory-timeouts) |
| Distributed timeouts | soft 500 ms, hard 2 s | `Resilience.DistributedSoftTimeout`, `DistributedHardTimeout` | Redis, Hybrid | [10](#10-distributed-timeouts) |
| Background operations | on | `Resilience.AllowBackground*Operations` | Redis, Hybrid | [11](#11-background-distributed-and-backplane-operations) |
| Circuit breakers | 5 s | `Resilience.*CircuitBreakerDuration` | Redis, Hybrid | [12](#12-circuit-breakers) |
| Auto-recovery | on — 1 000 items, 5 retries | `Resilience.AutoRecovery*` | Redis, Hybrid | [13](#13-auto-recovery) |
| Error surfacing | cache errors degrade; factory errors rethrow | `Resilience.ThrowOn*` | all | [14](#14-error-surfacing) |
| Stampede protection | always on, per instance | — | all | [15](#15-stampede-protection) |
| Invalidation verbs | — | `Remove`, `Expire`, `RemoveByTag`, `Clear` | all | [16](#16-remove-expire-removebytag-clear) |
| Tags | 16 per entry, 128 chars | `tags:` argument, `Security.MaximumTag*` | all | [17](#17-tags) |
| Backplane | **on** for Hybrid unless set `false` | `Backplane.Enabled` | Hybrid | [18](#18-backplane) |
| Memory size limit | unbounded | `Entry.MemorySizeLimit`, `Entry.Size`, `Entry.Priority` | InMemory, Hybrid | [19](#19-memory-size-limit-and-priority) |
| Auto-clone | off | `Entry.EnableAutoClone` | InMemory, Hybrid | [20](#20-auto-clone) |
| Serialization | System.Text.Json | `Serialization.Format` | Redis, Hybrid | [21](#21-serialization-format) |
| Compression | off | `Serialization.Compression.*` | Redis, Hybrid | [22](#22-compression) |
| Payload size limit | 1 MiB | `Serialization.MaximumPayloadBytes` | Redis, Hybrid | [23](#23-payload-size-limit) |
| Key version placement | `None` | `Redis.KeyVersionPlacement` | Redis, Hybrid | [24](#24-key-version-placement) |
| Key prefixes, named caches | `ApplicationPrefix` required | `ApplicationPrefix`, `EnvironmentPrefix`, `TenantPrefix` | all | [25](#25-key-prefixes-and-named-caches) |
| Key and tag guards | `Throw`; key 512 chars | `Security.*` | all | [26](#26-key-and-tag-guards) |
| Cached nulls | cached like any value | `CacheValue<T>` | all | [27](#27-cached-nulls-and-cachevaluet) |
| Master switch | on | `Enabled` | all | [28](#28-disabling-the-cache) |
| Presets | — | `UseDevelopmentDefaults()`, `UseProductionDefaults()` | all | [29](#29-presets) |

## 3. Entry lifetime at a glance

How the lifetime features stack for one entry read through `GetOrSet`:

```text
write                                  logical expiry                            physical expiry
  │                                          │                                          │
  ├─────────── Duration ───────────┬ jitter ─┤                                          │
  │                       ▲         0..window│                                          │
  │   EagerRefreshThreshold × Duration:      │                                          │
  │   next GetOrSet refreshes in background  │                                          │
  ├──────────────────── FailSafeMaxDuration, counted from the write ───────────────────┤
  │◄─────── fresh: served as a hit ─────────►│◄── stale: returned only when the ───────►│
  │                                          │    factory fails or times out            │
```

Duration ([§4](#4-layered-expiration)), jitter ([§5](#5-jitter)), eager refresh
([§6](#6-eager-refresh)), fail-safe ([§8](#8-fail-safe)). The physical lifetime is the **longer** of
the logical lifetime and `FailSafeMaxDuration`, not their sum — so the fallback window after expiry
is `FailSafeMaxDuration − Duration`: about 1 h 50 min for a default 10-minute entry, and **none** for
an entry that lives longer than `FailSafeMaxDuration`. With fail-safe off, physical expiry is logical
expiry.

---

## Expiration and freshness

### 4. Layered expiration

> **Default:** 10 minutes for every layer · **Modes:** all (layer settings apply only where the layer exists)

**What it does.** Sets how long an entry is fresh. In Hybrid, the in-process copy (L1) and the Redis
copy (L2) can have different lifetimes.

**How it works.**

- `DefaultExpiration` is the entry's logical duration. `Entry.LocalExpiration` overrides it for the
  memory layer (ignored in Redis mode); `Entry.DistributedExpiration` overrides it for Redis (ignored
  in InMemory mode).
- In Hybrid, an L1 miss falls through to L2, and an L2 hit repopulates L1. A short L1 under a long L2
  means "re-check Redis often, call the factory rarely".
- Per call there is no single duration knob: set `LocalExpiration`, `DistributedExpiration`, or both.
  Set **both** when you want one lifetime whatever the mode.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `DefaultExpiration` | 10 min | `WithDefaultExpiration` | — |
| `Entry.LocalExpiration` | `null` → `DefaultExpiration` | `WithLocalExpiration` | `LocalExpiration` |
| `Entry.DistributedExpiration` | `null` → `DefaultExpiration` | `WithDistributedExpiration` | `DistributedExpiration` |

Startup rules: every duration must be greater than zero, and in Hybrid the effective local lifetime
must not exceed the effective distributed one — otherwise a pod would keep serving a value Redis has
already dropped.

**Advantages.** A short L1 bounds how long one pod can serve data another pod has replaced, even when
the backplane is off or down. A longer L2 keeps the shared copy warm, so pods rarely run the factory.

**Trade-offs.**

- Every L1 expiry over a live L2 entry costs a Redis round trip. A very short L1 turns Hybrid into
  Redis mode with extra steps.
- Per-call durations are not validated, so a per-call L1 longer than L2 is accepted.

| Use it for | Avoid |
|---|---|
| Hybrid on several pods: an L1 of 1–5 min under a longer L2 | An L1 longer than L2 (rejected at startup, not checked per call) |
| Data with its own natural lifetime — a named cache ([§25](#25-key-prefixes-and-named-caches)) or a per-call override | Setting only `LocalExpiration` per call in Redis mode — that mode ignores it |

**Watch.** `caching.net.hits` / `caching.net.misses`, and `caching.net.layer.duration{cache.layer=redis}`
— shortening L1 shows up as more time spent in the Redis layer.

### 5. Jitter

> **Default:** on — 10% of the entry's lifetime, capped at 2 s · **Modes:** all

**What it does.** Adds a random 0…window to each entry's lifetime, so entries written at the same
moment do not all expire at the same moment.

**How it works.**

- `window = min(shortest lifetime × Entry.JitterFraction, Entry.JitterMaxDuration)`, where the
  shortest lifetime is the smallest of `DefaultExpiration`, `LocalExpiration` and
  `DistributedExpiration` — the layer that expires the entry first.
- Jitter only ever **adds** time. It is proportional because a flat window does not scale: a flat 2 s
  would let a 300 ms entry live 2.3 s.
- When a per-call override or `ctx.Overrides` changes an entry's duration, the window is recalculated
  from the new duration. An explicit per-call `JitterMaxDuration` is used literally, as a flat window.

| Entry lifetime | 10% | Applied (2 s cap) |
|---|---|---|
| 300 ms | 30 ms | 30 ms |
| 5 s | 500 ms | 500 ms |
| 10 min | 60 s | **2 s** |
| 1 h | 6 min | **2 s** |

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Entry.JitterFraction` | `0.1` | `WithProportionalJitter(fraction, maximum)` | `JitterFraction` |
| `Entry.JitterMaxDuration` | 2 s | `WithJitter(TimeSpan)` | `JitterMaxDuration` (flat; ignores the fraction) |

`JitterMaxDuration = 0` disables jitter. `JitterFraction = null` makes `JitterMaxDuration` a flat
window. Startup rules: the cap must not be negative; the fraction must be in `(0, 1]`.

**Advantages.** Spreads expiry after bulk loads, warm-ups and deploys, so the origin sees a ramp rather
than a spike; in Hybrid, pods' L1 copies of a key expire at slightly different moments. One random
number per write.

**Trade-offs.**

- An entry lives between `duration` and `duration + window`, so worst-case staleness grows by the
  window, and tests that assert exact expiry become flaky.
- The 2 s cap is negligible on long TTLs — 0.06% of an hour. Bulk loads with hour-long TTLs need a
  higher cap.
- It does nothing for a single hot key — that is stampede protection ([§15](#15-stampede-protection))
  and eager refresh ([§6](#6-eager-refresh)).

| Keep the default, or raise the cap | Disable it |
|---|---|
| Caches warmed in bulk; batch writers | Tests that assert expiry timing |
| Hybrid with many pods | Entries whose TTL is a contract: rate-limit windows, OTP and one-time-link expiry, lock-like entries |
| Long TTLs — `WithProportionalJitter(0.1, TimeSpan.FromMinutes(5))` | (The built-in health probe already disables it) |

**Watch.** No dedicated metric. Bursts on `caching.net.misses` and `caching.net.factory.executions`
at a fixed interval after a bulk load mean the window is too small.

### 6. Eager refresh

> **Default:** off (`UseProductionDefaults()` sets `0.8`) · **Modes:** all

**What it does.** Refreshes an entry in the background before it expires, so a hot key never expires
on the request path.

**How it works.**

- With a threshold of `0.8` and a 10-minute duration, the first `GetOrSet` read after minute 8 returns
  the cached value immediately and starts the factory in the background; its result replaces the
  entry.
- It is triggered by reads, not a timer, and only by `GetOrSet` — the only verb with a factory. An
  entry nobody reads after the threshold simply expires.
- A failed background refresh reaches no caller. The current value stays until it expires, and
  fail-safe can carry it beyond that.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Entry.EagerRefreshThreshold` | `null` (off) | `WithEagerRefresh(0.8f)` | `EagerRefreshThreshold` |

Startup rule: strictly between 0 and 1.

**Advantages.** No latency spike when a hot key expires, and far fewer of the cold-key events
stampedes are made of — which matters because stampede protection is per instance only
([§15](#15-stampede-protection)).

**Trade-offs.**

- More factory calls: a continuously read key reloads every `threshold × duration`. `0.8` is about 25%
  more origin load for hot keys; `0.5` doubles it.
- No benefit for rarely-read keys. Background failures are visible only in telemetry.

| Use it for | Avoid |
|---|---|
| Hot keys on latency-sensitive paths | Rarely-read keys — no read in the window means no refresh |
| Expensive factories: slow queries, aggregations, remote APIs | Origins billed or rate-limited per call, unless the extra load is budgeted |

**Watch.** `caching.net.background.operations` (eager refreshes), `caching.net.factory.executions`
(background runs included).

### 7. Adaptive caching and conditional refresh

> **Default:** available on the context-taking `GetOrSet` overload · **Modes:** all

**What it does.** Lets the factory see what is already cached and decide the new entry's behaviour
while it runs.

**How it works.** The context-taking overload passes a `CacheFactoryContext<T>`:

| Member | Use |
|---|---|
| `HasStaleValue`, `StaleValue` | The previous value, when one is still held (expired, or being eagerly refreshed) |
| `ETag`, `LastModified` | Stored with the entry. Read them to send a conditional request; set them from the response |
| `NotModified()` | Keep the cached value and restart its lifetime. Throws when there is no stale value — check `HasStaleValue` first |
| `Fail(reason)` | Report a failure without throwing. Fail-safe ([§8](#8-fail-safe)) serves the stale value if there is one |
| `Overrides` | `CacheEntryOverrides` for the entry this run produces. Jitter is recalculated from any new duration |

```csharp
var order = await cache.GetOrSetAsync<Order>($"order:{id}", async (ctx, ct) =>
{
    var response = await ordersApi.GetAsync(id, ifNoneMatch: ctx.ETag, ct);
    if (response.NotModified && ctx.HasStaleValue)
    {
        return ctx.NotModified();                    // 304: keep the value, restart its lifetime
    }

    ctx.ETag = response.ETag;

    // Closed orders rarely change; open ones do. Set both layers so L1 never outlives L2.
    var lifetime = response.Value.IsClosed ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(30);
    ctx.Overrides.LocalExpiration = lifetime;
    ctx.Overrides.DistributedExpiration = lifetime;
    return response.Value;
}, token: ct);
```

**Advantages.** A conditional request turns a refresh into a cheap 304 when nothing changed; the TTL
follows the data instead of a global guess; `Fail()` avoids an exception for expected failures such
as an upstream 503.

**Trade-offs.**

- The context overload needs an explicit type argument (`GetOrSetAsync<Order>`): the compiler cannot
  infer it from a lambda parameter.
- `NotModified()` throws when there is no stale value.
- `ctx.Overrides` is not validated, including Hybrid's "L1 must not outlive L2" — set both lifetimes.

| Use it for | Avoid |
|---|---|
| HTTP upstreams that support ETag or Last-Modified | Simple loads — the context-free overload is shorter and infers the type |
| Data whose volatility is known when it is loaded; upstreams that report failure without throwing | |

---

## Resilience

### 8. Fail-safe

> **Default:** **on** — 2 h fallback window, 30 s retry throttle (off in `UseDevelopmentDefaults()`) · **Modes:** all

**What it does.** When refreshing an expired entry fails, the cache returns the expired value instead
of an error.

**How it works.**

- An entry has a **logical** lifetime (duration plus jitter) and a **physical** one — the longer of
  the logical lifetime and `FailSafeMaxDuration`, both counted from the write ([§3](#3-entry-lifetime-at-a-glance)).
  After logical expiry it is a miss, but it is kept until physical expiry. An entry given a per-call
  lifetime longer than `FailSafeMaxDuration` has no fallback window — raise `FailSafeMaxDuration` on
  the same call.
- The next `GetOrSet` runs the factory. If it throws, times out ([§9](#9-factory-timeouts)) or calls
  `ctx.Fail()`, the stale value is returned **and re-cached for `FailSafeThrottleDuration`**, so the
  failing dependency is retried at most once per throttle window per key.
- A Redis error or timeout with a stale copy at hand also returns that copy.
- With no stale value, the caller gets the `failSafeDefaultValue` argument if it passed one, and
  otherwise the factory's original exception.
- Only `GetOrSet` uses it; `TryGet`, `GetOrDefault`, `ExistsAsync` and `GetManyAsync` treat an expired
  entry as a miss. It also decides what `Expire`, `RemoveByTag` and `Clear` leave behind
  ([§16](#16-remove-expire-removebytag-clear)).

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Resilience.FailSafeEnabled` | `true` | `WithFailSafe(enabled)` | `FailSafe` |
| `Resilience.FailSafeMaxDuration` | 2 h | `WithFailSafe(maxDuration: …)` | `FailSafeMaxDuration` |
| `Resilience.FailSafeThrottleDuration` | 30 s | `WithFailSafe(throttleDuration: …)` | `FailSafeThrottleDuration` |
| `Observability.FailSafeActivationLogLevel` | `Warning` | `WithObservability` | — |
| `failSafeDefaultValue` | none | — | Argument on the context-taking `GetOrSet*` overload |

Startup rules, while fail-safe is on: `FailSafeMaxDuration` must be at least `DefaultExpiration` — so
a `DefaultExpiration` over the 2 h default needs a larger `FailSafeMaxDuration` too; the throttle must
be greater than zero. Only `DefaultExpiration` is compared — a longer
`Entry.DistributedExpiration` or `Entry.LocalExpiration` passes and simply has no fallback window.

```csharp
// Last resort when there is nothing stale to fall back to.
var rates = await cache.GetOrSetAsync<FxRates>(
    "fx-rates",
    async (ctx, ct) => await ratesApi.GetAsync(ct),
    failSafeDefaultValue: CacheValue<FxRates?>.Of(FxRates.Empty),
    token: ct);
```

**Advantages.** An upstream outage is invisible to users: they get slightly old data instead of a 500.
The throttle stops a struggling dependency from being hammered, and with a factory soft timeout
([§9](#9-factory-timeouts)) a *slow* dependency becomes a *fast* stale answer. Each activation is
counted, logged and tagged `cache.result=stale` on the span.

**Trade-offs.**

- **Stale data is served silently** — nothing in the return value says so, for up to
  `FailSafeMaxDuration` after the write.
- **Storage.** Expired entries are retained: under the defaults every entry shorter than 2 h — a
  1-minute entry included — occupies Redis for 2 h. In memory, retained entries count against any
  `Entry.MemorySizeLimit`.
- **It hides bugs in development** (which is why the development preset turns it off) and **outages**,
  unless someone alerts on `caching.net.fail_safe.served`.
- `ClearAsync()` defaults to `allowFailSafe: true`, so cleared data can come back as a fallback
  ([§16](#16-remove-expire-removebytag-clear)).

| Turn it on | Turn it off — globally, per named cache, or per call with `FailSafe = false` |
|---|---|
| Reference data: catalogs, lookups, configuration, feature lists | Authorization: permissions, roles, token or session revocation |
| Responses from flaky or rate-limited third-party APIs | Money and stock used for decisions: balances, inventory, prices at checkout |
| Expensive reports and aggregates where minutes-old is fine | One-time values (OTPs, single-use links) and anything with a legal freshness requirement |
| Read-heavy production traffic | Development and tests; health probes (the built-in probe already disables it) |

**Watch.** `caching.net.fail_safe.served` — any sustained rate means an upstream is failing (see the
[runbook](OPERATIONS.md#upstream-dependency-failing)); `caching.net.factory.executions` with an error
result.

### 9. Factory timeouts

> **Default:** soft off, hard 30 s (`UseProductionDefaults()`: 1 s / 10 s) · **Modes:** all

**What it does.** Caps how long a caller waits for a factory.

**How it works.**

- The **soft timeout** applies only when fail-safe is on **and** a stale value exists; past it, the
  caller gets the stale value. A cold key has nothing stale, so only the hard timeout protects it.
- The **hard timeout** always applies; past it, the caller gets the stale value if there is one, and
  otherwise a timeout exception.
- With `AllowTimedOutFactoryBackgroundCompletion` (default `true`), a timed-out factory — soft or hard
  — keeps running, and its result is stored when it completes.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Resilience.FactorySoftTimeout` | infinite (off) | `WithFactoryTimeouts(softTimeout: …)` | `FactorySoftTimeout` |
| `Resilience.FactoryHardTimeout` | 30 s | `WithFactoryTimeouts(hardTimeout: …)` | `FactoryHardTimeout` |
| `Resilience.AllowTimedOutFactoryBackgroundCompletion` | `true` | `WithResilience` | — |
| `Observability.SyntheticTimeoutLogLevel` | `Debug` | `WithObservability` | — |

Startup rules: the hard timeout must be greater than zero; soft must not exceed hard.

**Advantages.** A slow dependency cannot blow the request's latency budget, and with background
completion it still refreshes the cache.

**Trade-offs.**

- The soft timeout does nothing for cold keys, or with fail-safe off. A soft timeout below the
  factory's normal p99 serves stale data on ordinary traffic.
- A timed-out factory still runs to completion, so the upstream carries the load either way.
- Timeouts log at `Debug` by default; they are visible in metrics, not logs.

| Use it for | Avoid |
|---|---|
| Latency-sensitive endpoints over slow queries or APIs: soft 0.5–1 s with fail-safe on | A soft timeout below the factory's normal latency |
| Always: a hard ceiling, so nothing waits forever | Expecting a soft timeout alone to protect a cold key |

**Watch.** `caching.net.errors{cache.error.type=SyntheticTimeoutException, cache.layer=factory}`.

### 10. Distributed timeouts

> **Default:** soft 500 ms, hard 2 s · **Modes:** Redis, Hybrid

**What it does.** Caps how long a single Redis operation can hold a caller.

**How it works.**

- The **soft timeout** applies only when fail-safe is on and a stale copy is at hand (in Hybrid, an
  expired L1 copy); past it, the stale copy is returned. Redis mode keeps no local copy, so there the
  soft timeout rarely has anything to return.
- The **hard timeout** always applies; past it, the Redis operation is abandoned for this call and
  treated as a miss, so the factory runs.
- `Redis.CommandTimeout` (2 s) is the Redis client's own timeout underneath both.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Resilience.DistributedSoftTimeout` | 500 ms | `WithResilience` | `DistributedSoftTimeout` |
| `Resilience.DistributedHardTimeout` | 2 s | `WithResilience` | `DistributedHardTimeout` |

Startup rule: soft must not exceed hard.

**Advantages.** A slow or overloaded Redis does not become a slow application; Hybrid keeps answering
from L1.

**Trade-offs.** A hard timeout becomes a factory call, so a slow Redis raises origin load. Values that
are too tight cause misses during ordinary latency spikes. Logged at `Debug` by default.

| Use it for | Avoid |
|---|---|
| The defaults, in most deployments; higher values for cross-region Redis or large payloads | Lowering them without measured Redis p99 latency |

**Watch.** `caching.net.layer.duration{cache.layer=redis}`, `caching.net.redis.errors`.

### 11. Background distributed and backplane operations

> **Default:** on · **Modes:** Redis, Hybrid (backplane publishes: Hybrid)

**What it does.** Releases the caller as soon as the local work is done; Redis writes and backplane
publishes finish in the background.

**How it works.** `SetAsync` and `GetOrSet` update L1 (in Hybrid) and return; the Redis write and the
backplane notification complete afterwards. Background failures are logged, counted and queued for
auto-recovery ([§13](#13-auto-recovery)); they never reach the caller.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Resilience.AllowBackgroundDistributedOperations` | `true` | `WithResilience` | `AllowBackgroundDistributedOperations` |
| `Resilience.AllowBackgroundBackplaneOperations` | `true` | `WithResilience` | `AllowBackgroundBackplaneOperations` |

**Advantages.** Redis latency and Redis outages stay off the request path.

**Trade-offs.**

- **No read-your-own-write across instances.** Another pod — or, in Redis mode, the *same* pod — can
  read the previous value or a miss for a short window after `SetAsync` returns. See
  [README, known limitations](../README.md#28-known-limitations).
- With background distributed operations **off**, serializer and payload-limit failures reach the
  caller even with `ThrowOnSerializationErrors: false`; a startup warning names this combination
  ([OPERATIONS](OPERATIONS.md#foreground-writes-surface-serialization-failures)).

| Use it for | Avoid |
|---|---|
| Ordinary traffic — leave it on | Turning it off globally |
| Off **per call** when the caller must see its own write in Redis, e.g. before publishing an event that makes another service read the key | |

**Watch.** `caching.net.redis.errors`, `caching.net.backplane.errors`. A Redis outage also raises
unobserved task exceptions — see
[OPERATIONS](OPERATIONS.md#unobserved-task-exceptions-during-a-redis-outage).

### 12. Circuit breakers

> **Default:** 5 s, for Redis and for the backplane separately · **Modes:** Redis, Hybrid

**What it does.** After a Redis or backplane error, stops trying that dependency for a short time.

**How it works.** An error opens the breaker for the configured duration. While it is open,
operations skip that layer — Hybrid serves from L1 and factories, Redis mode goes straight to the
factory. `TimeSpan.Zero` disables the breaker.

**Configure.**

| Setting | Default | Builder |
|---|---|---|
| `Resilience.DistributedCircuitBreakerDuration` | 5 s | `WithResilience` |
| `Resilience.BackplaneCircuitBreakerDuration` | 5 s | `WithResilience` |

No per-call override.

**Advantages.** No retry storms, no log floods, and no per-request timeout penalty while Redis is down.

**Trade-offs.** Right after Redis recovers, up to one breaker window still bypasses it: more factory
calls, and writes queued for auto-recovery. One transient error opens the breaker for the whole
instance.

| Use it for | Avoid |
|---|---|
| The defaults; a longer window for a Redis known to flap | Zero, except in tests that need every call to reach Redis |

**Watch.** `caching.net.errors`, `caching.net.redis.errors` and `caching.net.backplane.errors` with
`CircuitBreakerOpen`.

### 13. Auto-recovery

> **Default:** on — 2 s sweeps, at most 1 000 queued items, 5 retries each · **Modes:** Redis, Hybrid

**What it does.** Queues Redis writes and backplane notifications that failed during an outage, and
replays them when the dependency is back.

**How it works.** Failed operations go into a per-instance, in-memory queue, retried every
`AutoRecoveryDelay`. An item is discarded after `AutoRecoveryMaxRetryCount` attempts; the queue holds
at most `AutoRecoveryMaxItems`.

**Configure.**

| Setting | Default | Builder |
|---|---|---|
| `Resilience.AutoRecoveryEnabled` | `true` | `WithResilience` |
| `Resilience.AutoRecoveryDelay` | 2 s | `WithResilience` |
| `Resilience.AutoRecoveryMaxItems` | 1 000 (`null` = unbounded) | `WithResilience` |
| `Resilience.AutoRecoveryMaxRetryCount` | 5 (`null` = unlimited) | `WithResilience` |

Startup rules: both limits must be greater than zero when set.

**Advantages.** After an outage Redis receives the latest writes and other pods' L1 copies are
invalidated without a manual flush — the deployment converges instead of keeping a split view.

**Trade-offs.** The queue is per instance and lost on restart. Past the caps, some operations are
never replayed and only TTL expiry corrects the drift. Replay adds a burst of Redis traffic.

| Use it for | Avoid |
|---|---|
| Keep it on; raise `AutoRecoveryMaxItems` for write-heavy services that must converge after long outages (costs memory) | Turning it off, unless relying on TTL alone is an explicit decision |

**Watch.** The `Redis connection restored` log; `caching.net.redis.errors` and
`caching.net.backplane.errors` falling back to zero.

### 14. Error surfacing

> **Default:** cache-layer errors degrade; factory errors rethrow the original exception · **Modes:** all

**What it does.** Decides whether a failure inside the cache reaches the caller.

**How it works.** By default, Redis, serialization and backplane errors are logged and counted, and the
call carries on using L1 or the factory. A factory exception with no fail-safe fallback is rethrown as
the **original** exception, so the application's own error handling keeps working.

**Configure.**

| Setting | Default | Builder |
|---|---|---|
| `Resilience.ThrowOnDistributedCacheErrors` | `false` | `WithResilience` |
| `Resilience.ThrowOnSerializationErrors` | `false` | `WithResilience` |
| `Resilience.ThrowOnBackplaneErrors` | `false` | `WithResilience` |
| `Resilience.ThrowOriginalExceptions` | `true` | `WithResilience` |

`UseDevelopmentDefaults()` sets the first two to `true`.

**Advantages.** The cache never takes the application down.

**Trade-offs.** Failures are quiet: a misconfigured Redis looks like a low hit ratio, and a corrupt
payload silently becomes a miss. `ThrowOnSerializationErrors: false` does not hold on the write path
when background distributed operations are off ([§11](#11-background-distributed-and-backplane-operations)).

| Use it for | Avoid |
|---|---|
| `true` in development and integration tests, to surface bugs | `true` in production — one Redis blip becomes user-facing errors |

**Watch.** `caching.net.errors`, `caching.net.redis.errors`.

### 15. Stampede protection

> **Default:** always on, per instance · **Modes:** all (Redis mode runs the factory twice per cold key)

**What it does.** When many callers on one instance ask for the same missing key at once, the factory
runs once and every caller gets its result.

**How it works.**

- An in-process lock per key: the first caller runs the factory; the rest wait for it.
- InMemory and Hybrid run the factory **once** per cold key per instance; Redis mode runs it
  **twice**.
- The lock is **never distributed**. *N* pods racing the same cold key run the factory up to *N*
  times, for at most one factory duration: once the first pod writes L2, later readers hit it.

**Configure.** Nothing — it is not optional.

**Advantages.** A thundering herd inside one pod costs one origin call, for free.

**Trade-offs.**

- Across pods it does not hold, so **a factory must be a side-effect-free read**. Increments, sends,
  charges and job enqueues do not belong in a cache factory in any mode.
- Waiting callers share the one factory's latency and, under a hard timeout, its failure.

**To reduce cross-pod duplication**, reach for jitter ([§5](#5-jitter)), eager refresh
([§6](#6-eager-refresh)) and fail-safe ([§8](#8-fail-safe)) before a distributed lock, which puts a
Redis round trip on the read path and stalls whenever a lock holder dies. See
[README, known limitations](../README.md#28-known-limitations).

**Watch.** `caching.net.factory.executions` against `caching.net.misses`.

---

## Invalidation and consistency

### 16. Remove, Expire, RemoveByTag, Clear

> **Modes:** all. In Hybrid with the backplane on, all four propagate to other pods.

**What it does.** Four ways to make the cache stop serving something. They differ in scope and in
whether a fail-safe fallback survives.

| Verb | Scope | Kept as a fail-safe fallback? |
|---|---|---|
| `RemoveAsync(key)` | One key | **No** — gone from every layer |
| `ExpireAsync(key)` | One key | Only the in-memory (L1) copy, and only for a later `GetOrSet` with fail-safe on. The Redis copy is always deleted — so in Redis mode, `Expire` behaves like `Remove` |
| `RemoveByTagAsync(tag)` | Every entry with the tag | Per entry: entries written with fail-safe on are expired; entries written with it off are removed |
| `ClearAsync()` | The whole cache — this prefix only | **Yes** — the default `allowFailSafe: true` expires everything |
| `ClearAsync(allowFailSafe: false)` | Same | **No** — removes everything |

**Choosing one.**

- **Hard invalidation** — a revoked permission, a deleted record, a legal takedown: `RemoveAsync`,
  `ClearAsync(allowFailSafe: false)`, or tags on entries written with fail-safe off.
- **Soft invalidation** — "refresh when you can, but keep serving if the source is down":
  `ExpireAsync` or `ClearAsync()`.
- **Replace without a miss window** — `RefreshAsync` (in `CacheExtensions`) runs the factory and
  overwrites the entry, so readers never see the miss that remove-then-read creates.
- **Many keys** — `RemoveManyAsync`.

`ClearAsync` never issues `FLUSHDB`: it is scoped to this cache's key prefix, so it is safe on a
shared Redis database.

**Trade-offs.** `ClearAsync()` and `ExpireAsync()` look like deletions but leave fallbacks behind;
the default `allowFailSafe: true` on `Clear` surprises most people.

**Watch.** `caching.net.invalidations` — one per caller-requested call. Entries leaving the
in-process layer — expired, evicted for size, replaced, or removed by these verbs — are counted on
`caching.net.evictions`.

### 17. Tags

> **Default:** at most 16 tags per entry, 128 characters each; violations throw · **Modes:** all

**What it does.** Attaches labels to entries when they are written, so every entry with a label can be
invalidated in one call.

**How it works.**

- Pass `tags:` to `SetAsync`, `GetOrSetAsync` or `SetManyAsync`. `RemoveByTagAsync(tag)` takes one
  tag — loop for several.
- Tags are stored **with the entry**; there is no tag → keys index and no key scan. An invalidation
  writes one **marker** recording when the tag was invalidated, and touches no tagged entry.
- Invalidation happens **at read time**: a read of a tagged entry checks its tags' markers, and an
  entry written before one of them is treated as invalidated — expired if it was written with
  fail-safe on, removed if not — and cleaned up then, including its Redis copy.
- `ClearAsync` uses the same mechanism with reserved markers. In Redis mode, and in Hybrid without a
  backplane, every read checks them, so even an untagged hit pays two marker reads.
- Markers persist in Redis for **at least 10 days**, so an instance that was offline during an
  invalidation still sees it. In Hybrid, a marker's L1 copy is bounded by the local expiration, and
  the backplane evicts it on other pods within milliseconds.

**Configure.**

| Setting | Default | Builder |
|---|---|---|
| `Security.MaximumTagCount` | 16 | `WithSecurity` |
| `Security.MaximumTagLength` | 128 | `WithSecurity` |
| `Security.TagPolicy` | `Throw` (`Ignore`, `Warn`, `Throw`) | `WithSecurity` |
| `Security.AllowTagsInTelemetry` | `false` | `WithSecurity` |

**Advantages.** Invalidate a group — per tenant, per category, per parent entity — without tracking
its keys, in all three modes, with no `SCAN`/`KEYS` against Redis.

**Trade-offs.**

- **Read cost in Redis mode.** A hit costs 3 Redis commands for an untagged entry and 3 + *n* for an
  entry with *n* tags — ×3.10 and ×4.92 latency on loopback
  ([BENCHMARKS](BENCHMARKS.md#redis-mode-the-cost-of-authoritative-tag-markers)). Hybrid and InMemory
  answer markers from memory.
- Invalidation is lazy: an invalidated entry stays in storage until it is next read or expires.
- **Overrides on `RemoveByTagAsync` and `ClearAsync` are operational only.** Background flags,
  `SkipBackplaneNotification` and the distributed timeouts apply; lifetime, fail-safe and memory-layer
  overrides are ignored, because the marker's long lifetime is what keeps the invalidation effective.
  The first such call on a cache logs warning 3041 naming them.
- Tag values are often tenant- or user-scoped, so they are kept out of logs, traces and metrics by
  default.

| Use it for | Avoid |
|---|---|
| Group invalidation by tenant, category or parent aggregate | A unique tag per entry — that is what the key is for; many tags per entry on hot Redis-mode paths |

**Watch.** `caching.net.invalidations`; `caching.net.guard.violations` for tag-limit breaches.

### 18. Backplane

> **Default:** **on** for Hybrid unless `Backplane.Enabled` is set `false` · **Modes:** Hybrid only (rejected at startup in Redis and InMemory)

**What it does.** A Redis pub/sub channel that tells other pods to drop their L1 copy when one pod
writes, removes, expires or invalidates by tag.

**How it works.**

- After a write or invalidation, the instance publishes a notification; every other instance evicts
  its L1 copy, so its next read goes to Redis. An instance ignores its own messages.
- The channel prefix defaults to the cache's key prefix, so applications sharing a Redis instance do
  not receive each other's notifications.
- A Hybrid cache gets it from `UseHybrid(connectionString)`, and from a configuration section that
  does not set `Backplane.Enabled`. Hybrid set on `CachingOptions` directly in code gets no default.
  Hybrid without a backplane logs a startup warning.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Backplane.Enabled` | `false`; `true` for Hybrid unless set | `WithBackplane(enabled, channelPrefix)` | — |
| `Backplane.ChannelPrefix` | the key prefix | `WithBackplane(channelPrefix: …)` | — |
| `Backplane.WaitForInitialSubscribe` | `false` | `Options.Backplane` | — |
| `Resilience.AllowBackgroundBackplaneOperations` | `true` | `WithResilience` | `AllowBackgroundBackplaneOperations` |
| `Resilience.BackplaneCircuitBreakerDuration` | 5 s | `WithResilience` | — |
| — | — | — | `SkipBackplaneNotification` (suppresses one write's broadcast, for bulk warm-ups) |

**Advantages.** L1 latency with near-immediate consistency across pods, so `Entry.LocalExpiration`
can stay longer than it safely could without one.

**Trade-offs.**

- One pub/sub message per write, delivered to every pod: write-heavy caches multiply Redis traffic by
  pod count.
- Asynchronous: a short window remains in which another pod serves its old copy. A pod disconnected
  when a message is sent misses it — `Entry.LocalExpiration` bounds that pod's staleness, so keep it
  modest even with a backplane.

| Use it for | Avoid |
|---|---|
| Hybrid with more than one replica — always | Broadcasting bulk warm-up writes — skip them per call |

**Watch.** `caching.net.backplane.errors`, `caching.net.background.operations`, and the
`cache.backplane.publish` / `cache.backplane.receive` spans ([TELEMETRY](TELEMETRY.md)).

---

## Memory layer

### 19. Memory size limit and priority

> **Default:** unbounded; priority `Normal` · **Modes:** InMemory, Hybrid

**What it does.** Caps the in-process memory layer and chooses what is evicted first under pressure.

**How it works.**

- `Entry.MemorySizeLimit` caps the **sum of the entries' `Size` values** — not bytes; Caching.NET
  cannot measure an arbitrary object's footprint. `WithMemorySizeLimit(limit)` also sets
  `Entry.Size = 1`, making the limit a cap on the **number** of entries. To approximate bytes, charge
  each entry a size in a unit you choose (per call with `Size`).
- Once a limit is set, an entry with no size is **not cached**, so startup validation requires
  `Entry.Size > 0` alongside a limit.
- Under pressure `Low` goes first, then `Normal`, then `High`. `NeverRemove` is never evicted for size,
  but still expires.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Entry.MemorySizeLimit` | `null` (unbounded) | `WithMemorySizeLimit(limit, defaultEntrySize)` | — |
| `Entry.Size` | `null` | `WithMemorySizeLimit(…, defaultEntrySize)` | `Size` |
| `Entry.Priority` | `Normal` | `Options.Entry.Priority` | `Priority` |

**Advantages.** Bounded memory for unbounded key spaces (per-user or per-tenant keys); priority keeps
expensive entries resident longest.

**Trade-offs.** A cap on units, not bytes. Evictions mean more misses, and so more Redis and factory
calls. With fail-safe on, expired-but-retained entries still count. Overusing `NeverRemove` makes the
limit ineffective.

| Use it for | Avoid |
|---|---|
| Unbounded key spaces: per user, per tenant, per search query; memory-constrained pods | A limit so small the cache churns; `NeverRemove` on anything numerous |

**Watch.** `caching.net.evictions`.

### 20. Auto-clone

> **Default:** off · **Modes:** InMemory, Hybrid (no effect in Redis)

**What it does.** Hands each caller a deep copy from the memory layer, so mutating a returned object
cannot corrupt the cached instance.

**How it works.** Every memory hit is copied by serializing and deserializing it in the cache's
`Serialization.Format` ([§21](#21-serialization-format)) — InMemory caches configure that serializer
for cloning alone. Redis mode keeps nothing in memory, so there it has no effect.

**Configure.**

| Setting | Default | Builder | Per call |
|---|---|---|---|
| `Entry.EnableAutoClone` | `false` | `Options.Entry.EnableAutoClone` (no dedicated method) | `EnableAutoClone` |

**Advantages.** Removes a classic in-memory bug: a caller sorts or edits a cached list, and every other
caller sees the change.

**Trade-offs.**

- A serialization round trip on every memory hit — an L1 hit loses most of its speed advantage.
- Cloned types must be serializable in the configured format. In a trimmed or native-AOT application
  that means a source-generated `JsonSerializerOptions.TypeInfoResolver` covering them.

| Use it for | Avoid |
|---|---|
| Caches of mutable types that callers might modify | Hot paths — cache immutable types (records, `IReadOnlyList<T>`) and leave it off |

---

## Distributed payload

### 21. Serialization format

> **Default:** System.Text.Json · **Modes:** Redis, Hybrid (the memory layer stores objects, not bytes)

**What it does.** Chooses the wire format for values stored in Redis.

| | `SystemTextJson` (default) | `MessagePack` |
|---|---|---|
| Size and speed | Larger, slower | Smaller, faster |
| Readable in Redis | Yes | No |
| Safety | No type-name handling | Contractless; type names never written or read |
| Trim / native AOT | Yes, with a source-generated `JsonSerializerContext` | Reflection-based |
| Note | — | Writer and reader must agree on the shape |

**Configure.** `Serialization.Format`, or `WithJsonSerialization(jsonSerializerOptions)` /
`WithMessagePackSerialization()`. `Serialization.JsonSerializerOptions` is code-only — use it to
supply a source-generated `TypeInfoResolver` for trim and AOT.

**Trade-offs.** Switching the format on a live cache makes existing entries unreadable to the new
code: they fail to deserialize, are counted as serialization errors, treated as misses, and
overwritten. During a rolling deploy, old and new pods overwrite each other's entries until the
rollout finishes. Plan it as a cold-cache deploy.

| Use it for | Avoid |
|---|---|
| JSON by default — debuggable, AOT-friendly; MessagePack where payloads are large or hot and the gain is measured ([BENCHMARKS](BENCHMARKS.md)) | Switching format without planning for a cold cache |

**Watch.** `caching.net.serialization.duration`, `caching.net.payload.size`, `caching.net.errors`.

### 22. Compression

> **Default:** off; when on, Brotli above 16 KiB, with decompression capped at 16 MiB · **Modes:** Redis, Hybrid

**What it does.** Compresses large payloads before they go to Redis.

**How it works.** Payloads at or above `ThresholdBytes` are Brotli-compressed, and kept compressed only
when that is smaller. Every payload carries a one-byte format header, so turning compression on or off
later does not make existing entries unreadable. Decompression stops at `MaximumDecompressedBytes`
and the payload is rejected as corrupt, which bounds decompression-bomb exposure.

**Configure.**

| Setting | Default | Builder |
|---|---|---|
| `Serialization.Compression.Enabled` | `false` | `WithCompression(thresholdBytes, maximumDecompressedBytes)` |
| `Serialization.Compression.ThresholdBytes` | 16 KiB | same |
| `Serialization.Compression.MaximumDecompressedBytes` | 16 MiB | same |

Startup rules, while compression is on: the threshold must not be negative, and the decompression
ceiling must be greater than zero and at least `Serialization.MaximumPayloadBytes` — otherwise a
legitimate entry at the payload limit would be rejected on read.

**Advantages.** Less Redis memory and network for large payloads.

**Trade-offs.** CPU on every write and every L2 read; incompressible data gains nothing.

| Use it for | Avoid |
|---|---|
| Large, text-like payloads (JSON lists, documents) of 16 KiB and up | Small payloads; already-compressed data such as images or archives |

**Watch.** `caching.net.payload.size`, `caching.net.serialization.duration`.

### 23. Payload size limit

> **Default:** 1 MiB · **Modes:** Redis, Hybrid

**What it does.** Refuses to store, or to read back, a single value above a size.

**How it works.**

- **On write**, a serialized value over the limit is not written to Redis. The check runs **before**
  compression, so compression does not let a larger value fit. With background distributed operations
  on (the default) the caller still gets its value; with them off, the call throws
  `InvalidOperationException`.
- **On read**, a stored payload over the limit is rejected as corrupt and treated as a miss; a
  compressed one is additionally bounded by `Compression.MaximumDecompressedBytes`
  ([§22](#22-compression)).

**Configure.** `Serialization.MaximumPayloadBytes`, or `WithMaximumPayloadBytes(bytes)`. Must be
greater than zero.

**Advantages.** Bounds memory pressure and oversized-value denial of service; catches accidental
caching of whole aggregates.

**Trade-offs.** An oversized value silently stays uncached — only a warning and a metric say so.
Lowering the limit makes existing larger entries unreadable (they become misses).

| Use it for | Avoid |
|---|---|
| Keep the default; treat values near it as a signal to cache a projection | Raising it to make one oversized value fit |

**Watch.** `caching.net.guard.violations`, `caching.net.payload.size`.

### 24. Key version placement

> **Default:** `None` · **Modes:** Redis, Hybrid

**What it does.** Chooses where the engine's wire-format version (`v2`) appears in each Redis key.

| Placement | Physical key | Notes |
|---|---|---|
| `None` (default) | `orders-api:prod:Order:1` | Readable; prefix scans work. No isolation if a future engine upgrade changes the stored format — check its release notes and flush or wait out the TTL |
| `Prefix` | `v2:orders-api:prod:Order:1` | The layout 3.0.0–3.1.1 wrote; set it to keep reading their entries |
| `Suffix` | `orders-api:prod:Order:1:v2` | Keeps upgrade isolation and still lets `orders-api:*` find every key |

**Configure.** `Redis.KeyVersionPlacement`, or `WithKeyVersionPlacement(placement)`.

**Trade-offs.** Changing it changes **every** physical key: the deploy is a cold cache, and while old
and new replicas run side by side they cannot invalidate each other's entries. Use a recreate
deployment, or accept that window. See [OPERATIONS](OPERATIONS.md) and
[ARCHITECTURE §4](ARCHITECTURE.md#4-key-layout).

---

## Keys and isolation

### 25. Key prefixes and named caches

> **Default:** `ApplicationPrefix` required; one unnamed default cache · **Modes:** all

**What it does.** Namespaces every physical key, and lets one application run several independently
configured caches.

**How it works.**

- Physical key: `{ApplicationPrefix}[:{EnvironmentPrefix}][:{TenantPrefix}][:{CacheName}]:{key}`. The
  cache-name segment is added only for named caches, so default-cache keys stay short.
  `Redis.InstancePrefix` adds an outer prefix, for interoperating with an existing key layout.
- Register a named cache with `AddCaching("name", …)` or under `CacheOptions:NamedCaches`; resolve it
  with `[FromKeyedServices("name")]` or `ICacheProvider.GetCache("name")`.
- **Each named cache has its own full option set** — the way to give different kinds of data
  different defaults (fail-safe off for permissions, long TTLs for reference data) without per-call
  overrides.

**Advantages.** Applications, environments and caches sharing one Redis cannot read or invalidate
each other's keys; `ClearAsync` stays scoped to its own prefix.

**Trade-offs.** Changing any prefix moves the cache to a new key space — a cold cache. Each named cache
adds a value to the `cache.name` metric dimension; turn off `Observability.IncludeCacheNameDimension`
when you register many.

| Use it for | Avoid |
|---|---|
| A distinct `ApplicationPrefix` per application and `EnvironmentPrefix` per environment on shared Redis; one named cache per class of data | `TenantPrefix` in a multi-tenant process — put the tenant in the key instead |

### 26. Key and tag guards

> **Default:** keys up to 512 characters (prefix included), violations throw · **Modes:** all

**What it does.** Enforces key and tag limits on every call, and keeps raw keys and tags out of
telemetry.

**How it works.**

- Key length and tag limits are checked inside the cache adapter on every call, whether or not the
  call supplies overrides. The policy is `Ignore`, `Warn` (log and count) or `Throw`.
- `CacheKey` / `CacheKeyBuilder` reject `':'`, whitespace and control characters in caller-supplied
  segments, so a hostile id cannot forge an extra key segment.
- `ICacheGuard` validates keys and tags at a boundary, ahead of other work in the same request.
- Logs and spans carry a key **fingerprint**, not the key, unless `Security.AllowRawKeysInLogs` /
  `Security.AllowRawKeysInTelemetry` is set.

**Configure.** `Security.MaximumKeyLength` (512), `Security.KeyLengthPolicy` (`Throw`), tag limits as
in [§17](#17-tags); `WithSecurity`, `WithMaximumKeyLength`. Startup rule: the maximum must leave room
for a key after the cache's own prefix.

| Use it for | Avoid |
|---|---|
| `Throw` in every environment; `CacheKey.For<T>(id)` for every key built from input | `Warn` except while migrating legacy keys; raw keys in telemetry without treating it as a data-flow change ([SECURITY §9](SECURITY.md#9-raw-keys-in-telemetry-allowrawkeysintelemetry)) |

---

## Switches

### 27. Cached nulls and `CacheValue<T>`

> **Default:** a `null` from a factory is cached like any other value · **Modes:** all

**What it does.** Caches "not found" so repeated lookups of a missing record do not reach the
database, and lets readers tell a cached `null` from a miss.

**How it works.** A `GetOrSet` factory that returns `null` caches `null`. `TryGetAsync` returns
`CacheValue<T>`: `HasValue` is `true` for a cached `null` and `false` for a miss; `GetOrDefaultAsync`
cannot tell them apart. `failSafeDefaultValue` is a `CacheValue<T>` for the same reason.

**Trade-offs.** A cached `null` hides a record created later until the entry expires or is removed.

| Use it for | Avoid |
|---|---|
| Lookups by id that often miss (404-heavy endpoints) | Data whose creation is not paired with `RemoveAsync` of the key — or give `null` results a short lifetime via `ctx.Overrides` |

### 28. Disabling the cache

> **Default:** enabled · **Modes:** all

**What it does.** `Enabled: false` (or `Disable()`) registers a null cache: reads always miss, writes
are discarded, and factories run on every call. No memory cache, Redis connection or backplane is
created, and startup validation is skipped.

**Trade-offs.** Read once at registration and **not** hot-reloadable — a restart is needed.

| Use it for | Avoid |
|---|---|
| A kill switch while triaging an incident; local runs without Redis | Treating it as a runtime toggle |

### 29. Presets

`UseDevelopmentDefaults()` and `UseProductionDefaults()` set only the fields below; builder calls made
**after** a preset override it.

| Setting | `UseDevelopmentDefaults()` | `UseProductionDefaults()` |
|---|---|---|
| `DefaultExpiration` | 1 min | unchanged |
| `Resilience.FailSafeEnabled` | `false` | `true` |
| `Resilience.FailSafeMaxDuration` | unchanged | raised to 2 h — or `DefaultExpiration` + 2 h when that is 2 h or more; never lowered |
| `Resilience.FactorySoftTimeout` / `FactoryHardTimeout` | unchanged | 1 s / 10 s |
| `Resilience.DistributedSoftTimeout` / `DistributedHardTimeout` | unchanged | 500 ms / 2 s |
| `Resilience.ThrowOnDistributedCacheErrors` | `true` | `false` |
| `Resilience.ThrowOnSerializationErrors` | `true` | `false` |
| `Entry.EagerRefreshThreshold` | unchanged | `0.8` |
| `Observability.LogStartupSummary` | `true` | unchanged |

The development preset makes cache bugs visible instead of hiding them behind stale data; the
production preset favours availability — stale-but-fast over slow-or-failing. Apply one
conditionally: `if (builder.Environment.IsDevelopment()) cache.UseDevelopmentDefaults();`.

**Set `DefaultExpiration` before `UseProductionDefaults()`.** The preset sizes the fail-safe window
from the expiration it sees; a longer `WithDefaultExpiration(…)` *after* it leaves
`FailSafeMaxDuration` below the expiration, and startup validation fails ([§8](#8-fail-safe)) — raise
it with `WithFailSafe(maxDuration: …)`, or reorder.

---

## Putting it together

### 30. Recipes

#### Read-heavy reference data from a flaky upstream

Hybrid, a long fallback window, refreshes off the request path, and a fast stale answer when the
upstream is slow.

```csharp
builder.Services.AddCaching("reference-data", cache => cache
    .UseHybrid(redisConnectionString)
    .WithApplicationPrefix("orders-api")
    .WithDefaultExpiration(TimeSpan.FromMinutes(30))
    .WithLocalExpiration(TimeSpan.FromMinutes(5))
    .WithEagerRefresh(0.8f)
    .WithFailSafe(maxDuration: TimeSpan.FromHours(6))
    .WithFactoryTimeouts(softTimeout: TimeSpan.FromSeconds(1), hardTimeout: TimeSpan.FromSeconds(10))
    .WithProportionalJitter(0.1, TimeSpan.FromMinutes(1)));
```

#### Correctness-critical data (permissions)

Fail-safe off so a revoked permission is never served as a fallback, a short local lifetime, and hard
invalidation on change.

```csharp
builder.Services.AddCaching("authz", cache => cache
    .UseHybrid(redisConnectionString)
    .WithApplicationPrefix("orders-api")
    .WithDefaultExpiration(TimeSpan.FromMinutes(2))
    .WithLocalExpiration(TimeSpan.FromSeconds(30))
    .WithFailSafe(enabled: false));

// On revoke: RemoveAsync, never ExpireAsync.
await authz.RemoveAsync(CacheKey.For<UserPermissions>(userId).Build(), token: ct);
```

#### A hot key with an expensive factory

```csharp
var summary = await cache.GetOrSetAsync(
    "dashboard:summary",
    async ct => await reports.BuildSummaryAsync(ct),
    options: new CacheEntryOverrides
    {
        EagerRefreshThreshold = 0.75f,                        // refresh before expiry
        FactorySoftTimeout = TimeSpan.FromMilliseconds(500)   // stale answer if slow (fail-safe on)
    },
    token: ct);
```

#### Bulk warm-up at startup

Skip one backplane message per entry, and spread expiry widely with a flat per-call jitter window.

```csharp
await cache.SetManyAsync(
    products.ToDictionary(p => $"product:{p.Sku}", p => p),
    options: new CacheEntryOverrides
    {
        SkipBackplaneNotification = true,
        JitterMaxDuration = TimeSpan.FromMinutes(2)
    },
    tags: ["catalog"],
    token: ct);
```

#### The caller must read its own write

```csharp
await cache.SetAsync(
    $"order:{order.Id}",
    order,
    new CacheEntryOverrides { AllowBackgroundDistributedOperations = false },   // await the Redis write
    token: ct);

await bus.PublishAsync(new OrderUpdated(order.Id), ct);   // consumers may now read the key
```

### 31. Gotchas checklist

- [ ] `ClearAsync()` defaults to `allowFailSafe: true` — cleared data can come back as a fallback. Pass
      `allowFailSafe: false` for a hard clear.
- [ ] `ExpireAsync` is not `RemoveAsync`: it keeps an in-memory fallback. Revocations use `RemoveAsync`.
- [ ] Fail-safe keeps every entry in Redis for at least `FailSafeMaxDuration` (2 h), however short its
      own TTL — and an entry that lives longer than that gets no fallback window at all.
- [ ] With fail-safe on, `FailSafeMaxDuration` must be at least `DefaultExpiration`, or startup fails —
      so a `DefaultExpiration` over 2 h needs a larger `FailSafeMaxDuration`, and goes **before**
      `UseProductionDefaults()`.
- [ ] A factory soft timeout does nothing without fail-safe and a stale value.
- [ ] `TryGet` and `GetOrDefault` never return stale values; fail-safe is a `GetOrSet` feature.
- [ ] Per-call overrides and `ctx.Overrides` are not validated — including Hybrid's "L1 must not
      outlive L2". On `RemoveByTag` and `Clear`, only the operational ones apply.
- [ ] Hybrid set on `CachingOptions` directly in code gets no backplane default — use
      `UseHybrid(...)` or set `Backplane.Enabled`.
- [ ] `SetAsync` returns before Redis has the value. Turn background operations off per call to read
      your own write — and with them off, oversized or unserializable values throw to the caller.
- [ ] A factory can run more than once per key — once per pod, twice in Redis mode. No side effects in
      factories.
- [ ] Auto-cloned types must be serializable in the cache's serialization format.
- [ ] `MemorySizeLimit` counts units, not bytes; with a limit set, unsized entries are not cached.
- [ ] Jitter's 2 s cap is negligible on hour-long TTLs. Raise it for bulk-loaded caches.
- [ ] Changing `KeyVersionPlacement`, any key prefix, or the serializer format is a cold-cache deploy.
