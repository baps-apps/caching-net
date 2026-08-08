# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Caching.NET is a shared .NET caching NuGet package with three modes: **InMemory**, **Redis**, and
**Hybrid** (L1 memory + L2 Redis + backplane). Consumers reference the NuGet package, not a project
reference.

**v3.0.0 uses [FusionCache](https://github.com/ZiggyCreatures/FusionCache) as its internal cache
engine and exposes its own `ICacheService` as the cache operation contract — the engine is never
named in a public signature.** Caching.NET owns registration, configuration, security, connection
management and observability; consuming applications never register, configure or reference
FusionCache, and `Internal/FusionCacheService` is the only type that calls an engine operation. Read
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing anything structural — it records why
the composition is hand-rolled rather than using the engine's DI helpers.

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
plus keyed `ICacheService` / `ICacheGuard` projections. There is no `IFusionCache` registration
anywhere in the container. The default cache also gets non-keyed aliases resolved **through
`CacheInstance`**, never through the keyed `ICacheService` — see docs/ARCHITECTURE.md §2 for the
resolution cycle that motivates this.

### CacheEngineFactory

`Internal/CacheEngineFactory` is the single place engine setup happens: option mapping, memory cache,
Redis connection, serializer, backplane, key guard, logger adapter, event bridge. Nothing else in the
codebase touches engine configuration.

Mode mapping:

| Mode | Entry options (`DefaultEntryOptions`) | Tag/`Clear` markers (`TagsDefaultEntryOptions`) |
|---|---|---|
| `InMemory` | `SetSkipDistributedCache(true, skipBackplaneNotifications: true)` | same |
| `Redis` | `SetSkipMemoryCache(true)` — memory locker still active, memory cache bypassed | `SetSkipMemoryCache(true)` |
| `Hybrid` | neither | `MemoryCacheDuration = Entry.LocalExpiration ?? DefaultExpiration` |

`MapTagsEntryOptions` is not optional detail: the engine implements `RemoveByTag`/`Clear` as marker
entries with their own ten-day, memory-layer-included defaults, so a mode applied only to
`DefaultEntryOptions` silently loses invalidation across instances. See docs/ARCHITECTURE.md §3.1.

### Public surface

| Type | Namespace | Purpose |
|---|---|---|
| `ICacheService` | `Caching.NET` | The cache operation contract — eight verbs (`GetOrSet`, `GetOrDefault`, `TryGet`, `Set`, `Remove`, `Expire`, `RemoveByTag`, `Clear`), each with async/sync forms |
| `CacheValue<T>` | `Caching.NET` | Result of a read: a found value vs. an absence, distinguishing a cached `null` from a miss |
| `CacheFactoryContext<T>` | `Caching.NET` | Passed to a context-taking factory: stale value, ETag/`LastModified`, adaptive `Overrides` |
| `CacheEntryOverrides` | `Caching.NET.Options` | Per-call overrides, additive by construction — see docs/ARCHITECTURE.md §3 |
| `CacheEntryPriority` | `Caching.NET.Options` | In-process eviction priority |
| `ICacheProvider` | `Caching.NET` | Named-cache resolution |
| `ICacheGuard` | `Caching.NET` | Key/tag limits, key fingerprints |
| `CachingBuilder` | `Caching.NET` | Fluent configuration |
| `CachingOptions` (+ nested) | `Caching.NET.Options` | Configuration model |
| `CacheExtensions` | `Caching.NET.Extensions` | Batch/convenience operations |
| `CacheKey`, `CacheKeyBuilder`, `ICacheKeyFactory` | `Caching.NET.Keys` | Guarded key construction |
| `CacheTelemetry`, `CacheTelemetryAttributes`, `CacheResults`, `CacheLayers` | `Caching.NET.Telemetry` | Instrumentation names, and the `cache.result` / `cache.layer` value constants |
| `CachingHealthCheck`, `CachingLivenessHealthCheck` | `Caching.NET.Health` | Probes |
| `CachingDefaults`, `CacheConfigurationKeys` | `Caching.NET`, `Caching.NET.Configuration` | Registration and configuration-section constants |
| `CachingOptionsValidator` | `Caching.NET.Validation` | The `IValidateOptions<CachingOptions>` implementation |

### API design rules

1. **Never expose the cache engine.** No engine type appears in any public signature — not the
   operation contract, not per-call options, not telemetry names, not connection configuration.
   `ICacheService` is the API. `Internal/FusionCacheService` is the only type that calls an engine
   operation; `Internal/CacheEngineFactory` is the only type that configures one.
2. **The contract is eight verbs, permanently.** A new engine capability lands as a `CachingOptions`
   knob or a `CacheEntryOverrides` field, never a ninth verb on `ICacheService`. `CacheExtensions` may
   add a method only when it does something the eight verbs genuinely do not (batching, existence
   probing, forced refresh) — never a rename or a pass-through.
3. **Everything Caching.NET emits is branded `Caching.NET`**: logging categories, meter, activity
   source, metric names, package name, configuration section.

### Adding a feature

- **A knob** → `CachingOptions` group + `CacheEngineFactory` mapping + `CacheEntryOverrides` field (if
  it is per-call) + `CachingOptionsValidator` rule + `CachingBuilder` method + a
  `CacheEngineMappingTests` assertion.
- **A metric** → instrument in `CacheTelemetry`, recorder in `CacheTelemetryContext`, and a producer
  chosen from the one-producer-per-signal split: `Internal/FusionCacheService` for anything on the
  caller's synchronous path (hits, misses, operations, foreground invalidations), `CacheEventBridge`
  for anything that can only be told apart on the engine's own event pump (factory executions
  foreground and background, fail-safe, eager refresh, backplane publish/receive, evictions), or the
  layer decorators (`InstrumentedMemoryCache`, `InstrumentedDistributedCache`,
  `InstrumentedCacheSerializer`) for per-layer duration and payload size. Recording the same signal
  from two producers double-counts it. Keep the dimension inside the allow-list asserted by
  `CacheTelemetryTests`.
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
