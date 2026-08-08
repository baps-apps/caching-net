# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Caching.NET is a shared .NET caching NuGet package with three modes: **InMemory**, **Redis**, and
**Hybrid** (L1 memory + L2 Redis + backplane). Consumers reference the NuGet package, not a project
reference.

**v3.0.0 uses [FusionCache](https://github.com/ZiggyCreatures/FusionCache) as its internal cache
engine and exposes `IFusionCache` as the cache operation contract.** Caching.NET owns registration,
configuration, security, connection management and observability; consuming applications never
register, configure or reference FusionCache. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
before changing anything structural — it records why the composition is hand-rolled rather than
using the engine's DI helpers.

## Repository Layout

- `src/Caching.NET` — the library (NuGet package)
- `src/Caching.NET.Analyzers` — the `CACHENET001` analyzer, shipped **inside** the Caching.NET
  package under `analyzers/dotnet/cs`; never packed separately
- `samples/Caching.NET.Sample` — ASP.NET sample: registration, controllers, named caches, health checks
- `benchmark/Caching.NET.Benchmark` — BenchmarkDotNet suites
- `aot/Caching.NET.AotSmoke` — native-AOT smoke test
- `tests/`:
  - `Caching.NET.Tests` — unit tests
  - `Caching.NET.Tests.Properties` — property-based tests (FsCheck)
  - `Caching.NET.Tests.Integration` — **requires Docker** (Testcontainers spins up Redis)
  - `Caching.NET.Tests.Chaos` — **requires Docker**; outage, restart and fail-safe behaviour
  - `Caching.NET.Tests.Pod` — not a test project: a console "pod" the integration suite launches as a
    separate OS process to test backplane behaviour across real processes
- `docs/` — ARCHITECTURE, SECURITY, TELEMETRY, OPERATIONS, HEALTH-CHECKS, BENCHMARKS,
  MIGRATION-V2-TO-V3. `MIGRATION-V1-TO-V2.md` and `V2.0.0-RELEASE-IMPACT.md` are **historical**: they
  document the v2 surface and carry a banner saying so — do not rewrite them into v3 shape.

## Build & Test Commands

```bash
dotnet restore
dotnet build
dotnet test                 # Docker required for the integration and chaos suites
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"
dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs
```

Benchmarks:

```bash
cd benchmark/Caching.NET.Benchmark
dotnet run -c Release -- --filter '*InMemoryBenchmarks*'
CACHINGNET_BENCH_REDIS="127.0.0.1:63790,abortConnect=false" dotnet run -c Release -- --filter '*RedisBenchmarks*'
```

## Key Build Settings

- **Target framework:** `net10.0` only; SDK pinned in `global.json`
- **TreatWarningsAsErrors** globally via `Directory.Build.props` — including NuGet audit warnings, so
  a transitively-resolved vulnerable package must be pinned in `Directory.Packages.props`
- **Central package management** via `Directory.Packages.props` — versions go there, never in a `.csproj`
- **CodeStyle.NET** analyzer on src and test projects
- **GenerateDocumentationFile** is on for `src` — every public member needs XML docs
- Tests use **xUnit**; **Moq** is available but rarely needed (prefer a real in-memory cache)

## Architecture

### Registration

`Caching.NET.Extensions.ServiceCollectionExtensions`:

- `AddCaching(IConfiguration)` — binds `CacheOptions`, plus anything under `CacheOptions:NamedCaches`
- `AddCaching(IConfiguration, Action<CachingBuilder>)` — fluent overrides win over configuration
- `AddCaching(Action<CachingBuilder>)` — code-first
- `AddCachingOptions(Action<CachingOptions>)` — strongly typed
- `AddCaching(string cacheName, …)` — additional named caches
- `AddCachingHealthChecks(…)`, `ValidateCachingRegistration()`

Per cache the registration claims the name in `CacheRegistrationTracker` (duplicate names throw at
registration), sets up named options with `ValidateOnStart`, and registers a keyed `CacheInstance`
plus keyed `IFusionCache` / `ICacheGuard` projections. The default cache also gets non-keyed aliases
resolved **through `CacheInstance`**, never through the keyed `IFusionCache` — see
docs/ARCHITECTURE.md §2 for the resolution cycle that motivates this.

### CacheEngineFactory

`Internal/CacheEngineFactory` is the single place engine setup happens: option mapping, memory cache,
Redis connection, serializer, backplane, key guard, logger adapter, event bridge. Nothing else in the
codebase touches engine configuration.

Mode mapping:

| Mode | Entry options |
|---|---|
| `InMemory` | `SetSkipDistributedCache(true, skipBackplaneNotifications: true)` |
| `Redis` | `SetSkipMemoryCache(true)` — memory locker still active, memory cache bypassed |
| `Hybrid` | neither |

### Public surface

| Type | Namespace | Purpose |
|---|---|---|
| `IFusionCache` | `ZiggyCreatures.Caching.Fusion` | The cache operation contract |
| `ICacheProvider` | `Caching.NET` | Named-cache resolution |
| `ICacheGuard` | `Caching.NET` | Key/tag limits, key fingerprints |
| `CachingBuilder` | `Caching.NET` | Fluent configuration |
| `CachingOptions` (+ nested) | `Caching.NET.Options` | Configuration model |
| `CacheExtensions` | `Caching.NET.Extensions` | Batch/convenience operations |
| `CacheKey`, `CacheKeyBuilder`, `ICacheKeyFactory` | `Caching.NET.Keys` | Guarded key construction |
| `CacheTelemetry`, `CacheTelemetryAttributes` | `Caching.NET.Telemetry` | Instrumentation names |
| `CachingHealthCheck`, `CachingLivenessHealthCheck` | `Caching.NET.Health` | Probes |

### API design rules

1. **Never wrap the cache operation contract.** No pass-through methods, no renames, no
   `interface ICacheNet : IFusionCache`. The whole point of v3 is that the engine's operation surface
   is the API. If a capability is missing, it belongs in `CacheExtensions` only when it does something
   the contract genuinely does not.
2. **Never expose engine setup to applications.** No `AddFusionCache`, no `FusionCacheOptions` in a
   public signature, no engine package reference in a consumer project. `FusionCacheEntryOptions` in a
   *per-call* signature is fine — that is the operation contract.
3. **Everything Caching.NET emits is branded `Caching.NET`**: logging categories, meter, activity
   source, metric names, package name, configuration section.

### Adding a feature

- **A knob** → `CachingOptions` group + `CacheEngineFactory` mapping +
  `CachingOptionsValidator` rule + `CachingBuilder` method + a `CacheEngineMappingTests`
  assertion.
- **A metric** → instrument in `CacheTelemetry`, recorder in `CacheTelemetryContext`, subscription in
  `CacheEventBridge`. Keep the dimension inside the allow-list asserted by `CacheTelemetryTests`.
- **A validation rule** → `CachingOptionsValidator`, with a message that names the property and
  the fix, plus a test in `CachingOptionsValidatorTests`.

### Public API

`tests/Caching.NET.Tests/Api/PublicApi.approved.txt` is the approved public surface. Any change to a
public type or member fails `PublicApiTests`. To accept an intended change:

```bash
CACHINGNET_APPROVE_API=1 dotnet test tests/Caching.NET.Tests -f net10.0 --filter PublicApiTests
```

Then review the diff to the approved file as part of the change — that diff is the breaking-change
review.

### Testing conventions

- Prefer a real in-memory Caching.NET cache over a mock — `TestHost.BuildInMemory()`.
- Integration and chaos tests poll for the observable outcome instead of sleeping, except where a TTL
  is the thing under test.
- Tests asserting the *absence* of metrics must be in the `caching-net-metrics` xUnit collection
  (a `MeterListener` observes the whole process) and filter by cache name.
- Chaos tests that restart a container must bind a fixed host port — Docker re-randomises published
  ports across stop/start.

## Publishing

Scripts in `scripts/` use PowerShell Core (`pwsh`) to publish to GitHub Packages. Requires the
`GITHUB_PAT` env var. See `scripts/README.md`.
