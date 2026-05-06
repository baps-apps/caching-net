# Changelog

## 2.0.0 — 2026-05-06

Major release. Breaking changes from v1.x. See [docs/MIGRATION-V1-TO-V2.md](docs/MIGRATION-V1-TO-V2.md).

### Highlights

- Multi-target `net8.0`, `net9.0`, `net10.0` (single package).
- `KeyPrefix` mandatory across all modes (replaces `RedisInstanceName`).
- Striped lock manager with stable hashing — no per-key allocation, no leak.
- Polly v8 resilience pipelines (timeout + circuit breaker + retry) per backend.
- OpenTelemetry-native via static `CacheInstruments`. `ICacheTelemetry` removed.
- `PayloadEnvelope` wire format with schema-drift detection.
- `LoggerMessage` source-gen for hot-path logs.
- `Caching.NET.Analyzers` ships in the main package — compile-time `CN0001` blocks high-cardinality OTel tags.
- New API surface: `GetAsync`, `ExistsAsync`, `RefreshAsync`, `GetManyAsync`, `SetManyAsync`, `RemoveManyAsync`.
- `CacheCallOptions`: `AbsoluteExpiration`, `SlidingExpiration`, `AllowStaleFor`, `Tags`, `JitterPercentage`, `FactoryTimeout`.
- `CacheKey.For<T>(id).WithVariant(...).Build()` canonical key builder.
- `MessagePackCacheSerializer` opt-in via `WithMessagePackSerializer()`.
- Stale-while-revalidate orchestrator (in-process registry; bounded background refresh).
- TTL jitter (`WithTtlJitter(0.10)` default).
- TLS certificate audit logging + `cache.tls.validation` counter.
- Credential rotation hook (`RedisConnectionRotator` reloads multiplexer on options change).
- Server-side Redis MGET/MSET/KeyDelete pipelining (when `IConnectionMultiplexer` is registered).
- AOT/trim verified via `Caching.NET.AotSmoke` smoke project.
- Testcontainers Redis integration suite, Polly chaos suite, FsCheck property suite.
- BenchmarkDotNet perf-gate via `scripts/dev.ps1 bench:gate` (10% regression threshold).
- SPDX 2.2 SBOM emitted with the nupkg.

### Removed

- `ICacheTelemetry`, `NoopCacheTelemetry`, `OpenTelemetryCacheTelemetry`.
- `CacheOptions.RedisInstanceName`, `CachingBuilder.WithRedisInstanceName`.
- `RemoveAsync(IEnumerable<string>)` (renamed to `RemoveManyAsync`).
- All synchronous overloads (v2 is async-only).

### Defaults changed

- `Mode`: `Hybrid` → `InMemory` (zero-config friendlier).
- `StrictRedisCertificateValidation`: `false` → `true`.
- `MaximumKeyLength`: `null` → `512`.
- `TtlJitterPercentage`: `0.0` → `0.10`.
