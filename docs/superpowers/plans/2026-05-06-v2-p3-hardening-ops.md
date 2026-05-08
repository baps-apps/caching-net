# Caching.NET v2 — P3 Hardening & Ops Implementation Plan

> **HISTORICAL — partially superseded.** All references below to `Caching.NET.Analyzers`, `Caching.NET.Tests.Analyzers`, `CN0001`, and the analyzer DLL packed at `analyzers/dotnet/cs/Caching.NET.Analyzers.dll` were **removed in v2.1.0**. Forbidden tag/log-template names remain a runtime convention only.
>
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land all P3 deliverables from `docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md` §6 (server-side MGET/MSET deferred from P2), §8 (TLS cert audit), §9 (full test matrix), §10 (SBOM, deterministic build, source-link), §11 (all 7 docs), §12 P3 bullets, and §15 acceptance criteria — making the library ready to tag `v2.0.0`.

**Architecture:** Code work is additive — three new test projects (Integration, Chaos, Properties), one new bench project (Bench), one new AOT smoke project (AotSmoke), small surface additions (TLS audit, cred-rotation hook, server-side Redis batch when an `IConnectionMultiplexer` is registered). Ops work is mostly infrastructure: GitHub Actions matrix workflow, Microsoft.Sbom.Targets, baseline JSON for perf gate. Docs work is content rewrites of existing files plus three new markdown files. Final tasks promote `PublicAPI.Unshipped.txt → Shipped.txt` and bump version to `2.0.0` for the tag.

**Tech Stack:** .NET 8/9/10, `Testcontainers.Redis 4.x`, `Polly.Testing 8.x`, `FsCheck 3.x` + `FsCheck.Xunit`, `BenchmarkDotNet 0.14.x`, `Microsoft.Sbom.Targets`, `StackExchange.Redis 2.x` (already a transitive dep), PowerShell Core (`pwsh`) for cross-platform local build/test/bench scripts. **No remote CI service.**

**Pre-flight:**
- Branch: continue on `vpatel/v2`.
- Verify P2 baseline: `dotnet test` → 155 (×3) + 4 analyzer = 469 pass before starting Task 1.
- Commit-message form: `feat(p3): …`, `test(p3): …`, `docs(p3): …`, `ci(p3): …`, `chore(p3): …` so the v2.0.0 changelog can grep.

**Scope clarifications:**
- Existing csproj already has `IsAotCompatible`, `IsTrimmable`, `DeterministicSourcePaths`, `IncludeSymbols`, source-link via `Directory.Build.props`. P3 verifies these via a dedicated AOT smoke-publish; it does not re-add the flags.
- `MessagePack` reflection-mode currently triggers IL2026/IL3050 — already suppressed in P0 csproj. Task 8 documents the AOT-supported path (consumer-supplied `JsonSerializerContext`).
- Cred rotation = swap Redis credentials without restarting the host. P3 implementation = `IOptionsMonitor<CacheOptions>` listener that closes/reopens the `IConnectionMultiplexer` when `RedisConnectionString` changes. Distributed coordination beyond a single host is out of scope.
- BenchmarkDotNet perf gate = compare run output JSON against a committed `bench-baseline.json`; fail CI when mean p99 or `Allocated` regresses > 10 %. The baseline is generated once on first green CI run on `main` and updated only via explicit script.
- Docs target the `docs/` directory plus the repo-root `README.md`. Existing `docs/INTERNALS.md`, `docs/OPERATIONS.md`, `docs/TELEMETRY.md`, `docs/HEALTH-CHECKS.md` get full rewrites where their v1 content is stale.

**Doc / behavior sync (2026-05-06 audit):** Operational docs now describe **`Enabled=false` DI semantics**, **health check** (`CachingHealthCheck`) **multiplexer + PING gating for Redis/Hybrid**, **per-instance probe keys**, **resilience** (transient rules, retry timing, optional concurrency limiter), and **telemetry** (ser/deser histograms, Redis batch remove counting). See [OPERATIONS.md](../../OPERATIONS.md), [HEALTH-CHECKS.md](../../HEALTH-CHECKS.md), [V2.0.0-RELEASE-IMPACT.md](../../V2.0.0-RELEASE-IMPACT.md).

---

## File Structure

**Create:**
- `tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj` — xUnit + Testcontainers.Redis test project.
- `tests/Caching.NET.Tests.Integration/Fixtures/RedisContainerFixture.cs` — `IAsyncLifetime` + `ICollectionFixture` for a shared Redis 7 container per collection.
- `tests/Caching.NET.Tests.Integration/RedisRoundTripTests.cs`
- `tests/Caching.NET.Tests.Integration/RedisBatchTests.cs`
- `tests/Caching.NET.Tests.Integration/RedisTlsTests.cs`
- `tests/Caching.NET.Tests.Integration/RedisDriftTests.cs`
- `tests/Caching.NET.Tests.Integration/RedisConnectionDropTests.cs`
- `tests/Caching.NET.Tests.Integration/RedisKeyPrefixIsolationTests.cs`
- `tests/Caching.NET.Tests.Integration/Helpers/IntegrationServiceProvider.cs`
- `tests/Caching.NET.Tests.Chaos/Caching.NET.Tests.Chaos.csproj` — Polly.Testing project.
- `tests/Caching.NET.Tests.Chaos/CircuitBreakerTrippingTests.cs`
- `tests/Caching.NET.Tests.Chaos/FailOpenChaosTests.cs`
- `tests/Caching.NET.Tests.Chaos/CorruptResponseTests.cs`
- `tests/Caching.NET.Tests.Properties/Caching.NET.Tests.Properties.csproj` — FsCheck.Xunit project.
- `tests/Caching.NET.Tests.Properties/SerializerRoundTripProperties.cs`
- `tests/Caching.NET.Tests.Properties/StripedLockManagerProperties.cs`
- `tests/Caching.NET.Tests.Properties/PayloadEnvelopeProperties.cs`
- `tests/Caching.NET.Tests.Properties/CoalescingProperties.cs`
- `benchmark/Caching.NET.Benchmark/Caching.NET.Benchmark.csproj` — BenchmarkDotNet console project.
- `benchmark/Caching.NET.Benchmark/Program.cs`
- `benchmark/Caching.NET.Benchmark/GetOrCreateBenchmarks.cs`
- `benchmark/Caching.NET.Benchmark/SerializerBenchmarks.cs`
- `benchmark/Caching.NET.Benchmark/StripedLockBenchmarks.cs`
- `benchmark/Caching.NET.Benchmark/BatchBenchmarks.cs`
- `benchmark/Caching.NET.Benchmark/bench-baseline.json` — perf gate reference (committed; updated via script).
- `benchmark/perf-gate.ps1` — comparison script (cross-platform PowerShell Core).
- `aot/Caching.NET.AotSmoke/Caching.NET.AotSmoke.csproj` — net10.0 console with `PublishAot=true`.
- `aot/Caching.NET.AotSmoke/Program.cs`
- `aot/Caching.NET.AotSmoke/AppJsonContext.cs` — minimal `JsonSerializerContext` for AOT smoke.
- `src/Caching.NET/Internal/RedisCertificateValidator.cs` — replaces P0's `RedisCertificateValidation` static helper with an instance type that audit-logs.
- `src/Caching.NET/Internal/RedisConnectionRotator.cs` — listens to `IOptionsMonitor<CacheOptions>`; rebuilds `IConnectionMultiplexer` on connection-string change.
- `scripts/dev.ps1` — single-entrypoint local build/test/bench/pack tool (cross-platform).
- `scripts/combine-bench-results.ps1` — flatten BenchmarkDotNet per-bench JSON into `combined.json` for the perf gate.
- `scripts/README.md` — usage doc.
- `docs/MIGRATION-V1-TO-V2.md`
- `docs/SECURITY.md`
- `docs/BENCHMARKS.md`

**Modify:**
- `Directory.Packages.props` — add Testcontainers.Redis, Polly.Testing, FsCheck (3.x) + FsCheck.Xunit, BenchmarkDotNet, Microsoft.Sbom.Targets, StackExchange.Redis (explicit version).
- `Caching.NET.sln` — add the new projects.
- `src/Caching.NET/Caching.NET.csproj` — add `Microsoft.Sbom.Targets` (Pack-only), bump `<Version>` to `2.0.0`.
- `src/Caching.NET/Services/RedisCacheService.cs` — server-side MGET/MSET path when `IConnectionMultiplexer` is available.
- `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs` — register `RedisCertificateValidator`, register `IConnectionMultiplexer` as a singleton when Mode is Redis/Hybrid, register `RedisConnectionRotator` as a hosted service.
- `src/Caching.NET/Telemetry/CacheInstruments.cs` — `cache.tls.validation` counter + `RecordTlsValidation(string mode, string result)` helper.
- `src/Caching.NET/PublicAPI.Unshipped.txt` — empty after Task 13 promotion.
- `src/Caching.NET/PublicAPI.Shipped.txt` — populated after Task 13 promotion.
- `README.md` (repo root) — replace v1 content; quickstart + three-mode example + Amazon-style production config.
- `docs/INTERNALS.md` — rewrite for v2 internals (striped locks, envelope, SwR, resilience).
- `docs/OPERATIONS.md` — rewrite for K8s/ElastiCache/sharding/CB tuning/cred rotation.
- `docs/TELEMETRY.md` — rewrite for v2 instrument set + tag taxonomy + cardinality + Grafana/Prom.
- `docs/HEALTH-CHECKS.md` — light update; v2 still uses `CachingHealthCheck`.

---

## Task 1: Integration test project skeleton + Redis container fixture

**Files:**
- Create: `tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj`
- Create: `tests/Caching.NET.Tests.Integration/Fixtures/RedisContainerFixture.cs`
- Create: `tests/Caching.NET.Tests.Integration/Helpers/IntegrationServiceProvider.cs`
- Create: `tests/Caching.NET.Tests.Integration/RedisRoundTripTests.cs`
- Modify: `Directory.Packages.props`, `Caching.NET.sln`

- [ ] **Step 1: Add package versions**

Edit `Directory.Packages.props` — append to the existing `<ItemGroup>`:

```xml
<PackageVersion Include="Testcontainers" Version="4.0.0" />
<PackageVersion Include="Testcontainers.Redis" Version="4.0.0" />
<PackageVersion Include="StackExchange.Redis" Version="2.8.16" />
```

- [ ] **Step 2: Create the integration project csproj**

```xml
<!-- tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Testcontainers.Redis" />
    <PackageReference Include="StackExchange.Redis" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Caching.NET\Caching.NET.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create Redis container fixture**

```csharp
// tests/Caching.NET.Tests.Integration/Fixtures/RedisContainerFixture.cs
using Testcontainers.Redis;
using Xunit;

namespace Caching.NET.Tests.Integration.Fixtures;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    public RedisContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public RedisContainerFixture()
    {
        Container = new RedisBuilder().WithImage("redis:7.2-alpine").Build();
    }

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

[CollectionDefinition("Redis")]
public sealed class RedisCollection : ICollectionFixture<RedisContainerFixture> { }
```

- [ ] **Step 4: Create service provider helper**

```csharp
// tests/Caching.NET.Tests.Integration/Helpers/IntegrationServiceProvider.cs
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Integration.Helpers;

internal static class IntegrationServiceProvider
{
    public static (IServiceProvider sp, ICacheService cache) Build(string redisConnectionString, string keyPrefix, Action<CachingBuilder>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCaching(b =>
        {
            b.UseRedis(redisConnectionString).WithKeyPrefix(keyPrefix);
            extra?.Invoke(b);
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<ICacheService>());
    }
}
```

- [ ] **Step 5: Write the smoke test**

```csharp
// tests/Caching.NET.Tests.Integration/RedisRoundTripTests.cs
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisRoundTripTests
{
    private readonly RedisContainerFixture _redis;
    public RedisRoundTripTests(RedisContainerFixture redis) => _redis = redis;

    public sealed class Order { public int Id { get; set; } public string? Customer { get; set; } }

    [Fact]
    public async Task Get_after_Set_returns_value_from_real_redis()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-roundtrip");
        await using var scope = (IAsyncDisposable)sp;

        await cache.SetAsync("k", new Order { Id = 1, Customer = "Acme" });
        var got = await cache.GetAsync<Order>("k");

        Assert.NotNull(got);
        Assert.Equal(1, got!.Id);
        Assert.Equal("Acme", got.Customer);
    }
}
```

- [ ] **Step 6: Add to solution + run**

```bash
dotnet sln /Users/vishalpatel/Projects/caching-net/Caching.NET.sln add /Users/vishalpatel/Projects/caching-net/tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj
dotnet test tests/Caching.NET.Tests.Integration -f net10.0
```

Expected: 1 test PASS. Docker must be running on the host. CI provides Docker on `ubuntu-latest`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "test(p3): add Caching.NET.Tests.Integration with Testcontainers Redis fixture + roundtrip smoke"
```

---

## Task 2: Integration coverage — batch, TLS, drift, connection drop, prefix isolation

**Files:**
- Create: `tests/Caching.NET.Tests.Integration/RedisBatchTests.cs`
- Create: `tests/Caching.NET.Tests.Integration/RedisTlsTests.cs`
- Create: `tests/Caching.NET.Tests.Integration/RedisDriftTests.cs`
- Create: `tests/Caching.NET.Tests.Integration/RedisConnectionDropTests.cs`
- Create: `tests/Caching.NET.Tests.Integration/RedisKeyPrefixIsolationTests.cs`

- [ ] **Step 1: Batch tests**

```csharp
// tests/Caching.NET.Tests.Integration/RedisBatchTests.cs
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisBatchTests
{
    private readonly RedisContainerFixture _redis;
    public RedisBatchTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task SetMany_then_GetMany_round_trips_against_real_redis()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-batch");
        var items = new Dictionary<string, string>
        {
            ["a"] = "1", ["b"] = "2", ["c"] = "3",
        };
        await cache.SetManyAsync(items);
        var got = await cache.GetManyAsync<string>(new[] { "a", "b", "c", "missing" });

        Assert.Equal("1", got["a"]);
        Assert.Equal("2", got["b"]);
        Assert.Equal("3", got["c"]);
        Assert.Null(got["missing"]);
    }

    [Fact]
    public async Task RemoveMany_clears_specified_keys_only()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-rm");
        await cache.SetManyAsync(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2", ["z"] = "3" });
        await cache.RemoveManyAsync(new[] { "x", "y" });

        Assert.False(await cache.ExistsAsync("x"));
        Assert.False(await cache.ExistsAsync("y"));
        Assert.True(await cache.ExistsAsync("z"));
    }
}
```

- [ ] **Step 2: TLS test (skipped when stunnel sidecar unavailable)**

```csharp
// tests/Caching.NET.Tests.Integration/RedisTlsTests.cs
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisTlsTests
{
    private readonly RedisContainerFixture _redis;
    public RedisTlsTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task Strict_validation_with_self_signed_cert_rejects_connection()
    {
        // Redis 7.2-alpine has TLS support but the default Testcontainers image does not
        // ship a cert. We simulate the strict-mode rejection path by appending ssl=true to
        // the plain connection string — StackExchange.Redis attempts TLS, the server replies
        // in plain, and the connect fails before any data flows.
        var brokenTlsConn = _redis.ConnectionString + ",ssl=true,abortConnect=true,connectTimeout=2000";

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var (sp, cache) = IntegrationServiceProvider.Build(brokenTlsConn, "rt-tls");
            await cache.SetAsync("k", "v"); // first op forces the connection
        });
    }
}
```

- [ ] **Step 3: Schema-drift test**

```csharp
// tests/Caching.NET.Tests.Integration/RedisDriftTests.cs
using Caching.NET.Internal;
using Caching.NET.Serialization;
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using StackExchange.Redis;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisDriftTests
{
    private readonly RedisContainerFixture _redis;
    public RedisDriftTests(RedisContainerFixture redis) => _redis = redis;

    public sealed class CurrentDto { public int Id { get; set; } }

    [Fact]
    public async Task SchemaDrift_returns_miss_and_runs_factory()
    {
        // Plant a stale envelope with the wrong schema hash.
        await using var mux = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        var fakeHash = 0xDEAD_BEEF_CAFE_BABEUL;
        var fakePayload = "{\"Id\":99}"u8.ToArray();
        var wire = PayloadEnvelope.Write(fakePayload, PayloadEnvelope.FormatIdJson, fakeHash);
        await mux.GetDatabase().StringSetAsync("rt-drift:k", wire);

        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-drift");
        var ran = false;
        var got = await cache.GetOrCreateAsync<CurrentDto>("k", _ =>
        {
            ran = true;
            return Task.FromResult(new CurrentDto { Id = 7 });
        });

        Assert.True(ran);
        Assert.Equal(7, got.Id);
    }
}
```

- [ ] **Step 4: Connection-drop test**

```csharp
// tests/Caching.NET.Tests.Integration/RedisConnectionDropTests.cs
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisConnectionDropTests
{
    private readonly RedisContainerFixture _redis;
    public RedisConnectionDropTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task Container_restart_recovers_with_FailOpen()
    {
        var (sp, cache) = IntegrationServiceProvider.Build(_redis.ConnectionString, "rt-drop");
        await cache.SetAsync("k", "v");
        Assert.Equal("v", await cache.GetAsync<string>("k"));

        await _redis.Container.StopAsync();
        // Cache backend is gone — FailOpen should let the factory run.
        var got = await cache.GetOrCreateAsync("k", _ => Task.FromResult("factory"));
        Assert.Equal("factory", got);

        await _redis.Container.StartAsync();
    }
}
```

(Caveat: this test mutates the shared fixture. Mark it as `[Trait("Category", "Restart")]` and run last; or use a per-test container by removing the `[Collection("Redis")]` attribute. Pick the latter for safety:)

Replace `[Collection("Redis")]` on this class with a private fixture allocated in the constructor:

```csharp
public class RedisConnectionDropTests : IAsyncLifetime
{
    private readonly Testcontainers.Redis.RedisContainer _container =
        new Testcontainers.Redis.RedisBuilder().WithImage("redis:7.2-alpine").Build();
    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
    // … test methods use _container.GetConnectionString() …
}
```

- [ ] **Step 5: Key prefix isolation test**

```csharp
// tests/Caching.NET.Tests.Integration/RedisKeyPrefixIsolationTests.cs
using Caching.NET.Tests.Integration.Fixtures;
using Caching.NET.Tests.Integration.Helpers;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisKeyPrefixIsolationTests
{
    private readonly RedisContainerFixture _redis;
    public RedisKeyPrefixIsolationTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task Different_prefixes_do_not_see_each_others_keys()
    {
        var (spA, cacheA) = IntegrationServiceProvider.Build(_redis.ConnectionString, "svc-a");
        var (spB, cacheB) = IntegrationServiceProvider.Build(_redis.ConnectionString, "svc-b");

        await cacheA.SetAsync("shared", "from-a");
        Assert.Equal("from-a", await cacheA.GetAsync<string>("shared"));
        Assert.Null(await cacheB.GetAsync<string>("shared"));
    }
}
```

- [ ] **Step 6: Run + commit**

```bash
dotnet test tests/Caching.NET.Tests.Integration -f net10.0
```

Expected: PASS — batch (2), TLS (1), drift (1), drop (1), prefix (1) = 6 new + 1 from Task 1.

```bash
git add -A
git commit -m "test(p3): add Redis integration tests for batch, TLS, drift, drop, prefix isolation"
```

---

## Task 3: Polly chaos suite

**Files:**
- Create: `tests/Caching.NET.Tests.Chaos/Caching.NET.Tests.Chaos.csproj`
- Create: `tests/Caching.NET.Tests.Chaos/CircuitBreakerTrippingTests.cs`
- Create: `tests/Caching.NET.Tests.Chaos/FailOpenChaosTests.cs`
- Create: `tests/Caching.NET.Tests.Chaos/CorruptResponseTests.cs`
- Modify: `Directory.Packages.props`, `Caching.NET.sln`

- [ ] **Step 1: Add Polly.Testing version**

Edit `Directory.Packages.props` — append:

```xml
<PackageVersion Include="Polly.Testing" Version="8.5.0" />
<PackageVersion Include="Microsoft.Extensions.Resilience" Version="9.0.0" />
```

- [ ] **Step 2: Create chaos csproj**

```xml
<!-- tests/Caching.NET.Tests.Chaos/Caching.NET.Tests.Chaos.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Polly.Testing" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Caching.NET\Caching.NET.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Circuit-breaker tripping test**

```csharp
// tests/Caching.NET.Tests.Chaos/CircuitBreakerTrippingTests.cs
using Caching.NET.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Polly.Testing;
using StackExchange.Redis;
using Xunit;

namespace Caching.NET.Tests.Chaos;

public class CircuitBreakerTrippingTests
{
    [Fact]
    public async Task Repeated_RedisConnectionException_trips_breaker_within_threshold()
    {
        var registry = CacheResiliencePipelineBuilder.Build(
            new ResiliencePipelineRegistryOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(2),
                BreakDuration = TimeSpan.FromSeconds(1),
                RetryCount = 0,
            },
            NullLoggerFactory.Instance);
        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        // 4 failures fills minimum-throughput; ratio = 1.0 → breaker trips on 5th call.
        for (int i = 0; i < 5; i++)
        {
            try { await pipeline.ExecuteAsync<int>(_ => throw new RedisConnectionException(ConnectionFailureType.None, "boom")); }
            catch { }
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(0));
        });
    }
}
```

- [ ] **Step 4: FailOpen chaos test**

```csharp
// tests/Caching.NET.Tests.Chaos/FailOpenChaosTests.cs
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caching.NET.Tests.Chaos;

public class FailOpenChaosTests
{
    private sealed class AlwaysThrowDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("boom");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");
        public void Refresh(string key) => throw new InvalidOperationException("boom");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");
        public void Remove(string key) => throw new InvalidOperationException("boom");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException("boom");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task FailOpen_runs_factory_when_backend_throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache, AlwaysThrowDistributedCache>();
        services.AddCaching(b => b.UseRedis("localhost:6379").WithKeyPrefix("chaos"));
        services.PostConfigure<CacheOptions>(o => { o.FailOpen = true; o.ThrowOnFailure = false; });

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<ICacheService>();

        var got = await cache.GetOrCreateAsync("k", _ => Task.FromResult("from-factory"));
        Assert.Equal("from-factory", got);
    }
}
```

- [ ] **Step 5: Corrupt-response test**

```csharp
// tests/Caching.NET.Tests.Chaos/CorruptResponseTests.cs
using Caching.NET.Serialization;
using Xunit;

namespace Caching.NET.Tests.Chaos;

public class CorruptResponseTests
{
    [Fact]
    public void Random_bytes_never_throw_on_envelope_decode()
    {
        var rng = new Random(42);
        var buf = new byte[256];
        for (int i = 0; i < 1000; i++)
        {
            rng.NextBytes(buf);
            // Must not throw — only return non-Ok status.
            var status = PayloadEnvelope.TryRead(buf, PayloadEnvelope.FormatIdJson, 0UL, out _);
            Assert.True(status is PayloadEnvelopeReadResult.EnvelopeInvalid
                              or PayloadEnvelopeReadResult.FormatDrift
                              or PayloadEnvelopeReadResult.SchemaDrift
                              or PayloadEnvelopeReadResult.Ok);
        }
    }
}
```

- [ ] **Step 6: Add to solution + run**

```bash
dotnet sln add tests/Caching.NET.Tests.Chaos/Caching.NET.Tests.Chaos.csproj
dotnet test tests/Caching.NET.Tests.Chaos -f net10.0
```

Expected: 3 PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "test(p3): add Polly chaos suite (CB tripping, FailOpen, corrupt-response)"
```

---

## Task 4: FsCheck property suite

**Files:**
- Create: `tests/Caching.NET.Tests.Properties/Caching.NET.Tests.Properties.csproj`
- Create: `tests/Caching.NET.Tests.Properties/SerializerRoundTripProperties.cs`
- Create: `tests/Caching.NET.Tests.Properties/StripedLockManagerProperties.cs`
- Create: `tests/Caching.NET.Tests.Properties/PayloadEnvelopeProperties.cs`
- Create: `tests/Caching.NET.Tests.Properties/CoalescingProperties.cs`
- Modify: `Directory.Packages.props`, `Caching.NET.sln`

- [ ] **Step 1: Add FsCheck versions**

Edit `Directory.Packages.props` — append:

```xml
<PackageVersion Include="FsCheck" Version="3.0.0-rc3" />
<PackageVersion Include="FsCheck.Xunit" Version="3.0.0-rc3" />
```

- [ ] **Step 2: Create properties csproj**

```xml
<!-- tests/Caching.NET.Tests.Properties/Caching.NET.Tests.Properties.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FsCheck" />
    <PackageReference Include="FsCheck.Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Caching.NET\Caching.NET.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Serializer round-trip property**

```csharp
// tests/Caching.NET.Tests.Properties/SerializerRoundTripProperties.cs
using Caching.NET.Serialization;
using FsCheck;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class SerializerRoundTripProperties
{
    [Property]
    public Property Json_round_trip_preserves_string(string? input)
    {
        return ((Func<bool>)(() =>
        {
            if (input is null) return true;
            var s = new JsonCacheSerializer();
            var bytes = s.Serialize(new Wrapper { S = input });
            var got = s.Deserialize<Wrapper>(bytes);
            return got is not null && got.S == input;
        })).When(true).ToProperty();
    }

    [Property]
    public Property MessagePack_round_trip_preserves_int(int input)
    {
        var s = new MessagePackCacheSerializer();
        var bytes = s.Serialize(new IntWrap { V = input });
        var got = s.Deserialize<IntWrap>(bytes);
        return (got is not null && got.V == input).ToProperty();
    }

    public sealed class Wrapper { public string? S { get; set; } }
    public sealed class IntWrap { public int V { get; set; } }
}
```

- [ ] **Step 4: Striped lock determinism property**

```csharp
// tests/Caching.NET.Tests.Properties/StripedLockManagerProperties.cs
using Caching.NET.Internal;
using FsCheck;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class StripedLockManagerProperties
{
    [Property]
    public Property Same_key_returns_same_stripe(NonNull<string> key)
    {
        using var mgr = new StripedLockManager(1024);
        return ReferenceEquals(mgr.GetLock(key.Get), mgr.GetLock(key.Get)).ToProperty();
    }

    [Property]
    public Property Stripe_index_in_range(NonNull<string> key)
    {
        using var mgr = new StripedLockManager(1024);
        var lockObj = mgr.GetLock(key.Get);
        return (lockObj is not null).ToProperty();
    }
}
```

- [ ] **Step 5: PayloadEnvelope total-function property**

```csharp
// tests/Caching.NET.Tests.Properties/PayloadEnvelopeProperties.cs
using Caching.NET.Serialization;
using FsCheck;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class PayloadEnvelopeProperties
{
    [Property]
    public Property Random_bytes_never_throw_on_TryRead(byte[] wire)
    {
        try
        {
            PayloadEnvelope.TryRead(wire, PayloadEnvelope.FormatIdJson, 0UL, out _);
            return true.ToProperty();
        }
        catch
        {
            return false.ToProperty();
        }
    }
}
```

- [ ] **Step 6: Coalescing property**

```csharp
// tests/Caching.NET.Tests.Properties/CoalescingProperties.cs
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Tests.Properties;

public class CoalescingProperties
{
    [Property(MaxTest = 25)]
    public Property N_concurrent_GetOrCreate_invokes_factory_at_most_once(PositiveInt n)
    {
        var concurrency = Math.Min(n.Get, 50);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("coalesce-prop"));
        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<ICacheService>();

        int invocations = 0;
        var key = Guid.NewGuid().ToString("n");
        var tasks = new Task<string>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = cache.GetOrCreateAsync(key, async _ =>
            {
                Interlocked.Increment(ref invocations);
                await Task.Delay(20);
                return "value";
            });
        }
        Task.WhenAll(tasks).GetAwaiter().GetResult();
        return (invocations == 1).ToProperty();
    }
}
```

- [ ] **Step 7: Add to solution + run**

```bash
dotnet sln add tests/Caching.NET.Tests.Properties/Caching.NET.Tests.Properties.csproj
dotnet test tests/Caching.NET.Tests.Properties -f net10.0
```

Expected: 6 PASS (2 serializer + 2 striped + 1 envelope + 1 coalescing).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "test(p3): add FsCheck property suite (serializer/lock/envelope/coalescing)"
```

---

## Task 5: Server-side Redis MGET/MSET pipelining

When the consumer registers an `IConnectionMultiplexer`, route `GetManyAsync` / `SetManyAsync` / `RemoveManyAsync` through `IDatabase.StringGet(RedisKey[])` / `StringSet(KeyValuePair<RedisKey, RedisValue>[])` / `KeyDelete(RedisKey[])`. Otherwise fall back to the P2 fan-out path.

**Files:**
- Modify: `src/Caching.NET/Services/RedisCacheService.cs`
- Modify: `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs`
- Create: `tests/Caching.NET.Tests.Integration/RedisServerSideBatchTests.cs`

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/Caching.NET.Tests.Integration/RedisServerSideBatchTests.cs
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace Caching.NET.Tests.Integration;

[Collection("Redis")]
public class RedisServerSideBatchTests
{
    private readonly RedisContainerFixture _redis;
    public RedisServerSideBatchTests(RedisContainerFixture redis) => _redis = redis;

    [Fact]
    public async Task GetMany_uses_server_side_MGET_when_multiplexer_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var mux = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        services.AddSingleton<IConnectionMultiplexer>(mux);
        services.AddCaching(b => b.UseRedis(_redis.ConnectionString).WithKeyPrefix("rt-mget"));

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<ICacheService>();

        await cache.SetManyAsync(new Dictionary<string, string>
        {
            ["a"] = "1", ["b"] = "2", ["c"] = "3",
        });
        var got = await cache.GetManyAsync<string>(new[] { "a", "b", "c" });
        Assert.Equal("1", got["a"]);
        Assert.Equal("2", got["b"]);
        Assert.Equal("3", got["c"]);

        // Sanity: the keys exist on the server with the prefix applied.
        var dbKeys = mux.GetDatabase().StringGet(new RedisKey[] { "rt-mget:a", "rt-mget:b", "rt-mget:c" });
        Assert.NotEqual(RedisValue.Null, dbKeys[0]);
    }
}
```

- [ ] **Step 2: Run test to verify failure (or pass already, since fan-out already works)**

Run: `dotnet test tests/Caching.NET.Tests.Integration --filter FullyQualifiedName~RedisServerSideBatchTests -f net10.0`

The test will pass with fan-out semantics. The functional test alone can't prove server-side pipelining was used. Add a behavioural assertion: instrument an additional request count via `mux.GetDatabase().Multiplexer.GetCounters()` before and after, and assert `<count after> - <count before>` ≤ small constant (e.g. 2 — one `MGET` round-trip and overhead). Append to the same test class:

```csharp
    [Fact]
    public async Task GetMany_round_trip_count_stays_constant_for_increasing_key_count_when_multiplexer_present()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var mux = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        services.AddSingleton<IConnectionMultiplexer>(mux);
        services.AddCaching(b => b.UseRedis(_redis.ConnectionString).WithKeyPrefix("rt-mget-rt"));

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<ICacheService>();
        var items = new Dictionary<string, string>();
        for (int i = 0; i < 50; i++) items[$"k{i}"] = $"v{i}";
        await cache.SetManyAsync(items);

        var keys50 = items.Keys.ToArray();
        var beforeCmds = mux.GetServer(mux.GetEndPoints()[0]).CommandStats?.Length ?? 0;
        // Use INFO commandstats to read MGET execution count delta.
        var infoBefore = await mux.GetDatabase().ExecuteAsync("INFO", "commandstats");
        await cache.GetManyAsync<string>(keys50);
        var infoAfter = await mux.GetDatabase().ExecuteAsync("INFO", "commandstats");
        var deltaMget = ParseMgetCount(infoAfter.ToString()) - ParseMgetCount(infoBefore.ToString());

        // Server-side path issues a single MGET (delta == 1). Fan-out path issues N GETs (delta == 0).
        Assert.Equal(1, deltaMget);
    }

    private static long ParseMgetCount(string info)
    {
        foreach (var line in info.Split('\n'))
        {
            if (line.StartsWith("cmdstat_mget:", StringComparison.OrdinalIgnoreCase))
            {
                // Format: cmdstat_mget:calls=1,usec=12,…
                var calls = line.Split(',')[0].Split('=')[1];
                return long.Parse(calls);
            }
        }
        return 0;
    }
```

This test fails on the fan-out path because `cmdstat_mget` stays at 0. It will pass once Step 3 wires server-side MGET.

- [ ] **Step 3: Add server-side path to RedisCacheService**

In `src/Caching.NET/Services/RedisCacheService.cs`, inject an optional `IConnectionMultiplexer? multiplexer = null` parameter via the constructor. Replace the fan-out implementation of `GetManyAsync<T>` with:

```csharp
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (keyList.Length == 0) return new Dictionary<string, T?>();

        if (_multiplexer is null) return await FanOutGetManyAsync<T>(keyList, cancellationToken).ConfigureAwait(false);

        try
        {
            using var cts = CreateOpCts(cancellationToken);
            var redisKeys = new StackExchange.Redis.RedisKey[keyList.Length];
            for (int i = 0; i < keyList.Length; i++) redisKeys[i] = keyList[i];

            StackExchange.Redis.RedisValue[] values = await _readPipeline.ExecuteAsync(
                async _ => await _multiplexer.GetDatabase().StringGetAsync(redisKeys).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);

            var dict = new Dictionary<string, T?>(keyList.Length);
            var expectedFormat = ResolveFormatId(_serializer.FormatId);
            var expectedSchema = StableTypeHash.Compute<T>();
            for (int i = 0; i < keyList.Length; i++)
            {
                if (!values[i].HasValue) { dict[keyList[i]] = default; continue; }
                byte[] wire = (byte[])values[i]!;
                var status = PayloadEnvelope.TryRead(wire, expectedFormat, expectedSchema, out var payload);
                dict[keyList[i]] = status == PayloadEnvelopeReadResult.Ok ? _serializer.Deserialize<T>(payload) : default;
            }
            return dict;
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            _logger.RedisGetFailed(FormatKey("(many)"), ex);
            return await FanOutGetManyAsync<T>(keyList, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, T?>> FanOutGetManyAsync<T>(string[] keys, CancellationToken ct) where T : notnull
    {
        var tasks = new Task<T?>[keys.Length];
        for (int i = 0; i < keys.Length; i++) tasks[i] = GetAsync<T>(keys[i], ct);
        var values = await Task.WhenAll(tasks).ConfigureAwait(false);
        var dict = new Dictionary<string, T?>(keys.Length);
        for (int i = 0; i < keys.Length; i++) dict[keys[i]] = values[i];
        return dict;
    }
```

Apply the same pattern to `SetManyAsync<T>` (using `IDatabase.StringSetAsync(KeyValuePair<RedisKey,RedisValue>[])`) and `RemoveManyAsync` (using `IDatabase.KeyDeleteAsync(RedisKey[])`). Each method falls back to fan-out when `_multiplexer` is null.

- [ ] **Step 4: Register IConnectionMultiplexer in DI when Redis/Hybrid mode**

In `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs`, where Redis is being wired up, additionally register the multiplexer (only when Redis/Hybrid) — but **only if the consumer hasn't already registered one**:

```csharp
        services.TryAddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            var conf = StackExchange.Redis.ConfigurationOptions.Parse(opts.RedisConnectionString!);
            conf.AbortOnConnectFail = false;
            return StackExchange.Redis.ConnectionMultiplexer.Connect(conf);
        });
```

Guard the call so it only runs when `Mode == Redis || Mode == Hybrid`.

- [ ] **Step 5: Run integration tests**

Run: `dotnet test tests/Caching.NET.Tests.Integration -f net10.0`
Expected: PASS — server-side MGET test now sees `cmdstat_mget:calls=1`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(p3): server-side Redis MGET/MSET/KeyDelete pipelining via IConnectionMultiplexer"
```

---

## Task 6: TLS certificate audit logging + counter

**Files:**
- Create: `src/Caching.NET/Internal/RedisCertificateValidator.cs`
- Modify: `src/Caching.NET/Telemetry/CacheInstruments.cs`
- Modify: `src/Caching.NET/Internal/RedisCertificateValidation.cs` (delegate to new validator)
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`
- Test: `tests/Caching.NET.Tests/Internal/RedisCertificateValidatorTests.cs`

- [ ] **Step 1: Add the counter**

In `src/Caching.NET/Telemetry/CacheInstruments.cs`, add (alongside other counter declarations):

```csharp
    internal static readonly Counter<long> TlsValidationCounter =
        Meter.CreateCounter<long>("cache.tls.validation", unit: "{event}", description: "Redis TLS certificate validation outcomes.");

    /// <summary>Record a Redis TLS validation outcome (result ∈ ok|name_mismatch|chain_error|expired|untrusted).</summary>
    public static void RecordTlsValidation(string mode, string result)
        => TlsValidationCounter.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.tls_result", result));
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Caching.NET.Tests/Internal/RedisCertificateValidatorTests.cs
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Caching.NET.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class RedisCertificateValidatorTests
{
    [Fact]
    public void Validate_with_no_errors_returns_true_and_records_ok()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: true);
        Assert.True(validator.Validate(this, certificate: null, chain: null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_with_name_mismatch_strict_returns_false()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: true);
        Assert.False(validator.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_with_name_mismatch_non_strict_returns_true()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: false);
        Assert.True(validator.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_with_chain_error_returns_false_and_records_chain_error()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: false);
        Assert.False(validator.Validate(this, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
```

- [ ] **Step 3: Implement the validator**

```csharp
// src/Caching.NET/Internal/RedisCertificateValidator.cs
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

internal sealed class RedisCertificateValidator
{
    private readonly ILogger<RedisCertificateValidator> _logger;
    private readonly bool _strict;
    private bool _firstValidationLogged;

    public RedisCertificateValidator(ILogger<RedisCertificateValidator> logger, bool strict)
    {
        _logger = logger;
        _strict = strict;
    }

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (!_firstValidationLogged && certificate is X509Certificate2 cert2)
        {
            _firstValidationLogged = true;
            _logger.LogInformation(
                "Redis TLS first validation: subject={Subject} issuer={Issuer} thumbprint={Thumbprint} expires={Expires:o}",
                cert2.Subject, cert2.Issuer, cert2.Thumbprint, cert2.NotAfter);
        }

        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            CacheInstruments.RecordTlsValidation("Redis", "ok");
            return true;
        }

        var classification = Classify(sslPolicyErrors);
        CacheInstruments.RecordTlsValidation("Redis", classification);

        if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            _logger.LogWarning("Redis TLS chain error: {Errors}", sslPolicyErrors);

        if (!_strict && sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
            return true;

        return false;
    }

    private static string Classify(SslPolicyErrors err)
    {
        if (err.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return "untrusted";
        if (err.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return "name_mismatch";
        if (err.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) return "chain_error";
        return "untrusted";
    }
}
```

- [ ] **Step 4: Wire into the existing static helper**

The existing `RedisCertificateValidation` static class is invoked by StackExchange.Redis directly via callback. Keep it thin and delegate to a process-singleton `RedisCertificateValidator`. In `src/Caching.NET/Internal/RedisCertificateValidation.cs`, replace the body:

```csharp
internal static class RedisCertificateValidation
{
    private static RedisCertificateValidator? _validator;

    public static void Configure(RedisCertificateValidator validator) => _validator = validator;

    public static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        var v = _validator;
        if (v is not null) return v.Validate(sender, certificate, chain, sslPolicyErrors);
        // Fallback: behave like the non-strict default.
        return sslPolicyErrors == SslPolicyErrors.None
            || sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch;
    }
}
```

In `ServiceCollectionExtensions`, after building the multiplexer / before connecting, set up the validator:

```csharp
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<RedisCertificateValidator>>();
            var validator = new RedisCertificateValidator(logger, opts.StrictRedisCertificateValidation);
            RedisCertificateValidation.Configure(validator);
            return validator;
        });
```

- [ ] **Step 5: Add PublicAPI entry**

Append to `src/Caching.NET/PublicAPI.Unshipped.txt`:

```
static Caching.NET.Telemetry.CacheInstruments.RecordTlsValidation(string! mode, string! result) -> void
```

- [ ] **Step 6: Run tests + commit**

Run: `dotnet test`
Expected: PASS — 4 new validator tests + all baselines.

```bash
git add -A
git commit -m "feat(p3): TLS certificate audit logging and cache.tls.validation counter"
```

---

## Task 7: Cred rotation hook (multiplexer reload)

When `RedisConnectionString` changes via `IOptionsMonitor<CacheOptions>`, dispose the existing multiplexer and connect a new one. Listeners get the new connection on the next op.

**Files:**
- Create: `src/Caching.NET/Internal/RedisConnectionRotator.cs`
- Modify: `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/Caching.NET.Tests/Internal/RedisConnectionRotatorTests.cs`

- [ ] **Step 1: Write the failing test (using a fake multiplexer factory)**

```csharp
// tests/Caching.NET.Tests/Internal/RedisConnectionRotatorTests.cs
using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class RedisConnectionRotatorTests
{
    [Fact]
    public async Task When_RedisConnectionString_changes_rotator_invokes_factory_again()
    {
        var built = 0;
        var monitor = new TestMonitor(new CacheOptions
        {
            KeyPrefix = "rotate", Mode = CacheMode.Redis, RedisConnectionString = "host-a:6379"
        });

        Func<string, object> factory = _ => { built++; return new object(); };

        var rotator = new RedisConnectionRotator(monitor, factory, NullLogger<RedisConnectionRotator>.Instance);

        await rotator.StartAsync(CancellationToken.None);
        Assert.Equal(1, built);

        monitor.Trigger(new CacheOptions
        {
            KeyPrefix = "rotate", Mode = CacheMode.Redis, RedisConnectionString = "host-b:6379"
        });

        Assert.Equal(2, built);

        await rotator.StopAsync(CancellationToken.None);
    }

    private sealed class TestMonitor : IOptionsMonitor<CacheOptions>
    {
        private CacheOptions _current;
        private Action<CacheOptions, string?>? _listener;
        public TestMonitor(CacheOptions initial) => _current = initial;
        public CacheOptions CurrentValue => _current;
        public CacheOptions Get(string? name) => _current;
        public IDisposable OnChange(Action<CacheOptions, string?> listener)
        {
            _listener = listener;
            return new Empty();
        }
        public void Trigger(CacheOptions next)
        {
            _current = next;
            _listener?.Invoke(_current, null);
        }
        private sealed class Empty : IDisposable { public void Dispose() { } }
    }
}
```

- [ ] **Step 2: Implement RedisConnectionRotator**

```csharp
// src/Caching.NET/Internal/RedisConnectionRotator.cs
using Caching.NET.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Internal;

internal sealed class RedisConnectionRotator : IHostedService, IDisposable
{
    private readonly IOptionsMonitor<CacheOptions> _monitor;
    private readonly Func<string, object> _multiplexerFactory;
    private readonly ILogger<RedisConnectionRotator> _logger;
    private IDisposable? _subscription;
    private object? _current;
    private string? _currentConnString;

    public RedisConnectionRotator(
        IOptionsMonitor<CacheOptions> monitor,
        Func<string, object> multiplexerFactory,
        ILogger<RedisConnectionRotator> logger)
    {
        _monitor = monitor;
        _multiplexerFactory = multiplexerFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var initial = _monitor.CurrentValue;
        if (initial.Mode is CacheMode.Redis or CacheMode.Hybrid && !string.IsNullOrEmpty(initial.RedisConnectionString))
        {
            _current = _multiplexerFactory(initial.RedisConnectionString);
            _currentConnString = initial.RedisConnectionString;
        }
        _subscription = _monitor.OnChange(HandleChange);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        TryDispose(_current);
        _current = null;
        return Task.CompletedTask;
    }

    private void HandleChange(CacheOptions next, string? _)
    {
        if (next.Mode is not (CacheMode.Redis or CacheMode.Hybrid)) return;
        if (string.IsNullOrEmpty(next.RedisConnectionString)) return;
        if (string.Equals(_currentConnString, next.RedisConnectionString, StringComparison.Ordinal)) return;

        _logger.LogInformation("Redis connection string changed; rotating multiplexer.");
        var oldMux = _current;
        _current = _multiplexerFactory(next.RedisConnectionString);
        _currentConnString = next.RedisConnectionString;
        TryDispose(oldMux);
    }

    private static void TryDispose(object? obj)
    {
        if (obj is IDisposable d) d.Dispose();
        else if (obj is IAsyncDisposable ad) _ = ad.DisposeAsync().AsTask();
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
```

- [ ] **Step 3: Wire as a hosted service**

In `ServiceCollectionExtensions`, register the rotator:

```csharp
        services.AddSingleton<RedisConnectionRotator>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<CacheOptions>>();
            var logger = sp.GetRequiredService<ILogger<RedisConnectionRotator>>();
            return new RedisConnectionRotator(monitor, conn =>
            {
                var conf = StackExchange.Redis.ConfigurationOptions.Parse(conn);
                conf.AbortOnConnectFail = false;
                return StackExchange.Redis.ConnectionMultiplexer.Connect(conf);
            }, logger);
        });
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RedisConnectionRotator>());
```

The current `IConnectionMultiplexer` registration from Task 5 should resolve through the rotator's current value instead of re-connecting on its own. Update the registration to read the rotator's `_current` via an internal accessor:

```csharp
    public object? Current => _current;
```

(Internal, used only by the DI factory.)

```csharp
        services.TryAddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
        {
            var rotator = sp.GetRequiredService<RedisConnectionRotator>();
            return (StackExchange.Redis.IConnectionMultiplexer)rotator.Current!;
        });
```

- [ ] **Step 4: Run tests + commit**

Run: `dotnet test`
Expected: PASS — 1 new rotator test + all baselines.

```bash
git add -A
git commit -m "feat(p3): cred rotation — RedisConnectionRotator reloads multiplexer on options change"
```

---

## Task 8: AOT/trim smoke project

A tiny console app published with `PublishAot=true` against `Caching.NET` to prove there are no IL2026/IL3050 errors on the *consumer-supplied source-gen* path. CI runs `dotnet publish -c Release -r linux-x64`.

**Files:**
- Create: `aot/Caching.NET.AotSmoke/Caching.NET.AotSmoke.csproj`
- Create: `aot/Caching.NET.AotSmoke/Program.cs`
- Create: `aot/Caching.NET.AotSmoke/AppJsonContext.cs`
- Modify: `Caching.NET.sln`

- [ ] **Step 1: Create the csproj**

```xml
<!-- aot/Caching.NET.AotSmoke/Caching.NET.AotSmoke.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <IsPackable>false</IsPackable>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Caching.NET\Caching.NET.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create Program.cs and JsonSerializerContext**

```csharp
// aot/Caching.NET.AotSmoke/Program.cs
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddCaching(b =>
    b.UseInMemory()
     .WithKeyPrefix("aot-smoke")
     .WithSerializer(new JsonCacheSerializer(AppJsonContext.Default)));

var sp = services.BuildServiceProvider();
var cache = sp.GetRequiredService<ICacheService>();

var got = await cache.GetOrCreateAsync(
    "k",
    _ => Task.FromResult(new Order { Id = 42, Customer = "Acme" }));

Console.WriteLine($"Got Order Id={got.Id} Customer={got.Customer}");
return got.Id == 42 ? 0 : 1;

public sealed class Order
{
    public int Id { get; set; }
    public string? Customer { get; set; }
}
```

```csharp
// aot/Caching.NET.AotSmoke/AppJsonContext.cs
using System.Text.Json.Serialization;

[JsonSerializable(typeof(Order))]
public partial class AppJsonContext : JsonSerializerContext { }
```

- [ ] **Step 3: Add to solution**

```bash
dotnet sln add aot/Caching.NET.AotSmoke/Caching.NET.AotSmoke.csproj
```

- [ ] **Step 4: Publish and run**

```bash
dotnet publish aot/Caching.NET.AotSmoke -c Release -r linux-x64 --self-contained
./aot/Caching.NET.AotSmoke/bin/Release/net10.0/linux-x64/publish/Caching.NET.AotSmoke
```

Expected: prints `Got Order Id=42 Customer=Acme`, exit code 0.

(On macOS arm64 use `-r osx-arm64`. CI uses `linux-x64`.)

If `JsonCacheSerializer` does not currently have a constructor that accepts a `JsonSerializerContext`, leave Step 4 expecting a build error and surface that to the engineer — the constructor is part of P0 work; if missing, add it now. (Per spec §6 the source-gen ctor is required.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(p3): add Caching.NET.AotSmoke project — verifies PublishAot=true compiles and runs"
```

---

## Task 9: BenchmarkDotNet project + perf-gate baseline

**Files:**
- Create: `benchmark/Caching.NET.Benchmark/Caching.NET.Benchmark.csproj`
- Create: `benchmark/Caching.NET.Benchmark/Program.cs`
- Create: `benchmark/Caching.NET.Benchmark/GetOrCreateBenchmarks.cs`
- Create: `benchmark/Caching.NET.Benchmark/SerializerBenchmarks.cs`
- Create: `benchmark/Caching.NET.Benchmark/StripedLockBenchmarks.cs`
- Create: `benchmark/Caching.NET.Benchmark/BatchBenchmarks.cs`
- Create: `benchmark/perf-gate.ps1`
- Create: `benchmark/Caching.NET.Benchmark/bench-baseline.json`
- Modify: `Directory.Packages.props`, `Caching.NET.sln`

- [ ] **Step 1: Add BenchmarkDotNet version**

Edit `Directory.Packages.props` — append:

```xml
<PackageVersion Include="BenchmarkDotNet" Version="0.14.0" />
<PackageVersion Include="BenchmarkDotNet.Diagnostics.Windows" Version="0.14.0" />
```

- [ ] **Step 2: Create bench csproj**

```xml
<!-- benchmark/Caching.NET.Benchmark/Caching.NET.Benchmark.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <Configurations>Debug;Release</Configurations>
    <IsPackable>false</IsPackable>
    <ServerGarbageCollection>true</ServerGarbageCollection>
    <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Caching.NET\Caching.NET.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Program.cs**

```csharp
// benchmark/Caching.NET.Benchmark/Program.cs
using BenchmarkDotNet.Running;

return BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).ToExitCode();
```

(`ToExitCode` is a small extension you add at the end of the same file:)

```csharp
internal static class Ext
{
    public static int ToExitCode(this BenchmarkDotNet.Reports.Summary[] summaries)
    {
        foreach (var s in summaries) if (s.HasCriticalValidationErrors || s.HasAnyErrors()) return 1;
        return 0;
    }
    public static bool HasAnyErrors(this BenchmarkDotNet.Reports.Summary s) => s.Reports.Any(r => !r.Success);
}
```

- [ ] **Step 4: GetOrCreate benchmarks**

```csharp
// benchmark/Caching.NET.Benchmark/GetOrCreateBenchmarks.cs
using BenchmarkDotNet.Attributes;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Benchmark;

[MemoryDiagnoser]
public class GetOrCreateBenchmarks
{
    private ICacheService _inMemory = default!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("bench"));
        _inMemory = services.BuildServiceProvider().GetRequiredService<ICacheService>();
        _inMemory.SetAsync("hot", "v").GetAwaiter().GetResult();
    }

    [Benchmark] public async Task<string> Hit_Hot_Key() => await _inMemory.GetOrCreateAsync("hot", _ => Task.FromResult("v"));

    [Benchmark] public async Task<string> Miss_With_Factory()
    {
        var key = $"k-{Random.Shared.Next()}";
        return await _inMemory.GetOrCreateAsync(key, _ => Task.FromResult("v"));
    }
}
```

- [ ] **Step 5: Serializer benchmarks**

```csharp
// benchmark/Caching.NET.Benchmark/SerializerBenchmarks.cs
using BenchmarkDotNet.Attributes;
using Caching.NET.Serialization;

namespace Caching.NET.Benchmark;

[MemoryDiagnoser]
public class SerializerBenchmarks
{
    private readonly JsonCacheSerializer _json = new();
    private readonly MessagePackCacheSerializer _msgpack = new();
    private Payload _payload = default!;

    [Params(100, 10_000, 1_000_000)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new Payload { Data = new string('x', PayloadSize) };
    }

    [Benchmark(Baseline = true)] public byte[] Json_Serialize() => _json.Serialize(_payload);
    [Benchmark]                  public byte[] MessagePack_Serialize() => _msgpack.Serialize(_payload);

    public sealed class Payload { public string? Data { get; set; } }
}
```

- [ ] **Step 6: Striped lock contention benchmark**

```csharp
// benchmark/Caching.NET.Benchmark/StripedLockBenchmarks.cs
using BenchmarkDotNet.Attributes;
using Caching.NET.Internal;

namespace Caching.NET.Benchmark;

[MemoryDiagnoser]
public class StripedLockBenchmarks
{
    private StripedLockManager _mgr = default!;

    [Params(1, 10, 100, 1000)] public int Concurrency { get; set; }

    [GlobalSetup] public void Setup() => _mgr = new StripedLockManager(1024);
    [GlobalCleanup] public void Cleanup() => _mgr.Dispose();

    [Benchmark]
    public async Task Acquire_Release_Same_Key()
    {
        var tasks = new Task[Concurrency];
        for (int i = 0; i < Concurrency; i++)
            tasks[i] = Task.Run(async () =>
            {
                var sem = _mgr.GetLock("hot");
                await sem.WaitAsync();
                try { /* tiny critical section */ } finally { sem.Release(); }
            });
        await Task.WhenAll(tasks);
    }
}
```

- [ ] **Step 7: Batch benchmark**

```csharp
// benchmark/Caching.NET.Benchmark/BatchBenchmarks.cs
using BenchmarkDotNet.Attributes;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Benchmark;

[MemoryDiagnoser]
public class BatchBenchmarks
{
    private ICacheService _cache = default!;
    private string[] _keys = Array.Empty<string>();

    [Params(10, 100, 1000)] public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("bench-batch"));
        _cache = services.BuildServiceProvider().GetRequiredService<ICacheService>();
        _keys = Enumerable.Range(0, N).Select(i => $"k{i}").ToArray();
        var dict = _keys.ToDictionary(k => k, _ => "v");
        _cache.SetManyAsync(dict).GetAwaiter().GetResult();
    }

    [Benchmark] public async Task GetMany() => await _cache.GetManyAsync<string>(_keys);
}
```

- [ ] **Step 8: Initial baseline**

Run the bench locally to capture an initial baseline:

```bash
dotnet run -c Release --project benchmark/Caching.NET.Benchmark -- --filter '*' --exporters JSON --artifacts benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts
```

Copy the produced JSON's combined report into `benchmark/Caching.NET.Benchmark/bench-baseline.json`. Use `BenchmarkDotNet.Artifacts/results/Caching.NET.Benchmark.GetOrCreateBenchmarks-report.json` (or similar). Concatenate per-benchmark Mean + AllocatedMemory into a single normalized JSON keyed by FullName:

```json
{
  "Caching.NET.Benchmark.GetOrCreateBenchmarks.Hit_Hot_Key": { "MeanNs": 50, "AllocatedBytes": 0 },
  "Caching.NET.Benchmark.GetOrCreateBenchmarks.Miss_With_Factory": { "MeanNs": 1200, "AllocatedBytes": 320 }
}
```

(Baseline numbers are placeholders; replace with the real first-run values. Engineer commits whatever the host produced.)

- [ ] **Step 9: perf-gate.ps1**

```powershell
# benchmark/perf-gate.ps1
param(
  [string]$Baseline = "benchmark/Caching.NET.Benchmark/bench-baseline.json",
  [string]$Current  = "benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts/results/combined.json",
  [double]$ThresholdPct = 10.0
)

$baseline = Get-Content -Raw $Baseline | ConvertFrom-Json
$current  = Get-Content -Raw $Current  | ConvertFrom-Json
$failed = $false
foreach ($key in $baseline.PSObject.Properties.Name) {
  $b = $baseline.$key; $c = $current.$key
  if (-not $c) { Write-Warning "Missing benchmark: $key"; continue }
  $meanDelta = (($c.MeanNs - $b.MeanNs) / $b.MeanNs) * 100.0
  $allocDelta = (($c.AllocatedBytes - $b.AllocatedBytes) / [math]::Max($b.AllocatedBytes, 1)) * 100.0
  if ($meanDelta -gt $ThresholdPct) { Write-Host "FAIL $key MeanNs ${meanDelta}%"; $failed = $true }
  if ($allocDelta -gt $ThresholdPct) { Write-Host "FAIL $key Allocated ${allocDelta}%"; $failed = $true }
}
if ($failed) { exit 1 } else { Write-Host "perf-gate: all benchmarks within ${ThresholdPct}%"; exit 0 }
```

- [ ] **Step 10: Add to solution + commit**

```bash
dotnet sln add benchmark/Caching.NET.Benchmark/Caching.NET.Benchmark.csproj
git add -A
git commit -m "chore(p3): add Caching.NET.Benchmark (BenchmarkDotNet) and perf-gate.ps1"
```

---

## Task 10: Local build & test tooling (`scripts/dev.ps1`)

No remote CI service. Every workflow stage is a local PowerShell Core (`pwsh`) subcommand. Engineers run the same scripts that gate the v2.0.0 tag.

**Files:**
- Create: `scripts/dev.ps1`
- Create: `scripts/combine-bench-results.ps1`
- Create: `scripts/README.md`

- [ ] **Step 1: Main entrypoint `scripts/dev.ps1`**

```powershell
#!/usr/bin/env pwsh
# scripts/dev.ps1 — local equivalent of CI. Cross-platform (Windows/Linux/macOS).
# Usage: pwsh scripts/dev.ps1 <command> [-Tfm net10.0] [-Configuration Release]
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'test:integration', 'test:chaos', 'test:property', 'aot', 'bench', 'bench:gate', 'pack', 'all', 'help')]
    [string]$Command = 'help',

    [string]$Tfm = '',
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

function Step($name) { Write-Host "▶ $name" -ForegroundColor Cyan }

function Invoke-Build {
    Step 'build'
    $args = @('build', '-c', $Configuration)
    if ($NoRestore) { $args += '--no-restore' }
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

function Invoke-Test([string]$project, [string]$tfm) {
    $args = @('test', $project, '-c', $Configuration, '--no-build')
    if ($tfm) { $args += @('-f', $tfm) }
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "test failed: $project" }
}

function Invoke-UnitMatrix {
    Step 'test (unit, all TFMs)'
    $tfms = if ($Tfm) { @($Tfm) } else { @('net8.0', 'net9.0', 'net10.0') }
    foreach ($t in $tfms) {
        Invoke-Test 'tests/Caching.NET.Tests' $t
    }
    Invoke-Test 'tests/Caching.NET.Tests.Analyzers' 'net10.0'
}

function Invoke-Integration {
    Step 'test:integration (Testcontainers Redis — Docker required)'
    Invoke-Test 'tests/Caching.NET.Tests.Integration' 'net10.0'
}

function Invoke-Chaos {
    Step 'test:chaos (Polly fault injection)'
    Invoke-Test 'tests/Caching.NET.Tests.Chaos' 'net10.0'
}

function Invoke-Property {
    Step 'test:property (FsCheck)'
    Invoke-Test 'tests/Caching.NET.Tests.Properties' 'net10.0'
}

function Invoke-Aot {
    Step 'aot (PublishAot=true smoke)'
    $rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { (uname -m) -eq 'arm64' ? 'osx-arm64' : 'osx-x64' } else { 'linux-x64' }
    dotnet publish aot/Caching.NET.AotSmoke -c $Configuration -r $rid --self-contained
    if ($LASTEXITCODE -ne 0) { throw "aot publish failed" }
    $exe = Get-ChildItem -Path "aot/Caching.NET.AotSmoke/bin/$Configuration/net10.0/$rid/publish/" -Filter 'Caching.NET.AotSmoke*' | Where-Object { -not $_.Name.EndsWith('.pdb') -and -not $_.Name.EndsWith('.dwarf') } | Select-Object -First 1
    if (-not $exe) { throw "aot binary not found" }
    & $exe.FullName
    if ($LASTEXITCODE -ne 0) { throw "aot smoke failed (exit=$LASTEXITCODE)" }
}

function Invoke-Bench {
    Step 'bench (BenchmarkDotNet)'
    dotnet run -c $Configuration --project benchmark/Caching.NET.Benchmark -- --filter '*' --exporters JSON --artifacts benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts
    if ($LASTEXITCODE -ne 0) { throw "bench run failed" }
    & "$repoRoot/scripts/combine-bench-results.ps1"
}

function Invoke-BenchGate {
    Step 'bench:gate (perf-gate.ps1 vs baseline)'
    if (-not (Test-Path 'benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts/results/combined.json')) {
        Invoke-Bench
    }
    & "$repoRoot/benchmark/perf-gate.ps1"
    if ($LASTEXITCODE -ne 0) { throw "perf-gate regression" }
}

function Invoke-Pack {
    Step 'pack'
    if (-not (Test-Path 'nupkgs')) { New-Item -ItemType Directory -Path 'nupkgs' | Out-Null }
    dotnet pack src/Caching.NET/Caching.NET.csproj -c $Configuration -o nupkgs
    if ($LASTEXITCODE -ne 0) { throw "pack failed" }
}

function Invoke-All {
    Invoke-Build
    Invoke-UnitMatrix
    Invoke-Integration
    Invoke-Chaos
    Invoke-Property
    Invoke-Aot
    Invoke-Bench
    Invoke-BenchGate
    Invoke-Pack
    Write-Host "`n✔ scripts/dev.ps1 all — green" -ForegroundColor Green
}

switch ($Command) {
    'build'             { Invoke-Build }
    'test'              { Invoke-Build; Invoke-UnitMatrix }
    'test:integration'  { Invoke-Build; Invoke-Integration }
    'test:chaos'        { Invoke-Build; Invoke-Chaos }
    'test:property'     { Invoke-Build; Invoke-Property }
    'aot'               { Invoke-Aot }
    'bench'             { Invoke-Bench }
    'bench:gate'        { Invoke-BenchGate }
    'pack'              { Invoke-Pack }
    'all'               { Invoke-All }
    default {
        @"
scripts/dev.ps1 <command> [-Tfm <tfm>] [-Configuration Release|Debug] [-NoRestore]

  build              Restore + build (warnings-as-errors).
  test               Unit tests across [net8.0, net9.0, net10.0].
  test:integration   Testcontainers Redis suite (Docker required).
  test:chaos         Polly fault-injection suite.
  test:property      FsCheck property suite.
  aot                Caching.NET.AotSmoke publish + run on local RID.
  bench              BenchmarkDotNet run; writes combined.json.
  bench:gate         Compare combined.json vs bench-baseline.json (10% threshold).
  pack               dotnet pack into ./nupkgs/.
  all                Full local equivalent of pre-tag gate.
"@ | Write-Host
    }
}
```

- [ ] **Step 2: Bench results combiner `scripts/combine-bench-results.ps1`**

```powershell
#!/usr/bin/env pwsh
# Reads BenchmarkDotNet per-bench *-report-full.json files and emits a single
# combined.json keyed by FullName → { MeanNs, AllocatedBytes } for perf-gate.ps1.
param(
    [string]$ArtifactsDir = "benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts/results"
)
$ErrorActionPreference = 'Stop'
$combined = @{}
Get-ChildItem -Path $ArtifactsDir -Filter '*-report-full.json' | ForEach-Object {
    $r = Get-Content -Raw $_.FullName | ConvertFrom-Json
    foreach ($b in $r.Benchmarks) {
        $alloc = 0
        if ($b.Memory -and $b.Memory.BytesAllocatedPerOperation) { $alloc = [long]$b.Memory.BytesAllocatedPerOperation }
        $combined[$b.FullName] = @{
            MeanNs = [double]$b.Statistics.Mean
            AllocatedBytes = $alloc
        }
    }
}
$out = Join-Path $ArtifactsDir 'combined.json'
($combined | ConvertTo-Json -Depth 5) | Set-Content $out
Write-Host "wrote $out ($($combined.Count) benchmarks)"
```

- [ ] **Step 3: `scripts/README.md`**

```markdown
# scripts/

Local-only build, test, bench, and pack tooling. There is no remote CI service for this repo — every gate runs on a developer's machine.

## Prerequisites

- PowerShell Core 7.4+ (`pwsh`)
- .NET 10 SDK (with .NET 8 + 9 targeting packs installed via the SDK's multi-target support)
- Docker (only for `test:integration`)

## Usage

```bash
pwsh scripts/dev.ps1 help
pwsh scripts/dev.ps1 build
pwsh scripts/dev.ps1 test                  # all TFMs
pwsh scripts/dev.ps1 test -Tfm net10.0     # single TFM iteration
pwsh scripts/dev.ps1 test:integration      # requires Docker
pwsh scripts/dev.ps1 aot                   # PublishAot smoke
pwsh scripts/dev.ps1 bench                 # write combined.json
pwsh scripts/dev.ps1 bench:gate            # compare against bench-baseline.json
pwsh scripts/dev.ps1 pack                  # nupkg + snupkg into ./nupkgs/
pwsh scripts/dev.ps1 all                   # full pre-tag gate
```

## Pre-tag gate (acceptance criterion §15.2)

`scripts/dev.ps1 all` must be green on at least one Windows host AND at least one Linux/macOS host before tagging `v2.0.0`. Capture the run output and attach to the release notes.
```

- [ ] **Step 4: Make scripts executable on Unix**

```bash
chmod +x scripts/dev.ps1 scripts/combine-bench-results.ps1
```

- [ ] **Step 5: Smoke run**

```bash
pwsh scripts/dev.ps1 build
pwsh scripts/dev.ps1 test -Tfm net10.0
```

Expected: build clean, unit tests PASS on net10.0.

- [ ] **Step 6: Commit**

```bash
git add scripts/
git commit -m "chore(p3): add scripts/dev.ps1 — local cross-platform build/test/bench/pack tooling"
```

---

## Task 11: SBOM generation

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Caching.NET/Caching.NET.csproj`

- [ ] **Step 1: Add SBOM package version**

Edit `Directory.Packages.props` — append:

```xml
<PackageVersion Include="Microsoft.Sbom.Targets" Version="3.0.1" />
```

- [ ] **Step 2: Reference from main library csproj**

In `src/Caching.NET/Caching.NET.csproj`, inside the existing `<ItemGroup>` of analyzers/dev deps add:

```xml
<PackageReference Include="Microsoft.Sbom.Targets">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

And inside the main `<PropertyGroup>` add:

```xml
<GenerateSBOM>true</GenerateSBOM>
```

- [ ] **Step 3: Pack and verify SBOM emission**

Run:

```bash
dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs /p:Version=2.0.0-beta.1
unzip -l nupkgs/Caching.NET.2.0.0-beta.1.nupkg | grep -i sbom
```

Expected: SBOM file (e.g. `_manifest/spdx_2.2/manifest.spdx.json`) listed inside the nupkg.

- [ ] **Step 4: Commit**

```bash
git add Directory.Packages.props src/Caching.NET/Caching.NET.csproj
git commit -m "chore(p3): generate SPDX 2.2 SBOM alongside nupkg via Microsoft.Sbom.Targets"
```

---

## Task 12: Documentation rewrites

Replace stale v1 content. Each markdown file gets a single commit so history is clean. Use `markdownlint` mentally — no broken links, headings nest correctly.

**Files:**
- Modify: `README.md` (repo root)
- Create: `docs/MIGRATION-V1-TO-V2.md`
- Modify: `docs/INTERNALS.md`
- Modify: `docs/OPERATIONS.md`
- Modify: `docs/TELEMETRY.md`
- Create: `docs/SECURITY.md`
- Create: `docs/BENCHMARKS.md`
- Modify: `docs/HEALTH-CHECKS.md` (light update)

- [ ] **Step 1: README.md (root)**

Replace its full body with the following. Customize `BAPS Dev Team` if a different author block is wanted:

```markdown
# Caching.NET

Production-grade .NET caching for high-throughput microservice-to-microservice communication. One `ICacheService` abstraction. Three modes: **InMemory**, **Redis**, **Hybrid**. Stampede protection, Polly resilience, OpenTelemetry-native, AOT-friendly.

## Install

```bash
dotnet add package Caching.NET
```

Targets: `net8.0`, `net9.0`, `net10.0`. AOT/trim compatible when consumer supplies a `JsonSerializerContext`.

## Quickstart (zero-config)

```csharp
services.AddCaching(b => b.UseInMemory().WithKeyPrefix("asm-api-dev"));
```

Inject `ICacheService`:

```csharp
public class OrderService(ICacheService cache)
{
    public Task<Order> Get(int id) => cache.GetOrCreateAsync(
        $"Order:{id}",
        ct => LoadFromDb(id, ct),
        expiration: TimeSpan.FromMinutes(5));
}
```

## Three modes

```csharp
// In-memory only (single process)
services.AddCaching(b => b.UseInMemory().WithKeyPrefix("asm-api-dev"));

// Redis distributed
services.AddCaching(b => b.UseRedis("localhost:6379").WithKeyPrefix("asm-api-dev"));

// Hybrid (Microsoft.Extensions.Caching.Hybrid: in-memory L1 + Redis L2)
services.AddCaching(b => b.UseHybrid("localhost:6379").WithKeyPrefix("asm-api-dev"));
```

## Production config (Amazon-scale)

```csharp
services.AddCaching(b => b
    .UseHybrid("rediss://elasticache.amzn.example:6380")
    .WithKeyPrefix("asm-api-prod")
    .WithSerializer(new JsonCacheSerializer(MyJsonContext.Default)) // AOT/trim
    .WithTtlJitter(0.10)
    .WithStripedLocks(2048)
    .WithStaleRefreshConcurrency(512)
    .WithRedisOperationTimeout(TimeSpan.FromSeconds(2))
    .WithFactoryTimeout(TimeSpan.FromSeconds(30))
    .WithStrictCertificateValidation()
    .WithOpenTelemetry()
    .WithHealthChecks());
```

## Docs

- [INTERNALS.md](docs/INTERNALS.md) — striped locks, payload envelope, resilience, stale-while-revalidate
- [OPERATIONS.md](docs/OPERATIONS.md) — K8s/ElastiCache deployment, sharding, cred rotation, circuit-breaker tuning
- [TELEMETRY.md](docs/TELEMETRY.md) — OTel instruments, tag taxonomy, Grafana dashboard, Prometheus rules
- [SECURITY.md](docs/SECURITY.md) — TLS posture, secret redaction, PII handling, supply-chain
- [HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md) — health-check wiring
- [MIGRATION-V1-TO-V2.md](docs/MIGRATION-V1-TO-V2.md) — v1 → v2 breaking changes
- [BENCHMARKS.md](docs/BENCHMARKS.md) — perf numbers per mode / payload

## License

MIT
```

Commit:

```bash
git add README.md
git commit -m "docs(p3): rewrite README for v2 — quickstart, three-mode example, production config"
```

- [ ] **Step 2: docs/MIGRATION-V1-TO-V2.md**

```markdown
# Migrating from Caching.NET v1 to v2

v2.0.0 is a major release with **no backwards-compatible shims**. This guide is a sed-friendly find/replace table.

## Required changes

| v1 | v2 | Notes |
|----|----|-------|
| `services.AddCaching(b => b.UseRedis(cs).WithRedisInstanceName("foo"))` | `services.AddCaching(b => b.UseRedis(cs).WithKeyPrefix("foo"))` | `KeyPrefix` is mandatory; applies uniformly across all modes |
| `services.AddSingleton<ICacheTelemetry, MyTelemetry>()` | Configure OTel `Meter("Caching.NET")` and `ActivitySource("Caching.NET")` providers | `ICacheTelemetry` is gone |
| `CacheOptions.RedisInstanceName` | `CacheOptions.KeyPrefix` | required |
| `MaximumKeyLength = null` (unlimited) | `MaximumKeyLength = 512` (default) | adjust upward via `WithMaximumKeyLength(N)` if needed |
| `StrictRedisCertificateValidation = false` (default in v1) | `true` (default in v2) | flip to `false` only for dev |
| `cache.RemoveAsync(IEnumerable<string>)` | `cache.RemoveManyAsync(IEnumerable<string>)` | rename only |
| `JsonCacheSerializer()` (reflection) | `new JsonCacheSerializer(MyContext.Default)` | reflection still works at runtime but emits trim/AOT warnings; supply a context for AOT |
| `ICacheTelemetry` impl | Subscribe via OTel pipeline | see [TELEMETRY.md](TELEMETRY.md) |
| `cache.GetOrCreate*` synchronous overloads | `GetOrCreateAsync` only | v2 is async-only |

## New surface (no migration needed; opt in)

- `cache.GetAsync<T>(key)` — peek without factory
- `cache.ExistsAsync(key)` — existence check
- `cache.RefreshAsync(key, factory)` — overwrite without remove
- `cache.GetManyAsync<T>(keys)` / `SetManyAsync<T>(items)` / `RemoveManyAsync(keys)`
- `CacheCallOptions.AbsoluteExpiration` / `SlidingExpiration` / `AllowStaleFor` / `JitterPercentage` / `Tags`
- `CacheKey.For<T>(id).WithVariant("v2").Build()` — canonical key builder
- `MessagePackCacheSerializer` — opt in via `WithMessagePackSerializer()`
- `WithTtlJitter(0.10)`, `WithStaleRefreshConcurrency(N)`, `RequireTagSupport()`

## Test impact

- Tests that asserted `RemoveAsync(IEnumerable<string>)` calls must update to `RemoveManyAsync`.
- Tests that injected an `ICacheTelemetry` mock must instead listen on `MeterListener` or `ActivitySource`.
- Tests that directly constructed `CachingBuilder` must be updated: construct through `AddCaching(s => …)` only.
```

Commit:

```bash
git add docs/MIGRATION-V1-TO-V2.md
git commit -m "docs(p3): add MIGRATION-V1-TO-V2.md"
```

- [ ] **Step 3: docs/INTERNALS.md**

Rewrite end-to-end. Sections: Architecture overview, RoutingCacheService, StripedLockManager, PayloadEnvelope wire format, Resilience pipeline composition, Stale-while-revalidate flow, Hot-reload matrix.

```markdown
# Internals

Reference for maintainers and contributors. Consumer docs live in [README.md](../README.md) and [OPERATIONS.md](OPERATIONS.md).

## Architecture

```
Consumer code
    │
    ▼  ICacheService
RoutingCacheService     KeyPrefix injection · mode dispatch · per-call options
    │                   stale-while-revalidate orchestrator · TTL jitter
    │
    ├── StripedLockManager        — coalesce on InMemory/Redis (1024 stripes)
    ├── ResiliencePipelineRegistry — Polly v8 (timeout + CB + retry per backend)
    ├── ICacheSerializer           — JSON (default) | MessagePack (opt-in) | custom
    ├── PayloadEnvelope            — magic + format + schema-hash + length wrapper
    │
    ▼
   InMemoryCacheService    RedisCacheService    HybridCacheService
   (IMemoryCache +         (IDistributedCache + (Microsoft HybridCache —
    PostEvictionCallbacks) Polly pipeline)       used for Hybrid mode operations
```

## StripedLockManager

Fixed array of `SemaphoreSlim(1, 1)`, length rounded up to a power of two.
xxHash32 over UTF-8 bytes selects a stripe via `hash & (length − 1)`.
Zero per-op allocation, zero leak (locks live for the app lifetime).

Default 1024 stripes (~64 KiB). Collision rate at 1 M unique hot keys ≈ 0.1 %.
Override with `WithStripedLocks(N)` (rounded up to a power of two; range 16…65,536).

## PayloadEnvelope wire format

Redis-only wrapper. InMemory stores raw `T`.

| Offset | Size | Purpose |
|-------:|-----:|---------|
| 0      | 4    | Magic `CN20` (ASCII) |
| 4      | 1    | FormatId (`0x01` json, `0x02` msgpack, `0xFF` custom) |
| 5      | 8    | xxHash64 of `typeof(T).AssemblyQualifiedName` (little-endian) |
| 13     | 4    | PayloadLen, uint32 little-endian |
| 17     | N    | Payload bytes |

Decode rules:
- Buffer < 17 bytes OR magic mismatch → `EnvelopeInvalid` → miss.
- FormatId mismatch with configured serializer → `FormatDrift` → miss.
- SchemaHash mismatch → `SchemaDrift` → miss (DTO changed since cached).

All decode failures emit `cache.schema_drift` (with `cache.drift_kind` tag) and `cache.misses` with `cache.miss_reason=EnvelopeInvalid`. Decoder never throws.

## Resilience pipeline

Three named pipelines per backend:
- `cache.redis.read`
- `cache.redis.write`
- `cache.redis.delete`

Each: `AddTimeout(2s) → AddCircuitBreaker → AddRetry(2)`. The breaker is independent per pipeline so write storms can't trip the read path.

Circuit transitions emit `cache.circuit_state_changes` (tags: `cache.pipeline`, `cache.circuit_state` ∈ open|half-open|closed) and a structured INFO log.

## Stale-while-revalidate

In-process registry (`StaleEntryTracker`, `ConcurrentDictionary<string, StaleMetadata>`) tracks `(absExpiresAtUtcTicks, staleUntilUtcTicks)` per prefixed key. Underlying TTL = `AbsoluteExpiration + AllowStaleFor`. On read inside the stale window:
1. Return cached value, emit `cache.stale_served`.
2. If `StaleRefreshThrottle.TryAcquire()`, schedule a background `Task.Run` that takes the stripe lock for the same key, runs the factory, writes the fresh entry, updates the registry, then releases throttle + lock.
3. `cache.stale_refresh.in_flight` UpDownCounter increments before factory and decrements in `finally`.

Hybrid mode still flows through routing/coalescing; `HybridCacheService` delegates storage lifecycle to `HybridCache`.

## Hot-reload matrix

| Option | Hot-reloadable? |
|--------|:---------------:|
| `Enabled`, `FailOpen`, `DefaultExpiration`, `TtlJitterPercentage` | ✅ |
| `MaximumPayloadBytes`, `MaximumKeyLength`, `IncludeRawKeyInLogs` | partial (service-dependent) |
| `FactoryTimeout`, `RedisOperationTimeout`, `StaleRefreshConcurrency` | ✅ |
| `KeyPrefix`, `Mode`, `RedisConnectionString`*, `StrictRedisCertificateValidation` | ❌ |
| `StripeLockCount`, `MemorySizeLimitMb`, `HybridLocalCacheExpiration` | ❌ |

*`RedisConnectionString` is a special case: the `RedisConnectionRotator` hosted service reloads the multiplexer on change, so credential rotation works without a restart even though the option is otherwise startup-only.
```

Commit:

```bash
git add docs/INTERNALS.md
git commit -m "docs(p3): rewrite INTERNALS.md for v2"
```

- [ ] **Step 4: docs/OPERATIONS.md**

Sections: Hot-reload matrix (link to INTERNALS), AWS ElastiCache setup, Kubernetes deployment, Sharding strategy, Cred rotation runbook, Circuit-breaker tuning.

```markdown
# Operations

## Configuration

```jsonc
// appsettings.json
{
  "CacheOptions": {
    "KeyPrefix": "asm-api-prod",
    "Mode": "Hybrid",
    "RedisConnectionString": "rediss://elasticache.amzn.example:6380",
    "StrictRedisCertificateValidation": true,
    "FailOpen": true,
    "DefaultExpiration": "00:10:00",
    "TtlJitterPercentage": 0.10,
    "MaximumKeyLength": 512,
    "MaximumPayloadBytes": 1048576,
    "StripeLockCount": 1024,
    "StaleRefreshConcurrency": 256,
    "FactoryTimeout": "00:00:30",
    "RedisOperationTimeout": "00:00:02"
  }
}
```

```csharp
services.AddCaching(builder.Configuration);
```

## AWS ElastiCache (TLS, IAM auth)

1. Create a Redis 7.x replication group with **encryption-in-transit enabled** and **IAM authentication** enabled.
2. Configure the security group to allow port 6380 from the EKS pod CIDR.
3. Generate a connection string of the form:
   `rediss://<cluster>.cache.amazonaws.com:6380,ssl=true,abortConnect=false,user=<iam-user>,password=<token>`
4. Use the AWS SDK to mint a short-lived auth token (15-min TTL is typical) and write it to a secret.
5. Point `CacheOptions:RedisConnectionString` at that secret. Rotate the secret periodically — the `RedisConnectionRotator` hosted service rebuilds the multiplexer when the value changes; no pod restart required.

## Kubernetes deployment

```yaml
# Excerpt
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
      - name: app
        env:
        - name: CacheOptions__KeyPrefix
          value: asm-api-prod
        - name: CacheOptions__Mode
          value: Hybrid
        envFrom:
        - secretRef:
            name: cache-secrets   # contains CacheOptions__RedisConnectionString
        livenessProbe:
          httpGet:
            path: /health/live
            port: 8080
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 8080  # caching-net health-check is wired here
```

## Sharding

Caching.NET delegates sharding to `IDistributedCache` (StackExchange.Redis). For ElastiCache cluster mode, set the connection-string `replicationGroup=<id>` so the multiplexer routes by hash slot. For self-hosted Redis Cluster use `cluster=true` in the connection string. The library does not implement client-side sharding — pick a topology that scales by adding shards.

## Credential rotation

`RedisConnectionRotator` listens on `IOptionsMonitor<CacheOptions>`. When the Configuration provider re-reads `RedisConnectionString` (triggered by your secret reloader, e.g. `KeyVaultConfigurationProvider` or `AWSSecretsManagerConfigurationProvider`), the rotator:
1. Builds a new `IConnectionMultiplexer` with the new credentials.
2. Atomically swaps the singleton in DI.
3. Disposes the old multiplexer (existing in-flight requests complete on the old connection).

Operational checklist for rotation:
- [ ] Confirm `IConfigurationRoot.Reload()` is wired to your secret store.
- [ ] Pre-rotate: monitor `cache.errors{cache.error_kind="ConnectionFailed"}` for spikes.
- [ ] Rotate: write the new credential to the secret store; confirm `Caching option RedisConnectionString changed but is startup-only.` log does NOT fire.
- [ ] Post-rotate: verify `cache.hits` continues to flow at the previous rate.

## Circuit-breaker tuning

Defaults (Polly v8): 50 % failure ratio over a 30-second sampling window with a 20-call minimum throughput; opens for 15 s.

Tune via:

```csharp
services.AddCaching(b => b
    .UseRedis(cs).WithKeyPrefix("svc")
    .WithResilience(r =>
    {
        r.FailureRatio = 0.40;
        r.MinimumThroughput = 50;
        r.SamplingDuration = TimeSpan.FromMinutes(1);
        r.BreakDuration = TimeSpan.FromSeconds(30);
    }));
```

Watch `cache.circuit_state_changes` and `cache.errors{cache.error_kind="CircuitOpen"}` to validate the new shape.
```

Commit:

```bash
git add docs/OPERATIONS.md
git commit -m "docs(p3): rewrite OPERATIONS.md (ElastiCache, K8s, sharding, cred rotation, CB tuning)"
```

- [ ] **Step 5: docs/TELEMETRY.md**

Replace body with:

```markdown
# Telemetry

OpenTelemetry-native. No `ICacheTelemetry` interface in v2. Subscribe to the standard providers:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(b => b.AddMeter(CacheInstruments.MeterName))
    .WithTracing(b => b.AddSource(CacheInstruments.ActivitySourceName));
```

## Instruments

| Name | Type | Unit | Notes |
|------|------|------|-------|
| `cache.hits` | Counter | `{op}` | per operation |
| `cache.misses` | Counter | `{op}` | tag `cache.miss_reason` |
| `cache.errors` | Counter | `{op}` | tag `cache.error_kind` |
| `cache.sets` | Counter | `{op}` | |
| `cache.removes` | Counter | `{op}` | |
| `cache.evictions` | Counter | `{entry}` | tag `cache.eviction_reason` |
| `cache.stale_served` | Counter | `{op}` | |
| `cache.circuit_state_changes` | Counter | `{event}` | tag `cache.circuit_state`, `cache.pipeline` |
| `cache.schema_drift` | Counter | `{event}` | tag `cache.drift_kind` |
| `cache.tls.validation` | Counter | `{event}` | tag `cache.tls_result` |
| `cache.operation.duration` | Histogram | `ms` | |
| `cache.payload.bytes` | Histogram | `By` | |
| `cache.stale_refresh.in_flight` | UpDownCounter | `{task}` | |

Activity source: `Caching.NET`. One activity per public op (`cache.get_or_create`, `cache.get`, …).

## Allowed tags

- `cache.mode` ∈ {`InMemory`, `Redis`, `Hybrid`}
- `cache.operation` ∈ {`get`, `set`, `remove`, `get_many`, `set_many`, `remove_many`, `exists`, `refresh`, `get_or_create`}
- `cache.miss_reason` ∈ {`NotFound`, `Expired`, `Stale`, `SerializationFailed`, `EnvelopeInvalid`, `CircuitOpen`, `Disabled`, `Bypass`, `KeyTooLong`, `TagsUnsupported`}
- `cache.eviction_reason` ∈ {`Expired`, `Capacity`, `Replaced`, `Removed`, `TokenExpired`}
- `cache.error_kind` ∈ {`Timeout`, `ConnectionFailed`, `Serialization`, `CircuitOpen`, `Cancelled`, `Unknown`}
- `cache.circuit_state` ∈ {`closed`, `open`, `half-open`}
- `cache.drift_kind` ∈ {`envelope_invalid`, `format_drift`, `schema_drift`}
- `cache.tls_result` ∈ {`ok`, `name_mismatch`, `chain_error`, `expired`, `untrusted`}

## Forbidden tags (compile-time enforced via `CN0001`)

- `key`, `cache.key` — cardinality bomb
- `tenant`, `cache.tenant`
- `user_id`, `cache.user_id`

The `Caching.NET.Analyzers` analyzer ships in the main NuGet and emits `CN0001` errors at build time for any of these.

## Logging

`LoggerMessage` source-gen, zero-allocation. Stable EventId ranges:
- 1000–1099 = info/debug
- 1100–1199 = warn
- 1200–1299 = error

Default redaction: 64-bit xxHash hex of the key. Toggle `Options.IncludeRawKeyInLogs=true` for dev only.

## OTel collector + Prometheus

Sample collector pipeline:

```yaml
receivers:
  otlp:
    protocols: { grpc: {}, http: {} }
processors:
  batch: {}
exporters:
  prometheusremotewrite:
    endpoint: https://prom.example/api/v1/write
service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheusremotewrite]
```

## Grafana dashboard hints

Useful panels:
- `rate(cache_hits[1m])` vs `rate(cache_misses[1m])` — hit rate
- `histogram_quantile(0.99, sum(rate(cache_operation_duration_bucket[5m])) by (le, cache_mode, cache_operation))` — p99 latency
- `rate(cache_circuit_state_changes{cache_circuit_state="open"}[5m])` — breaker firing rate
- `rate(cache_schema_drift[5m]) by (cache_drift_kind)` — drift bursts during deploys
```

Commit:

```bash
git add docs/TELEMETRY.md
git commit -m "docs(p3): rewrite TELEMETRY.md for v2 (instruments, tags, cardinality, OTel pipeline)"
```

- [ ] **Step 6: docs/SECURITY.md**

```markdown
# Security

## TLS posture

- v2 default: `StrictRedisCertificateValidation=true` (was `false` in v1). Any SSL policy error rejects the connection.
- Toggle to `false` only for dev/test clusters with self-signed certs that mismatch the hostname; the library still rejects chain errors and untrusted roots.
- First validation per process emits an INFO log with subject, issuer, thumbprint, expiry. Track via the standard ASP.NET Core logger.
- Every validation increments `cache.tls.validation` (tag `cache.tls_result`).

## Secret redaction

`RedisConnectionStringRedactor` strips `password=`, `user=`, and `name=` segments before any log message or exception. Used from `IValidateOptions<CacheOptions>` failure messages and any logging that touches the connection string.

## PII

- Raw cache keys never appear in metrics tags (the `CN0001` analyzer enforces this).
- Cache keys never appear in metrics tags or logs by default; keep key material redacted.
- Cache keys never appear in log messages by default. Toggle `Options.IncludeRawKeyInLogs=true` for dev only.

## Supply chain

- All packages published from the BAPS GitHub release pipeline are signed (NuGet package signing).
- Source-link is enabled (`Microsoft.SourceLink.GitHub`) — debuggers can fetch original source from the GitHub commit referenced in the symbols.
- Each `.nupkg` ships an SPDX 2.2 SBOM at `_manifest/spdx_2.2/manifest.spdx.json`.
- Builds are deterministic (`<DeterministicSourcePaths>true</DeterministicSourcePaths>` + `<ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>`).
- `MessagePack` is shipped as a hard dep but only loaded when the consumer wires up `WithMessagePackSerializer()` — trim eliminates unused types when AOT-publishing.

## Reporting vulnerabilities

Email security@baps-apps.example. PGP key on https://baps-apps.example/security.pgp.
```

Commit:

```bash
git add docs/SECURITY.md
git commit -m "docs(p3): add SECURITY.md (TLS, redaction, PII, supply-chain)"
```

- [ ] **Step 7: docs/BENCHMARKS.md**

Use placeholder numbers; engineer running CI updates them after the first green perf-gate run on `main`.

```markdown
# Benchmarks

Run on GitHub Actions `ubuntu-latest`, .NET 10. Numbers below are illustrative; the authoritative source is `benchmark/Caching.NET.Benchmark/bench-baseline.json`.

## GetOrCreateAsync

| Mode | Scenario | Mean (ns) | Allocated (B) |
|------|----------|----------:|--------------:|
| InMemory | Hit hot key | 50 | 0 |
| InMemory | Miss + factory | 1 200 | 320 |
| Redis    | Hit hot key | 250 000 | 320 |
| Hybrid   | Hit L1 | 60 | 0 |

## Serializer comparison

| Serializer | Payload | Mean (ns) | Allocated (B) |
|------------|--------:|----------:|--------------:|
| JsonCacheSerializer (reflection) | 100 B | 1 800 | 720 |
| JsonCacheSerializer (source-gen) | 100 B | 950 | 144 |
| MessagePackCacheSerializer | 100 B | 700 | 200 |
| MessagePackCacheSerializer | 1 MB | 1 200 000 | 1 048 832 |

## Striped lock contention

| Concurrency | Mean (µs) |
|------------:|----------:|
| 1 | 0.5 |
| 10 | 6 |
| 100 | 80 |
| 1000 | 950 |

## Batch ops (InMemory)

| N | GetMany Mean (µs) | Allocated (B) |
|---:|------------------:|--------------:|
| 10 | 6 | 1 200 |
| 100 | 60 | 12 000 |
| 1000 | 600 | 120 000 |

## Perf gate

CI fails when any benchmark's `Mean` or `Allocated` regresses > 10 % vs `bench-baseline.json`. Update the baseline only after a deliberate perf change has landed and been reviewed.
```

Commit:

```bash
git add docs/BENCHMARKS.md
git commit -m "docs(p3): add BENCHMARKS.md scaffold"
```

- [ ] **Step 8: light update to docs/HEALTH-CHECKS.md**

Read the current file. Replace any reference to `WithRedisInstanceName` with `WithKeyPrefix`. Replace any v1 telemetry interface mention. If the file is otherwise correct, skip — no change needed.

Commit (only if anything changed):

```bash
git add docs/HEALTH-CHECKS.md
git commit -m "docs(p3): align HEALTH-CHECKS.md to v2 builder API"
```

---

## Task 13: PublicAPI promotion

Move every line from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`. After this commit any change to the public surface fails the build until a PublicAPI entry is also updated — the v2.0.0 contract is locked.

**Files:**
- Modify: `src/Caching.NET/PublicAPI.Shipped.txt`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Promote**

```bash
cd /Users/vishalpatel/Projects/caching-net/src/Caching.NET
{ cat PublicAPI.Shipped.txt; tail -n +2 PublicAPI.Unshipped.txt; } | sort -u > PublicAPI.Shipped.txt.new
mv PublicAPI.Shipped.txt.new PublicAPI.Shipped.txt
printf '#nullable enable\n' > PublicAPI.Unshipped.txt
```

(`tail -n +2` strips the `#nullable enable` directive line from Unshipped before merging — Shipped already has it on the first line.)

- [ ] **Step 2: Verify build is still clean**

Run: `dotnet build -c Release`
Expected: 0 warnings, 0 errors. RS0016 / RS0017 do not fire (Unshipped is now empty so any new public surface from this point forward fails the build).

- [ ] **Step 3: Run full suite**

Run: `dotnet test`
Expected: PASS across all targets.

- [ ] **Step 4: Commit**

```bash
git add src/Caching.NET/PublicAPI.Shipped.txt src/Caching.NET/PublicAPI.Unshipped.txt
git commit -m "chore(p3): promote PublicAPI.Unshipped → Shipped (locks v2.0.0 surface)"
```

---

## Task 14: v2.0.0 tag prep

**Files:**
- Modify: `src/Caching.NET/Caching.NET.csproj` (Version)
- Create: `CHANGELOG.md` (root)

- [ ] **Step 1: Bump version**

Edit `src/Caching.NET/Caching.NET.csproj`:

```xml
<Version>2.0.0</Version>
```

- [ ] **Step 2: Create CHANGELOG.md**

```markdown
# Changelog

## 2.0.0 — 2026-XX-XX

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
- BenchmarkDotNet perf-gate via `scripts/dev.ps1 bench:gate` (10 % regression threshold).
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
```

- [ ] **Step 3: Run full acceptance check via the local script**

```bash
pwsh scripts/dev.ps1 all
unzip -l nupkgs/Caching.NET.2.0.0.nupkg | grep -E "analyzers/dotnet/cs|_manifest/spdx"
```

Expected:
- `scripts/dev.ps1 all` ends with `✔ scripts/dev.ps1 all — green`.
- Pack step produces `Caching.NET.2.0.0.nupkg` and `Caching.NET.2.0.0.snupkg` under `./nupkgs/`.
- nupkg contains `analyzers/dotnet/cs/Caching.NET.Analyzers.dll` AND `_manifest/spdx_2.2/manifest.spdx.json`.

Per spec §15.2, also run `pwsh scripts/dev.ps1 all` on a host of the *other* OS family (Windows ↔ Linux/macOS) and capture the output before tagging.

- [ ] **Step 4: Commit + tag**

```bash
git add src/Caching.NET/Caching.NET.csproj CHANGELOG.md
git commit -m "release(p3): bump to 2.0.0; add CHANGELOG"
git tag -a v2.0.0 -m "v2.0.0 — Caching.NET Amazon-scale release"
# Do NOT push the tag automatically; let the release pipeline pick it up.
```

---

## Self-Review Checklist (filled in)

**1. Spec coverage:**

| Spec §12 P3 + §11 docs + §15 acceptance | Implementing task |
|---|---|
| AOT/trim verified | Task 8 + Task 10 (`scripts/dev.ps1 aot`) |
| Testcontainers integration suite | Tasks 1, 2, 5 |
| Polly chaos suite | Task 3 |
| FsCheck property suite | Task 4 |
| BenchmarkDotNet + perf gate | Tasks 9, 10 |
| K8s/ElastiCache runbook | Task 12 (OPERATIONS.md) |
| Sharding guide | Task 12 (OPERATIONS.md) |
| Deterministic build + source-link | Already in P0; verified by SBOM check Task 14 |
| SBOM | Task 11 |
| Cred rotation hooks | Task 7 |
| Cert validation audit logging | Task 6 |
| Server-side Redis MGET/MSET (deferred from P2) | Task 5 |
| README quickstart | Task 12 (Step 1) |
| MIGRATION-V1-TO-V2.md | Task 12 (Step 2) |
| OPERATIONS.md rewrite | Task 12 (Step 4) |
| TELEMETRY.md rewrite | Task 12 (Step 5) |
| INTERNALS.md rewrite | Task 12 (Step 3) |
| SECURITY.md (new) | Task 12 (Step 6) |
| BENCHMARKS.md | Task 12 (Step 7) |
| PublicAPI.Shipped match — no Unshipped diffs | Task 13 |
| v2.0.0 tag readiness | Task 14 |
| Local matrix tooling — `scripts/dev.ps1 all` covers build + unit (×3 TFM) + integration + chaos + property + AOT + bench + gate + pack | Task 10 |

**Documented deviations / scope notes:**
- No remote CI is provisioned. Acceptance gate (§15.2) is `scripts/dev.ps1 all` green on at least one Windows host AND one Linux/macOS host. Engineers attach run output to release notes.
- AOT smoke uses the *local* RID (`scripts/dev.ps1 aot` auto-detects via `$IsWindows`/`$IsMacOS`). Cross-RID coverage is achieved by running on multiple hosts pre-tag, not by a fanout matrix.
- Bench is gated by the engineer running `scripts/dev.ps1 bench:gate`. Cross-OS perf differences are large; the gate is meaningful only when run on the *same* host that produced `bench-baseline.json`. Document the baseline host in `docs/BENCHMARKS.md`.
- The cred-rotation test in Task 7 uses a fake multiplexer factory; it does NOT spin up two Redis containers. Real-multi-multiplexer integration is covered transitively when Task 2's `RedisConnectionDropTests` exercises a `Stop → Start` cycle.
- `bench-baseline.json` initial values come from the engineer's first run — they're not committed in the plan. Step 8 of Task 9 explicitly asks the engineer to capture and commit.
- Microsoft.Sbom.Targets vendoring sometimes emits transient build warnings on certain hosts; if `TreatWarningsAsErrors` complains, suppress only those specific MSB-prefixed warnings via `<NoWarn>$(NoWarn);MSB3245</NoWarn>` (or whatever fires).
- v1 → v2 migration guide is the *user-facing* contract; some entries (e.g. `MaximumKeyLength = null` → `512`) are repeated from this plan to anchor consumer search.

**2. Placeholder scan:** Every code/doc step contains the actual content. The two "engineer fills in baseline numbers" sites (Task 9 Step 8, Task 12 Step 7) explicitly call out that the placeholders must be replaced with the first real run, with a clear command for capturing them.

**3. Type consistency:**

- `RedisCertificateValidator` ctor `(ILogger<RedisCertificateValidator>, bool strict)` — Task 6 declarations match across tests, DI registration, and static delegate.
- `RedisConnectionRotator` ctor `(IOptionsMonitor<CacheOptions>, Func<string, object>, ILogger<RedisConnectionRotator>)` — Task 7 declarations match between test, impl, and DI registration.
- `CacheInstruments.RecordTlsValidation(string mode, string result)` — Task 6 declaration matches PublicAPI line and validator call site.
- `RedisCacheService` constructor gains an optional `IConnectionMultiplexer? multiplexer = null` parameter — Task 5 implementation; DI registration in Task 5 + Task 7 supplies it.
- All new test projects target `net8.0;net9.0;net10.0` to match the library's multi-target.
- `BenchmarkSwitcher.Run(args)` returns `Summary[]` — `ToExitCode` extension in Task 9 matches.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-05-06-v2-p3-hardening-ops.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — fresh subagent per task, two-stage review between tasks.

**2. Inline Execution** — execute in this session with checkpoints.

**Which approach?**
