# Test Runtime Speed-up Design

> **HISTORICAL.** `tests/Caching.NET.Tests.Analyzers` was **removed in v2.1.0** along with the `Caching.NET.Analyzers` project. Test-project listings below that include it are stale.

**Date:** 2026-05-06
**Status:** Draft
**Topic:** Reduce `dotnet test` wall time for local iteration without weakening CI coverage.

## Motivation

`dotnet test --no-build --no-restore` currently takes ~43s wall on this machine (Apple Silicon). Four of five test projects multi-target `net8.0;net9.0;net10.0`, so unit tests run three times against ~182 cases per TFM. The library legitimately needs to validate behavior on each supported runtime, but paying that cost on every local build is the wrong trade for inner-loop iteration. xUnit defaults are also unconfigured, leaving parallelism on the table.

## Goals

1. **Local default fast.** Single TFM (`net10.0`) for tests when no CI/override flag is set.
2. **CI unchanged.** Full matrix (`net8.0;net9.0;net10.0`) still runs on every PR — automatically detected.
3. **Manual full-matrix override** for pre-push validation locally.
4. **xUnit parallelism configured explicitly** across all five test projects, with collection-level isolation preserved where required (Redis Testcontainer).

## Non-goals

- Splitting test projects, removing tests, or skipping categories by default.
- Diagnosing the intermittent `HybridCacheServiceTests.GetOrCreateAsync_StoresAndReturnsValue` flake observed on net9.0. Tracked separately; out of scope here.
- Changing the library's own multi-target list (`src/Caching.NET/Caching.NET.csproj` continues to ship `net8.0;net9.0;net10.0`).

## Design

### Conditional target frameworks

Add a single MSBuild property `CachingTestTargets` defined in `tests/Directory.Build.props`:

```xml
<PropertyGroup>
  <CachingTestTargets Condition="'$(CI)' == 'true' Or '$(FullTestMatrix)' == 'true'">net8.0;net9.0;net10.0</CachingTestTargets>
  <CachingTestTargets Condition="'$(CachingTestTargets)' == ''">net10.0</CachingTestTargets>
</PropertyGroup>
```

Each multi-targeted test csproj replaces its hard-coded list with:

```xml
<TargetFrameworks>$(CachingTestTargets)</TargetFrameworks>
```

Affected projects:

- `tests/Caching.NET.Tests/Caching.NET.Tests.csproj`
- `tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj`
- `tests/Caching.NET.Tests.Properties/Caching.NET.Tests.Properties.csproj`
- `tests/Caching.NET.Tests.Chaos/Caching.NET.Tests.Chaos.csproj`

`tests/Caching.NET.Tests.Analyzers` stays `net10.0` (already single-targeted; analyzer host pins net10).

#### Triggers

| Context                                    | TFMs run                |
| ------------------------------------------ | ----------------------- |
| Local `dotnet test`                        | `net10.0`               |
| Local `dotnet test -p:FullTestMatrix=true` | `net8.0;net9.0;net10.0` |
| GitHub Actions (any workflow)              | `net8.0;net9.0;net10.0` |
| Other CI providers setting `CI=true`       | `net8.0;net9.0;net10.0` |

`CI=true` is set automatically by GitHub Actions, GitLab, CircleCI, Azure Pipelines, and most modern CI hosts. No workflow YAML changes required.

### xUnit runner configuration

Add `xunit.runner.json` to each of the five test projects with `<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>` set in the csproj (or via wildcard in `tests/Directory.Build.props`).

Standard config for unit-style projects (Caching.NET.Tests, Caching.NET.Tests.Properties, Caching.NET.Tests.Analyzers):

```json
{
  "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
  "parallelizeAssembly": true,
  "parallelizeTestCollections": true,
  "maxParallelThreads": -1
}
```

Same config for `Caching.NET.Tests.Integration` and `Caching.NET.Tests.Chaos`. Integration tests already use `[Collection("Redis")]` to serialize tests sharing the `RedisContainerFixture`, so cross-collection parallelism is safe — tests inside the `Redis` collection still run serially within themselves. Chaos tests do not share fixtures (each builds its own DI provider), so unrestricted parallelism is safe there too.

`maxParallelThreads: -1` lets xUnit use `Environment.ProcessorCount`.

To centralize copy-to-output behavior, add to `tests/Directory.Build.props`:

```xml
<ItemGroup>
  <None Update="xunit.runner.json" Condition="Exists('xunit.runner.json')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

### Verification plan

1. **Local default run** — `dotnet test --no-build --no-restore` should report only `net10.0` test DLLs. Wall time target: under 20s (down from ~43s).
2. **Local full-matrix run** — `dotnet test --no-build --no-restore -p:FullTestMatrix=true` should match current behavior: net8.0 + net9.0 + net10.0 binaries built and exercised.
3. **CI parity** — open a draft PR; confirm GH Actions still runs all three TFMs. (`CI=true` is set by the runner.)
4. **No regressions** — pass count per TFM unchanged for both modes (currently 182 unit + 10 integration + 11 properties + 4 chaos + 7 analyzer per TFM).

## Risks

- **CI variable collision.** If a developer has `CI=true` exported in their shell, local runs will silently use the full matrix. Mitigation: documented in the design + explicit `FullTestMatrix` override is the documented escape hatch.
- **Future test added without copy-to-output.** Centralizing the `xunit.runner.json` copy rule in `tests/Directory.Build.props` (above) prevents this.
- **Pre-existing flake.** `HybridCacheServiceTests.GetOrCreateAsync_StoresAndReturnsValue` was observed failing once during baseline measurement. Faster test runs may surface it more frequently. Will be tracked separately; this design does not introduce or fix it.

## Out of scope, for follow-up

- Diagnose Hybrid flake (likely Redis-down fallback + memory pressure under parallelism, but unverified).
- Solution filter (`.slnf`) for "fast inner loop" that excludes Integration + Chaos.
- Build-time speed-ups (e.g., `Directory.Packages.props` audit, NuGet cache warming).
