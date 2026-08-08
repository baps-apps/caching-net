# Contributing to Caching.NET

How to get set up, run the tests, and submit a change.

## Overview

Caching.NET is a shared .NET caching package providing **InMemory**, **Redis**, and **Hybrid**
modes. Since v3.0.0 it uses an internal cache engine and exposes that engine's operation contract
(`IFusionCache`) as the cache API, while owning registration, configuration, security, connection
management and observability under its own names.

**Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before making a structural change.** It records
why the engine is composed by hand rather than through its own DI helpers, and what belongs where.

## Repository structure

- **src/Caching.NET** — the library
- **src/Caching.NET.Analyzers** — the `CACHENET001` analyzer, shipped **inside** the Caching.NET
  package under `analyzers/dotnet/cs`; never packed separately
- **tests/Caching.NET.Tests** — unit tests (xUnit)
- **tests/Caching.NET.Tests.Properties** — property-based tests (FsCheck)
- **tests/Caching.NET.Tests.Integration** — integration tests (**requires Docker**)
- **tests/Caching.NET.Tests.Chaos** — outage and restart tests (**requires Docker**)
- **tests/Caching.NET.Tests.Pod** — not a test project: a console cache instance the integration
  suite launches as a separate OS process to exercise backplane behaviour across real processes
- **benchmark/Caching.NET.Benchmark** — BenchmarkDotNet suites
- **aot/Caching.NET.AotSmoke** — native-AOT smoke test
- **samples/Caching.NET.Sample** — sample ASP.NET Core app
- **docs/** — architecture, security, telemetry, operations, health checks, benchmarks, migration

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- Docker (for the integration and chaos suites)
- Git

## Getting started

```bash
git clone https://github.com/baps-apps/caching-net.git
cd caching-net
dotnet restore
dotnet build
dotnet test          # Docker required for the integration and chaos suites
```

All tests must pass before submitting a pull request.

## Development workflow

- **Branch** from the default branch.
- **Code style:** CodeStyle.NET plus central package management (`Directory.Packages.props`).
  `TreatWarningsAsErrors` is on globally — including NuGet audit warnings, so a transitively
  resolved vulnerable package must be pinned in `Directory.Packages.props`.
- **Public XML docs** are required on `src`; `GenerateDocumentationFile` is enabled.

### API design rules

1. **Never wrap the cache operation contract.** No pass-through methods, no renames, no
   `interface ICacheNet : IFusionCache`. Exposing the engine's operation surface directly is the
   point of v3; a wrapper reintroduces exactly the maintenance cost and capability loss the
   redesign removed. If something is genuinely missing, add it to
   `Caching.NET.Extensions.CacheExtensions` — and only if it does something the contract does not.
2. **Never expose engine setup to applications.** No `AddFusionCache`, no `FusionCacheOptions` in a
   public signature, no engine package reference in a consumer project.
   `FusionCacheEntryOptions` in a *per-call* signature is fine; that is the operation contract.
3. **Everything Caching.NET emits is branded `Caching.NET`**: logging categories, meter, activity
   source, metric names, package name, configuration section.
4. **Extend through configuration and the builder**, not through new required constructor
   parameters on public types.

### Adding a feature

| Change | Touch |
|---|---|
| A configuration knob | `CachingOptions` group → `CacheEngineFactory` mapping → `CachingOptionsValidator` rule → `CachingBuilder` method → a `CacheEngineMappingTests` assertion |
| A metric | `CacheTelemetry` instrument → `CacheTelemetryContext` recorder → `CacheEventBridge` subscription. Keep the dimension inside the allow-list asserted by `CacheTelemetryTests` |
| A validation rule | `CachingOptionsValidator` — the message must name the property and the fix — plus a test in `CachingOptionsValidatorTests` |

### Public API changes

`tests/Caching.NET.Tests/Api/PublicApi.approved.txt` is the approved public surface. Any addition,
removal or signature change on a public type fails `PublicApiTests`, which lists removals as
breaking. Accept an intended change with:

```bash
CACHINGNET_APPROVE_API=1 dotnet test tests/Caching.NET.Tests -f net10.0 --filter PublicApiTests
```

Then review the diff to the approved file as part of the change — that diff **is** the
breaking-change review, and it belongs in the pull request.

### Testing conventions

- Prefer a real in-memory Caching.NET cache over a mock: `TestHost.BuildInMemory()`.
- Integration and chaos tests poll for the observable outcome instead of sleeping, except where a
  TTL is the thing under test.
- Tests asserting the *absence* of metrics must join the `caching-net-metrics` xUnit collection (a
  `MeterListener` observes the whole process) and filter by cache name. Cache events are dispatched
  on a background pump, so assertions on their arrival must poll.
- Chaos tests that restart a container must bind a fixed host port — Docker re-randomises published
  ports across stop/start.
- No performance claim without a benchmark. Add a suite and record the numbers in
  [docs/BENCHMARKS.md](docs/BENCHMARKS.md).

### Documentation

Update [README.md](README.md) whenever configuration, behaviour or the public API changes, plus the
matching topic doc: [ARCHITECTURE](docs/ARCHITECTURE.md), [SECURITY](docs/SECURITY.md),
[TELEMETRY](docs/TELEMETRY.md), [OPERATIONS](docs/OPERATIONS.md),
[HEALTH-CHECKS](docs/HEALTH-CHECKS.md), [BENCHMARKS](docs/BENCHMARKS.md).

The README feature matrix must stay honest: never list a feature as supported in a mode where it is
not, and back a mode-specific claim with a test.

## Submitting changes

1. **Commit** with clear, present-tense messages.
2. **Changelog:** add an entry to [CHANGELOG.md](CHANGELOG.md) under Added / Changed / Fixed /
   Removed / Security for anything user-visible.
3. **Pull request** against the default branch. Describe the change, link related issues, and
   confirm `dotnet build` and `dotnet test` pass.

## Versioning and releases

[Semantic Versioning](https://semver.org/):

- **MAJOR** — breaking API or behaviour changes
- **MINOR** — backwards-compatible features or options
- **PATCH** — bug fixes and internal improvements

The package version is set in
[src/Caching.NET/Caching.NET.csproj](src/Caching.NET/Caching.NET.csproj). Release and tagging are
maintained by the maintainers; publishing scripts live in [scripts/](scripts/).

## Questions and issues

- **Bugs and feature requests:** open an issue with a clear description and reproduction steps.
- **Security:** do not open a public issue. Use the BAPS internal security process — see
  [docs/SECURITY.md](docs/SECURITY.md).
