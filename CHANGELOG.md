# Changelog

All notable changes to Caching.NET are documented in this file.

The project follows [Semantic Versioning](https://semver.org/). See [docs/INTERNALS.md](docs/INTERNALS.md) for versioning policy.

## [Unreleased]

### Changed

- Renamed `docs/IMPLEMENTATION.md` to `docs/INTERNALS.md` and restructured all documentation to follow the shared package documentation template

## [1.0.0](https://github.com/baps-apps/caching-net/releases/tag/v1.0.0) - Initial release

### Added

- **ICacheService** abstraction for shared caching across .NET applications.
- **Three cache modes:**
  - **InMemory** – in-process memory cache only.
  - **Redis** – distributed Redis via `Microsoft.Extensions.Caching.StackExchangeRedis`.
  - **Hybrid** – in-memory + optional Redis with stampede protection via `Microsoft.Extensions.Caching.Hybrid`.
- **CacheOptions** configuration bound from `CacheOptions` section:
  - `Enabled` (default: `false`, opt-in) – when false, registers `NoOpCacheService`; invalid option values do not fail startup.
  - `Mode` – InMemory, Redis, or Hybrid.
  - `DefaultExpiration` / `DefaultLocalExpiration` (TimeSpan format).
  - `RedisConnectionString`, `RedisInstanceName`, `MaximumPayloadBytes`, `MaximumKeyLength`, `MemorySizeLimitMb`.
  - `FailOpen`, `ThrowOnFailure`, `FactoryTimeout`, `StrictRedisCertificateValidation`.
- **CacheCallOptions** for per-call overrides: `OverrideMode`, `BypassCache`, `ForceRefresh`, `CoalesceConcurrent`.
- **CacheSerializerOptions** for custom JSON serialization (Redis/Hybrid).
- **AddCaching(IConfiguration)** extension – binds options, validates when enabled, registers mode-specific services and `RoutingCacheService` as `ICacheService`.
- **AddCachingHealthChecks** for lightweight pipeline health checks.
- **ValidateCacheRegistration** for fail-fast DI validation after host build.
- **ICacheTelemetry** abstraction and optional **OpenTelemetryCacheTelemetry** for metrics and spans.
- Data annotations and conditional validation on `CacheOptions` (validated only when `Enabled` is true).
- Target framework: **.NET 10** (`net10.0`).

### Documentation

- [README.md](README.md) – quick start, configuration, per-call options, telemetry, security.
- [docs/INTERNALS.md](docs/INTERNALS.md) – implementation details, modes, configuration, telemetry.
- [docs/OPERATIONS.md](docs/OPERATIONS.md) – production runbooks (when present).

---
