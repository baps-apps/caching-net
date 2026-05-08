# Migrating from Caching.NET v1 to v2

v2.0.0 is a major release with **no backwards-compatible shims**. This guide is a find/replace table.

## Required changes

| v1 | v2 | Notes |
|----|----|-------|
| `services.AddCaching(b => b.UseRedis(cs).WithRedisInstanceName("foo"))` | `services.AddCaching(b => b.UseRedis(cs).WithKeyPrefix("foo"))` | `KeyPrefix` is mandatory; **`':'` forbidden inside prefix** (e.g. use `asm-api-dev`, not `asm:api:dev`) |
| `services.AddSingleton<ICacheTelemetry, MyTelemetry>()` | Configure OTel `Meter("Caching.NET")` and `ActivitySource("Caching.NET")` providers | `ICacheTelemetry` is gone |
| `CacheOptions.RedisInstanceName` | `CacheOptions.KeyPrefix` | required |
| `MaximumKeyLength = null` (unlimited) | `MaximumKeyLength = 512` (default) | adjust upward if needed |
| `StrictRedisCertificateValidation = false` (default in v1) | `true` (default in v2) | flip to `false` only for dev |
| `cache.RemoveAsync(IEnumerable<string>)` | `cache.RemoveManyAsync(IEnumerable<string>)` | rename only |
| `JsonCacheSerializer()` (reflection) | `new JsonCacheSerializer(MyContext.Default)` | reflection still works at runtime but emits trim/AOT warnings |
| `ICacheTelemetry` impl | Subscribe via OTel pipeline | see [TELEMETRY.md](TELEMETRY.md) |
| synchronous overloads | `GetOrCreateAsync` only | v2 is async-only |
| Custom `ICacheSerializer`: `Deserialize<T>(ReadOnlySpan<byte>)` | `Deserialize<T>(ReadOnlyMemory<byte>)` | avoid Span-only implementations; Redis uses non-copying payload slices |
| Pre-release `ResiliencePipelineRegistryOptions` / public `CacheResiliencePipelineBuilder` | `CacheResilienceOptions` + `WithResilience(...)` only | Polly pipeline registry is internal; no supported extension point that returns `ResiliencePipelineRegistry<string>` |
| `new CachingHealthCheck(...)` / `new CachingLivenessHealthCheck(...)` from app code | `AddCachingHealthChecks` / `WithHealthChecks()` only | Health check implementations are **internal**; they register as `IHealthCheck` via DI |

## `Enabled = false` and validation

- **`CacheOptionsValidator` short-circuits** when `Enabled` is false: no `KeyPrefix`, Redis string, or range checks run. You can ship `Mode: Hybrid` with an empty or placeholder `RedisConnectionString` while the cache is off.
- **DI registration** skips all backends (memory, Redis, hybrid, serializer, Polly registry, TLS validator). `RoutingCacheService` remains the `ICacheService` implementation and short-circuits as above.
- **Health check** (`WithHealthChecks` / `AddCachingHealthChecks`) is still registered; the check returns **Healthy** with *Caching is disabled via configuration* and does not probe Redis.
- **Runtime toggle:** `IOptionsMonitor<CacheOptions>` can flip `Enabled` off without restart; flipping **on** does not lazily wire backends — **restart** to enable caching after a cold start with `Enabled: false`.

Use `CachingBuilder.Enable()` in fluent setup to force enable when JSON had `Enabled: false`.

## Redis payload envelope schema hash

The Redis wire envelope stores an xxHash64 **schema hash** derived from `typeof(T).FullName` (not assembly-qualified names). **Upgrading the `Caching.NET` package or bumping your assembly file version does not change this hash**, so existing Redis entries remain readable across those deploys.

- **One-time transition:** If you previously ran builds that hashed `AssemblyQualifiedName`, upgrading to this algorithm causes **one** schema-drift pass on existing keys (entries refresh from source). New writes use the stable hash.
- **Intentional invalidation:** Apply `[CacheSchema("v2")]` (see `Caching.NET.CacheSchemaAttribute`) when you change serialized shape and want old entries treated as drift without renaming the type.

## New surface (opt in)

- `cache.GetAsync<T>(key)` — peek without factory (Hybrid uses `HybridCache` plus a **value-type box** so misses do not cache `default(T)` for structs)
- `cache.ExistsAsync(key)` — existence check (**Hybrid** prefers `IDistributedCache.GetAsync` when registered to avoid full deserialization when bytes are present; may fall back to `GetAsync<object>`)
- `cache.RefreshAsync(key, factory)` — overwrite without remove
- `cache.GetManyAsync<T>(keys)` / `SetManyAsync<T>(items)` / `RemoveManyAsync(keys)`
- `CacheCallOptions.AbsoluteExpiration` / `SlidingExpiration` / `AllowStaleFor` / `JitterPercentage` / `Tags`
- `CacheKey.For<T>(id).WithVariant("v2").Build()` — canonical key builder
- `MessagePackCacheSerializer` — opt in via `WithMessagePackSerializer()`
- `CachingBuilder.Enable()`, `UseDevelopmentDefaults()`, `UseProductionDefaults()`, `WithKeyValidator(...)`, `WithKeyTransformer(...)`
- `ICacheKeyFactory` (optional) — same key shape as `CacheKey.For` / `CacheKeyBuilder`; register a custom implementation **before** `AddCaching` for tenant or environment segments

## Key length and prefix budget

`MaximumKeyLength` applies to the **full physical key** after routing prepends `KeyPrefix` and a `':'` separator. Startup validation (when `Enabled` is true) requires at least **32 characters** remain for the user segment after `KeyPrefix` + separator, and enforces `KeyPrefix` / Redis string rules. At runtime, keys over the limit are rejected (miss / no-op paths) with structured logs.

## Defaults changed

| Option | v1 | v2 |
|--------|----|----|
| `Mode` | `Hybrid` | `InMemory` |
| `StrictRedisCertificateValidation` | `false` | `true` |
| `MaximumKeyLength` | `null` (unlimited) | `512` |
| `TtlJitterPercentage` | `0.0` | `0.10` |

## Test impact

- Tests that asserted `RemoveAsync(IEnumerable<string>)` calls must update to `RemoveManyAsync`.
- Tests that injected an `ICacheTelemetry` mock must instead listen on `MeterListener` or `ActivitySource`.
- Tests that directly constructed `CachingBuilder` must be updated: construct through `AddCaching(s => …)` only.
