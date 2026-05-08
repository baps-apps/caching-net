# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Caching.NET is a shared .NET caching NuGet package exposing a single `ICacheService` abstraction with three modes: **InMemory**, **Redis**, and **Hybrid** (in-memory + optional Redis with stampede protection). Consumers reference the NuGet package, not a project reference.

## Repository Layout

- `src/Caching.NET` — main library (NuGet package)
- `samples/Caching.NET.Sample` — ASP.NET sample app demonstrating DI registration, controllers, telemetry wiring
- `tests/`:
  - `Caching.NET.Tests` — unit tests
  - `Caching.NET.Tests.Properties` — property-based tests (FsCheck-style)
  - `Caching.NET.Tests.Integration` — **requires Docker** (Testcontainers spins up Redis)
  - `Caching.NET.Tests.Chaos` — **requires Docker** (Testcontainers; resilience/fault-injection)

## Build & Test Commands

```bash
dotnet restore              # restore all packages
dotnet build                # build entire solution
dotnet test                 # run all tests
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"  # run a single test
dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs  # create NuGet package
```

## Key Build Settings

- **Target frameworks:** `net8.0`, `net9.0`, and `net10.0` (multi-target NuGet package); SDK version pinned in `global.json`
- **TreatWarningsAsErrors** is enabled globally via `Directory.Build.props` — all warnings must be resolved
- **Central package management** via `Directory.Packages.props` — add/update package versions there, not in individual `.csproj` files
- **CodeStyle.NET** analyzer is enabled on both src and test projects
- Tests use **xUnit** with **Moq** for mocking

## Architecture

### DI Registration & Builder API

`ServiceCollectionExtensions` provides four `AddCaching` overloads:

- `AddCaching()` — **InMemory** mode + enabled defaults (10-minute expiration). Startup validation still requires a non-empty `KeyPrefix` when `Enabled=true` (`KeyPrefix` must not contain `':'`; routing reserves it as the delimiter after the prefix), so production apps normally use `AddCaching(IConfiguration)` or `AddCaching(s => … WithKeyPrefix(...))`.
- `AddCaching(IConfiguration)` — reads `CacheOptions` from config section
- `AddCaching(Action<CachingBuilder>)` — fluent code-first configuration
- `AddCaching(IConfiguration, Action<CachingBuilder>)` — config base + fluent overrides (fluent wins on conflict)

All overloads delegate to a shared `AddCachingCore` that:

1. Binds config (if provided), then applies fluent overrides via `PostConfigure`
2. When `Enabled=true`, registers cache infrastructure based on the resolved `Mode` (memory cache, Redis/Hybrid services, serializer, Polly registry, TLS validator as applicable)
3. **Always** registers `RoutingCacheService` as the `ICacheService` singleton
4. **Always** registers `ICacheKeyFactory` via `TryAddSingleton` (`DefaultCacheKeyFactory` mirrors `CacheKey.For`); register a custom `ICacheKeyFactory` **before** `AddCaching` to replace the default

There is no `NoOpCacheService`. When `Enabled=false`, **no backends are registered** (no `IMemoryCache`, Redis multiplexer, hybrid stack, serializer, or Polly registry), `IValidateOptions<CacheOptions>` skips validation, and `RoutingCacheService` still short-circuits: `GetOrCreateAsync` runs the factory directly, all other operations return completed tasks / defaults. Health checks registered via `WithHealthChecks()` still run and report healthy when caching is disabled.

### Hot-Reloadable Enabled Flag

`RoutingCacheService` reads `Enabled` from `IOptionsMonitor<CacheOptions>.CurrentValue` on every call. Flipping `Enabled` to **false** takes effect immediately (calls short-circuit to the factory or no-op). Flipping to **true** only uses real backends if they were registered at startup: if the process started with `Enabled=false`, **no** memory/Redis/hybrid services were registered—restart after enabling so `AddCaching` wires backends. If the host started with `Enabled=true`, toggling `Enabled` off and on at runtime continues to use the existing backends. Mode and connection strings remain startup-only (read from `IOptions<CacheOptions>` at construction time).

### Service Resolution Flow

**`RoutingCacheService`** is the central dispatcher registered as `ICacheService`. It resolves to the correct concrete service (`InMemoryCacheService`, `RedisCacheService`, or `HybridCacheService`) based on the configured mode. It also handles per-call overrides via `CacheCallOptions` (mode override, bypass, force refresh, concurrency coalescing) through the internal `IRoutingCacheService` interface.

### CachingBuilder

Fluent API. Method groups (see source for full list — adding a knob? extend the matching group):

- **Mode:** `UseInMemory()`, `UseRedis(conn|Action<ConfigurationOptions>)`, `UseHybrid(...)`
- **Toggle / presets:** `Enable()`, `Disable()`, `UseDevelopmentDefaults()`, `UseProductionDefaults()`
- **Expiration & payload caps:** `WithDefaultExpiration`, `WithDefaultLocalExpiration`, `WithMaximumPayloadBytes`, `WithMaximumKeyLength`, `WithMemorySizeLimit`, `WithFactoryTimeout`
- **Stampede / jitter / coalescing:** `WithStripedLocks`, `WithTtlJitter`, `WithStaleRefreshConcurrency`
- **Keys:** `WithKeyPrefix`, `WithKeyValidator`, `WithKeyTransformer`
- **Serialization:** `WithSerializer<T>()`, `WithSerializer(ICacheSerializer)`, `WithMessagePackSerializer()`
- **Resilience / Redis:** `WithResilience(Action<CacheResilienceOptions>)`, `WithRedisOperationTimeout`, `WithStrictCertificateValidation`, `WithPermissiveRedisTls`
- **Tags / observability:** `RequireTagSupport()`, `WithOpenTelemetry()`, `WithHealthChecks(name, splitLivenessReadiness)`

Use via `AddCaching(Action<CachingBuilder>)`; each method returns `this` for chaining.

### Extension Methods for Per-Call Options

`CacheServiceCallExtensions` provides overloads that accept `CacheCallOptions`. These cast `ICacheService` to `IRoutingCacheService` internally — new per-call features go here, not on `ICacheService`.

### API Stability Contract

`ICacheService` is the stable public interface. New capabilities are added via:

1. Extension methods (`CacheServiceCallExtensions`)
2. Per-call options (`CacheCallOptions`)
3. Configuration (`CacheOptions`)
4. Builder methods (`CachingBuilder`)

Avoid adding new members to `ICacheService` directly.

### Telemetry

Static `CacheInstruments` (`Meter` / `ActivitySource`) — subscribe with `AddMeter(CacheInstruments.MeterName)` / `AddSource(CacheInstruments.ActivitySourceName)`; both names are **`Caching.NET`**. `WithOpenTelemetry()` remains an API-compatibility hook for apps that already call it.

## Publishing

Scripts in `scripts/` use PowerShell Core (`pwsh`) to publish to GitHub Packages. Requires `GITHUB_PAT` env var. See `scripts/README.md` for details.
