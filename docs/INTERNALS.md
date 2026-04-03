# Caching.NET internals

This document describes how Caching.NET works internally — architecture, implementation details, and extension points. For usage and configuration, see the [README](../README.md).

## Versioning and compatibility

- **Versioning:** Caching.NET follows [Semantic Versioning](https://semver.org/).
  - **MAJOR** (`X.0.0`): Breaking API or behavior changes (for example, changes to `ICacheService` signature or semantics).
  - **MINOR** (`1.X.0`): Backwards-compatible feature work (new configuration options, new overloads/extension methods, non-breaking defaults).
  - **PATCH** (`1.0.X`): Bug fixes and internal improvements only.
- **API stability contract:** `ICacheService` is the **stable contract** for consumers. New capabilities are added through:
  - Extension methods (for example, `CacheServiceCallExtensions`).
  - Per-call options (`CacheCallOptions`).
  - Configuration (`CacheOptions`).
  - Builder methods (`CachingBuilder`).

  rather than adding members to `ICacheService`. This minimizes breaking changes and means consumers don't need to update until they want new features.
- **Runtime support:** The library targets **.NET 10** (`net10.0`) for the core package, tests, and sample app. Other runtimes can be supported in the future via multi-targeting; current packages are focused on .NET 10+ environments.

## Core abstraction: `ICacheService`

The entire library is built around a single abstraction: `Caching.NET.Abstractions.ICacheService`.

Key methods:

- `GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default)`
  - Fetches a value by `key`, or runs `factory` to create the value when it is not present.
  - `expiration`: overall/absolute lifetime of the entry.
  - `localExpiration`: only meaningful for **Hybrid** (controls in-memory tier lifetime). Ignored by **InMemory** and **Redis** modes.
- `SetAsync<T>(...)`
  - Stores a value with the same `expiration` / `localExpiration` semantics as `GetOrCreateAsync`.
- `RemoveAsync(string key)` / `RemoveAsync(IEnumerable<string> keys)`
  - Removes by key; `keys` overload loops individual removals and ignores null/empty keys.
- `RemoveByTagAsync(string tag)` / `RemoveByTagAsync(IEnumerable<string> tags)`
  - Tag-based removal. **Only Hybrid supports tags** (via `HybridCache.RemoveByTagAsync`). In **InMemory** and **Redis** modes these calls are **no-op** (ignored); the library logs at debug level so you can detect misuse. If your application relies on tag-based invalidation, you must use **Hybrid** mode.

Consumers always depend on `ICacheService`; concrete implementations are chosen by configuration and DI.

### Per-call options (`CacheCallOptions`)

Per-call options allow callers to override behavior on individual cache operations via `CacheServiceCallExtensions`:

- **`OverrideMode`**: Use a different cache mode for this call (e.g. Hybrid -> InMemory for a single key).
- **`BypassCache`**: When true, the cache is not read or written; the factory is always executed and the result returned without caching. Use for debugging or emergency "cache off" at a callsite.
- **`ForceRefresh`**: When true, the factory is always executed; the result is then written to the cache and returned. Use to refresh stale data without removing the key first.
- **`CoalesceConcurrent`**: When true, concurrent `GetOrCreateAsync` calls for the same key on the same process are coalesced (using a per-key asynchronous lock) so that one caller runs the factory while others await its result. This is especially useful for **InMemory** and **Redis** modes to reduce stampede-like behavior without enabling Hybrid.

These options are passed through extension method overloads that cast `ICacheService` to the internal `IRoutingCacheService` interface.

## Implementations by mode

### Disabled (RoutingCacheService short-circuit)

When `CacheOptions.Enabled` is `false`, `RoutingCacheService` short-circuits all operations:

- `GetOrCreateAsync`: validates that `key` is not null or whitespace, then always executes `factory` and returns the result (no caching performed).
- `SetAsync` / `Remove*` / `RemoveByTag*`: all are no-op and return completed tasks.

There is no separate `NoOpCacheService` class — `RoutingCacheService` reads `Enabled` from `IOptionsMonitor<CacheOptions>.CurrentValue` on every call, so flipping `Enabled` in configuration takes effect immediately without restart.

Use when: Turn caching off for local development, troubleshooting, or specific environments **without changing application code**.

### InMemory: `InMemoryCacheService`

- Backed by `Microsoft.Extensions.Caching.Memory.IMemoryCache`.
- Cache tier:
  - Single in-process memory cache.
  - No distributed or cross-process sharing.
- `GetOrCreateAsync`:
  - If the key exists, returns the cached value.
  - Otherwise runs `factory`, writes the result to `IMemoryCache`, and returns it.
  - Expiration is chosen as: `expiration ?? CacheOptions.DefaultExpiration ?? 10 minutes`.
  - `localExpiration` is accepted but ignored.
- `SetAsync`:
  - Same expiration semantics as `GetOrCreateAsync`.
- `RemoveAsync` / `RemoveAsync(IEnumerable<string> keys)`:
  - Removes by key, ignoring null/whitespace keys.
- `RemoveByTag*`:
  - No-op; logs at debug when called so you can confirm tag support is not available in this mode.

Use when: You only need **per-process** caching and do not require Redis.

### Redis: `RedisCacheService`

- Backed by `Microsoft.Extensions.Caching.Distributed.IDistributedCache` (typically Redis via `AddStackExchangeRedisCache`).
- Serialization:
  - Values are serialized to JSON using `System.Text.Json.JsonSerializer`. You can supply custom `JsonSerializerOptions` by registering `CacheSerializerOptions` in DI; otherwise a default (case-insensitive) is used.
- Resilience:
  - When `CacheOptions.FailOpen` is true (default), Redis get/set/remove failures are caught, logged (warning/error), and the operation either falls back to the factory (get) or is skipped (set/remove). Set `FailOpen=false` and optionally `ThrowOnFailure=true` to propagate exceptions.
- Key and payload limits:
  - If `CacheOptions.MaximumKeyLength` is set and the key exceeds it, the operation skips the cache (get: run factory; set: no-op) and a warning is logged. If `MaximumPayloadBytes` is set and the serialized payload exceeds it, set is skipped and a warning is logged. **Recommended:** set these limits in production.
- `GetOrCreateAsync`:
  - If key exceeds `MaximumKeyLength`, runs factory and returns without caching.
  - Otherwise attempts to read bytes from Redis; on success and valid deserialization, returns cached value.
  - On Redis or deserialization failure, if FailOpen: logs and runs factory; otherwise throws.
  - Then calls `SetAsync` to store the result; SetAsync failures are logged and not thrown when FailOpen.
- `SetAsync`:
  - If key exceeds `MaximumKeyLength`, returns without writing.
  - Serializes value; if serialization fails, logs and returns (or throws if ThrowOnFailure).
  - If payload exceeds `MaximumPayloadBytes`, logs warning and does not cache.
  - Otherwise uses `expiration ?? CacheOptions.DefaultExpiration ?? 10 minutes` as `AbsoluteExpirationRelativeToNow`.
- `RemoveAsync` / `RemoveAsync(IEnumerable<string> keys)`:
  - Removes keys in Redis; ignores null/whitespace keys. On failure, logs and does not throw when FailOpen.
- `RemoveByTag*`:
  - No-op; logs at debug that tag APIs are not supported in Redis mode.

Use when: You need **distributed** caching shared across multiple application instances.

### Hybrid: `HybridCacheService`

- Wraps `Microsoft.Extensions.Caching.Hybrid.HybridCache`.
- Cache tiering:
  - In-memory tier (fast local cache).
  - Optional Redis tier when `RedisConnectionString` is provided.
  - Provides **stampede protection** (coalesces concurrent requests for the same key) so many concurrent misses for the same key share a single factory execution.
- Expiration behavior:
  - `expiration`: overall/distributed expiration for the entry. Defaults to `CacheOptions.DefaultExpiration` or 10 minutes.
  - `localExpiration`: in-memory tier expiration. If not provided, falls back to `expiration` or a default of 5 minutes.
- `GetOrCreateAsync`:
  - When caching is **disabled** or `HybridCache` is `null`: logs a debug message and executes `factory` directly.
  - When caching is enabled: builds `HybridCacheEntryOptions` from `expiration` / `localExpiration` and delegates to `HybridCache.GetOrCreateAsync`.
  - On exception, logs an error and falls back to executing `factory` directly.
- `SetAsync`:
  - When caching is disabled or `HybridCache` is `null`, returns immediately (no-op).
  - Otherwise uses `HybridCache.SetAsync` with the same entry options.
- `RemoveAsync` / `RemoveAsync(IEnumerable<string> keys)`:
  - No-op when caching is disabled or `HybridCache` is `null`.
  - Otherwise delegates to `HybridCache.RemoveAsync`.
- `RemoveByTag*`:
  - No-op when caching is disabled or `HybridCache` is `null`.
  - Otherwise calls `HybridCache.RemoveByTagAsync`.

Use when: You want the **best of both worlds** — fast in-memory reads, optional distributed Redis tier, and stampede protection built in.

## DI registration and service resolution

Consumer applications call one of four `AddCaching` overloads:

- `AddCaching()` — zero-config: Hybrid (in-memory only, no Redis), enabled, 10-minute default expiration.
- `AddCaching(IConfiguration)` — reads `CacheOptions` from config section.
- `AddCaching(Action<CachingBuilder>)` — fluent code-first configuration.
- `AddCaching(IConfiguration, Action<CachingBuilder>)` — config base + fluent overrides (fluent wins on conflict).

All overloads delegate to a shared `AddCachingCore` that:

1. Binds config (if provided), then applies fluent overrides via `PostConfigure`.
2. Registers cache infrastructure based on the resolved `Mode`.
3. **Always** registers `RoutingCacheService` as the `ICacheService` singleton.

**`RoutingCacheService`** is the central dispatcher registered as `ICacheService`. It resolves to the correct concrete service (`InMemoryCacheService`, `RedisCacheService`, or `HybridCacheService`) based on the configured mode. It also handles per-call overrides via `CacheCallOptions` through the internal `IRoutingCacheService` interface.

**Startup validation:** After building the service provider, call `serviceProvider.ValidateCacheRegistration()` to ensure `ICacheService` resolves and the configured mode's backing services are registered. This fails fast on DI misconfiguration; it does **not** probe Redis or the cache backend. See [OPERATIONS.md](OPERATIONS.md).

Applications only inject `ICacheService` in their own services and do not refer to concrete implementations directly.

## Configuration deep dive

This section covers how configuration values are interpreted and how they interact. For how to set values, see the [README configuration section](../README.md#configuration).

### Core properties

- **`Enabled`** (`bool`, default `false`): When `false`, `RoutingCacheService` short-circuits all operations and configuration validation for other properties is skipped. When `true`, `CacheOptions` is validated on startup; data annotations and custom validation rules must all pass, otherwise an `OptionsValidationException` is thrown.
- **`Mode`** (`CacheMode`): `InMemory`, `Redis`, or `Hybrid`. Read at startup only (restart required to change).

### Expiration

- **`DefaultExpiration`** (`string?`, TimeSpan format): Default absolute expiration when an explicit `expiration` is not passed. Parsed via `CacheOptions.GetDefaultExpiration()`. When not set, concrete implementations fall back to an internal default (currently 10 minutes) so entries are never unbounded.
- **`DefaultLocalExpiration`** (`string?`, TimeSpan format): Default in-memory tier expiration for Hybrid when `localExpiration` is omitted. Ignored by InMemory and Redis modes. Parsed via `CacheOptions.GetDefaultLocalExpiration()`. When not set, Hybrid falls back to the overall expiration or an internal default (currently 5 minutes).
- **Tuning guidance:** For high-churn or volatile data, use shorter TTLs (1-5 minutes). For stable reference data, longer TTLs (30-60 minutes) are fine.

### Redis connection

- **`RedisConnectionString`** (`string?`): Required for `Redis` mode; optional for `Hybrid` (Hybrid will still function with in-memory-only if omitted). Read at startup only.
- **`RedisInstanceName`** (`string?`): Optional prefix for Redis keys. For multi-tenant or multi-service clusters, use a unique prefix per service (e.g. `myservice:`).

### Limits

- **`MaximumPayloadBytes`**, **`MaximumKeyLength`**: When `null`, no additional limits beyond what the underlying caches enforce. **Hybrid:** passed through to `HybridCache`. **Redis:** keys/payloads exceeding limits are not cached; a warning is logged and the request falls back to the factory. **Recommendation:** in production, set conservative limits.
- **`MemorySizeLimitMb`** (`int?`): When set, `AddMemoryCache` is called with `SizeLimit = MemorySizeLimitMb * 1024 * 1024`. Without per-entry sizes, eviction may be count-based. Use in production to avoid unbounded memory growth.

### Resilience

- **`FailOpen`** (`bool`, default `true`): When true, cache failures cause get to fall back to the factory and set/remove to be skipped (logged). When false, exceptions are propagated.
- **`ThrowOnFailure`** (`bool`, default `false`): When true and FailOpen is false, cache layer exceptions are thrown. Use for strict failure visibility.
- **Interaction:** When `FailOpen` is true and `ThrowOnFailure` is true, `FailOpen` wins.
- **`FactoryTimeout`** (`string?`, TimeSpan format): Optional timeout for the factory in `GetOrCreateAsync`. When set, the factory is cancelled after this duration (applied in `RoutingCacheService`).

### Security and TLS

- **`StrictRedisCertificateValidation`** (`bool`, default `false`): When false, Redis TLS validation allows `RemoteCertificateNameMismatch` but rejects all other SSL policy errors. When true, any SSL policy errors (including hostname mismatches) cause the connection to be rejected.
- **Environment-specific guidance:**
  - For **development/test** where Redis cannot present a hostname-matching certificate, the default (non-strict) behavior is typically sufficient.
  - For **production**, set `StrictRedisCertificateValidation` to `true` or replace the callback with a stricter implementation.
  - Always prefer encrypted Redis connections where supported.

### Serialization

Register `CacheSerializerOptions` in DI and set `JsonSerializerOptions` when you need custom JSON behavior (naming policy, converters, ignore conditions). When not registered, Redis uses a default (case-insensitive) serializer. Do not cache types that require polymorphic or complex serialization unless you configure appropriate converters.

## Telemetry and logging internals

Caching.NET deliberately does **not** take a hard dependency on any specific telemetry stack. For consumer-facing telemetry setup, see [TELEMETRY.md](TELEMETRY.md).

### Logging

All implementations use `ILogger<T>`:

- **HybridCacheService:** Debug when cache disabled/unavailable; Error on get/set/remove/tag failures (with fail-open fallback).
- **RedisCacheService:** Warning/Error on Redis or serialization failures (when FailOpen); Warning when key/payload exceeds limits; Debug when tag APIs are called (no-op).
- **InMemoryCacheService:** Debug when tag APIs are called (no-op).

Keys are truncated in log messages (e.g. to 64 characters) to avoid logging PII or very long keys.

### Log event IDs

Standardized log event IDs are defined in `Caching.NET.Internal.CacheLogEvents`:

| Event ID | Name                       | Scope    |
| -------- | -------------------------- | -------- |
| 1000     | `RedisGetFailed`           | Redis    |
| 1001     | `RedisSetFailed`           | Redis    |
| 1002     | `RedisRemoveFailed`        | Redis    |
| 1003     | `RedisSerializationFailed` | Redis    |
| 1004     | `RedisKeyTooLong`          | Redis    |
| 1005     | `RedisPayloadTooLarge`     | Redis    |
| 1100     | `HybridGetFailed`          | Hybrid   |
| 1101     | `HybridSetFailed`          | Hybrid   |
| 1102     | `HybridRemoveFailed`       | Hybrid   |
| 1103     | `HybridTagRemoveFailed`    | Hybrid   |
| 1104     | `HybridCacheDisabled`      | Hybrid   |
| 1200     | `TagNotSupported`          | Tag APIs |

### Telemetry abstraction (`ICacheTelemetry`)

- `Caching.NET.Abstractions.ICacheTelemetry` with methods: `OnCacheHit`, `OnCacheMiss`, `OnCacheSet`, `OnCacheRemove`, `OnCacheRemoveByTag`, `OnCacheError`, `OnFactoryTimeout`.
- `NoopCacheTelemetry` is registered by default so that telemetry calls are inexpensive no-ops unless you plug in a real implementation.
- `OpenTelemetryCacheTelemetry` uses `Meter` and `ActivitySource` to emit:
  - Metrics: `cache.requests`, `cache.hits`, `cache.misses`, `cache.failures`.
  - Spans for cache errors and factory timeouts.
- Applications can replace the default by registering their own `ICacheTelemetry` before or after `AddCaching`.
- Meter name: `Caching.NET.Cache`
- ActivitySource name: `Caching.NET.Cache`

For full metrics reference, trace tags, custom providers, and dashboard queries, see [TELEMETRY.md](TELEMETRY.md).

## Extension points

### Adding a new cache mode

1. Implement a new backing service implementing `ICacheService`.
2. Register it in `AddCachingCore` based on the new `CacheMode` enum value.
3. Add routing logic to `RoutingCacheService`.

### Adding new per-call options

1. Add a property to `CacheCallOptions`.
2. Handle the new option in `RoutingCacheService`.

### Adding new configuration

1. Add a property to `CacheOptions`.
2. Handle it in the relevant service(s).

### Adding new extension methods

Add to `CacheServiceCallExtensions`. These methods cast `ICacheService` to `IRoutingCacheService` internally.

### Adding new builder methods

Add to `CachingBuilder`. Each method returns the builder for chaining.

## Design decisions

### RoutingCacheService as central dispatcher

**Context:** Needed to support per-call mode overrides and runtime disabled toggle.

**Decision:** Single `RoutingCacheService` registered as `ICacheService` that delegates to concrete services.

**Rationale:** Avoids DI complexity of swapping registrations; keeps `ICacheService` stable.

### Extension methods over interface members

**Context:** Adding features like BypassCache and ForceRefresh.

**Decision:** Per-call options via `CacheCallOptions` + extension methods rather than new `ICacheService` members.

**Rationale:** Preserves API stability contract; consumers don't need to update until they want new features.

### Enabled flag on IOptionsMonitor (hot-reloadable)

**Context:** Ops need to disable caching during incidents without restart.

**Decision:** `RoutingCacheService` reads `Enabled` from `IOptionsMonitor.CurrentValue` on every call.

**Rationale:** Immediate effect; mode and connection strings remain startup-only because changing them requires re-registering services.
