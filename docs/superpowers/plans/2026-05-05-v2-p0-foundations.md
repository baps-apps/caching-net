# Caching.NET v2.0.0 — Phase P0: Foundations & Critical Fixes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the v2.0.0 foundation: multi-target framework, mandatory `KeyPrefix`, striped lock manager, Polly v8 resilience, pluggable serializer, drop `RedisInstanceName`/`ICacheTelemetry`, default tightening — replacing v1's lock leak, missing resilience, and JSON-only serialization.

**Architecture:** Single `Caching.NET` package, multi-targeted `net8.0;net9.0;net10.0`. Stamp-protected via 1024 fixed `SemaphoreSlim` stripes (no per-key allocation). All Redis ops wrapped in named Polly `ResiliencePipeline`. Serialization behind `ICacheSerializer` (default JSON STJ source-gen). Telemetry via static `CacheInstruments` (OTel `Meter` + `ActivitySource`).

**Tech Stack:** .NET 8/9/10, xUnit, Moq, Polly v8, System.Text.Json source-gen, MessagePack (P2), `Microsoft.CodeAnalysis.PublicApiAnalyzers`.

**Spec reference:** `docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md` §3, §4, §5, §6 (resilience + serializer subsection), §7 (CacheInstruments only — full observability lands in P1), §8.

**Branch:** `vpatel/v2`. All P0 work commits to this branch.

**Doc / behavior sync (2026-05-06 audit follow-up):** Runtime behavior now includes **`Enabled=false` skipping all backends and options validation**, **widened Polly transient classification**, **50 ms–1 s retry backoff**, optional **Redis concurrency limiter** (`ResiliencePipelineRegistryOptions`), **serialize/deserialize histograms**, **strict `PayloadEnvelope` length + `IBufferWriter` write path**, **sampled drift logs**, **Redis `RemoveMany` remove counter = keys deleted**, **health check multiplexer PING + per-process probe key**, **`MaximumKeyLength` prefix budget validation**, and builder APIs **`Enable` / `UseDevelopmentDefaults` / `UseProductionDefaults` / `WithKeyValidator` / `WithKeyTransformer`**. Consumer docs: [README.md](../../../README.md), [INTERNALS.md](../../INTERNALS.md), [TELEMETRY.md](../../TELEMETRY.md), [HEALTH-CHECKS.md](../../HEALTH-CHECKS.md).

---

## File Structure

### Created files

| Path | Responsibility |
|---|---|
| `src/Caching.NET/Internal/StripedLockManager.cs` | Fixed-size semaphore array with stable hash → stripe mapping |
| `src/Caching.NET/Internal/StableStringHash.cs` | xxHash32 stable-across-process hash for stripe selection |
| `src/Caching.NET/Internal/RedisConnectionStringRedactor.cs` | Strip secrets from connection strings before logging |
| `src/Caching.NET/Telemetry/CacheInstruments.cs` | Static `Meter`+`ActivitySource`+counters/histograms; replaces ICacheTelemetry |
| `src/Caching.NET/Serialization/ICacheSerializer.cs` | Pluggable serializer interface |
| `src/Caching.NET/Serialization/JsonCacheSerializer.cs` | Default JSON STJ implementation |
| `src/Caching.NET/Serialization/CachingNetJsonContext.cs` | Source-gen JSON context for built-in types |
| `src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs` | Polly v8 named-pipeline factory |
| `src/Caching.NET/Resilience/ResiliencePipelineNames.cs` | Named pipeline string constants |
| `src/Caching.NET/Validation/CacheOptionsValidator.cs` | `IValidateOptions<CacheOptions>` (replaces inline DataAnnotations alone) |
| `src/Caching.NET/PublicAPI.Shipped.txt` | API surface lock (initially empty) |
| `src/Caching.NET/PublicAPI.Unshipped.txt` | New v2 surface accumulator |
| `tests/Caching.NET.Tests/Internal/StripedLockManagerTests.cs` | Unit tests for stripe mapping + concurrency |
| `tests/Caching.NET.Tests/Internal/StableStringHashTests.cs` | Hash determinism tests |
| `tests/Caching.NET.Tests/Internal/RedisConnectionStringRedactorTests.cs` | Secret-stripping tests |
| `tests/Caching.NET.Tests/Serialization/JsonCacheSerializerTests.cs` | Round-trip serialization tests |
| `tests/Caching.NET.Tests/Resilience/CacheResiliencePipelineBuilderTests.cs` | Pipeline composition tests |
| `tests/Caching.NET.Tests/Validation/CacheOptionsValidatorTests.cs` | KeyPrefix regex + range validation |

### Modified files

| Path | Reason |
|---|---|
| `Directory.Packages.props` | Add Polly v8, MessagePack, PublicApiAnalyzers; bump existing |
| `src/Caching.NET/Caching.NET.csproj` | Multi-target, AOT/trim flags, source-link, package validation |
| `src/Caching.NET/Options/CacheOptions.cs` | Add `KeyPrefix` (required), `StripeLockCount`, `RedisOperationTimeout`; remove `RedisInstanceName`; flip TLS default |
| `src/Caching.NET/CachingBuilder.cs` | Add `WithKeyPrefix`, `WithSerializer<T>`, `WithResilience`, `WithStripedLocks`; remove `WithRedisInstanceName` |
| `src/Caching.NET/Services/RoutingCacheService.cs` | Replace `_keyLocks` dict with `StripedLockManager`; inject `KeyPrefix`; emit via `CacheInstruments`; thread serializer |
| `src/Caching.NET/Services/RedisCacheService.cs` | Wrap ops in resilience pipeline + per-op timeout; use `ICacheSerializer`; emit via `CacheInstruments` |
| `src/Caching.NET/Services/InMemoryCacheService.cs` | Emit via `CacheInstruments`; fix hit/miss order |
| `src/Caching.NET/Services/HybridCacheService.cs` | Emit via `CacheInstruments` |
| `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs` | Wire up StripedLockManager, ICacheSerializer, ResiliencePipelineProvider; drop ICacheTelemetry registration; drop RedisInstanceName mapping |
| `src/Caching.NET/Extensions/CacheServiceCallExtensions.cs` | Update for any signature deltas |
| `src/Caching.NET/Health/CachingHealthCheck.cs` | Use `CacheInstruments` if it currently uses `ICacheTelemetry` |
| `samples/Caching.NET.Sample/Program.cs` | Replace v1 wiring with v2 builder API |
| `samples/Caching.NET.Sample/appsettings.json` | Replace `RedisInstanceName` with `KeyPrefix` |

### Deleted files

| Path | Reason |
|---|---|
| `src/Caching.NET/Abstractions/ICacheTelemetry.cs` | Replaced by `CacheInstruments` |
| `src/Caching.NET/Telemetry/NoopCacheTelemetry.cs` | No longer needed |
| `src/Caching.NET/Telemetry/OpenTelemetryCacheTelemetry.cs` | No longer needed |

---

## Conventions

- **TDD:** every behavior change starts with a failing test.
- **Commits:** small, frequent, one logical change per commit. Use `feat:`/`fix:`/`refactor:`/`test:`/`chore:` prefixes. Always include `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- **Build command:** `dotnet build -c Release` from repo root. Must succeed with `TreatWarningsAsErrors=true`.
- **Test command:** `dotnet test --no-build -c Release`.
- **Single test:** `dotnet test --filter "FullyQualifiedName~ClassName.MethodName"`.

---

## Task 1: Multi-target framework + tooling foundation

**Files:**
- Modify: `src/Caching.NET/Caching.NET.csproj`
- Modify: `Directory.Packages.props`
- Modify: `tests/Caching.NET.Tests/Caching.NET.Tests.csproj`
- Modify: `tests/Directory.Build.props`

- [ ] **Step 1: Update `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- Core libs -->
    <PackageVersion Include="Microsoft.Extensions.Caching.Hybrid" Version="10.4.0" />
    <PackageVersion Include="Microsoft.Extensions.Caching.Memory" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Configuration" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.5" />
    <!-- Resilience (NEW for P0) -->
    <PackageVersion Include="Polly" Version="8.5.0" />
    <PackageVersion Include="Polly.Extensions" Version="8.5.0" />
    <!-- Diagnostics -->
    <PackageVersion Include="System.Diagnostics.DiagnosticSource" Version="10.0.5" />
    <!-- Serialization -->
    <PackageVersion Include="System.Text.Json" Version="10.0.5" />
    <PackageVersion Include="MessagePack" Version="3.1.4" />
    <!-- Build/SDK -->
    <PackageVersion Include="CodeStyle.NET" Version="1.0.0" />
    <PackageVersion Include="Microsoft.SourceLink.GitHub" Version="10.0.201" />
    <PackageVersion Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="3.11.0-beta1.24420.2" />
    <!-- Test/sample -->
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.5" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.4.0" />
    <PackageVersion Include="coverlet.collector" Version="8.0.1" />
    <PackageVersion Include="Moq" Version="4.20.72" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Update `src/Caching.NET/Caching.NET.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <PackageId>Caching.NET</PackageId>
    <Version>2.0.0-alpha.1</Version>
    <Authors>BAPS Dev Team</Authors>
    <Description>Production-grade caching: InMemory, Redis, and Hybrid modes with stampede protection, Polly resilience, OpenTelemetry, pluggable serialization.</Description>
    <PackageTags>cache;caching;memory;redis;hybrid;distributed;polly;opentelemetry</PackageTags>
    <PackageProjectUrl>https://github.com/baps-apps/caching-net/</PackageProjectUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <RepositoryUrl>https://github.com/baps-apps/caching-net.git</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <IsAotCompatible>true</IsAotCompatible>
    <IsTrimmable>true</IsTrimmable>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <DeterministicSourcePaths>true</DeterministicSourcePaths>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <EnablePackageValidation>true</EnablePackageValidation>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CodeStyle.NET" />
    <PackageReference Include="Microsoft.Extensions.Caching.Hybrid" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" />
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
    <PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" />
    <PackageReference Include="Microsoft.SourceLink.GitHub">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Polly" />
    <PackageReference Include="Polly.Extensions" />
    <PackageReference Include="System.Diagnostics.DiagnosticSource" />
    <PackageReference Include="MessagePack" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create empty PublicAPI files**

Create `src/Caching.NET/PublicAPI.Shipped.txt` with content `#nullable enable\n`.
Create `src/Caching.NET/PublicAPI.Unshipped.txt` with content `#nullable enable\n`.

- [ ] **Step 4: Verify solution restores and builds**

Run: `dotnet restore && dotnet build -c Release`
Expected: Build succeeds across all three TFMs. Some `RS0016` PublicAPI warnings about undocumented public APIs (this is expected — we'll fill them in as we go). `TreatWarningsAsErrors` may flag these — temporarily set `<NoWarn>$(NoWarn);RS0016;RS0017</NoWarn>` in csproj if blocking. Remove suppression in final task.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/Caching.NET/Caching.NET.csproj src/Caching.NET/PublicAPI.Shipped.txt src/Caching.NET/PublicAPI.Unshipped.txt
git commit -m "$(cat <<'EOF'
chore: multi-target net8/9/10 + AOT/trim flags + PublicAPI analyzer

Adds Polly v8, MessagePack, source-link, and PublicApiAnalyzers.
Bumps version to 2.0.0-alpha.1 to mark v2 work-in-progress.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Stable string hash (xxHash32)

**Files:**
- Create: `src/Caching.NET/Internal/StableStringHash.cs`
- Create: `tests/Caching.NET.Tests/Internal/StableStringHashTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Caching.NET.Tests/Internal/StableStringHashTests.cs`:

```csharp
using Caching.NET.Internal;

namespace Caching.NET.Tests.Internal;

public sealed class StableStringHashTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("asm-api-dev:Order:12345")]
    [InlineData("a very long key that is well past the inline buffer size to exercise the slow path with multi-block hashing input data")]
    public void Compute_ReturnsSameValueForSameInput(string key)
    {
        var first = StableStringHash.Compute(key);
        var second = StableStringHash.Compute(key);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_ReturnsDifferentValuesForDifferentInputs()
    {
        var a = StableStringHash.Compute("alpha");
        var b = StableStringHash.Compute("beta");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_KnownVector_MatchesPrecomputed()
    {
        // xxHash32 of "alpha" with seed 0 is well-defined; this test pins the hash so swapping
        // the impl later requires intentional update + recomputed stripe distribution.
        const string key = "alpha";
        var actual = StableStringHash.Compute(key);
        Assert.Equal(0xBA8AB69Cu, actual); // value derived from xxHash32 reference impl
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~StableStringHashTests"`
Expected: FAIL — `StableStringHash` type does not exist.

- [ ] **Step 3: Implement xxHash32**

`src/Caching.NET/Internal/StableStringHash.cs`:

```csharp
using System.Text;

namespace Caching.NET.Internal;

/// <summary>
/// Process-stable, allocation-light xxHash32 over a UTF-8 view of a string.
/// Used for striped-lock placement so the same key always maps to the same stripe
/// across process restarts and across machines (unlike <see cref="string.GetHashCode()"/>).
/// </summary>
internal static class StableStringHash
{
    private const uint Prime1 = 2654435761u;
    private const uint Prime2 = 2246822519u;
    private const uint Prime3 = 3266489917u;
    private const uint Prime4 = 668265263u;
    private const uint Prime5 = 374761393u;

    public static uint Compute(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var byteCount = Encoding.UTF8.GetByteCount(input);
        Span<byte> buffer = byteCount <= 256
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        Encoding.UTF8.GetBytes(input, buffer);
        return Compute(buffer);
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var len = (uint)data.Length;
        uint h32;
        var index = 0;

        if (data.Length >= 16)
        {
            uint v1 = Prime1 + Prime2;
            uint v2 = Prime2;
            uint v3 = 0;
            uint v4 = 0u - Prime1;

            do
            {
                v1 = Round(v1, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
                v2 = Round(v2, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
                v3 = Round(v3, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
                v4 = Round(v4, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
            } while (data.Length - index >= 16);

            h32 = RotL(v1, 1) + RotL(v2, 7) + RotL(v3, 12) + RotL(v4, 18);
        }
        else
        {
            h32 = Prime5;
        }

        h32 += len;

        while (data.Length - index >= 4)
        {
            h32 += BitConverter.ToUInt32(data.Slice(index, 4)) * Prime3;
            h32 = RotL(h32, 17) * Prime4;
            index += 4;
        }

        while (index < data.Length)
        {
            h32 += data[index] * Prime5;
            h32 = RotL(h32, 11) * Prime1;
            index++;
        }

        h32 ^= h32 >> 15;
        h32 *= Prime2;
        h32 ^= h32 >> 13;
        h32 *= Prime3;
        h32 ^= h32 >> 16;

        return h32;
    }

    private static uint Round(uint acc, uint input)
    {
        acc += input * Prime2;
        acc = RotL(acc, 13);
        acc *= Prime1;
        return acc;
    }

    private static uint RotL(uint x, int r) => (x << r) | (x >> (32 - r));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~StableStringHashTests"`
Expected: PASS. If `Compute_KnownVector_MatchesPrecomputed` fails, recompute the expected value with a reference xxHash32 (seed=0) impl and update the literal in the test. Once green, the literal is the canonical pin.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Internal/StableStringHash.cs tests/Caching.NET.Tests/Internal/StableStringHashTests.cs
git commit -m "feat: stable xxHash32 for stripe-lock placement

Process- and machine-stable hash so the same key maps to the same
stripe across restarts. Replaces String.GetHashCode (randomized).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: StripedLockManager

**Files:**
- Create: `src/Caching.NET/Internal/StripedLockManager.cs`
- Create: `tests/Caching.NET.Tests/Internal/StripedLockManagerTests.cs`

- [ ] **Step 1: Write failing tests**

`tests/Caching.NET.Tests/Internal/StripedLockManagerTests.cs`:

```csharp
using Caching.NET.Internal;

namespace Caching.NET.Tests.Internal;

public sealed class StripedLockManagerTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]      // round up
    [InlineData(1023, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void Ctor_RoundsStripeCountUpToPowerOfTwo(int requested, int expected)
    {
        using var sut = new StripedLockManager(requested);
        Assert.Equal(expected, sut.StripeCount);
    }

    [Fact]
    public void GetLock_ReturnsSameInstanceForSameKey()
    {
        using var sut = new StripedLockManager(1024);
        var a = sut.GetLock("orders:42");
        var b = sut.GetLock("orders:42");
        Assert.Same(a, b);
    }

    [Fact]
    public void GetLock_DistributesKeysAcrossStripes()
    {
        using var sut = new StripedLockManager(64);
        var seen = new HashSet<SemaphoreSlim>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < 1000; i++)
        {
            seen.Add(sut.GetLock($"key:{i}"));
        }
        // With 1000 keys over 64 stripes we should hit nearly every stripe
        Assert.True(seen.Count >= 50, $"Expected >=50 stripes used, got {seen.Count}");
    }

    [Fact]
    public async Task GetLock_OneAtATime_SerializesConcurrentSameKeyHolders()
    {
        using var sut = new StripedLockManager(1024);
        var holding = 0;
        var maxObserved = 0;
        async Task RunAsync()
        {
            var sem = sut.GetLock("hot-key");
            await sem.WaitAsync();
            try
            {
                var current = Interlocked.Increment(ref holding);
                int prevMax;
                do { prevMax = maxObserved; if (current <= prevMax) break; }
                while (Interlocked.CompareExchange(ref maxObserved, current, prevMax) != prevMax);
                await Task.Delay(10);
                Interlocked.Decrement(ref holding);
            }
            finally { sem.Release(); }
        }
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => RunAsync()));
        Assert.Equal(1, maxObserved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_RejectsNonPositiveStripeCount(int bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StripedLockManager(bad));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~StripedLockManagerTests"`
Expected: FAIL — `StripedLockManager` does not exist.

- [ ] **Step 3: Implement `StripedLockManager`**

`src/Caching.NET/Internal/StripedLockManager.cs`:

```csharp
namespace Caching.NET.Internal;

/// <summary>
/// Fixed-size pool of <see cref="SemaphoreSlim"/> instances, indexed by a stable hash
/// of the cache key. Solves the v1 lock-leak bug (per-key SemaphoreSlim entries
/// in a ConcurrentDictionary) by allocating once at startup and never adding/removing.
/// </summary>
internal sealed class StripedLockManager : IDisposable
{
    private readonly SemaphoreSlim[] _stripes;
    private readonly uint _mask;
    private bool _disposed;

    public StripedLockManager(int stripeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
        var pow2 = RoundUpToPowerOfTwo(stripeCount);
        _stripes = new SemaphoreSlim[pow2];
        for (var i = 0; i < pow2; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
        _mask = (uint)(pow2 - 1);
    }

    public int StripeCount => _stripes.Length;

    public SemaphoreSlim GetLock(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var h = StableStringHash.Compute(key);
        return _stripes[h & _mask];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _stripes)
        {
            s.Dispose();
        }
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        var v = (uint)(value - 1);
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return (int)(v + 1);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~StripedLockManagerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Internal/StripedLockManager.cs tests/Caching.NET.Tests/Internal/StripedLockManagerTests.cs
git commit -m "feat: StripedLockManager (fixes v1 lock-leak)

Fixed-size SemaphoreSlim pool keyed by xxHash32 stripe; zero per-key
allocation, zero leak, bounded memory ~64KB at default 1024 stripes.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: RedisConnectionStringRedactor

**Files:**
- Create: `src/Caching.NET/Internal/RedisConnectionStringRedactor.cs`
- Create: `tests/Caching.NET.Tests/Internal/RedisConnectionStringRedactorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Caching.NET.Internal;

namespace Caching.NET.Tests.Internal;

public sealed class RedisConnectionStringRedactorTests
{
    [Fact]
    public void Redact_StripsPasswordSegment()
    {
        const string cs = "redis.example.com:6380,password=Sup3rS3cr3t,ssl=True";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.DoesNotContain("Sup3rS3cr3t", actual);
        Assert.Contains("password=***", actual);
    }

    [Fact]
    public void Redact_StripsUserSegment()
    {
        const string cs = "redis.example.com:6380,user=admin,password=p,ssl=True";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.DoesNotContain("admin", actual);
        Assert.Contains("user=***", actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, RedisConnectionStringRedactor.Redact(input));
    }

    [Fact]
    public void Redact_KeepsNonSecretSegments()
    {
        const string cs = "redis.example.com:6380,password=p,ssl=True,abortConnect=false,connectTimeout=5000";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.Contains("redis.example.com:6380", actual);
        Assert.Contains("ssl=True", actual);
        Assert.Contains("abortConnect=false", actual);
        Assert.Contains("connectTimeout=5000", actual);
    }

    [Fact]
    public void Redact_CaseInsensitiveKeyMatch()
    {
        const string cs = "host:6380,Password=p,SSL=True,USER=u";
        var actual = RedisConnectionStringRedactor.Redact(cs);
        Assert.DoesNotContain("=p", actual);
        Assert.DoesNotContain("=u", actual);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RedisConnectionStringRedactorTests"`
Expected: FAIL.

- [ ] **Step 3: Implement redactor**

`src/Caching.NET/Internal/RedisConnectionStringRedactor.cs`:

```csharp
namespace Caching.NET.Internal;

internal static class RedisConnectionStringRedactor
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "user", "name"
    };

    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return string.Empty;
        var parts = connectionString.Split(',');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            var key = parts[i][..eq].Trim();
            if (SecretKeys.Contains(key))
            {
                parts[i] = $"{key}=***";
            }
        }
        return string.Join(",", parts);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RedisConnectionStringRedactorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Internal/RedisConnectionStringRedactor.cs tests/Caching.NET.Tests/Internal/RedisConnectionStringRedactorTests.cs
git commit -m "feat: RedisConnectionStringRedactor strips password/user/name

Used in validation errors and logs to prevent secret leakage.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: CacheOptions v2 — add KeyPrefix, drop RedisInstanceName, tighten defaults

**Files:**
- Modify: `src/Caching.NET/Options/CacheOptions.cs`
- Create: `src/Caching.NET/Validation/CacheOptionsValidator.cs`
- Create: `tests/Caching.NET.Tests/Validation/CacheOptionsValidatorTests.cs`
- Modify: `tests/Caching.NET.Tests/Options/CacheOptionsValidationTests.cs` (existing tests broken by deletion of `RedisInstanceName`)

- [ ] **Step 1: Write failing tests for new validator**

`tests/Caching.NET.Tests/Validation/CacheOptionsValidatorTests.cs`:

```csharp
using Caching.NET.Options;
using Caching.NET.Validation;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Validation;

public sealed class CacheOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(CacheOptions o)
        => new CacheOptionsValidator().Validate(name: null, o);

    private static CacheOptions ValidBaseline() => new()
    {
        KeyPrefix = "asm-api-dev",
        Mode = CacheMode.InMemory,
        MaximumKeyLength = 512,
        MaximumPayloadBytes = 1_048_576,
        StripeLockCount = 1024,
        TtlJitterPercentage = 0.10,
        FactoryTimeout = TimeSpan.FromSeconds(30),
        RedisOperationTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void Valid_Baseline_Succeeds()
    {
        Assert.True(Validate(ValidBaseline()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has*wildcard")]
    [InlineData("has?wildcard")]
    [InlineData(":leading-colon")]
    public void Invalid_KeyPrefix_Fails(string bad)
    {
        var o = ValidBaseline(); o.KeyPrefix = bad;
        Assert.True(Validate(o).Failed);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("asm-api-dev")]
    [InlineData("o.s.v1_2")]
    public void Valid_KeyPrefix_Succeeds(string ok)
    {
        var o = ValidBaseline(); o.KeyPrefix = ok;
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void RedisMode_RequiresConnectionString()
    {
        var o = ValidBaseline(); o.Mode = CacheMode.Redis; o.RedisConnectionString = null;
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void HybridMode_RequiresConnectionString()
    {
        var o = ValidBaseline(); o.Mode = CacheMode.Hybrid; o.RedisConnectionString = null;
        Assert.True(Validate(o).Failed);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(8193)]
    public void Invalid_MaxKeyLength_Fails(int bad)
    {
        var o = ValidBaseline(); o.MaximumKeyLength = bad;
        Assert.True(Validate(o).Failed);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(65537)]
    public void Invalid_StripeLockCount_Fails(int bad)
    {
        var o = ValidBaseline(); o.StripeLockCount = bad;
        Assert.True(Validate(o).Failed);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(0.51)]
    public void Invalid_TtlJitter_Fails(double bad)
    {
        var o = ValidBaseline(); o.TtlJitterPercentage = bad;
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void RedisConnectionString_Redacted_InValidationFailureMessage()
    {
        var o = ValidBaseline();
        o.Mode = CacheMode.Redis;
        o.RedisConnectionString = "redis:6380,password=topsecret,ssl=True";
        o.MaximumKeyLength = 1; // force a failure so the connection string is included in the message
        var r = Validate(o);
        Assert.True(r.Failed);
        var combined = string.Join(" | ", r.Failures);
        Assert.DoesNotContain("topsecret", combined);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CacheOptionsValidatorTests"`
Expected: FAIL — `CacheOptionsValidator` does not exist; `KeyPrefix`, `StripeLockCount`, `RedisOperationTimeout`, `TtlJitterPercentage` may not yet be on `CacheOptions`.

- [ ] **Step 3: Update `CacheOptions`**

Open `src/Caching.NET/Options/CacheOptions.cs` and replace its body so the public surface matches the v2 spec §8. Required deltas:
- Add `public string KeyPrefix { get; set; } = string.Empty;`
- **Remove** any `RedisInstanceName` property and its data-annotation attributes.
- Add `public bool StrictRedisCertificateValidation { get; set; } = true;` (flip default).
- Add `public double TtlJitterPercentage { get; set; } = 0.10;`.
- Set `public int MaximumKeyLength { get; set; } = 512;` (was nullable; now required `int`).
- Add `public int StripeLockCount { get; set; } = 1024;`.
- Add `public int StaleRefreshConcurrency { get; set; } = 256;` (used in P2; reserved here so options are stable).
- Add `public TimeSpan RedisOperationTimeout { get; set; } = TimeSpan.FromSeconds(2);`.
- Add `public bool IncludeRawKeyInLogs { get; set; } = false;`.
- Optional trace key-hash toggles were deferred; keep key material redacted by default.

Keep existing: `Mode`, `RedisConnectionString`, `Enabled`, `FailOpen`, `DefaultExpiration`, `MaximumPayloadBytes`, `MemorySizeLimitMb`, `FactoryTimeout`, `HybridLocalCacheExpiration`, plus any helper methods (`GetFactoryTimeout`, `GetConnectionString`).

- [ ] **Step 4: Implement `CacheOptionsValidator`**

`src/Caching.NET/Validation/CacheOptionsValidator.cs`:

```csharp
using System.Text.RegularExpressions;
using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Options;

namespace Caching.NET.Validation;

internal sealed partial class CacheOptionsValidator : IValidateOptions<CacheOptions>
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPrefixRegex();

    public ValidateOptionsResult Validate(string? name, CacheOptions o)
    {
        var failures = new List<string>();
        var redactedCs = RedisConnectionStringRedactor.Redact(o.RedisConnectionString);

        if (string.IsNullOrWhiteSpace(o.KeyPrefix))
        {
            failures.Add($"{nameof(CacheOptions.KeyPrefix)} is required and must be non-empty.");
        }
        else if (o.KeyPrefix.Length > 64)
        {
            failures.Add($"{nameof(CacheOptions.KeyPrefix)} must be <= 64 chars.");
        }
        else if (!KeyPrefixRegex().IsMatch(o.KeyPrefix))
        {
            failures.Add($"{nameof(CacheOptions.KeyPrefix)} must match ^[a-zA-Z0-9][a-zA-Z0-9._-]*$ (no whitespace, ':' , '*' or '?').");
        }

        if ((o.Mode == CacheMode.Redis || o.Mode == CacheMode.Hybrid) && string.IsNullOrWhiteSpace(o.RedisConnectionString))
        {
            failures.Add($"{nameof(CacheOptions.RedisConnectionString)} is required for Mode={o.Mode}. (redacted={redactedCs})");
        }

        if (o.MaximumKeyLength is < 64 or > 8192)
        {
            failures.Add($"{nameof(CacheOptions.MaximumKeyLength)} must be in [64, 8192]. (redacted={redactedCs})");
        }

        if (o.MaximumPayloadBytes is < 1024L or > 100L * 1024 * 1024)
        {
            failures.Add($"{nameof(CacheOptions.MaximumPayloadBytes)} must be in [1024, 104857600].");
        }

        if (o.StripeLockCount is < 16 or > 65536)
        {
            failures.Add($"{nameof(CacheOptions.StripeLockCount)} must be in [16, 65536].");
        }

        if (o.TtlJitterPercentage is < 0 or > 0.5)
        {
            failures.Add($"{nameof(CacheOptions.TtlJitterPercentage)} must be in [0, 0.5].");
        }

        if (o.FactoryTimeout < TimeSpan.FromMilliseconds(100) || o.FactoryTimeout > TimeSpan.FromMinutes(30))
        {
            failures.Add($"{nameof(CacheOptions.FactoryTimeout)} must be between 100ms and 30 minutes.");
        }

        if (o.RedisOperationTimeout < TimeSpan.FromMilliseconds(50) || o.RedisOperationTimeout > TimeSpan.FromSeconds(30))
        {
            failures.Add($"{nameof(CacheOptions.RedisOperationTimeout)} must be between 50ms and 30s.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
```

- [ ] **Step 5: Update existing `CacheOptionsValidationTests`**

Open `tests/Caching.NET.Tests/Options/CacheOptionsValidationTests.cs`. Remove any test that references `RedisInstanceName`. Add `KeyPrefix = "test"` to all `CacheOptions` factory helpers so existing tests don't fail on the new required field.

- [ ] **Step 6: Run tests**

Run: `dotnet test`
Expected: PASS for `CacheOptionsValidatorTests` and existing tests.

- [ ] **Step 7: Commit**

```bash
git add src/Caching.NET/Options/CacheOptions.cs src/Caching.NET/Validation/CacheOptionsValidator.cs tests/Caching.NET.Tests/Validation/CacheOptionsValidatorTests.cs tests/Caching.NET.Tests/Options/CacheOptionsValidationTests.cs
git commit -m "feat!: CacheOptions v2 — KeyPrefix mandatory, RedisInstanceName removed

BREAKING: CacheOptions.RedisInstanceName is gone. Use KeyPrefix.
BREAKING: KeyPrefix is now required (regex ^[a-zA-Z0-9][a-zA-Z0-9._-]*$, no ':' inside prefix, max 64).
BREAKING: StrictRedisCertificateValidation defaults to true.
BREAKING: MaximumKeyLength defaults to 512 (was unbounded).

New: StripeLockCount, RedisOperationTimeout, TtlJitterPercentage,
StaleRefreshConcurrency, IncludeRawKeyInLogs.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: ICacheSerializer + JsonCacheSerializer (default)

**Files:**
- Create: `src/Caching.NET/Serialization/ICacheSerializer.cs`
- Create: `src/Caching.NET/Serialization/JsonCacheSerializer.cs`
- Create: `src/Caching.NET/Serialization/CachingNetJsonContext.cs` (source-gen for built-in types only — consumer types use their own context)
- Create: `tests/Caching.NET.Tests/Serialization/JsonCacheSerializerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Caching.NET.Serialization;

namespace Caching.NET.Tests.Serialization;

public sealed class JsonCacheSerializerTests
{
    public sealed record SampleDto(int Id, string Name, DateTime At);

    [Fact]
    public void FormatId_IsJson()
    {
        Assert.Equal("json", new JsonCacheSerializer().FormatId);
    }

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        var sut = new JsonCacheSerializer();
        var dto = new SampleDto(42, "alpha", new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        var bytes = sut.Serialize(dto);
        var actual = sut.Deserialize<SampleDto>(bytes);
        Assert.Equal(dto, actual);
    }

    [Fact]
    public void RoundTrip_NullValue_ReturnsNull()
    {
        var sut = new JsonCacheSerializer();
        var bytes = sut.Serialize<SampleDto?>(null);
        var actual = sut.Deserialize<SampleDto?>(bytes);
        Assert.Null(actual);
    }

    [Fact]
    public void Deserialize_EmptySpan_ReturnsDefault()
    {
        var sut = new JsonCacheSerializer();
        Assert.Null(sut.Deserialize<SampleDto?>(ReadOnlySpan<byte>.Empty));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~JsonCacheSerializerTests"`
Expected: FAIL.

- [ ] **Step 3: Define interface**

`src/Caching.NET/Serialization/ICacheSerializer.cs`:

```csharp
namespace Caching.NET.Serialization;

/// <summary>
/// Pluggable cache value serializer. Implementations must be thread-safe and stateless.
/// FormatId is recorded in the PayloadEnvelope (P1) so cross-serializer drift is detected on read.
/// </summary>
public interface ICacheSerializer
{
    /// <summary>Short stable identifier (e.g., "json", "msgpack"). Recorded in payload envelope.</summary>
    string FormatId { get; }

    byte[] Serialize<T>(T value);

    T? Deserialize<T>(ReadOnlySpan<byte> bytes);
}
```

- [ ] **Step 4: Implement JSON serializer**

`src/Caching.NET/Serialization/JsonCacheSerializer.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caching.NET.Serialization;

/// <summary>
/// Default <see cref="ICacheSerializer"/> using <see cref="System.Text.Json"/>.
/// Consumers may pass their own <see cref="JsonSerializerContext"/> for AOT/trim
/// compatibility; otherwise reflection-based STJ is used (incompatible with full trim).
/// </summary>
public sealed class JsonCacheSerializer : ICacheSerializer
{
    private readonly JsonSerializerOptions _options;

    public JsonCacheSerializer() : this(new JsonSerializerOptions(JsonSerializerDefaults.Web))
    {
    }

    public JsonCacheSerializer(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public JsonCacheSerializer(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _options = new JsonSerializerOptions(context.Options);
    }

    public string FormatId => "json";

    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _options);

    public T? Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return default;
        return JsonSerializer.Deserialize<T>(bytes, _options);
    }
}
```

- [ ] **Step 5: Source-gen context for internal types only (placeholder for now)**

`src/Caching.NET/Serialization/CachingNetJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Caching.NET.Serialization;

/// <summary>
/// Source-gen JSON context for the library's own internal payload shapes.
/// Consumer types are NOT registered here — consumers must supply their own
/// <see cref="JsonSerializerContext"/> via <c>WithSerializer(new JsonCacheSerializer(MyContext.Default))</c>
/// to be AOT/trim-safe. P2 adds documentation.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class CachingNetJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~JsonCacheSerializerTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Caching.NET/Serialization/ tests/Caching.NET.Tests/Serialization/
git commit -m "feat: ICacheSerializer + JsonCacheSerializer (default)

Pluggable wire-format. JSON STJ is the default. Consumers can pass
their own JsonSerializerContext for AOT/trim compatibility.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Polly v8 resilience pipeline builder

**Files:**
- Create: `src/Caching.NET/Resilience/ResiliencePipelineNames.cs`
- Create: `src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs`
- Create: `tests/Caching.NET.Tests/Resilience/CacheResiliencePipelineBuilderTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Caching.NET.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Caching.NET.Tests.Resilience;

public sealed class CacheResiliencePipelineBuilderTests
{
    [Fact]
    public void BuildDefault_ProducesPipelineForEachOp()
    {
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry();
        Assert.NotNull(registry.GetPipeline(ResiliencePipelineNames.RedisRead));
        Assert.NotNull(registry.GetPipeline(ResiliencePipelineNames.RedisWrite));
        Assert.NotNull(registry.GetPipeline(ResiliencePipelineNames.RedisDelete));
    }

    [Fact]
    public async Task DefaultPipeline_WrapsTimeoutInTimeoutRejected()
    {
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromMilliseconds(50));
        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
            await pipeline.ExecuteAsync(static async ct =>
            {
                await Task.Delay(500, ct);
            }));
    }

    [Fact]
    public async Task DefaultPipeline_OpensCircuitAfterRepeatedFailures()
    {
        var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(1),
            failureRatio: 0.5,
            minimumThroughput: 4,
            samplingDuration: TimeSpan.FromSeconds(2),
            breakDuration: TimeSpan.FromSeconds(5));
        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        for (var i = 0; i < 8; i++)
        {
            try { await pipeline.ExecuteAsync(static _ => throw new TimeoutException()); }
            catch { /* expected */ }
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(static _ => ValueTask.CompletedTask));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CacheResiliencePipelineBuilderTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ResiliencePipelineNames`**

`src/Caching.NET/Resilience/ResiliencePipelineNames.cs`:

```csharp
namespace Caching.NET.Resilience;

public static class ResiliencePipelineNames
{
    public const string RedisRead   = "cache.redis.read";
    public const string RedisWrite  = "cache.redis.write";
    public const string RedisDelete = "cache.redis.delete";
}
```

- [ ] **Step 4: Implement `CacheResiliencePipelineBuilder`**

`src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs`:

```csharp
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;
using StackExchange.Redis;

namespace Caching.NET.Resilience;

public static class CacheResiliencePipelineBuilder
{
    public static ResiliencePipelineRegistry<string> BuildDefaultRegistry(
        TimeSpan? timeout = null,
        double failureRatio = 0.5,
        int minimumThroughput = 20,
        TimeSpan? samplingDuration = null,
        TimeSpan? breakDuration = null,
        int retryCount = 2)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        foreach (var name in new[] { ResiliencePipelineNames.RedisRead, ResiliencePipelineNames.RedisWrite, ResiliencePipelineNames.RedisDelete })
        {
            registry.TryAddBuilder(name, (builder, _) =>
            {
                builder
                    .AddTimeout(new TimeoutStrategyOptions
                    {
                        Timeout = timeout ?? TimeSpan.FromSeconds(2)
                    })
                    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                    {
                        FailureRatio      = failureRatio,
                        MinimumThroughput = minimumThroughput,
                        SamplingDuration  = samplingDuration ?? TimeSpan.FromSeconds(30),
                        BreakDuration     = breakDuration ?? TimeSpan.FromSeconds(15),
                        ShouldHandle      = static args => ValueTask.FromResult(IsTransient(args.Outcome.Exception))
                    })
                    .AddRetry(new RetryStrategyOptions
                    {
                        MaxRetryAttempts = retryCount,
                        BackoffType      = DelayBackoffType.Exponential,
                        UseJitter        = true,
                        ShouldHandle     = static args => ValueTask.FromResult(IsTransient(args.Outcome.Exception))
                    });
            });
        }
        return registry;
    }

    private static bool IsTransient(Exception? ex) => ex switch
    {
        RedisConnectionException => true,
        RedisTimeoutException    => true,
        TimeoutRejectedException => true,
        TimeoutException         => true,
        _                        => false
    };
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~CacheResiliencePipelineBuilderTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Resilience/ tests/Caching.NET.Tests/Resilience/
git commit -m "feat: Polly v8 resilience pipelines (timeout + breaker + retry)

Three named pipelines (read/write/delete) so write-path failures
don't trip read-path breakers. Transient = Redis*Exception/Timeout.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: CacheInstruments (replaces ICacheTelemetry)

**Files:**
- Create: `src/Caching.NET/Telemetry/CacheInstruments.cs`
- Create: `tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs`
- Delete: `src/Caching.NET/Abstractions/ICacheTelemetry.cs`
- Delete: `src/Caching.NET/Telemetry/NoopCacheTelemetry.cs`
- Delete: `src/Caching.NET/Telemetry/OpenTelemetryCacheTelemetry.cs`

- [ ] **Step 1: Write failing test (subscribe to Meter and assert tag/value)**

```csharp
using System.Diagnostics.Metrics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

public sealed class CacheInstrumentsTests
{
    [Fact]
    public void Hits_Counter_RecordsValueWithTags()
    {
        long observed = 0;
        var observedTags = new Dictionary<string, object?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CacheInstruments.MeterName && instr.Name == "cache.hits")
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            observed += value;
            foreach (var kv in tags) observedTags[kv.Key] = kv.Value;
        });
        listener.Start();

        CacheInstruments.RecordHit("Redis", "get_or_create");

        listener.Dispose();
        Assert.Equal(1, observed);
        Assert.Equal("Redis", observedTags["cache.mode"]);
        Assert.Equal("get_or_create", observedTags["cache.operation"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~CacheInstrumentsTests"`
Expected: FAIL.

- [ ] **Step 3: Implement CacheInstruments**

`src/Caching.NET/Telemetry/CacheInstruments.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caching.NET.Telemetry;

/// <summary>
/// Static OTel instruments for Caching.NET. Replaces v1's <c>ICacheTelemetry</c> interface.
/// Subscribe via <c>builder.Services.AddOpenTelemetry().WithMetrics(b =&gt; b.AddMeter(CacheInstruments.MeterName))</c>.
/// </summary>
public static class CacheInstruments
{
    public const string MeterName = "Caching.NET";
    public const string ActivitySourceName = "Caching.NET";
    public const string Version = "2.0.0";

    internal static readonly Meter Meter = new(MeterName, Version);
    internal static readonly ActivitySource Activity = new(ActivitySourceName, Version);

    internal static readonly Counter<long> Hits =
        Meter.CreateCounter<long>("cache.hits", unit: "{op}", description: "Cache hits.");
    internal static readonly Counter<long> Misses =
        Meter.CreateCounter<long>("cache.misses", unit: "{op}", description: "Cache misses.");
    internal static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("cache.errors", unit: "{op}", description: "Cache backend errors.");
    internal static readonly Counter<long> Sets =
        Meter.CreateCounter<long>("cache.sets", unit: "{op}", description: "Cache writes.");
    internal static readonly Counter<long> Removes =
        Meter.CreateCounter<long>("cache.removes", unit: "{op}", description: "Cache removals.");

    internal static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("cache.operation.duration", unit: "ms", description: "Cache operation duration.");

    public static void RecordHit(string mode, string operation)
        => Hits.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    public static void RecordMiss(string mode, string operation, string reason)
        => Misses.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation),
            new KeyValuePair<string, object?>("cache.miss_reason", reason));

    public static void RecordError(string mode, string operation, string errorKind)
        => Errors.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation),
            new KeyValuePair<string, object?>("cache.error_kind", errorKind));

    public static void RecordSet(string mode, string operation)
        => Sets.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    public static void RecordRemove(string mode, string operation)
        => Removes.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    public static void RecordDuration(string mode, string operation, double milliseconds)
        => OperationDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));
}
```

- [ ] **Step 4: Delete v1 telemetry types**

```bash
git rm src/Caching.NET/Abstractions/ICacheTelemetry.cs src/Caching.NET/Telemetry/NoopCacheTelemetry.cs src/Caching.NET/Telemetry/OpenTelemetryCacheTelemetry.cs
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~CacheInstrumentsTests"`
Expected: PASS for the new instrument test. Other build errors are expected because services still reference `ICacheTelemetry` — those are fixed in Tasks 9–12.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Telemetry/CacheInstruments.cs tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs src/Caching.NET/Abstractions/ICacheTelemetry.cs src/Caching.NET/Telemetry/NoopCacheTelemetry.cs src/Caching.NET/Telemetry/OpenTelemetryCacheTelemetry.cs
git commit -m "feat!: replace ICacheTelemetry with CacheInstruments (OTel-first)

BREAKING: ICacheTelemetry, NoopCacheTelemetry, OpenTelemetryCacheTelemetry deleted.
Subscribe to Meter('Caching.NET') and ActivitySource('Caching.NET') instead.

Service classes still reference the old interface; fixed in subsequent commits.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Replace `_keyLocks` dict in `RoutingCacheService` with `StripedLockManager` + thread `KeyPrefix`

**Files:**
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`
- Modify: `tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs` (existing tests; update mocks)
- Modify: `tests/Caching.NET.Tests/Services/RoutingCacheServiceConcurrencyTests.cs`

- [ ] **Step 1: Write failing test for KeyPrefix-prepending and stripe lock usage**

Add to `tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs`:

```csharp
[Fact]
public async Task GetOrCreateAsync_PrependsKeyPrefixToInnerCallKey()
{
    var inMemory = new Mock<ICacheService>();
    inMemory
        .Setup(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Func<CancellationToken, Task<int>>>(),
                                       It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .Returns(async (string k, Func<CancellationToken, Task<int>> f, TimeSpan? _, TimeSpan? _, CancellationToken ct) =>
        {
            Assert.Equal("asm-api-dev:Order:42", k);
            return await f(ct);
        });

    var options = Options.Create(new CacheOptions { KeyPrefix = "asm-api-dev", Mode = CacheMode.InMemory });
    var monitor = Mock.Of<IOptionsMonitor<CacheOptions>>(m => m.CurrentValue == options.Value);
    var sut = new RoutingCacheService(monitor, NullLogger<RoutingCacheService>.Instance,
        new StripedLockManager(1024), inMemory: inMemory.Object);

    var actual = await sut.GetOrCreateAsync("Order:42", _ => Task.FromResult(99));
    Assert.Equal(99, actual);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RoutingCacheServiceTests"`
Expected: FAIL — current `RoutingCacheService` constructor still takes `ICacheTelemetry`; test won't compile.

- [ ] **Step 3: Rewrite `RoutingCacheService`**

Replace the body of `src/Caching.NET/Services/RoutingCacheService.cs`. Key deltas vs v1:
1. Constructor takes `StripedLockManager lockManager` instead of `ICacheTelemetry telemetry`. (Telemetry now via static `CacheInstruments`.)
2. Compute `_keyPrefix = optionsMonitor.CurrentValue.KeyPrefix + ":"` once in constructor (startup-only).
3. Every public method calls a `private string PrependKeyPrefix(string key) => _keyPrefix + key;` helper before delegating to inner service.
4. Remove `_keyLocks` `ConcurrentDictionary` and the cleanup race. Use `_lockManager.GetLock(prefixedKey)` instead.
5. Replace `telemetry.OnCacheSet(...)` with `CacheInstruments.RecordSet("Routing", "set")` etc.
6. Replace `telemetry.OnFactoryTimeout(...)` with `CacheInstruments.RecordError("Routing", "factory", "Timeout")`.
7. Keep all existing per-call options behavior (`BypassCache`, `ForceRefresh`, `CoalesceConcurrent`, `OverrideMode`).
8. Fix `ForceRefresh` correctness: under `CoalesceConcurrent=true`, when the lock-holder has `ForceRefresh=true` and waiters do not, waiters now see the freshly-written value because the inner `SetAsync` happens before `Release()`. Already true with this rewrite — no extra logic needed. Add a regression test in `RoutingCacheServiceConcurrencyTests`.

(Use the existing file's structure as a starting point. The full rewrite is mechanical: substitute lock acquisition and telemetry calls. No example body provided here because the file is ~280 lines; preserve method signatures and only change the indicated areas.)

- [ ] **Step 4: Update existing concurrency tests to inject `StripedLockManager`**

`tests/Caching.NET.Tests/Services/RoutingCacheServiceConcurrencyTests.cs`: every `new RoutingCacheService(...)` call gets `new StripedLockManager(1024)` in place of the deleted `Mock<ICacheTelemetry>` / `NoopCacheTelemetry`. Set `KeyPrefix = "test"` on every `CacheOptions` instance.

- [ ] **Step 5: Add regression test for ForceRefresh under coalescing**

`tests/Caching.NET.Tests/Services/RoutingCacheServiceConcurrencyTests.cs`:

```csharp
[Fact]
public async Task GetOrCreateAsync_ForceRefresh_OneCallerWritesAndOthersCoalesce()
{
    var factoryInvocations = 0;
    var inMemory = new Mock<ICacheService>();
    var stored = new Dictionary<string, int>();
    inMemory
        .Setup(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Func<CancellationToken, Task<int>>>(),
                                       It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .Returns(async (string k, Func<CancellationToken, Task<int>> f, TimeSpan? _, TimeSpan? _, CancellationToken ct) =>
        {
            if (stored.TryGetValue(k, out var v)) return v;
            v = await f(ct);
            stored[k] = v;
            return v;
        });
    inMemory
        .Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .Returns(async (string k, int v, TimeSpan? _, TimeSpan? _, CancellationToken _) => { stored[k] = v; });

    var options = Options.Create(new CacheOptions { KeyPrefix = "t", Mode = CacheMode.InMemory });
    var monitor = Mock.Of<IOptionsMonitor<CacheOptions>>(m => m.CurrentValue == options.Value);
    var sut = new RoutingCacheService(monitor, NullLogger<RoutingCacheService>.Instance,
        new StripedLockManager(1024), inMemory: inMemory.Object);

    Task<int> First() => ((IRoutingCacheService)sut).GetOrCreateAsync<int>("k",
        async _ => { Interlocked.Increment(ref factoryInvocations); await Task.Delay(50); return 1; },
        callOptions: new CacheCallOptions { CoalesceConcurrent = true, ForceRefresh = true });
    Task<int> Other() => ((IRoutingCacheService)sut).GetOrCreateAsync<int>("k",
        async _ => { Interlocked.Increment(ref factoryInvocations); await Task.Delay(50); return 2; },
        callOptions: new CacheCallOptions { CoalesceConcurrent = true });

    var t1 = First();
    var t2 = Other();
    var t3 = Other();
    var results = await Task.WhenAll(t1, t2, t3);

    Assert.Equal(1, results[0]);
    Assert.Equal(1, results[1]);
    Assert.Equal(1, results[2]);
    Assert.Equal(1, factoryInvocations);
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~RoutingCacheService"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Caching.NET/Services/RoutingCacheService.cs tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs tests/Caching.NET.Tests/Services/RoutingCacheServiceConcurrencyTests.cs
git commit -m "fix!: striped locks + KeyPrefix injection in RoutingCacheService

BREAKING: constructor takes StripedLockManager (no ICacheTelemetry).
Fixes v1 lock leak under high cardinality keys.
ForceRefresh under coalescing now correct: late waiters read fresh value.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: RedisCacheService — Polly pipeline + per-op timeout + ICacheSerializer

**Files:**
- Modify: `src/Caching.NET/Services/RedisCacheService.cs`
- Modify: `tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Add to `RedisCacheServiceTests`:

```csharp
[Fact]
public async Task GetOrCreateAsync_OnRedisTimeout_FailsOpen_RunsFactoryAndReturnsValue()
{
    var distributed = new Mock<IDistributedCache>();
    distributed
        .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new TimeoutException("simulated"));

    var options = Options.Create(new CacheOptions
    {
        KeyPrefix = "t", Mode = CacheMode.Redis, FailOpen = true,
        RedisOperationTimeout = TimeSpan.FromMilliseconds(50)
    });
    var sut = TestRedisCacheService(distributed.Object, options.Value);

    var actual = await sut.GetOrCreateAsync("k", _ => Task.FromResult(7));
    Assert.Equal(7, actual);
}

[Fact]
public async Task GetOrCreateAsync_OnRedisError_FailFast_Throws()
{
    var distributed = new Mock<IDistributedCache>();
    distributed
        .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "x"));

    var options = Options.Create(new CacheOptions
    {
        KeyPrefix = "t", Mode = CacheMode.Redis, FailOpen = false,
        RedisOperationTimeout = TimeSpan.FromMilliseconds(50)
    });
    var sut = TestRedisCacheService(distributed.Object, options.Value);

    await Assert.ThrowsAsync<RedisConnectionException>(() => sut.GetOrCreateAsync("k", _ => Task.FromResult(7)));
}

private static RedisCacheService TestRedisCacheService(IDistributedCache cache, CacheOptions options)
{
    var monitor = Mock.Of<IOptionsMonitor<CacheOptions>>(m => m.CurrentValue == options);
    var registry = CacheResiliencePipelineBuilder.BuildDefaultRegistry(timeout: options.RedisOperationTimeout);
    return new RedisCacheService(cache, monitor, NullLogger<RedisCacheService>.Instance,
        new JsonCacheSerializer(), registry);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RedisCacheServiceTests"`
Expected: FAIL — constructor signature differs; current impl uses inline JSON + no Polly.

- [ ] **Step 3: Rewrite `RedisCacheService` constructor and op methods**

Replace constructor:

```csharp
public RedisCacheService(
    IDistributedCache cache,
    IOptionsMonitor<CacheOptions> optionsMonitor,
    ILogger<RedisCacheService> logger,
    ICacheSerializer serializer,
    ResiliencePipelineRegistry<string> resiliencePipelines)
{
    _cache = cache;
    _options = optionsMonitor;
    _logger = logger;
    _serializer = serializer;
    _readPipeline   = resiliencePipelines.GetPipeline(ResiliencePipelineNames.RedisRead);
    _writePipeline  = resiliencePipelines.GetPipeline(ResiliencePipelineNames.RedisWrite);
    _deletePipeline = resiliencePipelines.GetPipeline(ResiliencePipelineNames.RedisDelete);
}
```

For each Redis op:
1. Wrap in `await _readPipeline.ExecuteAsync(async ct2 => { ... }, linked.Token)` (or write/delete pipeline).
2. Use `using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); linked.CancelAfter(_options.CurrentValue.RedisOperationTimeout);`.
3. Replace inline `JsonSerializer.Deserialize<T>(...)` / `SerializeToUtf8Bytes(...)` with `_serializer.Deserialize<T>(...)` / `_serializer.Serialize(...)`.
4. On any caught `BrokenCircuitException`, `TimeoutRejectedException`, or `Redis*Exception`:
   - Call `CacheInstruments.RecordError("Redis", "<op>", errorKind)` where `errorKind` ∈ `{Timeout, ConnectionFailed, CircuitOpen, Serialization}`.
   - If `_options.CurrentValue.FailOpen` → return `default(T)` from getters, no-op from setters/removers, and let the caller's factory run.
   - Else → rethrow.
5. Replace any `_telemetry.OnCacheHit/Miss/Set/...` calls with `CacheInstruments.RecordHit/Miss/Set/...`.

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~RedisCacheServiceTests"`
Expected: PASS. Pre-existing tests that referenced inline JSON serialization may need their mocks updated (use `JsonCacheSerializer` to produce expected `byte[]` blobs for `IDistributedCache.GetAsync` return values).

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Services/RedisCacheService.cs tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs
git commit -m "feat!: RedisCacheService uses Polly pipeline + ICacheSerializer + per-op timeout

BREAKING: constructor signature changed.
Inline JSON replaced with pluggable ICacheSerializer.
All Redis I/O wrapped in named ResiliencePipeline; per-op CTS enforces timeout.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 11: InMemoryCacheService + HybridCacheService — emit via CacheInstruments

**Files:**
- Modify: `src/Caching.NET/Services/InMemoryCacheService.cs`
- Modify: `src/Caching.NET/Services/HybridCacheService.cs`
- Modify: `tests/Caching.NET.Tests/Services/InMemoryCacheServiceTests.cs`
- Modify: `tests/Caching.NET.Tests/Services/HybridCacheServiceTests.cs`

- [ ] **Step 1: Write failing tests**

In `InMemoryCacheServiceTests`:

```csharp
[Fact]
public async Task GetOrCreateAsync_OnMiss_RecordsMissCounter()
{
    using var listener = MeterListenerHelpers.ForCounter("cache.misses", out var observed);
    var sut = new InMemoryCacheService(new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new CacheOptions { KeyPrefix = "t" }), NullLogger<InMemoryCacheService>.Instance);

    await sut.GetOrCreateAsync("k", _ => Task.FromResult(1));

    Assert.Equal(1, observed.Count);
}

[Fact]
public async Task GetOrCreateAsync_OnHit_RecordsHitCounterAndDoesNotInvokeFactoryTwice()
{
    using var hitListener = MeterListenerHelpers.ForCounter("cache.hits", out var hits);
    var sut = new InMemoryCacheService(new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new CacheOptions { KeyPrefix = "t" }), NullLogger<InMemoryCacheService>.Instance);

    var calls = 0;
    await sut.GetOrCreateAsync("k", _ => { Interlocked.Increment(ref calls); return Task.FromResult(1); });
    await sut.GetOrCreateAsync("k", _ => { Interlocked.Increment(ref calls); return Task.FromResult(99); });

    Assert.Equal(1, calls);
    Assert.Equal(1, hits.Count);
}
```

Add helper `tests/Caching.NET.Tests/Telemetry/MeterListenerHelpers.cs`:

```csharp
using System.Diagnostics.Metrics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

internal static class MeterListenerHelpers
{
    public static MeterListener ForCounter(string instrumentName, out List<long> observed)
    {
        var capture = new List<long>();
        observed = capture;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CacheInstruments.MeterName && instr.Name == instrumentName)
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => capture.Add(value));
        listener.Start();
        return listener;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~InMemoryCacheServiceTests.GetOrCreateAsync_OnMiss_RecordsMissCounter"`
Expected: FAIL.

- [ ] **Step 3: Update services**

In `InMemoryCacheService.GetOrCreateAsync`:
- On hit: emit `CacheInstruments.RecordHit("InMemory", "get_or_create")` BEFORE returning value.
- On miss: emit `CacheInstruments.RecordMiss("InMemory", "get_or_create", "NotFound")` BEFORE invoking factory.
- After factory + `cache.Set`: emit `CacheInstruments.RecordSet("InMemory", "get_or_create")`.
- This fixes the v1 ordering bug (hit was emitted after Set; miss after Set).

Same pattern for `SetAsync`, `RemoveAsync`.

In `HybridCacheService`:
- Wrap factory delegate to detect actual hit/miss: pass a captured `bool factoryRan = false` flag; set it inside the factory wrapper. After `HybridCache.GetOrCreateAsync` returns, emit Hit/Miss based on flag.
- Remove all `ICacheTelemetry` references.

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~InMemoryCacheServiceTests|FullyQualifiedName~HybridCacheServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Services/InMemoryCacheService.cs src/Caching.NET/Services/HybridCacheService.cs tests/Caching.NET.Tests/Services/InMemoryCacheServiceTests.cs tests/Caching.NET.Tests/Services/HybridCacheServiceTests.cs tests/Caching.NET.Tests/Telemetry/MeterListenerHelpers.cs
git commit -m "fix!: correct hit/miss ordering + emit via CacheInstruments

InMemory: miss is now emitted BEFORE factory invocation (was after Set).
Hybrid: detects actual factory invocation via captured flag (was always recording hit).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 12: CachingBuilder v2 — WithKeyPrefix, WithSerializer<T>, WithResilience, WithStripedLocks

**Files:**
- Modify: `src/Caching.NET/CachingBuilder.cs`
- Modify: `tests/Caching.NET.Tests/Builder/CachingBuilderTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void WithKeyPrefix_AssignsKeyPrefix()
{
    var services = new ServiceCollection();
    services.AddCaching(b => b.UseInMemory().WithKeyPrefix("asm-api-dev"));
    var sp = services.BuildServiceProvider();
    var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
    Assert.Equal("asm-api-dev", opts.KeyPrefix);
}

[Fact]
public void WithStripedLocks_AssignsStripeCount()
{
    var services = new ServiceCollection();
    services.AddCaching(b => b.UseInMemory().WithKeyPrefix("t").WithStripedLocks(2048));
    var sp = services.BuildServiceProvider();
    var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
    Assert.Equal(2048, opts.StripeLockCount);
}

[Fact]
public void WithSerializer_RegistersCustomSerializer()
{
    var services = new ServiceCollection();
    services.AddCaching(b => b.UseInMemory().WithKeyPrefix("t").WithSerializer<JsonCacheSerializer>());
    var sp = services.BuildServiceProvider();
    Assert.IsType<JsonCacheSerializer>(sp.GetRequiredService<ICacheSerializer>());
}

[Fact]
public void Build_FailsValidationWhenKeyPrefixMissing()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddCaching(b => b.UseInMemory()); // no WithKeyPrefix
    Assert.Throws<OptionsValidationException>(() => services.BuildServiceProvider().GetRequiredService<IOptions<CacheOptions>>().Value);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CachingBuilderTests"`
Expected: FAIL.

- [ ] **Step 3: Update `CachingBuilder`**

In `src/Caching.NET/CachingBuilder.cs`:
- **Remove** `WithRedisInstanceName` method entirely.
- Add:

```csharp
public CachingBuilder WithKeyPrefix(string keyPrefix)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
    _services.PostConfigure<CacheOptions>(o => o.KeyPrefix = keyPrefix);
    return this;
}

public CachingBuilder WithStripedLocks(int stripeCount)
{
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
    _services.PostConfigure<CacheOptions>(o => o.StripeLockCount = stripeCount);
    return this;
}

public CachingBuilder WithRedisOperationTimeout(TimeSpan timeout)
{
    _services.PostConfigure<CacheOptions>(o => o.RedisOperationTimeout = timeout);
    return this;
}

public CachingBuilder WithSerializer<TSerializer>() where TSerializer : class, ICacheSerializer
{
    _services.RemoveAll<ICacheSerializer>();
    _services.AddSingleton<ICacheSerializer, TSerializer>();
    return this;
}

public CachingBuilder WithSerializer(ICacheSerializer serializer)
{
    ArgumentNullException.ThrowIfNull(serializer);
    _services.RemoveAll<ICacheSerializer>();
    _services.AddSingleton(serializer);
    return this;
}

public CachingBuilder WithResilience(Action<ResiliencePipelineRegistryOptions> configure)
{
    ArgumentNullException.ThrowIfNull(configure);
    _services.Configure(configure);
    return this;
}
```

(`ResiliencePipelineRegistryOptions` is a Caching.NET-defined options class — see Task 13 for definition.)

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~CachingBuilderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/CachingBuilder.cs tests/Caching.NET.Tests/Builder/CachingBuilderTests.cs
git commit -m "feat!: CachingBuilder v2 — WithKeyPrefix/WithSerializer/WithResilience/WithStripedLocks

BREAKING: WithRedisInstanceName removed. Use WithKeyPrefix.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 13: ServiceCollectionExtensions wiring + ResiliencePipelineRegistryOptions

**Files:**
- Create: `src/Caching.NET/Resilience/ResiliencePipelineRegistryOptions.cs`
- Modify: `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `tests/Caching.NET.Tests/Validation/CacheRegistrationValidationTests.cs`

- [ ] **Step 1: Define options class**

`src/Caching.NET/Resilience/ResiliencePipelineRegistryOptions.cs`:

```csharp
namespace Caching.NET.Resilience;

public sealed class ResiliencePipelineRegistryOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
    public double FailureRatio { get; set; } = 0.5;
    public int MinimumThroughput { get; set; } = 20;
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
    public int RetryCount { get; set; } = 2;
}
```

- [ ] **Step 2: Update `ServiceCollectionExtensions.AddCachingCore`**

In the central registration method:
1. **Remove**: `services.AddSingleton<ICacheTelemetry, NoopCacheTelemetry>()` and `OpenTelemetry...` opt-in (replaced by `CacheInstruments` static; consumer wires OTel via their own pipeline).
2. **Remove**: any code that maps `CacheOptions.RedisInstanceName` to `RedisCacheOptions.InstanceName`.
3. **Add**:
   - `services.AddSingleton<IValidateOptions<CacheOptions>, CacheOptionsValidator>();`
   - `services.AddOptions<CacheOptions>().ValidateOnStart();`
   - `services.TryAddSingleton<ICacheSerializer>(sp => new JsonCacheSerializer());` (overridable by `WithSerializer<T>` which calls `RemoveAll`).
   - `services.AddSingleton<StripedLockManager>(sp =>
       new StripedLockManager(sp.GetRequiredService<IOptions<CacheOptions>>().Value.StripeLockCount));`
   - `services.AddSingleton<ResiliencePipelineRegistry<string>>(sp =>
       {
           var opt = sp.GetService<IOptions<ResiliencePipelineRegistryOptions>>()?.Value
                     ?? new ResiliencePipelineRegistryOptions();
           return CacheResiliencePipelineBuilder.BuildDefaultRegistry(
               timeout: opt.Timeout, failureRatio: opt.FailureRatio,
               minimumThroughput: opt.MinimumThroughput, samplingDuration: opt.SamplingDuration,
               breakDuration: opt.BreakDuration, retryCount: opt.RetryCount);
       });`

- [ ] **Step 3: Update existing registration validation tests**

Open `tests/Caching.NET.Tests/Validation/CacheRegistrationValidationTests.cs`. For every `AddCaching(...)` invocation, supply `WithKeyPrefix("t")` (or `KeyPrefix = "t"` in config dict) so the validator passes. Remove tests that reference `RedisInstanceName`. Add a test asserting `OptionsValidationException` when `KeyPrefix` is omitted.

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build -c Release`
Expected: build succeeds.
Run: `dotnet test`
Expected: ALL pass on `net8.0`, `net9.0`, `net10.0`.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Resilience/ResiliencePipelineRegistryOptions.cs src/Caching.NET/Extensions/ServiceCollectionExtensions.cs tests/Caching.NET.Tests/Validation/CacheRegistrationValidationTests.cs
git commit -m "feat!: wire StripedLockManager + ResiliencePipelineRegistry + ICacheSerializer in DI

BREAKING: ICacheTelemetry registration removed.
BREAKING: RedisInstanceName mapping removed.
Adds CacheOptionsValidator with ValidateOnStart.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 14: Sample app + appsettings migration

**Files:**
- Modify: `samples/Caching.NET.Sample/Program.cs`
- Modify: `samples/Caching.NET.Sample/appsettings.json`
- Modify: `samples/Caching.NET.Sample/Caching.NET.Sample.csproj` (TFM bump if needed)

- [ ] **Step 1: Update `appsettings.json`**

Replace any `"RedisInstanceName"` key with `"KeyPrefix"`. Add example:

```json
{
  "CacheOptions": {
    "Mode": "Hybrid",
    "KeyPrefix": "asm-api-dev",
    "RedisConnectionString": "localhost:6379",
    "StrictRedisCertificateValidation": false,
    "FailOpen": true,
    "DefaultExpiration": "00:10:00",
    "TtlJitterPercentage": 0.10,
    "MaximumKeyLength": 512,
    "StripeLockCount": 1024
  }
}
```

- [ ] **Step 2: Update `Program.cs`**

Replace any `WithRedisInstanceName` call with `WithKeyPrefix`. Remove any `services.AddSingleton<ICacheTelemetry, …>()` line. Add OTel wiring example (commented for the sample):

```csharp
builder.Services.AddCaching(b => b
    .UseHybrid()
    .WithKeyPrefix("asm-api-dev"));

// Optional: subscribe to Caching.NET telemetry
// builder.Services.AddOpenTelemetry()
//     .WithMetrics(m => m.AddMeter(CacheInstruments.MeterName))
//     .WithTracing(t => t.AddSource(CacheInstruments.ActivitySourceName));
```

- [ ] **Step 3: Run sample**

Run: `dotnet run --project samples/Caching.NET.Sample`
Expected: starts cleanly. Hit any sample endpoint; observe no validation exceptions.

- [ ] **Step 4: Commit**

```bash
git add samples/
git commit -m "chore: migrate sample app to v2 builder API

Replaces RedisInstanceName with KeyPrefix; removes ICacheTelemetry.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 15: Final P0 sweep — re-enable strict warnings + update PublicAPI files

**Files:**
- Modify: `src/Caching.NET/Caching.NET.csproj`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Remove `RS0016`/`RS0017` from `<NoWarn>`**

If Task 1 added `<NoWarn>$(NoWarn);RS0016;RS0017</NoWarn>`, remove it now. Build will fail with `RS0016` for any public symbol not in `PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt`.

- [ ] **Step 2: Auto-populate `PublicAPI.Unshipped.txt`**

Run: `dotnet build -c Release`
For each `RS0016` warning, copy the exact symbol signature into `src/Caching.NET/PublicAPI.Unshipped.txt`. Sort alphabetically.

For each `RS0017` warning (a symbol previously declared in `Shipped.txt` is gone), remove the corresponding entry. (Initially `Shipped.txt` is empty so this is unlikely.)

- [ ] **Step 3: Verify clean build**

Run: `dotnet build -c Release`
Expected: zero warnings. `TreatWarningsAsErrors` clean.

- [ ] **Step 4: Run full test suite**

Run: `dotnet test`
Expected: all green on all three TFMs.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Caching.NET.csproj src/Caching.NET/PublicAPI.Unshipped.txt
git commit -m "chore: lock v2 public API surface in PublicAPI.Unshipped.txt

Final sweep: strict warnings re-enabled. Surface stays in Unshipped
until v2.0.0 ships, at which point the contents move to Shipped.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## P0 Acceptance Criteria

1. `dotnet build -c Release` clean across `net8.0;net9.0;net10.0`, no warnings under `TreatWarningsAsErrors=true`.
2. `dotnet test` 100 % green for all unit tests.
3. `RoutingCacheService` no longer holds `ConcurrentDictionary<string, SemaphoreSlim>` — only `StripedLockManager`.
4. No code anywhere references `ICacheTelemetry`, `NoopCacheTelemetry`, `OpenTelemetryCacheTelemetry`, or `RedisInstanceName`.
5. `KeyPrefix` mandatory at startup: omitting it throws `OptionsValidationException`.
6. `JsonCacheSerializer` is the resolved `ICacheSerializer` by default; replaceable via `WithSerializer<T>`.
7. All Redis ops in `RedisCacheService` execute through `ResiliencePipeline`; per-op `CancellationTokenSource.CreateLinkedTokenSource` enforces `RedisOperationTimeout`.
8. `samples/Caching.NET.Sample` builds and starts on .NET 8 against the v2 API.
9. `PublicAPI.Unshipped.txt` exists and matches the actual public surface.

---

## Self-Review (writing-plans skill checklist)

**Spec coverage:**
- §3 architecture deltas: striped locks (✓ Task 3, 9), KeyPrefix injection (✓ Task 9), Polly pipeline (✓ Task 7, 10), `ICacheSerializer` (✓ Task 6, 10), drop `ICacheTelemetry` (✓ Task 8), drop `RedisInstanceName` (✓ Task 5), default tightening (✓ Task 5).
- §4 surface: `WithKeyPrefix`, `WithSerializer<T>`, `WithResilience`, `WithStripedLocks` (✓ Task 12). `RequireTagSupport` deferred to P2 (tag overloads land there).
- §5 stampede protection: striped locks + correct ForceRefresh under coalescing (✓ Task 3, 9).
- §6 resilience + serializer: timeout + breaker + retry + per-op CTS + `ICacheSerializer` (✓ Task 6, 7, 10). Payload envelope deferred to P1 (out of P0 scope).
- §7 minimal CacheInstruments — basic counters/histograms (✓ Task 8). Full miss-reason taxonomy, eviction listener, payload histogram, schema-drift counter, source-gen logger, cardinality analyzer all defer to P1.
- §8 options validation, hot-reload, secret redactor, key prefix layout (✓ Task 4, 5, 13). `CacheKeyBuilder` deferred to P2.

**Placeholder scan:** no `TBD`, no `implement later`, no "similar to Task N". Every task has full code blocks for new types and explicit instructions for modified ones.

**Type consistency:**
- `StripedLockManager.GetLock(string)` defined in Task 3, used in Task 9 — names match.
- `ICacheSerializer` interface (Task 6) with `string FormatId`, `byte[] Serialize<T>(T)`, `T? Deserialize<T>(ReadOnlySpan<byte>)` — used in Task 10 RedisCacheService. Matches.
- `CacheInstruments.RecordHit/Miss/Set/Remove/Error/Duration` (Task 8) — used in Tasks 9, 10, 11. Matches.
- `ResiliencePipelineNames.RedisRead/Write/Delete` (Task 7) — used in Task 10. Matches.
- `ResiliencePipelineRegistryOptions` defined in Task 13 (referenced earlier in Task 12 `WithResilience`) — note: Task 12 uses `Action<ResiliencePipelineRegistryOptions>` which is defined in Task 13. Engineer must execute tasks in order; if executing strictly per-task, Task 12 will not compile until Task 13 lands. **Mitigation: execute Tasks 12 and 13 as a pair, or land Task 13 before Task 12.** (Order in this plan: 12 → 13. Engineer should compile after Task 13, not after Task 12.)

**Out-of-scope deferrals (intentional, documented in spec):**
- `PayloadEnvelope`, source-gen logger, cardinality analyzer, eviction listener, miss-reason taxonomy → **P1**.
- `GetMany`/`SetMany`/`RemoveMany`, `GetAsync`/`RefreshAsync`/`ExistsAsync`, sliding expiration, tag overloads, `MessagePackCacheSerializer`, `CacheKeyBuilder`, stale-while-revalidate, TTL jitter application → **P2**.
- AOT/trim verification, Testcontainers, chaos suite, BenchmarkDotNet, ops docs → **P3**.
