# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Caching.NET is a shared .NET caching NuGet package exposing a single `ICacheService` abstraction with three modes: **InMemory**, **Redis**, and **Hybrid** (in-memory + optional Redis with stampede protection). Consumers reference the NuGet package, not a project reference.

## Build & Test Commands

```bash
dotnet restore              # restore all packages
dotnet build                # build entire solution
dotnet test                 # run all tests
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"  # run a single test
dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs  # create NuGet package
```

## Key Build Settings

- **Target framework:** .NET 10 (`net10.0`), SDK version pinned in `global.json`
- **TreatWarningsAsErrors** is enabled globally via `Directory.Build.props` — all warnings must be resolved
- **Central package management** via `Directory.Packages.props` — add/update package versions there, not in individual `.csproj` files
- **CodeStyle.NET** analyzer is enabled on both src and test projects
- Tests use **xUnit** with **Moq** for mocking

## Architecture

### DI Registration & Builder API

`ServiceCollectionExtensions` provides four `AddCaching` overloads:
- `AddCaching()` — zero-config: InMemory, enabled, 10-minute default expiration
- `AddCaching(IConfiguration)` — reads `CacheOptions` from config section
- `AddCaching(Action<CachingBuilder>)` — fluent code-first configuration
- `AddCaching(IConfiguration, Action<CachingBuilder>)` — config base + fluent overrides (fluent wins on conflict)

All overloads delegate to a shared `AddCachingCore` that:
1. Binds config (if provided), then applies fluent overrides via `PostConfigure`
2. Registers cache infrastructure based on the resolved `Mode`
3. **Always** registers `RoutingCacheService` as the `ICacheService` singleton

There is no `NoOpCacheService`. When `Enabled=false`, `RoutingCacheService` short-circuits: `GetOrCreateAsync` runs the factory directly, all other operations return `Task.CompletedTask`.

### Hot-Reloadable Enabled Flag

`RoutingCacheService` reads `Enabled` from `IOptionsMonitor<CacheOptions>.CurrentValue` on every call. Flipping `Enabled` in appsettings takes effect immediately without restart. Mode and connection strings are startup-only (read from `IOptions<CacheOptions>` at construction time).

### Service Resolution Flow

**`RoutingCacheService`** is the central dispatcher registered as `ICacheService`. It resolves to the correct concrete service (`InMemoryCacheService`, `RedisCacheService`, or `HybridCacheService`) based on the configured mode. It also handles per-call overrides via `CacheCallOptions` (mode override, bypass, force refresh, concurrency coalescing) through the internal `IRoutingCacheService` interface.

### CachingBuilder

Fluent API for configuring cache mode (`UseInMemory()`, `UseRedis(conn)`, `UseHybrid()`), expiration, payload limits, telemetry (`WithOpenTelemetry()`), health checks (`WithHealthChecks()`), and explicit disable (`Disable()`). Each method returns the builder for chaining.

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

`ICacheTelemetry` abstraction with `NoopCacheTelemetry` (default) and `OpenTelemetryCacheTelemetry` (opt-in via `WithOpenTelemetry()`). Meter/ActivitySource name: `Caching.NET.Cache`.

## Publishing

Scripts in `scripts/` use PowerShell Core (`pwsh`) to publish to GitHub Packages. Requires `GITHUB_PAT` env var. See `scripts/README.md` for details.
