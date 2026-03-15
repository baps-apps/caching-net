# Caching.NET implementation details

This document describes how Caching.NET works internally so that consumer applications can use it correctly and predictably.

## Versioning, compatibility, and support

- **Versioning:** Caching.NET follows [Semantic Versioning](https://semver.org/).
  - **MAJOR** (`X.0.0`): Breaking API or behavior changes (for example, changes to `ICacheService` signature or semantics).
  - **MINOR** (`1.X.0`): Backwards-compatible feature work (new configuration options, new overloads/extension methods, non-breaking defaults).
  - **PATCH** (`1.0.X`): Bug fixes and internal improvements only.
- **Stable abstraction:** `ICacheService` is the **stable contract** for consumers. New capabilities are added through:
  - Extension methods (for example, `CacheServiceCallExtensions`).
  - Per-call options (`CacheCallOptions`).
  - Configuration (`CacheOptions`).
  rather than adding members to `ICacheService`. This minimizes breaking changes.
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
  - Tag-based removal. **Only Hybrid supports tags** (via `HybridCache.RemoveByTagAsync`). In **InMemory** and **Redis** modes these calls are **no-op** (ignored); the library logs at debug level so you can detect misuse (e.g., "RemoveByTagAsync is not supported in InMemory mode; no-op for tag X. Use Hybrid mode for tag support."). If your application relies on tag-based invalidation, you must use **Hybrid** mode.

Consumers always depend on `ICacheService`; concrete implementations are chosen by configuration and DI.

### API stability (enterprise)

`ICacheService` is the stable contract. New features are added via extension methods (`CacheServiceCallExtensions`), per-call options (`CacheCallOptions`), and configuration rather than new interface members. Prefer decorators and options when evolving the library.

## Implementations by mode

### Disabled: `NoOpCacheService`

- Registered when `CacheOptions.Enabled` is `false`.
- `GetOrCreateAsync`:
  - Validates that `key` is not null or whitespace.
  - Always executes `factory` and returns the result (no caching performed).
- `SetAsync` / `Remove`* / `RemoveByTag*`:
  - All are no-op and return completed tasks.
- Use case:
  - Turn caching off for local development, troubleshooting, or specific environments **without changing application code**.

### InMemory: `InMemoryCacheService`

- Backed by `Microsoft.Extensions.Caching.Memory.IMemoryCache`.
- Cache tier:
  - Single in-process memory cache.
  - No distributed or cross-process sharing.
- `GetOrCreateAsync`:
  - If the key exists, returns the cached value.
  - Otherwise runs `factory`, writes the result to `IMemoryCache`, and returns it.
  - Expiration is chosen as:
    - `expiration ?? CacheOptions.DefaultExpiration ?? 10 minutes`.
  - `localExpiration` is accepted but ignored.
- `SetAsync`:
  - Same expiration semantics as `GetOrCreateAsync`.
- `RemoveAsync` / `RemoveAsync(IEnumerable<string> keys)`:
  - Removes by key, ignoring null/whitespace keys.
- `RemoveByTag`*:
  - No-op; logs at debug when called so you can confirm tag support is not available in this mode.

Use when:

- You only need **per-process** caching and do not require Redis.

### Redis: `RedisCacheService`

- Backed by `Microsoft.Extensions.Caching.Distributed.IDistributedCache` (typically Redis via `AddStackExchangeRedisCache`).
- Serialization:
  - Values are serialized to JSON using `System.Text.Json.JsonSerializer`. You can supply custom `JsonSerializerOptions` by registering `CacheSerializerOptions` in DI (e.g. `services.Configure<CacheSerializerOptions>(o => o.JsonSerializerOptions = myOptions)`); otherwise a default (case-insensitive) is used.
- Resilience:
  - When `CacheOptions.FailOpen` is true (default), Redis get/set/remove failures are caught, logged (warning/error), and the operation either falls back to the factory (get) or is skipped (set/remove). Set `FailOpen=false` and optionally `ThrowOnFailure=true` to propagate exceptions.
- Key and payload limits:
  - If `CacheOptions.MaximumKeyLength` is set and the key exceeds it, the operation skips the cache (get: run factory; set: no-op) and a warning is logged. If `MaximumPayloadBytes` is set and the serialized payload exceeds it, set is skipped and a warning is logged. **Recommended:** set these limits in production and treat "skip cache" as acceptable so requests still succeed.
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
- `RemoveByTag`*:
  - No-op; logs at debug that tag APIs are not supported in Redis mode.

Use when:

- You need **distributed** caching shared across multiple application instances.

### Hybrid: `HybridCacheService`

- Wraps `Microsoft.Extensions.Caching.Hybrid.HybridCache`.
- Cache tiering:
  - In-memory tier (fast local cache).
  - Optional Redis tier when `RedisConnectionString` is provided.
  - Provides **stampede protection** (coalesces concurrent requests for the same key) so many concurrent misses for the same key share a single factory execution.
- Expiration behavior:
  - `expiration`:
    - Overall/distributed expiration for the entry.
    - Defaults to `CacheOptions.DefaultExpiration` or 10 minutes.
  - `localExpiration`:
    - In-memory tier expiration.
    - If not provided, falls back to `expiration` or a default of 5 minutes.
- `GetOrCreateAsync`:
  - When caching is **disabled** or `HybridCache` is `null`:
    - Logs a debug message and executes `factory` directly (no caching).
  - When caching is enabled:
    - Builds `HybridCacheEntryOptions` from `expiration` / `localExpiration`.
    - Delegates to `HybridCache.GetOrCreateAsync` using a wrapper around `factory`.
    - On exception, logs an error and falls back to executing `factory` directly.
- `SetAsync`:
  - When caching is disabled or `HybridCache` is `null`, returns immediately (no-op).
  - Otherwise uses `HybridCache.SetAsync` with the same entry options.
- `RemoveAsync` / `RemoveAsync(IEnumerable<string> keys)`:
  - No-op when caching is disabled or `HybridCache` is `null`.
  - Otherwise delegates to `HybridCache.RemoveAsync`.
- `RemoveByTag`*:
  - No-op when caching is disabled or `HybridCache` is `null`.
  - Otherwise calls `HybridCache.RemoveByTagAsync`.

Use when:

- You want the **best of both worlds**:
  - Fast in-memory reads.
  - Optional distributed Redis tier.
  - Stampede protection built in.

### Configuration: `CacheOptions`

The `CacheOptions` section is bound from configuration using the key constant `Caching.NET.Configuration.CacheConfigurationKeys.CacheOptions`.

Key properties:

- `Enabled` (`bool`, default `false`):
  - When `false`, `NoOpCacheService` is registered and all caching is effectively disabled, and configuration validation for other `CacheOptions` properties is skipped so that invalid values do not cause application startup to fail.
  - When `true`, `CacheOptions` is validated on startup; data annotations and custom validation rules must all pass, otherwise an `OptionsValidationException` is thrown when options are realized.
- `Mode` (`CacheMode`):
  - `InMemory`, `Redis`, or `Hybrid`.
// Expiration defaults
- `DefaultExpiration` (`string?`, TimeSpan format):
  - Default absolute expiration when an explicit `expiration` is not passed into `GetOrCreateAsync` / `SetAsync`.
  - Parsed via `CacheOptions.GetDefaultExpiration()`. When this option is not set, concrete implementations
    fall back to an internal default (currently 10 minutes) so entries are never unbounded by default.
- `DefaultLocalExpiration` (`string?`, TimeSpan format):
  - Default in-memory tier expiration for Hybrid when `localExpiration` is omitted.
  - Ignored by InMemory and Redis modes.
  - Parsed via `CacheOptions.GetDefaultLocalExpiration()`. When not set, Hybrid falls back to the overall
    expiration or an internal default (currently 5 minutes) for the in-memory tier.
- `RedisConnectionString` (`string?`):
  - Required for `Redis` mode; optional for `Hybrid` (Hybrid will still function with in-memory-only if omitted).
- `RedisInstanceName` (`string?`):
  - Optional prefix used for Redis keys. For multi-tenant or multi-service clusters, use a unique prefix per service (e.g. `myservice:`).
- `MaximumPayloadBytes`, `MaximumKeyLength`:
  - When these options are **null**, Caching.NET does not apply additional limits beyond what the underlying caches enforce.
    This is convenient for development but not recommended for large enterprise workloads.
  - **Hybrid:** Passed through to `HybridCache`; entries or keys exceeding limits may be skipped (see HybridCache behavior).
  - **Redis:** When set, keys/payloads exceeding limits are not cached; a warning is logged and the request falls back to the factory.
  - **Recommendation:** In production, set conservative limits (for example, payload size in the low megabytes and key length
    in the low hundreds to ~1k characters) to avoid pathological memory and network usage.
- `FailOpen` (`bool`, default true):
  - When true, cache failures (e.g. Redis unavailable) cause get to fall back to the factory and set/remove to be skipped (logged). When false, exceptions are propagated.
- `ThrowOnFailure` (`bool`, default false):
  - When true and FailOpen is false, cache layer exceptions are thrown. Use for strict failure visibility.
- `FactoryTimeout` (`string?`, TimeSpan format):
  - Optional timeout for the factory in `GetOrCreateAsync`. When set, the factory is cancelled after this duration (applied in `RoutingCacheService`).
- `MemorySizeLimitMb` (`int?`):
  - When set, the in-memory cache is configured with a size limit (see Memory and Hybrid cache sizing below).
  - `StrictRedisCertificateValidation` (`bool`, default false):
  - When false (default), Redis TLS validation allows `RemoteCertificateNameMismatch` but rejects all other SSL policy errors. This matches many existing non-production and some production setups where Redis certificates do not strictly match hostnames.
  - When true, any SSL policy errors (including hostname mismatches) cause the connection to be rejected. Use this when you require a strict TLS posture for Redis.

### Dependency injection and registration

Consumer applications call:

```csharp
services.AddCaching(configuration);
```

The extension method:

- Binds and validates `CacheOptions`.
- When `Enabled == false`, registers `NoOpCacheService` as `ICacheService`.
- When `Enabled == true`, registers mode-specific concrete services (InMemory/Redis/Hybrid as applicable) and exposes a single `ICacheService` as `RoutingCacheService` which:
  - Uses `CacheOptions.Mode` by default.
  - Can route individual calls to a different mode or apply per-call flags when using `CacheServiceCallExtensions` with `CacheCallOptions`.

**Startup validation:** After building the service provider (e.g. in host startup), call `serviceProvider.ValidateCacheRegistration()` to ensure `ICacheService` resolves and the configured mode's backing services are registered. This fails fast on DI misconfiguration; it does not probe Redis or the cache backend. See [OPERATIONS.md](OPERATIONS.md).

**Per-call options (`CacheCallOptions`):**

- `OverrideMode`: Use a different cache mode for this call (e.g. Hybrid → InMemory for a single key).
- `BypassCache`: When true, the cache is not read or written; the factory is always executed and the result returned without caching. Use for debugging or emergency "cache off" at a callsite.
- `ForceRefresh`: When true, the factory is always executed; the result is then written to the cache and returned. Use to refresh stale data without removing the key first.
- `CoalesceConcurrent`: When true, concurrent `GetOrCreateAsync` calls for the same key on the same process are coalesced (using a per-key asynchronous lock) so that one caller runs the factory while others await its result. This is especially useful for **InMemory** and **Redis** modes to reduce stampede-like behavior without enabling Hybrid.

Applications only inject `ICacheService` in their own services and do not refer to concrete implementations directly.

### Telemetry and observability

Caching.NET deliberately does **not** take a hard dependency on any specific telemetry stack. Instead:

- **Logging**:
  - All implementations use `ILogger<T>`:
    - **HybridCacheService:** Debug when cache disabled/unavailable; Error on get/set/remove/tag failures (with fail-open fallback).
    - **RedisCacheService:** Warning/Error on Redis or serialization failures (when FailOpen); Warning when key/payload exceeds limits; Debug when tag APIs are called (no-op).
    - **InMemoryCacheService:** Debug when tag APIs are called (no-op).
  - Keys are truncated in log messages (e.g. to 64 characters) to avoid logging PII or very long keys.
  - Standardized log event IDs are defined in `Caching.NET.Internal.CacheLogEvents`:
    - Redis: `RedisGetFailed` (1000), `RedisSetFailed` (1001), `RedisRemoveFailed` (1002), `RedisSerializationFailed` (1003), `RedisKeyTooLong` (1004), `RedisPayloadTooLarge` (1005).
    - Hybrid: `HybridGetFailed` (1100), `HybridSetFailed` (1101), `HybridRemoveFailed` (1102), `HybridTagRemoveFailed` (1103), `HybridCacheDisabled` (1104).
    - Tag APIs not supported: `TagNotSupported` (1200).
- **Telemetry abstraction (`ICacheTelemetry`)**:
  - The core package defines `Caching.NET.Abstractions.ICacheTelemetry` with methods such as:
    - `OnCacheHit`, `OnCacheMiss`, `OnCacheSet`, `OnCacheRemove`, `OnCacheRemoveByTag`.
    - `OnCacheError` (for failures such as Redis errors or serialization issues).
    - `OnFactoryTimeout` (when a configured factory timeout is exceeded).
  - A default implementation `NoopCacheTelemetry` is registered by `AddCaching` so that telemetry calls are inexpensive no-ops unless you plug in a real implementation.
  - An optional `OpenTelemetryCacheTelemetry` implementation in `Caching.NET.Telemetry` uses `Meter` and `ActivitySource` to emit:
    - Metrics: `cache.requests`, `cache.hits`, `cache.misses`, `cache.failures`.
    - Spans for cache errors and factory timeouts.
  - Applications can replace the default by registering their own `ICacheTelemetry` before or after `AddCaching` (DI will resolve the last registration).

**Integrating with OpenTelemetry.NET**

- Configure your OpenTelemetry `MeterProvider` and `TracerProvider` to listen to:
  - Meter: `"Caching.NET.Cache"`.
  - ActivitySource: `"Caching.NET.Cache"`.
- Register `OpenTelemetryCacheTelemetry` instead of the default:

```csharp
using Caching.NET.Abstractions;
using Caching.NET.Telemetry;

builder.Services.AddSingleton<ICacheTelemetry, OpenTelemetryCacheTelemetry>();
builder.Services.AddCaching(builder.Configuration);
```

With this in place:

- `InMemoryCacheService`, `RedisCacheService`, and `HybridCacheService` record cache hits, misses, sets, and removes with the `cache.mode` and `cache.operation` tags.
- `RedisCacheService` and `HybridCacheService` call `OnCacheError` when failures occur; `RoutingCacheService` calls `OnFactoryTimeout` when factory timeouts are configured and exceeded.

**Suggested metric/tag conventions**

- Metric names:
  - `cache.requests` – total cache operations.
  - `cache.hits` / `cache.misses` – hit/miss counters for `GetOrCreateAsync`.
  - `cache.failures` – number of cache-level failures (for example, Redis exceptions, serialization failures).
- Recommended metric tags:
  - `cache.mode` – `InMemory`, `Redis`, `Hybrid`.
  - `cache.operation` – `get_or_create`, `set`, `remove`, `remove_by_tag`.
  - `cache.key_prefix` – optional, for multi-tenant or per-service prefixes.
  - `exception.type` – for failure metrics.

## Default expirations and eviction strategy

- **DefaultExpiration** (and **DefaultLocalExpiration** for Hybrid) default to 10 and 5 minutes when not specified. For high-churn or volatile data, use shorter TTLs (e.g. 1–5 minutes) to reduce staleness and memory use. For stable reference data, longer TTLs (e.g. 30–60 minutes) are fine. Tune per service and key pattern.
- **Eviction:** In-memory cache eviction is implementation-dependent. When `MemorySizeLimitMb` is set, the library configures `IMemoryCache` with a size limit; entries added by Caching.NET do not set a per-entry `Size`, so eviction may be count-based (each entry = 1 unit) unless the host configures entry sizes elsewhere. For precise size-based eviction, configure the memory cache in your application.

## Memory and Hybrid cache sizing

- **MemorySizeLimitMb:** When set in `CacheOptions`, `AddMemoryCache` is called with `SizeLimit = MemorySizeLimitMb * 1024 * 1024`. This caps the in-memory cache size in "units"; without per-entry sizes, behavior is implementation-dependent (often count-based). Use in production to avoid unbounded memory growth.
- **Hybrid:** `MaximumPayloadBytes` and `MaximumKeyLength` are passed to `HybridCache`; entries exceeding these may be skipped by the hybrid layer. See Microsoft docs for exact behavior.

## Serialization options for Redis

- Register `**CacheSerializerOptions`** in DI and set `**JsonSerializerOptions**` when you need custom JSON behavior (naming policy, converters, ignore conditions). Example: `services.Configure<CacheSerializerOptions>(o => o.JsonSerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });`
- When not registered, Redis uses a default (case-insensitive) serializer. Do not cache types that require polymorphic or complex serialization unless you configure appropriate converters.

## Security and multi-tenant considerations

- **PII and secrets:** Do **not** cache secrets (tokens, passwords, API keys). Avoid caching PII in shared caches unless access is controlled and keys/values are not logged. The library truncates keys in log messages to reduce exposure.
- **Redis key prefixing:** Use `**RedisInstanceName`** (e.g. `myservice:`) so keys are namespaced per application or tenant. For multi-tenant shared Redis, use a prefix that includes tenant id where appropriate to avoid key collisions and to simplify purging by tenant.

### Redis TLS and certificate validation

- When using Redis over TLS, Caching.NET wires `ConfigurationOptions.CertificateValidation` to `RedisCertificateValidation.ValidateServerCertificate`.
- The default implementation:
  - When `StrictRedisCertificateValidation` is **false** (default), allows `RemoteCertificateNameMismatch` but rejects all other SSL policy errors, logging details in both cases.
  - When `StrictRedisCertificateValidation` is **true**, rejects any SSL policy errors (including hostname mismatches) and logs the error details.
- **Environment-specific guidance:**
  - For **development/test** scenarios where your Redis provider cannot present a hostname-matching certificate, the default (non-strict) behavior is typically sufficient. If you need custom behavior, override the callback in your application.
  - For **production systems**, set `StrictRedisCertificateValidation` to `true` for a strict TLS posture or replace the callback with an even stricter implementation that enforces your organization’s TLS policy (for example, custom chain validation or certificate pinning).
  - Always prefer encrypted Redis connections where supported and ensure credentials and traffic are protected in transit.

