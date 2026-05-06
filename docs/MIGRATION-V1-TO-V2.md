# Migrating from Caching.NET v1 to v2

v2.0.0 is a major release with **no backwards-compatible shims**. This guide is a find/replace table.

## Required changes

| v1 | v2 | Notes |
|----|----|-------|
| `services.AddCaching(b => b.UseRedis(cs).WithRedisInstanceName("foo"))` | `services.AddCaching(b => b.UseRedis(cs).WithKeyPrefix("foo"))` | `KeyPrefix` is mandatory; applies uniformly across all modes |
| `services.AddSingleton<ICacheTelemetry, MyTelemetry>()` | Configure OTel `Meter("Caching.NET")` and `ActivitySource("Caching.NET")` providers | `ICacheTelemetry` is gone |
| `CacheOptions.RedisInstanceName` | `CacheOptions.KeyPrefix` | required |
| `MaximumKeyLength = null` (unlimited) | `MaximumKeyLength = 512` (default) | adjust upward if needed |
| `StrictRedisCertificateValidation = false` (default in v1) | `true` (default in v2) | flip to `false` only for dev |
| `cache.RemoveAsync(IEnumerable<string>)` | `cache.RemoveManyAsync(IEnumerable<string>)` | rename only |
| `JsonCacheSerializer()` (reflection) | `new JsonCacheSerializer(MyContext.Default)` | reflection still works at runtime but emits trim/AOT warnings |
| `ICacheTelemetry` impl | Subscribe via OTel pipeline | see [TELEMETRY.md](TELEMETRY.md) |
| synchronous overloads | `GetOrCreateAsync` only | v2 is async-only |

## New surface (opt in)

- `cache.GetAsync<T>(key)` — peek without factory
- `cache.ExistsAsync(key)` — existence check
- `cache.RefreshAsync(key, factory)` — overwrite without remove
- `cache.GetManyAsync<T>(keys)` / `SetManyAsync<T>(items)` / `RemoveManyAsync(keys)`
- `CacheCallOptions.AbsoluteExpiration` / `SlidingExpiration` / `AllowStaleFor` / `JitterPercentage` / `Tags`
- `CacheKey.For<T>(id).WithVariant("v2").Build()` — canonical key builder
- `MessagePackCacheSerializer` — opt in via `WithMessagePackSerializer()`

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
- Tests that constructed a bare `new CachingBuilder()` (rather than going through `AddCaching`) will hit `InvalidOperationException` on builder methods that need the `IServiceCollection`. Use `AddCaching(s => …)` always.
