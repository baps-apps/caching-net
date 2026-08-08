# Engine-Agnostic Cache Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `IFusionCache` public surface with a Caching.NET-owned `ICacheService` contract, and replace the engine's telemetry sources with Caching.NET's own, so the cache engine can be swapped without a consumer source change.

**Architecture:** A single internal adapter, `Internal/FusionCacheService`, implements `ICacheService` over `IFusionCache` and becomes the only type that calls an engine *operation*; `CacheEngineFactory` remains the only type that performs engine *setup*. Per-layer telemetry moves from the engine's activity sources and meters to Caching.NET-owned decorators around the memory cache, the distributed cache, the serializer and the backplane. Nothing else in the composition changes.

**Tech Stack:** .NET 10, C#, xUnit, FusionCache 2.6.0 (internal only), StackExchange.Redis (internal only), BenchmarkDotNet, Roslyn analyzers, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-08-engine-agnostic-cache-contract-design.md`

## Global Constraints

- Target framework `net10.0` only. SDK pinned in `global.json`.
- `TreatWarningsAsErrors` is on globally via `Directory.Build.props`, including NuGet audit warnings.
- Central package management: versions go in `Directory.Packages.props`, never in a `.csproj`.
- `GenerateDocumentationFile` is on for `src` — **every public member needs an XML doc comment** or the build fails.
- `CodeStyle.NET` analyzer runs on `src` and test projects.
- Tests use xUnit. Prefer a real in-memory Caching.NET cache (`TestHost.BuildInMemory()`) over a mock.
- Tests asserting the *absence* of a metric must be in the `caching-net-metrics` xUnit collection and must filter by cache name — a `MeterListener` observes the whole process.
- Integration and chaos tests poll for the observable outcome instead of sleeping, except where a TTL is the thing under test.
- Integration and chaos suites require Docker.
- No type from `ZiggyCreatures.Caching.Fusion`, `StackExchange.Redis`, or `Microsoft.Extensions.Caching.Memory` may appear in any public signature. This is enforced by a test added in Task 15.
- Everything Caching.NET emits is branded `Caching.NET`: logging categories, meter, activity source, metric names, configuration section.
- Build: `dotnet build`. Test: `dotnet test`. Single test: `dotnet test --filter "FullyQualifiedName~ClassName.MethodName"`.

## File Structure

**New files in `src/Caching.NET`:**

| File | Responsibility |
|---|---|
| `ICacheService.cs` | The public operation contract — 8 verbs, async and sync |
| `CacheValue.cs` | `CacheValue<TValue>` readonly struct, the read result |
| `CacheFactoryContext.cs` | `CacheFactoryContext<TValue>`, the factory execution context |
| `Options/CacheEntryOverrides.cs` | Per-call options; every property nullable |
| `Options/CacheEntryPriority.cs` | L1 eviction priority enum |
| `Internal/FusionCacheService.cs` | The adapter. The only type that calls an engine operation |
| `Internal/CacheEntryOverridesMapper.cs` | `CacheEntryOverrides` → `FusionCacheEntryOptions`, additively |
| `Internal/NullCacheService.cs` | `Enabled=false` implementation |
| `Internal/InstrumentedMemoryCache.cs` | L1 decorator emitting `cache.memory.*` spans and metrics |
| `Internal/InstrumentedDistributedCache.cs` | L2 decorator emitting `cache.redis.*` spans and metrics |

**Deleted:** `Internal/KeyGuardEntryOptionsProvider.cs`, `Telemetry/EngineTelemetryNames.cs`.

**Modified in `src/Caching.NET`:** `ICacheProvider.cs`, `CachingBuilder.cs`, `Internal/CacheProvider.cs`, `Internal/CacheInstance.cs`, `Internal/CacheEngineFactory.cs`, `Internal/CacheGuard.cs`, `Internal/CacheEventBridge.cs`, `Internal/InstrumentedBackplane.cs`, `Internal/RedisConnectionProvider.cs`, `Extensions/ServiceCollectionExtensions.cs`, `Extensions/CacheExtensions.cs`, `Health/CachingHealthCheck.cs`, `Options/CacheEntryOptions.cs`, `Options/RedisOptions.cs`, `Options/CacheSecurityOptions.cs`, `Options/CacheObservabilityOptions.cs`, `Telemetry/CacheTelemetry.cs`, `Telemetry/CacheTelemetryContext.cs`, `Telemetry/CacheTelemetryAttributes.cs`, `Validation/CachingOptionsValidator.cs`.

**Modified elsewhere:** `src/Caching.NET.Analyzers/CacheEntryOptionsAnalyzer.cs`, ~30 call-site files across `tests/`, `samples/`, `benchmark/`, `aot/`, plus documentation.

---

## Task 1: Value and options types

**Files:**
- Create: `src/Caching.NET/CacheValue.cs`
- Create: `src/Caching.NET/Options/CacheEntryPriority.cs`
- Create: `src/Caching.NET/Options/CacheEntryOverrides.cs`
- Test: `tests/Caching.NET.Tests/Caching/CacheValueTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Caching.NET.CacheValue<TValue>` with `HasValue`, `Value`, `GetValueOrDefault(TValue?)`, `static None`, `static Of(TValue)`, `Deconstruct(out bool, out TValue?)`. `Caching.NET.Options.CacheEntryPriority` with `Low`, `Normal`, `High`, `NeverRemove`. `Caching.NET.Options.CacheEntryOverrides` with the nullable properties listed below.

- [ ] **Step 1: Write the failing test**

Create `tests/Caching.NET.Tests/Caching/CacheValueTests.cs`:

```csharp
namespace Caching.NET.Tests.Caching;

public class CacheValueTests
{
    [Fact]
    public void None_HasNoValue()
    {
        var value = CacheValue<int>.None;

        Assert.False(value.HasValue);
        Assert.Equal(0, value.GetValueOrDefault());
        Assert.Equal(-1, value.GetValueOrDefault(-1));
    }

    [Fact]
    public void Of_CarriesTheValue()
    {
        var value = CacheValue<string>.Of("hello");

        Assert.True(value.HasValue);
        Assert.Equal("hello", value.Value);
        Assert.Equal("hello", value.GetValueOrDefault("fallback"));
    }

    [Fact]
    public void Value_OnEmpty_Throws()
    {
        var value = CacheValue<string>.None;

        Assert.Throws<InvalidOperationException>(() => value.Value);
    }

    [Fact]
    public void Of_Null_StillHasValue()
    {
        // A cached null is a hit, not a miss: the distinction is the whole point of the type.
        var value = CacheValue<string?>.Of(null);

        Assert.True(value.HasValue);
        Assert.Null(value.Value);
    }

    [Fact]
    public void Deconstruct_ExposesBothParts()
    {
        var (hasValue, value) = CacheValue<int>.Of(7);

        Assert.True(hasValue);
        Assert.Equal(7, value);
    }

    [Fact]
    public void Default_IsNone()
    {
        CacheValue<int> value = default;

        Assert.False(value.HasValue);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CacheValueTests"`
Expected: build failure — `CacheValue<>` does not exist.

- [ ] **Step 3: Create `src/Caching.NET/CacheValue.cs`**

```csharp
namespace Caching.NET;

/// <summary>
/// The result of a cache read: either a value, or the absence of one. A cached <c>null</c> is a
/// value, not an absence, which is why a nullable return type cannot express this.
/// </summary>
/// <typeparam name="TValue">The cached value type.</typeparam>
public readonly struct CacheValue<TValue> : IEquatable<CacheValue<TValue>>
{
    private readonly TValue _value;

    private CacheValue(TValue value, bool hasValue)
    {
        _value = value;
        HasValue = hasValue;
    }

    /// <summary>Whether a value was found.</summary>
    public bool HasValue { get; }

    /// <summary>The value found.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HasValue"/> is <c>false</c>.</exception>
    public TValue Value => HasValue
        ? _value
        : throw new InvalidOperationException("No value is present. Check HasValue before reading Value, or call GetValueOrDefault.");

    /// <summary>An empty result.</summary>
    public static CacheValue<TValue> None => default;

    /// <summary>A result carrying <paramref name="value"/>.</summary>
    /// <param name="value">The value, which may itself be <c>null</c>.</param>
    public static CacheValue<TValue> Of(TValue value) => new(value, hasValue: true);

    /// <summary>Returns the value, or <paramref name="fallback"/> when there is none.</summary>
    /// <param name="fallback">Returned when <see cref="HasValue"/> is <c>false</c>.</param>
    public TValue? GetValueOrDefault(TValue? fallback = default) => HasValue ? _value : fallback;

    /// <summary>Splits the result into its two parts.</summary>
    /// <param name="hasValue">Receives <see cref="HasValue"/>.</param>
    /// <param name="value">Receives the value, or <c>default</c> when there is none.</param>
    public void Deconstruct(out bool hasValue, out TValue? value)
    {
        hasValue = HasValue;
        value = HasValue ? _value : default;
    }

    /// <inheritdoc />
    public bool Equals(CacheValue<TValue> other)
        => HasValue == other.HasValue
        && (!HasValue || EqualityComparer<TValue>.Default.Equals(_value, other._value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CacheValue<TValue> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HasValue ? HashCode.Combine(true, _value) : 0;

    /// <summary>Equality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator ==(CacheValue<TValue> left, CacheValue<TValue> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator !=(CacheValue<TValue> left, CacheValue<TValue> right) => !left.Equals(right);
}
```

- [ ] **Step 4: Create `src/Caching.NET/Options/CacheEntryPriority.cs`**

```csharp
namespace Caching.NET.Options;

/// <summary>
/// Eviction priority for an entry held in the in-process memory layer. Entries with a lower
/// priority are evicted first when the memory layer is under size pressure.
/// </summary>
public enum CacheEntryPriority
{
    /// <summary>Evicted first.</summary>
    Low = 0,

    /// <summary>The default.</summary>
    Normal = 1,

    /// <summary>Evicted after <see cref="Normal"/> entries.</summary>
    High = 2,

    /// <summary>Never evicted for size pressure. Still expires normally.</summary>
    NeverRemove = 3
}
```

- [ ] **Step 5: Create `src/Caching.NET/Options/CacheEntryOverrides.cs`**

```csharp
namespace Caching.NET.Options;

/// <summary>
/// Per-call overrides applied on top of the cache's configured defaults. Every property is
/// nullable; <c>null</c> means "use the configured value".
/// </summary>
/// <remarks>
/// Overrides are <b>additive</b>. Supplying one property changes that property and nothing else —
/// the cache mode, the key guard, and every unspecified setting are preserved. There is no way to
/// build an options object that escapes the configured defaults.
/// </remarks>
/// <example>
/// <code><![CDATA[
/// await cache.SetAsync("Order:42", order, new CacheEntryOverrides
/// {
///     DistributedExpiration = TimeSpan.FromMinutes(1)
/// });
/// ]]></code>
/// </example>
public sealed class CacheEntryOverrides
{
    /// <summary>Lifetime of the copy held in the in-process memory layer.</summary>
    public TimeSpan? LocalExpiration { get; set; }

    /// <summary>Lifetime of the copy held in the distributed layer.</summary>
    public TimeSpan? DistributedExpiration { get; set; }

    /// <summary>Maximum random amount added to the entry's duration to spread expirations.</summary>
    public TimeSpan? JitterMaxDuration { get; set; }

    /// <summary>
    /// Fraction of the entry lifetime after which a read triggers a non-blocking background
    /// refresh while still returning the current value. Valid range <c>(0.0, 1.0)</c>.
    /// </summary>
    public float? EagerRefreshThreshold { get; set; }

    /// <summary>Whether an expired value may be served when the factory fails or times out.</summary>
    public bool? FailSafe { get; set; }

    /// <summary>How long past expiration a value stays eligible for fail-safe.</summary>
    public TimeSpan? FailSafeMaxDuration { get; set; }

    /// <summary>Minimum interval between two factory retries while fail-safe is serving.</summary>
    public TimeSpan? FailSafeThrottleDuration { get; set; }

    /// <summary>After this, a stale value is returned and the factory continues in the background.</summary>
    public TimeSpan? FactorySoftTimeout { get; set; }

    /// <summary>After this, the factory is abandoned even when no stale value exists.</summary>
    public TimeSpan? FactoryHardTimeout { get; set; }

    /// <summary>After this, the distributed layer is skipped when a memory value is available.</summary>
    public TimeSpan? DistributedSoftTimeout { get; set; }

    /// <summary>After this, the distributed layer is abandoned for this operation.</summary>
    public TimeSpan? DistributedHardTimeout { get; set; }

    /// <summary>Whether distributed writes may complete after the caller has been released.</summary>
    public bool? AllowBackgroundDistributedOperations { get; set; }

    /// <summary>Whether backplane publishes may complete after the caller has been released.</summary>
    public bool? AllowBackgroundBackplaneOperations { get; set; }

    /// <summary>Whether values handed back from the memory layer are deep-cloned.</summary>
    public bool? EnableAutoClone { get; set; }

    /// <summary>Eviction priority in the in-process memory layer.</summary>
    public CacheEntryPriority? Priority { get; set; }

    /// <summary>Relative size charged against the memory layer's configured size limit.</summary>
    public long? Size { get; set; }

    /// <summary>
    /// Suppresses the cross-instance invalidation broadcast for this write. Other instances keep
    /// serving their current in-process copy until it expires on its own.
    /// </summary>
    /// <remarks>
    /// Intended for bulk warm-up: writing many entries at startup without publishing one
    /// invalidation per entry to every other instance.
    /// </remarks>
    public bool? SkipBackplaneNotification { get; set; }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CacheValueTests"`
Expected: PASS, 6 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Caching.NET/CacheValue.cs src/Caching.NET/Options/CacheEntryPriority.cs src/Caching.NET/Options/CacheEntryOverrides.cs tests/Caching.NET.Tests/Caching/CacheValueTests.cs
git commit -m "feat: add CacheValue, CacheEntryPriority and CacheEntryOverrides"
```

---

## Task 2: Overrides mapper

**Files:**
- Create: `src/Caching.NET/Internal/CacheEntryOverridesMapper.cs`
- Test: `tests/Caching.NET.Tests/Internal/CacheEntryOverridesMapperTests.cs`

**Interfaces:**
- Consumes: `CacheEntryOverrides`, `CacheEntryPriority` from Task 1.
- Produces: `internal static class CacheEntryOverridesMapper` with `static FusionCacheEntryOptions? Resolve(CacheEntryOverrides? overrides, IFusionCache inner)`. Returns `null` when `overrides` is `null`.

**Context for the implementer:** the engine's `FusionCacheEntryOptions` *replaces* the cache's configured defaults when passed to a call. `IFusionCache.CreateEntryOptions()` returns a duplicate of those defaults, including the cache-mode skip flags (`SetSkipMemoryCache` for Redis mode, `SetSkipDistributedCache` for InMemory mode) set in `CacheEngineFactory.MapEntryOptions`. Starting from `CreateEntryOptions()` and applying only non-null overrides is what makes overrides additive.

Engine property names differ from ours. The mapping is: `DistributedExpiration`→`DistributedCacheDuration`, `LocalExpiration`→`MemoryCacheDuration`, `FailSafe`→`IsFailSafeEnabled`, `AllowBackgroundDistributedOperations`→`AllowBackgroundDistributedCacheOperations`, `DistributedSoftTimeout`→`DistributedCacheSoftTimeout`, `DistributedHardTimeout`→`DistributedCacheHardTimeout`, `SkipBackplaneNotification`→`SkipBackplaneNotifications`. The rest match by name.

- [ ] **Step 1: Write the failing test**

Create `tests/Caching.NET.Tests/Internal/CacheEntryOverridesMapperTests.cs`:

```csharp
using Caching.NET.Internal;
using Caching.NET.Options;

namespace Caching.NET.Tests.Internal;

public class CacheEntryOverridesMapperTests
{
    [Fact]
    public void NullOverrides_ResolveToNull()
    {
        using var host = TestHost.BuildInMemory();
        var inner = host.EngineCache();

        Assert.Null(CacheEntryOverridesMapper.Resolve(null, inner));
    }

    [Fact]
    public void EmptyOverrides_PreserveEveryDefault()
    {
        using var host = TestHost.BuildInMemory(c => c.WithDefaultExpiration(TimeSpan.FromMinutes(7)));
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(new CacheEntryOverrides(), inner);

        Assert.NotNull(resolved);
        Assert.Equal(TimeSpan.FromMinutes(7), resolved!.Duration);
    }

    [Fact]
    public void InMemoryMode_PreservesSkipDistributedCache_WhenOverridesSupplied()
    {
        using var host = TestHost.BuildInMemory();
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides { DistributedExpiration = TimeSpan.FromMinutes(1) },
            inner);

        Assert.True(resolved!.SkipDistributedCache);
    }

    [Fact]
    public void RedisMode_PreservesSkipMemoryCache_WhenOverridesSupplied()
    {
        using var host = TestHost.Build(c => c
            .UseRedis("localhost:6379,abortConnect=false")
            .WithApplicationPrefix("tests"));
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides { LocalExpiration = TimeSpan.FromMinutes(1) },
            inner);

        Assert.True(resolved!.SkipMemoryCache);
    }

    [Fact]
    public void EveryOverride_IsApplied()
    {
        using var host = TestHost.BuildInMemory();
        var inner = host.EngineCache();

        var resolved = CacheEntryOverridesMapper.Resolve(
            new CacheEntryOverrides
            {
                LocalExpiration = TimeSpan.FromSeconds(11),
                DistributedExpiration = TimeSpan.FromSeconds(22),
                JitterMaxDuration = TimeSpan.FromSeconds(3),
                EagerRefreshThreshold = 0.75f,
                FailSafe = true,
                FailSafeMaxDuration = TimeSpan.FromMinutes(5),
                FailSafeThrottleDuration = TimeSpan.FromSeconds(9),
                FactorySoftTimeout = TimeSpan.FromMilliseconds(120),
                FactoryHardTimeout = TimeSpan.FromMilliseconds(340),
                DistributedSoftTimeout = TimeSpan.FromMilliseconds(56),
                DistributedHardTimeout = TimeSpan.FromMilliseconds(78),
                AllowBackgroundDistributedOperations = false,
                AllowBackgroundBackplaneOperations = false,
                EnableAutoClone = true,
                Priority = CacheEntryPriority.NeverRemove,
                Size = 42,
                SkipBackplaneNotification = true
            },
            inner);

        Assert.NotNull(resolved);
        Assert.Equal(TimeSpan.FromSeconds(11), resolved!.MemoryCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(22), resolved.DistributedCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), resolved.JitterMaxDuration);
        Assert.Equal(0.75f, resolved.EagerRefreshThreshold);
        Assert.True(resolved.IsFailSafeEnabled);
        Assert.Equal(TimeSpan.FromMinutes(5), resolved.FailSafeMaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(9), resolved.FailSafeThrottleDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(120), resolved.FactorySoftTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(340), resolved.FactoryHardTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(56), resolved.DistributedCacheSoftTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(78), resolved.DistributedCacheHardTimeout);
        Assert.False(resolved.AllowBackgroundDistributedCacheOperations);
        Assert.False(resolved.AllowBackgroundBackplaneOperations);
        Assert.True(resolved.EnableAutoClone);
        Assert.Equal(Microsoft.Extensions.Caching.Memory.CacheItemPriority.NeverRemove, resolved.Priority);
        Assert.Equal(42, resolved.Size);
        Assert.True(resolved.SkipBackplaneNotifications);
    }
}
```

- [ ] **Step 2: Add the `EngineCache()` test helper**

The mapper needs the raw engine cache, which tests can still reach at this point because Task 6 has not yet removed the registration. Add to `tests/Caching.NET.Tests/TestHost.cs`:

```csharp
    /// <summary>
    /// The raw engine cache behind the default Caching.NET cache. Internal-only: used by tests that
    /// assert how Caching.NET maps onto the engine.
    /// </summary>
    public static IFusionCache EngineCache(this ServiceProvider provider)
        => provider.GetRequiredService<Caching.NET.Internal.CacheInstance>();
```

That will not compile — `CacheInstance` is keyed and not directly resolvable. Use the keyed form instead:

```csharp
    public static ZiggyCreatures.Caching.Fusion.IFusionCache EngineCache(this ServiceProvider provider)
        => provider.GetRequiredKeyedService<Caching.NET.Internal.CacheInstance>(CachingDefaults.DefaultCacheName).Cache;
```

`CacheInstance` is `internal`, so add an `InternalsVisibleTo` if one is not already present. Check first:

Run: `grep -rn "InternalsVisibleTo" src/Caching.NET/`
If absent, add to `src/Caching.NET/Caching.NET.csproj` inside an `<ItemGroup>`:

```xml
<InternalsVisibleTo Include="Caching.NET.Tests" />
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CacheEntryOverridesMapperTests"`
Expected: build failure — `CacheEntryOverridesMapper` does not exist.

- [ ] **Step 4: Create `src/Caching.NET/Internal/CacheEntryOverridesMapper.cs`**

```csharp
using Caching.NET.Options;
using Microsoft.Extensions.Caching.Memory;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Internal;

/// <summary>
/// Translates <see cref="CacheEntryOverrides"/> onto the engine's per-call entry options.
/// </summary>
/// <remarks>
/// The result always starts from <see cref="IFusionCache.CreateEntryOptions"/>, which duplicates the
/// cache's configured defaults including the cache-mode skip flags. Only non-null overrides are then
/// applied. This is what makes overrides additive: there is no code path that hands the engine an
/// options object built from scratch, so no call can escape the mode's guarantees.
/// </remarks>
internal static class CacheEntryOverridesMapper
{
    /// <summary>
    /// Returns <c>null</c> when no overrides were supplied, so the engine uses its configured
    /// defaults directly.
    /// </summary>
    public static FusionCacheEntryOptions? Resolve(CacheEntryOverrides? overrides, IFusionCache inner)
    {
        if (overrides is null)
        {
            return null;
        }

        var options = inner.CreateEntryOptions();

        if (overrides.LocalExpiration is { } localExpiration)
        {
            options.MemoryCacheDuration = localExpiration;
        }

        if (overrides.DistributedExpiration is { } distributedExpiration)
        {
            options.DistributedCacheDuration = distributedExpiration;
        }

        if (overrides.JitterMaxDuration is { } jitter)
        {
            options.JitterMaxDuration = jitter;
        }

        if (overrides.EagerRefreshThreshold is { } eagerRefresh)
        {
            options.EagerRefreshThreshold = eagerRefresh;
        }

        if (overrides.FailSafe is { } failSafe)
        {
            options.IsFailSafeEnabled = failSafe;
        }

        if (overrides.FailSafeMaxDuration is { } failSafeMax)
        {
            options.FailSafeMaxDuration = failSafeMax;
        }

        if (overrides.FailSafeThrottleDuration is { } failSafeThrottle)
        {
            options.FailSafeThrottleDuration = failSafeThrottle;
        }

        if (overrides.FactorySoftTimeout is { } factorySoft)
        {
            options.FactorySoftTimeout = factorySoft;
        }

        if (overrides.FactoryHardTimeout is { } factoryHard)
        {
            options.FactoryHardTimeout = factoryHard;
        }

        if (overrides.DistributedSoftTimeout is { } distributedSoft)
        {
            options.DistributedCacheSoftTimeout = distributedSoft;
        }

        if (overrides.DistributedHardTimeout is { } distributedHard)
        {
            options.DistributedCacheHardTimeout = distributedHard;
        }

        if (overrides.AllowBackgroundDistributedOperations is { } backgroundDistributed)
        {
            options.AllowBackgroundDistributedCacheOperations = backgroundDistributed;
        }

        if (overrides.AllowBackgroundBackplaneOperations is { } backgroundBackplane)
        {
            options.AllowBackgroundBackplaneOperations = backgroundBackplane;
        }

        if (overrides.EnableAutoClone is { } autoClone)
        {
            options.EnableAutoClone = autoClone;
        }

        if (overrides.Priority is { } priority)
        {
            options.Priority = MapPriority(priority);
        }

        if (overrides.Size is { } size)
        {
            options.Size = size;
        }

        if (overrides.SkipBackplaneNotification is { } skipBackplane)
        {
            options.SkipBackplaneNotifications = skipBackplane;
        }

        return options;
    }

    /// <summary>Maps the Caching.NET priority onto the memory layer's own enum.</summary>
    public static CacheItemPriority MapPriority(CacheEntryPriority priority) => priority switch
    {
        CacheEntryPriority.Low => CacheItemPriority.Low,
        CacheEntryPriority.High => CacheItemPriority.High,
        CacheEntryPriority.NeverRemove => CacheItemPriority.NeverRemove,
        _ => CacheItemPriority.Normal
    };
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CacheEntryOverridesMapperTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Internal/CacheEntryOverridesMapper.cs tests/Caching.NET.Tests/Internal/CacheEntryOverridesMapperTests.cs tests/Caching.NET.Tests/TestHost.cs src/Caching.NET/Caching.NET.csproj
git commit -m "feat: map CacheEntryOverrides additively onto engine entry options"
```

---

## Task 3: ICacheService contract and factory context

**Files:**
- Create: `src/Caching.NET/ICacheService.cs`
- Create: `src/Caching.NET/CacheFactoryContext.cs`
- Test: none yet — the interface has no implementation until Task 4.

**Interfaces:**
- Consumes: `CacheValue<TValue>`, `CacheEntryOverrides` from Task 1.
- Produces: `Caching.NET.ICacheService` and `Caching.NET.CacheFactoryContext<TValue>`. Every later task depends on these exact signatures.

- [ ] **Step 1: Create `src/Caching.NET/CacheFactoryContext.cs`**

`CacheFactoryContext<TValue>` wraps the engine's `FusionCacheFactoryExecutionContext<TValue>`. It is constructed by the adapter, never by an application, so the constructor is internal.

```csharp
using Caching.NET.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET;

/// <summary>
/// Passed to a get-or-set factory. Exposes the stale value when one exists, conditional-request
/// metadata, and per-execution overrides for adaptive expiration.
/// </summary>
/// <typeparam name="TValue">The cached value type.</typeparam>
/// <example>
/// <code><![CDATA[
/// var order = await cache.GetOrSetAsync("Order:42", async (ctx, ct) =>
/// {
///     var response = await http.GetAsync($"/orders/42?etag={ctx.ETag}", ct);
///     if (response.StatusCode == HttpStatusCode.NotModified)
///     {
///         return ctx.NotModified();
///     }
///
///     ctx.ETag = response.Headers.ETag?.Tag;
///     ctx.Overrides.DistributedExpiration = TimeSpan.FromMinutes(30);
///     return await response.Content.ReadFromJsonAsync<Order>(ct);
/// });
/// ]]></code>
/// </example>
public sealed class CacheFactoryContext<TValue>
{
    private readonly FusionCacheFactoryExecutionContext<TValue> _inner;

    internal CacheFactoryContext(FusionCacheFactoryExecutionContext<TValue> inner)
    {
        _inner = inner;
        Overrides = new CacheEntryOverrides();
    }

    /// <summary>Whether a previously cached value is available for conditional refresh.</summary>
    public bool HasStaleValue => _inner.HasStaleValue;

    /// <summary>The previously cached value, when one exists.</summary>
    public CacheValue<TValue> StaleValue => _inner.HasStaleValue
        ? CacheValue<TValue>.Of(_inner.StaleValue.Value)
        : CacheValue<TValue>.None;

    /// <summary>Entity tag carried with the cached entry, for conditional requests.</summary>
    public string? ETag
    {
        get => _inner.ETag;
        set => _inner.ETag = value;
    }

    /// <summary>Last-modified timestamp carried with the cached entry.</summary>
    public DateTimeOffset? LastModified
    {
        get => _inner.LastModified;
        set => _inner.LastModified = value;
    }

    /// <summary>
    /// Overrides applied to the entry this execution produces. Set any property to change the
    /// entry's behaviour for this execution only; unset properties keep the configured defaults.
    /// </summary>
    public CacheEntryOverrides Overrides { get; }

    /// <summary>
    /// Signals that the upstream value has not changed, so the existing cached entry is kept and
    /// its lifetime restarted.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no stale value to keep.</exception>
    public TValue NotModified()
    {
        if (!_inner.HasStaleValue)
        {
            throw new InvalidOperationException("NotModified() requires a stale value. Check HasStaleValue first.");
        }

        return _inner.NotModified();
    }

    /// <summary>
    /// Signals that the upstream failed in a way that does not warrant an exception, so fail-safe
    /// serves the stale value if one exists.
    /// </summary>
    /// <param name="reason">Recorded by the engine for diagnostics.</param>
    public TValue Fail(string reason) => _inner is null
        ? throw new InvalidOperationException("Fail() is not available on a disabled cache.")
        : _inner.Fail(reason);

    /// <summary>Applies <see cref="Overrides"/> onto the engine context. Called by the adapter.</summary>
    /// <remarks>
    /// Mutates the engine's options object in place rather than assigning a new one: the engine's
    /// own idiom is in-place mutation and <c>Options</c> may be get-only.
    /// </remarks>
    internal void ApplyOverrides()
    {
        if (_inner is null)
        {
            return;
        }

        Internal.CacheEntryOverridesMapper.Apply(Overrides, _inner.Options);
    }
}
```

**The disabled-cache constructor.** `NullCacheService` (Task 5) has no engine context to wrap, so
`_inner` is nullable and every member guards on it. Declare the field as
`private readonly FusionCacheFactoryExecutionContext<TValue>? _inner;`, add the second constructor,
and back `ETag` and `LastModified` with local fields when `_inner` is null:

```csharp
    private string? _detachedETag;
    private DateTimeOffset? _detachedLastModified;

    /// <summary>Context for a disabled cache: no stale value, nothing to adapt.</summary>
    internal CacheFactoryContext()
    {
        _inner = null;
        Overrides = new CacheEntryOverrides();
    }

    public bool HasStaleValue => _inner?.HasStaleValue ?? false;

    public CacheValue<TValue> StaleValue => _inner is { HasStaleValue: true }
        ? CacheValue<TValue>.Of(_inner.StaleValue.Value)
        : CacheValue<TValue>.None;

    public string? ETag
    {
        get => _inner is null ? _detachedETag : _inner.ETag;
        set
        {
            if (_inner is null)
            {
                _detachedETag = value;
            }
            else
            {
                _inner.ETag = value;
            }
        }
    }

    public DateTimeOffset? LastModified
    {
        get => _inner is null ? _detachedLastModified : _inner.LastModified;
        set
        {
            if (_inner is null)
            {
                _detachedLastModified = value;
            }
            else
            {
                _inner.LastModified = value;
            }
        }
    }
```

`NotModified()` already throws when there is no stale value, which is the correct behaviour on a
disabled cache too — leave its existing guard in place and keep the same message.

- [ ] **Step 2: Add the `Apply` overload the context needs to `CacheEntryOverridesMapper`**

The context mutates an options object the engine already owns, rather than duplicating defaults. Refactor `Resolve` to delegate to a shared applier. In `src/Caching.NET/Internal/CacheEntryOverridesMapper.cs`, replace the body of `Resolve` with:

```csharp
    public static FusionCacheEntryOptions? Resolve(CacheEntryOverrides? overrides, IFusionCache inner)
        => overrides is null ? null : Apply(overrides, inner.CreateEntryOptions());

    /// <summary>Applies non-null overrides onto an existing engine options instance, in place.</summary>
    public static FusionCacheEntryOptions Apply(CacheEntryOverrides overrides, FusionCacheEntryOptions options)
    {
        // ... the existing body of Resolve, from `if (overrides.LocalExpiration ...` to `return options;`
    }
```

- [ ] **Step 3: Create `src/Caching.NET/ICacheService.cs`**

```csharp
using Caching.NET.Options;

namespace Caching.NET;

/// <summary>
/// The Caching.NET cache operation contract. Resolve the default cache by injecting this type, or a
/// named cache with <c>[FromKeyedServices("name")]</c>.
/// </summary>
/// <remarks>
/// Every operation applies the cache's configured defaults — mode, durations, fail-safe, timeouts
/// and the key guard. Supplying <see cref="CacheEntryOverrides"/> changes only the properties it
/// sets.
/// </remarks>
public interface ICacheService
{
    /// <summary>Logical name of this cache instance.</summary>
    string CacheName { get; }

    /// <summary>Returns the cached value, running <paramref name="factory"/> on a miss.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value when none is cached.</param>
    /// <param name="failSafeDefaultValue">Returned when the factory fails and no stale value exists.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry, for later tag invalidation.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Returns the cached value, or <paramref name="defaultValue"/> when none is cached.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="defaultValue">Returned on a miss.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default);

    /// <summary>Reads the cached value, distinguishing a cached <c>null</c> from a miss.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask<CacheValue<TValue>> TryGetAsync<TValue>(
        string key,
        CacheEntryOverrides? options = null,
        CancellationToken token = default);

    /// <summary>Writes a value.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask SetAsync<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Removes an entry from every layer.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Marks an entry expired, keeping it eligible for fail-safe.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask ExpireAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Invalidates every entry carrying <paramref name="tag"/>.</summary>
    /// <param name="tag">The tag to invalidate.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask RemoveByTagAsync(string tag, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Invalidates every entry in this cache.</summary>
    /// <param name="allowFailSafe">Whether cleared entries stay eligible for fail-safe.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    ValueTask ClearAsync(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="GetOrSetAsync{TValue}"/>.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value when none is cached.</param>
    /// <param name="failSafeDefaultValue">Returned when the factory fails and no stale value exists.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry.</param>
    /// <param name="token">Cancellation token.</param>
    TValue? GetOrSet<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, TValue?> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Synchronous <see cref="GetOrDefaultAsync{TValue}"/>.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="defaultValue">Returned on a miss.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    TValue? GetOrDefault<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default);

    /// <summary>Synchronous <see cref="TryGetAsync{TValue}"/>.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    CacheValue<TValue> TryGet<TValue>(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="SetAsync{TValue}"/>.</summary>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="tags">Tags applied to the entry.</param>
    /// <param name="token">Cancellation token.</param>
    void Set<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default);

    /// <summary>Synchronous <see cref="RemoveAsync"/>.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    void Remove(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="ExpireAsync"/>.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    void Expire(string key, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="RemoveByTagAsync"/>.</summary>
    /// <param name="tag">The tag to invalidate.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    void RemoveByTag(string tag, CacheEntryOverrides? options = null, CancellationToken token = default);

    /// <summary>Synchronous <see cref="ClearAsync"/>.</summary>
    /// <param name="allowFailSafe">Whether cleared entries stay eligible for fail-safe.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="token">Cancellation token.</param>
    void Clear(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default);
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Caching.NET`
Expected: success. `GenerateDocumentationFile` is on, so a missing XML doc fails here.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/ICacheService.cs src/Caching.NET/CacheFactoryContext.cs src/Caching.NET/Internal/CacheEntryOverridesMapper.cs
git commit -m "feat: add ICacheService contract and CacheFactoryContext"
```

---

## Task 4: The adapter

**Files:**
- Create: `src/Caching.NET/Internal/FusionCacheService.cs`
- Test: `tests/Caching.NET.Tests/Caching/CacheServiceTests.cs`

**Interfaces:**
- Consumes: `ICacheService`, `CacheFactoryContext<TValue>`, `CacheValue<TValue>`, `CacheEntryOverrides`, `CacheEntryOverridesMapper` from Tasks 1–3. `CacheGuard` from `Internal`.
- Produces: `internal sealed class FusionCacheService : ICacheService` with constructor `FusionCacheService(IFusionCache inner, CacheGuard guard)`. Exposes `internal IFusionCache Inner { get; }` for `CacheInstance` disposal ordering.

**Context for the implementer:** the guard calls are the point of this class beyond type translation. `guard.ValidateKey(key)` runs on *every* operation, including ones that pass overrides — the engine hook it replaces only fired on calls with no explicit options. `guard.ValidateTags(tags)` runs whenever tags are supplied.

- [ ] **Step 1: Write the failing test**

Create `tests/Caching.NET.Tests/Caching/CacheServiceTests.cs`:

```csharp
using Caching.NET.Options;

namespace Caching.NET.Tests.Caching;

public class CacheServiceTests
{
    [Fact]
    public async Task GetOrSetAsync_RunsFactoryOnMiss_ThenCaches()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();
        var calls = 0;

        var first = await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(7); });
        var second = await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(9); });

        Assert.Equal(7, first);
        Assert.Equal(7, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TryGetAsync_DistinguishesCachedNullFromMiss()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        var miss = await cache.TryGetAsync<string?>("absent");
        await cache.SetAsync<string?>("present", null);
        var hit = await cache.TryGetAsync<string?>("present");

        Assert.False(miss.HasValue);
        Assert.True(hit.HasValue);
        Assert.Null(hit.Value);
    }

    [Fact]
    public async Task RemoveAsync_EvictsTheEntry()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("k", 1);
        await cache.RemoveAsync("k");

        Assert.False((await cache.TryGetAsync<int>("k")).HasValue);
    }

    [Fact]
    public async Task RemoveByTagAsync_EvictsTaggedEntries()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("a", 1, tags: ["group"]);
        await cache.SetAsync("b", 2, tags: ["group"]);
        await cache.SetAsync("c", 3);

        await cache.RemoveByTagAsync("group");

        Assert.False((await cache.TryGetAsync<int>("a")).HasValue);
        Assert.False((await cache.TryGetAsync<int>("b")).HasValue);
        Assert.True((await cache.TryGetAsync<int>("c")).HasValue);
    }

    [Fact]
    public async Task ClearAsync_EvictsEverything()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("a", 1);
        await cache.ClearAsync();

        Assert.False((await cache.TryGetAsync<int>("a")).HasValue);
    }

    [Fact]
    public void SyncVerbs_BehaveLikeTheirAsyncTwins()
    {
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        cache.Set("k", 5);
        Assert.Equal(5, cache.GetOrDefault<int>("k"));
        Assert.True(cache.TryGet<int>("k").HasValue);

        cache.Remove("k");
        Assert.False(cache.TryGet<int>("k").HasValue);

        var produced = cache.GetOrSet<int>("j", (_, _) => 11);
        Assert.Equal(11, produced);
    }

    [Fact]
    public async Task Overrides_AreAdditive_ShortLocalExpirationDoesNotDisableTheCache()
    {
        using var host = TestHost.BuildInMemory(c => c.WithDefaultExpiration(TimeSpan.FromMinutes(10)));
        var cache = host.Cache();

        await cache.SetAsync("k", 1, new CacheEntryOverrides { Size = 1 });

        Assert.True((await cache.TryGetAsync<int>("k")).HasValue);
    }

    [Fact]
    public async Task KeyGuard_FiresEvenWhenOverridesArePassed()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithMaximumKeyLength(20)
            .WithSecurity(s => s.KeyLengthPolicy = CacheGuardPolicy.Throw));
        var cache = host.Cache();

        var tooLong = new string('x', 64);

        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.SetAsync(tooLong, 1, new CacheEntryOverrides { Size = 1 }).AsTask());
    }

    [Fact]
    public async Task TagGuard_FiresOnEveryCallThatSuppliesTags()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithSecurity(s =>
            {
                s.TagPolicy = CacheGuardPolicy.Throw;
                s.MaximumTagCount = 1;
            }));
        var cache = host.Cache();

        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.SetAsync("k", 1, tags: ["a", "b"]).AsTask());
    }

    [Fact]
    public async Task FactoryContext_ExposesStaleValueAndNotModified()
    {
        using var host = TestHost.BuildInMemory(c => c
            .WithDefaultExpiration(TimeSpan.FromMilliseconds(50))
            .WithFailSafe(true, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(1)));
        var cache = host.Cache();

        await cache.GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));
        await Task.Delay(120);

        var refreshed = await cache.GetOrSetAsync<int>("k", (ctx, _) =>
        {
            Assert.True(ctx.HasStaleValue);
            Assert.Equal(1, ctx.StaleValue.Value);
            return Task.FromResult(ctx.NotModified());
        });

        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task FactoryContext_OverridesDriveAdaptiveExpiration()
    {
        using var host = TestHost.BuildInMemory(c => c.WithDefaultExpiration(TimeSpan.FromMilliseconds(40)));
        var cache = host.Cache();

        await cache.GetOrSetAsync<int>("k", (ctx, _) =>
        {
            ctx.Overrides.LocalExpiration = TimeSpan.FromMinutes(10);
            return Task.FromResult(3);
        });

        await Task.Delay(120);

        // The configured default would have expired by now; the per-execution override did not.
        Assert.True((await cache.TryGetAsync<int>("k")).HasValue);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CacheServiceTests"`
Expected: build failure — `TestHost.Cache()` still returns `IFusionCache`, which has no `GetOrSetAsync` overload taking `CacheFactoryContext`.

- [ ] **Step 3: Create `src/Caching.NET/Internal/FusionCacheService.cs`**

```csharp
using Caching.NET.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Internal;

/// <summary>
/// Implements <see cref="ICacheService"/> over the internal cache engine. This is the only type in
/// Caching.NET that calls an engine <i>operation</i>; <see cref="CacheEngineFactory"/> is the only
/// type that performs engine <i>setup</i>.
/// </summary>
/// <remarks>
/// Swapping engines means adding a sibling implementation of <see cref="ICacheService"/> and
/// changing the one line in <see cref="CacheEngineFactory"/> that constructs this class.
/// </remarks>
internal sealed class FusionCacheService : ICacheService
{
    private readonly IFusionCache _inner;
    private readonly CacheGuard _guard;

    public FusionCacheService(IFusionCache inner, CacheGuard guard)
    {
        _inner = inner;
        _guard = guard;
    }

    /// <summary>The engine cache, for disposal ordering in <see cref="CacheInstance"/>.</summary>
    public IFusionCache Inner => _inner;

    public string CacheName => _guard.CacheName;

    public ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var materializedTags = Validate(key, tags);

        return _inner.GetOrSetAsync<TValue?>(
            key,
            (ctx, ct) =>
            {
                var wrapped = new CacheFactoryContext<TValue>(ctx!);
                var result = factory(wrapped, ct);
                return result.IsCompletedSuccessfully
                    ? Complete(wrapped, result.Result)
                    : Awaited(wrapped, result);
            },
            ToMaybe(failSafeDefaultValue),
            Resolve(options),
            materializedTags,
            token);

        static Task<TValue?> Complete(CacheFactoryContext<TValue> ctx, TValue? value)
        {
            ctx.ApplyOverrides();
            return Task.FromResult(value);
        }

        static async Task<TValue?> Awaited(CacheFactoryContext<TValue> ctx, Task<TValue?> pending)
        {
            var value = await pending.ConfigureAwait(false);
            ctx.ApplyOverrides();
            return value;
        }
    }

    public ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        return _inner.GetOrDefaultAsync(key, defaultValue, Resolve(options), token);
    }

    public async ValueTask<CacheValue<TValue>> TryGetAsync<TValue>(
        string key,
        CacheEntryOverrides? options = null,
        CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        var result = await _inner.TryGetAsync<TValue>(key, Resolve(options), token).ConfigureAwait(false);
        return result.HasValue ? CacheValue<TValue>.Of(result.Value) : CacheValue<TValue>.None;
    }

    public ValueTask SetAsync<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        var materializedTags = Validate(key, tags);
        return _inner.SetAsync(key, value, Resolve(options), materializedTags, token);
    }

    public ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        return _inner.RemoveAsync(key, Resolve(options), token);
    }

    public ValueTask ExpireAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        return _inner.ExpireAsync(key, Resolve(options), token);
    }

    public ValueTask RemoveByTagAsync(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateTags([tag]);
        return _inner.RemoveByTagAsync(tag, Resolve(options), token);
    }

    public ValueTask ClearAsync(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
        => _inner.ClearAsync(allowFailSafe, Resolve(options), token);

    public TValue? GetOrSet<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, TValue?> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var materializedTags = Validate(key, tags);

        return _inner.GetOrSet<TValue?>(
            key,
            (ctx, ct) =>
            {
                var wrapped = new CacheFactoryContext<TValue>(ctx!);
                var value = factory(wrapped, ct);
                wrapped.ApplyOverrides();
                return value;
            },
            ToMaybe(failSafeDefaultValue),
            Resolve(options),
            materializedTags,
            token);
    }

    public TValue? GetOrDefault<TValue>(
        string key,
        TValue? defaultValue = default,
        CacheEntryOverrides? options = null,
        CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        return _inner.GetOrDefault(key, defaultValue, Resolve(options), token);
    }

    public CacheValue<TValue> TryGet<TValue>(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        var result = _inner.TryGet<TValue>(key, Resolve(options), token);
        return result.HasValue ? CacheValue<TValue>.Of(result.Value) : CacheValue<TValue>.None;
    }

    public void Set<TValue>(
        string key,
        TValue value,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        var materializedTags = Validate(key, tags);
        _inner.Set(key, value, Resolve(options), materializedTags, token);
    }

    public void Remove(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        _inner.Remove(key, Resolve(options), token);
    }

    public void Expire(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        _inner.Expire(key, Resolve(options), token);
    }

    public void RemoveByTag(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateTags([tag]);
        _inner.RemoveByTag(tag, Resolve(options), token);
    }

    public void Clear(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
        => _inner.Clear(allowFailSafe, Resolve(options), token);

    private FusionCacheEntryOptions? Resolve(CacheEntryOverrides? options)
        => CacheEntryOverridesMapper.Resolve(options, _inner);

    /// <summary>
    /// Validates the key, and the tags when any were supplied. Tags are materialised once so the
    /// guard and the engine never enumerate a lazy sequence twice.
    /// </summary>
    private string[]? Validate(string key, IEnumerable<string>? tags)
    {
        _guard.ValidateKey(key);

        if (tags is null)
        {
            return null;
        }

        var materialized = tags as string[] ?? [.. tags];
        _guard.ValidateTags(materialized);
        return materialized;
    }

    private static MaybeValue<TValue?> ToMaybe<TValue>(CacheValue<TValue?> value)
        => value.HasValue ? MaybeValue<TValue?>.FromValue(value.Value) : default;
}
```

- [ ] **Step 4: Point `TestHost.Cache()` at the new contract**

In `tests/Caching.NET.Tests/TestHost.cs`, change the two accessors. `ICacheService` is not registered until Task 6, so resolve through `CacheInstance` for now:

```csharp
    public static ICacheService Cache(this ServiceProvider provider)
        => new Caching.NET.Internal.FusionCacheService(provider.EngineCache(), provider.Guard());

    internal static Caching.NET.Internal.CacheGuard Guard(this ServiceProvider provider)
        => (Caching.NET.Internal.CacheGuard)provider
            .GetRequiredKeyedService<Caching.NET.Internal.CacheInstance>(CachingDefaults.DefaultCacheName).Guard;
```

Task 6 replaces both with a plain `GetRequiredService<ICacheService>()`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CacheServiceTests"`
Expected: PASS, 11 tests.

Note: `RedisMode_PreservesSkipMemoryCache_WhenOverridesSupplied` in `CacheEntryOverridesMapperTests` constructs a Redis-mode host without a Redis server. If it fails on connection, set `AbortOnConnectFail = false` in that test's builder — it only inspects mapped options and never issues a command.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Internal/FusionCacheService.cs tests/Caching.NET.Tests/Caching/CacheServiceTests.cs tests/Caching.NET.Tests/TestHost.cs
git commit -m "feat: add FusionCacheService adapter with per-call guard enforcement"
```

---

## Task 5: Null cache service

**Files:**
- Create: `src/Caching.NET/Internal/NullCacheService.cs`
- Test: none in this task. `NullCacheServiceTests` cannot compile until Task 6 changes `CacheInstance.Cache` to `ICacheService`, so it lives in Task 6 Step 9a. **The gate for this task is `dotnet build src/Caching.NET`.**

**Interfaces:**
- Consumes: `ICacheService` from Task 3.
- Produces: `internal sealed class NullCacheService : ICacheService` with constructor `NullCacheService(string cacheName)`.

- [ ] **Step 1: Reference material — the test Task 6 will run against this class**

Do not create this file now. It is here so the behaviour this class must have is unambiguous. Task 6 Step 9a creates it verbatim.

```csharp
namespace Caching.NET.Tests.Caching;

public class NullCacheServiceTests
{
    private static ICacheService Build() => TestHost
        .Build(c => c.UseInMemory().WithApplicationPrefix("tests").Disable())
        .DisabledCache();

    [Fact]
    public async Task ReadsAlwaysMiss()
    {
        var cache = Build();

        await cache.SetAsync("k", 1);

        Assert.False((await cache.TryGetAsync<int>("k")).HasValue);
        Assert.Equal(0, await cache.GetOrDefaultAsync<int>("k"));
        Assert.Equal(-1, await cache.GetOrDefaultAsync("k", -1));
    }

    [Fact]
    public async Task FactoryRunsOnEveryCall()
    {
        var cache = Build();
        var calls = 0;

        await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(1); });
        await cache.GetOrSetAsync<int>("k", (_, _) => { calls++; return Task.FromResult(1); });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvalidationVerbsAreNoOps()
    {
        var cache = Build();

        await cache.RemoveAsync("k");
        await cache.ExpireAsync("k");
        await cache.RemoveByTagAsync("t");
        await cache.ClearAsync();
    }

    [Fact]
    public void SyncVerbsMatchAsync()
    {
        var cache = Build();
        var calls = 0;

        cache.Set("k", 1);
        Assert.False(cache.TryGet<int>("k").HasValue);
        Assert.Equal(5, cache.GetOrSet<int>("k", (_, _) => { calls++; return 5; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CacheNameIsPreserved()
    {
        Assert.Equal(CachingDefaults.DefaultCacheName, Build().CacheName);
    }
}
```

Add the accessor to `tests/Caching.NET.Tests/TestHost.cs`:

```csharp
    /// <summary>The cache from a provider registered with Enabled=false.</summary>
    public static ICacheService DisabledCache(this ServiceProvider provider)
        => provider.GetRequiredKeyedService<Caching.NET.Internal.CacheInstance>(CachingDefaults.DefaultCacheName).Cache;
```

This requires `CacheInstance.Cache` to be `ICacheService`, which Task 6 does. Until then, the accessor will not compile — so **write the test now and expect it to fail at build**, and mark this test class as the first thing to re-run at the end of Task 6.

- [ ] **Step 2: Create `src/Caching.NET/Internal/NullCacheService.cs`**

```csharp
using Caching.NET.Options;

namespace Caching.NET.Internal;

/// <summary>
/// The cache registered when <see cref="CachingOptions.Enabled"/> is <c>false</c>: reads always
/// miss, writes are discarded, and get-or-set factories run on every call.
/// </summary>
/// <remarks>
/// No engine object, memory cache, Redis connection or backplane is created. This exists so that
/// disabling the cache is a configuration change rather than a code change in the application.
/// </remarks>
internal sealed class NullCacheService : ICacheService
{
    public NullCacheService(string cacheName) => CacheName = cacheName;

    public string CacheName { get; }

    public async ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return await factory(null!, token).ConfigureAwait(false);
    }

    public ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key, TValue? defaultValue = default, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.FromResult(defaultValue);

    public ValueTask<CacheValue<TValue>> TryGetAsync<TValue>(
        string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.FromResult(CacheValue<TValue>.None);

    public ValueTask SetAsync<TValue>(
        string key, TValue value, CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask ExpireAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask RemoveByTagAsync(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask ClearAsync(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public TValue? GetOrSet<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, TValue?> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory(null!, token);
    }

    public TValue? GetOrDefault<TValue>(
        string key, TValue? defaultValue = default, CacheEntryOverrides? options = null, CancellationToken token = default)
        => defaultValue;

    public CacheValue<TValue> TryGet<TValue>(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
        => CacheValue<TValue>.None;

    public void Set<TValue>(
        string key, TValue value, CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null, CancellationToken token = default)
    {
    }

    public void Remove(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }

    public void Expire(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }

    public void RemoveByTag(string tag, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }

    public void Clear(bool allowFailSafe = true, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
    }
}
```

**Do not pass `null!` as the factory context** — the snippet above shows it only to be replaced. A
factory that touched the context would throw a `NullReferenceException`. Task 3 already added the
internal parameterless constructor for exactly this case. Use it in both places:

```csharp
        return await factory(new CacheFactoryContext<TValue>(), token).ConfigureAwait(false);
```

```csharp
        return factory(new CacheFactoryContext<TValue>(), token);
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Caching.NET`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/Caching.NET/Internal/NullCacheService.cs
git commit -m "feat: add NullCacheService for the disabled cache"
```

Do not add a test file or touch `TestHost` in this task — Task 6 Step 9a does both.

---

## Task 6: Wire the contract through composition and DI

**Files:**
- Modify: `src/Caching.NET/Internal/CacheInstance.cs`
- Modify: `src/Caching.NET/Internal/CacheEngineFactory.cs:26-103`
- Modify: `src/Caching.NET/Internal/CacheProvider.cs`
- Modify: `src/Caching.NET/ICacheProvider.cs`
- Modify: `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs:250-269`
- Modify: `src/Caching.NET/Health/CachingHealthCheck.cs`
- Delete: `src/Caching.NET/Internal/KeyGuardEntryOptionsProvider.cs`
- Modify: `src/Caching.NET/Internal/CacheGuard.cs` — delete `ValidatePhysicalKey`
- Modify: `tests/Caching.NET.Tests/TestHost.cs`
- Test: `tests/Caching.NET.Tests/Registration/RegistrationTests.cs` (existing, extended)

**Interfaces:**
- Consumes: `FusionCacheService`, `NullCacheService` from Tasks 4–5.
- Produces: `CacheInstance.Cache` typed `ICacheService`; `ICacheProvider.Default`/`GetCache`/`GetCacheOrNull` returning `ICacheService`; keyed and non-keyed `ICacheService` DI registrations. `IFusionCache` is no longer registered.

- [ ] **Step 1: Write the failing test**

Append to `tests/Caching.NET.Tests/Registration/RegistrationTests.cs`:

```csharp
    [Fact]
    public void EngineCacheIsNotResolvable()
    {
        using var host = TestHost.BuildInMemory();

        Assert.Null(host.GetService<ZiggyCreatures.Caching.Fusion.IFusionCache>());
    }

    [Fact]
    public void DefaultCacheResolvesAsICacheService()
    {
        using var host = TestHost.BuildInMemory();

        var cache = host.GetRequiredService<ICacheService>();

        Assert.Equal(CachingDefaults.DefaultCacheName, cache.CacheName);
    }

    [Fact]
    public void NamedCacheResolvesByKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(c => c.UseInMemory().WithApplicationPrefix("tests"));
        services.AddCaching("hot", c => c.UseInMemory().WithApplicationPrefix("tests"));
        using var host = services.BuildServiceProvider();

        Assert.Equal("hot", host.GetRequiredKeyedService<ICacheService>("hot").CacheName);
        Assert.Same(
            host.GetRequiredService<ICacheService>(),
            host.GetRequiredService<ICacheProvider>().Default);
    }

    [Fact]
    public void ProviderReturnsCacheServices()
    {
        using var host = TestHost.BuildInMemory();
        var provider = host.GetRequiredService<ICacheProvider>();

        Assert.NotNull(provider.Default);
        Assert.NotNull(provider.GetCache(CachingDefaults.DefaultCacheName));
        Assert.Null(provider.GetCacheOrNull("absent"));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~RegistrationTests"`
Expected: FAIL — `IFusionCache` still resolves; `ICacheService` does not.

- [ ] **Step 3: Change `CacheInstance`**

In `src/Caching.NET/Internal/CacheInstance.cs`, change the constructor parameter and property from `IFusionCache cache` to `ICacheService cache`, and drop the `using ZiggyCreatures.Caching.Fusion;`:

```csharp
    public CacheInstance(
        string cacheName,
        ICacheService cache,
        CacheGuard guard,
        Telemetry.CacheTelemetryContext telemetry,
        params IDisposable?[] ownedResources)

    public ICacheService Cache { get; }
```

- [ ] **Step 4: Change `CacheEngineFactory`**

Replace the disabled branch at lines 34-39:

```csharp
        if (!options.Enabled)
        {
            CacheLogMessages.CachingDisabled(logger, cacheName);
            return new CacheInstance(cacheName, new NullCacheService(cacheName), guard, telemetry);
        }
```

Remove the `using ZiggyCreatures.Caching.Fusion.NullObjects;` import.

Replace the return at line 102:

```csharp
        var service = new FusionCacheService(cache, guard);

        return new CacheInstance(cacheName, service, guard, telemetry, eventBridge, cache, distributedCache, memoryCache, redisConnection);
```

Remove the `DefaultEntryOptionsProvider` assignment from `MapEngineOptions` (lines 117-119), including its comment. The `CacheGuard guard` parameter of `MapEngineOptions` becomes unused — remove it from the signature and from the call site at line 41.

- [ ] **Step 5: Delete the key-guard provider and its engine hook**

```bash
git rm src/Caching.NET/Internal/KeyGuardEntryOptionsProvider.cs
```

In `src/Caching.NET/Internal/CacheGuard.cs`, delete `ValidatePhysicalKey` (lines 36-40). `ValidateKey` already measures `_prefixLength + key.Length`, which is the same string the engine used to pass.

- [ ] **Step 6: Change `ICacheProvider` and `CacheProvider`**

In `src/Caching.NET/ICacheProvider.cs`, replace all four `IFusionCache` occurrences with `ICacheService`, delete `using ZiggyCreatures.Caching.Fusion;`, and update the `<remarks>` and `<example>` blocks to name `ICacheService`.

In `src/Caching.NET/Internal/CacheProvider.cs`, same substitution on the three members; delete the engine `using`.

- [ ] **Step 7: Change DI registration**

In `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs`, replace lines 251-253 and 267:

```csharp
        services.AddKeyedSingleton<ICacheService>(
            cacheName,
            static (sp, key) => sp.GetRequiredKeyedService<CacheInstance>(key).Cache);
```

```csharp
            // Resolved through CacheInstance rather than through the keyed ICacheService so the two
            // registrations can never form a resolution cycle.
            services.TryAddSingleton<ICacheService>(sp => sp.GetRequiredKeyedService<CacheInstance>(cacheName).Cache);
```

Delete the `using ZiggyCreatures.Caching.Fusion;` import.

- [ ] **Step 8: Change `CachingHealthCheck`**

In `src/Caching.NET/Health/CachingHealthCheck.cs`, replace every `IFusionCache` with `ICacheService` and delete the engine `using`. The probe's cache calls translate directly — `SetAsync`/`TryGetAsync`/`RemoveAsync` have the same names.

- [ ] **Step 9: Simplify `TestHost`**

In `tests/Caching.NET.Tests/TestHost.cs`, replace the temporary accessors from Tasks 2 and 4 with:

```csharp
    public static ICacheService Cache(this ServiceProvider provider) => provider.GetRequiredService<ICacheService>();

    public static ICacheService NamedCache(this ServiceProvider provider, string cacheName)
        => provider.GetRequiredKeyedService<ICacheService>(cacheName);

    public static ICacheService DisabledCache(this ServiceProvider provider) => provider.Cache();

    /// <summary>
    /// The raw engine cache behind the default Caching.NET cache. Used only by tests that assert how
    /// Caching.NET maps onto the engine.
    /// </summary>
    internal static ZiggyCreatures.Caching.Fusion.IFusionCache EngineCache(this ServiceProvider provider)
        => ((Caching.NET.Internal.FusionCacheService)provider
            .GetRequiredKeyedService<Caching.NET.Internal.CacheInstance>(CachingDefaults.DefaultCacheName).Cache).Inner;
```

Delete the `Guard()` helper added in Task 4.

- [ ] **Step 9a: Create the null-cache tests**

`CacheInstance.Cache` is now `ICacheService`, so the tests deferred from Task 5 compile. Create `tests/Caching.NET.Tests/Caching/NullCacheServiceTests.cs` exactly as written in **Task 5 Step 1**, which carries the full file. `TestHost.DisabledCache()` added in Step 9 above is what those tests resolve through.

- [ ] **Step 10: Run the whole unit suite**

Run: `dotnet test tests/Caching.NET.Tests`
Expected: many failures in files not yet migrated — that is expected and Task 12 fixes them. The four `RegistrationTests` cases from Step 1, all `CacheServiceTests`, `NullCacheServiceTests` and `CacheEntryOverridesMapperTests` must pass:

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~RegistrationTests|FullyQualifiedName~CacheServiceTests|FullyQualifiedName~NullCacheServiceTests|FullyQualifiedName~CacheEntryOverridesMapperTests"`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add -A src/Caching.NET tests/Caching.NET.Tests
git commit -m "feat!: register ICacheService instead of the engine cache"
```

---

## Task 7: Rebind CacheExtensions

**Files:**
- Modify: `src/Caching.NET/Extensions/CacheExtensions.cs`
- Test: `tests/Caching.NET.Tests/Extensions/CacheExtensionsTests.cs` (existing)

**Interfaces:**
- Consumes: `ICacheService`, `CacheEntryOverrides`.
- Produces: the same five extension methods, now on `ICacheService` with `CacheEntryOverrides`.

- [ ] **Step 1: Rewrite the signatures**

In `src/Caching.NET/Extensions/CacheExtensions.cs`, replace `using ZiggyCreatures.Caching.Fusion;` with `using Caching.NET.Options;`, then for all five methods replace `this IFusionCache cache` with `this ICacheService cache` and `FusionCacheEntryOptions? options` with `CacheEntryOverrides? options`. No body changes are needed — `GetOrDefaultAsync`, `SetAsync`, `RemoveAsync` and `TryGetAsync` keep their names and parameter order.

One body change: `ExistsAsync` reads `result.HasValue` off the return of `TryGetAsync`, which is now `CacheValue<TValue>` rather than `MaybeValue<TValue>`. `HasValue` exists on both, so it compiles unchanged. Verify rather than assume.

- [ ] **Step 2: Update the existing tests**

In `tests/Caching.NET.Tests/Extensions/CacheExtensionsTests.cs`, replace any `FusionCacheEntryOptions` with `CacheEntryOverrides` and remove the engine `using`.

- [ ] **Step 3: Run**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CacheExtensionsTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Caching.NET/Extensions/CacheExtensions.cs tests/Caching.NET.Tests/Extensions/CacheExtensionsTests.cs
git commit -m "feat!: rebind CacheExtensions to ICacheService"
```

---

## Task 8: Leak scrub — priority and Redis configuration

**Files:**
- Modify: `src/Caching.NET/Options/CacheEntryOptions.cs:1,40-41`
- Modify: `src/Caching.NET/Options/RedisOptions.cs:17,69`
- Modify: `src/Caching.NET/CachingBuilder.cs:65-70,91-96`
- Modify: `src/Caching.NET/Internal/RedisConnectionProvider.cs:147`
- Modify: `src/Caching.NET/Internal/CacheEngineFactory.cs:173`
- Modify: `src/Caching.NET/Validation/CachingOptionsValidator.cs:204-206,337`
- Test: `tests/Caching.NET.Tests/Validation/CachingOptionsValidatorTests.cs` (existing)

**Interfaces:**
- Consumes: `CacheEntryPriority`, `CacheEntryOverridesMapper.MapPriority` from Tasks 1–2.
- Produces: `CacheEntryOptions.Priority` typed `CacheEntryPriority`. `RedisOptions.ConfigureConnection`, `CachingBuilder.UseRedis(Action<ConfigurationOptions>)` and `CachingBuilder.UseHybrid(Action<ConfigurationOptions>, bool)` no longer exist.

**Behaviour change to handle — read this before starting.** `ConfigureConnection` is a *working code-first configuration path*, not a validation loophole. [`RedisConnectionProvider.BuildConfiguration`](../../../src/Caching.NET/Internal/RedisConnectionProvider.cs) starts from `new ConfigurationOptions()` when `Redis.Configuration` is empty, so the delegate is the only thing that can supply endpoints in that case. The validator is correct today.

Removing it makes `Redis.Configuration` genuinely required for `Redis` and `Hybrid`, and loses capability a connection string cannot express. Two typed replacements cover the common case using BCL types only:

- `RedisOptions.ClientCertificate` (`X509Certificate2?`) — TLS client certificate
- `RedisOptions.ValidateServerCertificate` (`RemoteCertificateValidationCallback?`) — extra server-certificate validation

Accepted as lost, with no engine-free expression: Sentinel `CommandMap`, `ReconnectRetryPolicy`, `BacklogPolicy`, `SocketManager`, `LoggerFactory`. Each can be added as a typed member later if an application needs one.

- [ ] **Step 1: Write the failing test**

Append to `tests/Caching.NET.Tests/Validation/CachingOptionsValidatorTests.cs`:

```csharp
    [Fact]
    public void RedisModeWithoutConfiguration_Fails()
    {
        var options = Valid();
        options.Mode = CacheMode.Redis;
        options.Redis.Configuration = null;

        AssertFails(options, "Redis.Configuration is not set");
    }

    [Fact]
    public void PriorityUsesTheCachingNetEnum()
    {
        var options = Valid();
        options.Entry.Priority = CacheEntryPriority.NeverRemove;

        Assert.Equal(CacheEntryPriority.NeverRemove, options.Entry.Priority);
    }
```

Match `Valid()` and `AssertFails(...)` to the helpers already in that file.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CachingOptionsValidatorTests"`
Expected: FAIL — `CacheEntryPriority` is not the property's type.

- [ ] **Step 3: Change `CacheEntryOptions`**

In `src/Caching.NET/Options/CacheEntryOptions.cs`, delete `using Microsoft.Extensions.Caching.Memory;` and change line 40-41:

```csharp
    /// <summary>Eviction priority for the in-process memory layer. Default <see cref="CacheEntryPriority.Normal"/>.</summary>
    public CacheEntryPriority Priority { get; set; } = CacheEntryPriority.Normal;
```

- [ ] **Step 4: Map it in `CacheEngineFactory`**

At line 173, change `Priority = entry.Priority,` to:

```csharp
            Priority = CacheEntryOverridesMapper.MapPriority(entry.Priority),
```

- [ ] **Step 5: Replace `RedisOptions.ConfigureConnection` with typed TLS members**

Delete the property at line 69 and its XML doc. In the `Configuration` doc at line 17, drop the `<see cref="ConfigureConnection"/>` clause. Delete `using StackExchange.Redis;` if nothing else in the file uses it. Add:

```csharp
    /// <summary>
    /// Client certificate presented during the TLS handshake, for servers that require mutual TLS.
    /// Requires <see cref="UseTls"/>.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; set; }

    /// <summary>
    /// Additional server-certificate validation, run after Caching.NET's own check. Return
    /// <c>false</c> to reject the connection.
    /// </summary>
    /// <remarks>
    /// Caching.NET's own validation still runs first and still honours
    /// <see cref="StrictCertificateValidation"/>. This callback can only tighten the result, never
    /// loosen it.
    /// </remarks>
    public RemoteCertificateValidationCallback? ValidateServerCertificate { get; set; }
```

with `using System.Security.Cryptography.X509Certificates;` and `using System.Net.Security;`.

In `src/Caching.NET/Internal/RedisConnectionProvider.cs`, replace line 147 (`_options.ConfigureConnection?.Invoke(configuration);`) with the two typed hooks, placed inside the existing `if (configuration.Ssl)` block at lines 141-144 so they only apply to a TLS connection:

```csharp
        if (configuration.Ssl)
        {
            configuration.CertificateValidation += _certificateValidator.Validate;

            if (_options.ValidateServerCertificate is { } validate)
            {
                configuration.CertificateValidation += (sender, cert, chain, errors) => validate(sender, cert, chain, errors);
            }

            if (_options.ClientCertificate is { } clientCertificate)
            {
                configuration.CertificateSelection += (_, _, _, _, _) => clientCertificate;
            }
        }

        return configuration;
```

Confirm the multicast semantics of `CertificateValidation` in StackExchange.Redis before relying on "can only tighten": if the delegate is multicast and only the last return value wins, chain the calls explicitly instead so Caching.NET's own result is honoured. Verify with a test in `tests/Caching.NET.Tests/Internal/RedisCertificateValidatorTests.cs`, which already covers the validator.

- [ ] **Step 5a: Validate the new members**

Add to `CachingOptionsValidator.ValidateRedis`:

```csharp
        if (!redis.UseTls && (redis.ClientCertificate is not null || redis.ValidateServerCertificate is not null))
        {
            failures.Add("Redis.ClientCertificate and Redis.ValidateServerCertificate require Redis.UseTls=true, or a connection string with ssl=true.");
        }
```

with a matching test in `CachingOptionsValidatorTests`.

- [ ] **Step 6: Remove the builder overloads**

In `src/Caching.NET/CachingBuilder.cs`, delete `UseRedis(Action<ConfigurationOptions>)` (lines 65-70) and `UseHybrid(Action<ConfigurationOptions>, bool)` (lines 91-96), including their XML docs. Delete `using StackExchange.Redis;`. `WithRedis(Action<RedisOptions>)` at line 287 stays and is now the documented way to reach connection settings.

- [ ] **Step 7: Fix the validator**

In `src/Caching.NET/Validation/CachingOptionsValidator.cs`, change line 204 to test only `string.IsNullOrWhiteSpace(redis.Configuration)`, change the message at line 206 to:

```csharp
            failures.Add($"Mode is {options.Mode} but Redis.Configuration is not set. Provide a connection string, or switch to Mode=InMemory.");
```

and at line 337 drop the `|| redis.ConfigureConnection is not null` clause.

- [ ] **Step 8: Run**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~CachingOptionsValidatorTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add -A src/Caching.NET tests/Caching.NET.Tests/Validation
git commit -m "feat!: replace CacheItemPriority and remove StackExchange.Redis from the public API"
```

---

## Task 9: Telemetry surface and operation spans

**Files:**
- Modify: `src/Caching.NET/Telemetry/CacheTelemetry.cs`
- Modify: `src/Caching.NET/Telemetry/CacheTelemetryAttributes.cs`
- Modify: `src/Caching.NET/Telemetry/CacheTelemetryContext.cs`
- Modify: `src/Caching.NET/Options/CacheSecurityOptions.cs`
- Modify: `src/Caching.NET/Options/CacheObservabilityOptions.cs`
- Modify: `src/Caching.NET/Internal/FusionCacheService.cs`
- Modify: `src/Caching.NET/Internal/CacheEngineFactory.cs`
- Delete: `src/Caching.NET/Telemetry/EngineTelemetryNames.cs`
- Rewrite: `tests/Caching.NET.Tests/Telemetry/SpanKeyExposureTests.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/OperationSpanTests.cs`

**Interfaces:**
- Consumes: `FusionCacheService` from Task 4.
- Produces: `CacheTelemetry.ActivitySourceNames == [ "Caching.NET" ]`, `MeterNames == [ "Caching.NET" ]`; `CacheTelemetry.LayerDuration` histogram; `CacheTelemetryAttributes.Key == "cache.key"`; `CacheSecurityOptions.AllowRawKeysInTelemetry`; `CacheObservabilityOptions.EnableLayerMetrics`; `CacheTelemetryContext.RecordLayer(string layer, string operation, string result, double milliseconds)` and `CacheTelemetryContext.StartOperation(string name, string key)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Caching.NET.Tests/Telemetry/OperationSpanTests.cs`:

```csharp
using System.Diagnostics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

public class OperationSpanTests
{
    [Fact]
    public async Task GetOrSet_EmitsABrandedOperationSpan()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory();

        await host.Cache().GetOrSetAsync<int>("Order:42", (_, _) => Task.FromResult(1));

        var span = Assert.Single(recorder.Activities, a => a.OperationName == "cache.get_or_set");
        Assert.Equal(CacheTelemetry.SystemName, span.GetTagItem(CacheTelemetryAttributes.System));
        Assert.Equal("InMemory", span.GetTagItem(CacheTelemetryAttributes.Mode));
        Assert.Equal("miss", span.GetTagItem(CacheTelemetryAttributes.Result));
        Assert.Equal(true, span.GetTagItem(CacheTelemetryAttributes.FactoryExecuted));
    }

    [Fact]
    public async Task WarmRead_IsTaggedAsAHit()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory();

        await host.Cache().SetAsync("k", 1);
        await host.Cache().GetOrDefaultAsync<int>("k");

        var span = Assert.Single(recorder.Activities, a => a.OperationName == "cache.get_or_default");
        Assert.Equal("hit", span.GetTagItem(CacheTelemetryAttributes.Result));
    }

    [Fact]
    public async Task EveryVerbEmitsItsOwnSpan()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory();
        var cache = host.Cache();

        await cache.SetAsync("k", 1, tags: ["t"]);
        await cache.TryGetAsync<int>("k");
        await cache.ExpireAsync("k");
        await cache.RemoveAsync("k");
        await cache.RemoveByTagAsync("t");
        await cache.ClearAsync();

        var names = recorder.Activities.Select(a => a.OperationName).ToArray();
        Assert.Contains("cache.set", names);
        Assert.Contains("cache.try_get", names);
        Assert.Contains("cache.expire", names);
        Assert.Contains("cache.remove", names);
        Assert.Contains("cache.remove_by_tag", names);
        Assert.Contains("cache.clear", names);
    }
}
```

Reuse the `SpanRecorder` class currently nested in `SpanKeyExposureTests` by extracting it to `tests/Caching.NET.Tests/Telemetry/SpanRecorder.cs` as an `internal sealed class` in namespace `Caching.NET.Tests.Telemetry`, unchanged apart from accessibility.

Rewrite `tests/Caching.NET.Tests/Telemetry/SpanKeyExposureTests.cs` entirely:

```csharp
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// Pins what reaches a tracing backend. Caching.NET emits every cache span itself, so the raw key is
/// present only when the application asks for it.
/// </summary>
public class SpanKeyExposureTests
{
    private const string SecretBearingKey = "Order:user-4815162342";

    [Fact]
    public async Task ByDefault_SpansCarryAFingerprintAndNeverTheKey()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory();

        await host.Cache().GetOrSetAsync<int>(SecretBearingKey, (_, _) => Task.FromResult(1));

        var spans = recorder.Activities.Where(a => a.OperationName.StartsWith("cache.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(spans);

        foreach (var span in spans)
        {
            Assert.DoesNotContain(
                span.TagObjects,
                tag => tag.Value?.ToString()?.Contains(SecretBearingKey, StringComparison.Ordinal) == true);
        }

        Assert.Contains(spans, s => s.GetTagItem(CacheTelemetryAttributes.KeyFingerprint) is not null);
    }

    [Fact]
    public async Task WhenOptedIn_SpansCarryTheCallerKey()
    {
        using var recorder = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildInMemory(c => c.WithSecurity(s => s.AllowRawKeysInTelemetry = true));

        await host.Cache().GetOrSetAsync<int>(SecretBearingKey, (_, _) => Task.FromResult(1));

        var keys = recorder.Activities
            .Select(a => a.GetTagItem(CacheTelemetryAttributes.Key)?.ToString())
            .Where(v => v is not null)
            .ToArray();

        Assert.Contains(SecretBearingKey, keys);
        // The caller's key, not the prefixed physical key.
        Assert.DoesNotContain(keys, k => k!.StartsWith("tests:", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheBrandedSourceIsPublished()
    {
        Assert.Equal([CacheTelemetry.ActivitySourceName], CacheTelemetry.ActivitySourceNames);
        Assert.Equal([CacheTelemetry.MeterName], CacheTelemetry.MeterNames);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~OperationSpanTests|FullyQualifiedName~SpanKeyExposureTests"`
Expected: build failure — `AllowRawKeysInTelemetry` and `CacheTelemetryAttributes.Key` do not exist.

- [ ] **Step 3: Strip `CacheTelemetry`**

In `src/Caching.NET/Telemetry/CacheTelemetry.cs`, delete `EngineActivitySourceNames`, `EngineKeyAttributeName`, `EngineMeterNames`, and rewrite the two remaining arrays and the class `<example>`:

```csharp
/// <summary>
/// Caching.NET-owned OpenTelemetry instrumentation. These names are the consumer contract; the
/// internal cache engine is never named in telemetry configuration and emits nothing of its own,
/// because its sources are never registered.
/// </summary>
/// <example>
/// <code><![CDATA[
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceNames))
///     .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterNames));
/// ]]></code>
/// </example>
```

```csharp
    /// <summary>Every activity source Caching.NET emits from.</summary>
    public static readonly string[] ActivitySourceNames = [ActivitySourceName];

    /// <summary>Every meter Caching.NET emits from.</summary>
    public static readonly string[] MeterNames = [MeterName];
```

Add the new instrument beside the other histograms:

```csharp
    internal static readonly Histogram<double> LayerDuration =
        Meter.CreateHistogram<double>("caching.net.layer.duration", "ms", "Per-layer operation duration.");
```

Then:

```bash
git rm src/Caching.NET/Telemetry/EngineTelemetryNames.cs
```

- [ ] **Step 4: Add the key attribute**

In `src/Caching.NET/Telemetry/CacheTelemetryAttributes.cs`, delete the `<remarks>` block on the class that warns about engine sources (lines 8-13), and add:

```csharp
    /// <summary>
    /// The caller's cache key. Emitted only when
    /// <see cref="Options.CacheSecurityOptions.AllowRawKeysInTelemetry"/> is set; otherwise
    /// <see cref="KeyFingerprint"/> is emitted instead. The physical key is never recorded.
    /// </summary>
    public const string Key = "cache.key";
```

Update the `KeyFingerprint` doc — it currently says Caching.NET never sets it, which stops being true.

- [ ] **Step 5: Add the two options**

In `src/Caching.NET/Options/CacheSecurityOptions.cs`:

```csharp
    /// <summary>
    /// When <c>true</c>, cache spans carry the caller's key in <c>cache.key</c> instead of a
    /// fingerprint. Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Cache keys routinely embed tenant, user and record identifiers. Enabling this exports them to
    /// the tracing backend, where span attributes are indexed, retained under that backend's policy,
    /// and readable by everyone with trace access. Treat it as a data-flow change, not a debug
    /// toggle.
    /// </remarks>
    public bool AllowRawKeysInTelemetry { get; set; }
```

In `src/Caching.NET/Options/CacheObservabilityOptions.cs`:

```csharp
    /// <summary>
    /// Whether per-layer duration is recorded on <c>caching.net.layer.duration</c>. Default
    /// <c>true</c>. Counters are unaffected.
    /// </summary>
    public bool EnableLayerMetrics { get; set; } = true;
```

- [ ] **Step 6: Extend `CacheTelemetryContext`**

Add to `src/Caching.NET/Telemetry/CacheTelemetryContext.cs`, and read the two new options in the constructor (`AllowRawKeysInTelemetry`, `EnableLayerMetrics`) into readonly fields:

```csharp
    public bool AllowRawKeysInTelemetry { get; }

    public bool LayerMetricsEnabled { get; }

    /// <summary>Records the duration and outcome of one layer's part of an operation.</summary>
    public void RecordLayer(string layer, string operation, string result, double milliseconds)
    {
        if (!MetricsEnabled || !LayerMetricsEnabled)
        {
            return;
        }

        var tags = BaseTags();
        tags.Add(CacheTelemetryAttributes.Layer, layer);
        tags.Add(CacheTelemetryAttributes.Operation, operation);
        tags.Add(CacheTelemetryAttributes.Result, result);
        CacheTelemetry.LayerDuration.Record(milliseconds, tags);
    }

    /// <summary>
    /// Starts an operation span carrying the standard tags plus a key identifier. Returns
    /// <c>null</c> when tracing is off or no listener is attached, so callers must not build
    /// attribute values before checking the result.
    /// </summary>
    public Activity? StartOperation(string name, string key)
    {
        var activity = StartActivity(name);
        if (activity is null)
        {
            return null;
        }

        if (AllowRawKeysInTelemetry)
        {
            activity.SetTag(CacheTelemetryAttributes.Key, key);
        }
        else
        {
            activity.SetTag(CacheTelemetryAttributes.KeyFingerprint, Internal.KeyFingerprint.Compute(key));
        }

        return activity;
    }
```

`StartActivity` at line 210 currently checks only `TracingEnabled`; add `|| !CacheTelemetry.Activity.HasListeners()` so no `Activity` is allocated with nobody listening.

- [ ] **Step 7: Emit spans from the adapter**

`FusionCacheService` gains a `CacheTelemetryContext _telemetry` constructor parameter. Update the call site in `CacheEngineFactory` to `new FusionCacheService(cache, guard, telemetry)`.

Wrap each verb. The pattern, shown for `GetOrSetAsync` and `RemoveAsync`; apply the same shape to all sixteen:

```csharp
    public async ValueTask<TValue?> GetOrSetAsync<TValue>(
        string key,
        Func<CacheFactoryContext<TValue>, CancellationToken, Task<TValue?>> factory,
        CacheValue<TValue?> failSafeDefaultValue = default,
        CacheEntryOverrides? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var materializedTags = Validate(key, tags);

        using var activity = _telemetry.StartOperation("cache.get_or_set", key);
        var factoryExecuted = false;

        var value = await _inner.GetOrSetAsync<TValue?>(
            key,
            async (ctx, ct) =>
            {
                factoryExecuted = true;
                var wrapped = new CacheFactoryContext<TValue>(ctx!);
                var produced = await factory(wrapped, ct).ConfigureAwait(false);
                wrapped.ApplyOverrides();
                return produced;
            },
            ToMaybe(failSafeDefaultValue),
            Resolve(options),
            materializedTags,
            token).ConfigureAwait(false);

        if (activity is not null)
        {
            activity.SetTag(CacheTelemetryAttributes.Result, factoryExecuted ? CacheResults.Miss : CacheResults.Hit);
            activity.SetTag(CacheTelemetryAttributes.Layer, factoryExecuted ? CacheLayers.Factory : CacheLayers.Memory);
            activity.SetTag(CacheTelemetryAttributes.FactoryExecuted, factoryExecuted);
        }

        return value;
    }

    public async ValueTask RemoveAsync(string key, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        using var activity = _telemetry.StartOperation("cache.remove", key);
        activity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Removed);

        // Awaited, not returned: a `using` span on a returned task closes when the method returns,
        // which would record the duration of starting the work rather than doing it.
        await _inner.RemoveAsync(key, Resolve(options), token).ConfigureAwait(false);
    }
```

Span names, one per verb: `cache.get_or_set`, `cache.get_or_default`, `cache.try_get`, `cache.set`, `cache.remove`, `cache.expire`, `cache.remove_by_tag`, `cache.clear`. Sync twins use the same names. `cache.clear` has no key, so call `StartActivity("cache.clear")` directly rather than `StartOperation`. `cache.remove_by_tag` passes the tag as the key argument, and is subject to the same `AllowRawKeysInTelemetry` switch.

`TryGetAsync` and `TryGet` tag `Result` from `result.HasValue`.

**`GetOrDefaultAsync` and `GetOrDefault` carry no `Result` tag, and keep delegating to the engine.** Deriving a hit/miss would mean substituting the engine's `TryGet` plus a caller-side default, and the two verbs are not known to agree on stale-value handling under fail-safe. That is an unnecessary behavioural bet for one tag, and the outcome is already visible on the `cache.memory.get` / `cache.redis.get` child spans the Task 10 decorators emit:

```csharp
    public ValueTask<TValue?> GetOrDefaultAsync<TValue>(
        string key, TValue? defaultValue = default, CacheEntryOverrides? options = null, CancellationToken token = default)
    {
        _guard.ValidateKey(key);
        // No cache.result tag: see above. The layer child spans carry the outcome.
        using var activity = _telemetry.StartOperation("cache.get_or_default", key);
        return _inner.GetOrDefaultAsync(key, defaultValue, Resolve(options), token);
    }
```

Note the `using` on a span around a returned `ValueTask`: the span closes when the method returns, not when the task completes, so the recorded duration would be wrong. For every verb that returns the engine's task without awaiting it, either `await` it so the span spans the operation, or do not open a span. Prefer awaiting — make every span-bearing verb `async` and `await ... .ConfigureAwait(false)`.

- [ ] **Step 8: Emit a factory span**

Inside the factory wrapper in both `GetOrSetAsync` and `GetOrSet`, wrap the caller's delegate:

```csharp
                using var factoryActivity = _telemetry.StartActivity("cache.factory");
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var produced = await factory(wrapped, ct).ConfigureAwait(false);
                    factoryActivity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Hit);
                    _telemetry.RecordLayer(CacheLayers.Factory, "get", CacheResults.Hit, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return produced;
                }
                catch
                {
                    factoryActivity?.SetTag(CacheTelemetryAttributes.Result, CacheResults.Error);
                    _telemetry.RecordLayer(CacheLayers.Factory, "get", CacheResults.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    throw;
                }
```

- [ ] **Step 9: Run**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~OperationSpanTests|FullyQualifiedName~SpanKeyExposureTests"`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add -A src/Caching.NET tests/Caching.NET.Tests/Telemetry
git commit -m "feat!: emit Caching.NET operation spans and stop publishing engine telemetry names"
```

---

## Task 10: Layer decorators

**Files:**
- Create: `src/Caching.NET/Internal/InstrumentedMemoryCache.cs`
- Create: `src/Caching.NET/Internal/InstrumentedDistributedCache.cs`
- Modify: `src/Caching.NET/Internal/CacheEngineFactory.cs`
- Modify: `src/Caching.NET/Internal/InstrumentedBackplane.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/LayerTelemetryTests.cs`
- Test: `tests/Caching.NET.Tests.Integration/HybridLayerAttributionTests.cs`

**Interfaces:**
- Consumes: `CacheTelemetryContext.RecordLayer` and `StartActivity` from Task 9.
- Produces: `InstrumentedMemoryCache.Wrap(IMemoryCache, CacheTelemetryContext)` and `InstrumentedDistributedCache.Wrap(IDistributedCache, CacheTelemetryContext)`, each returning the inner instance unchanged when instrumentation is off — the pattern `InstrumentedBackplane.Wrap` already uses.

**Context for the implementer:** `MemoryCache` is created in `CacheEngineFactory.CreateMemoryCache` and handed to the `FusionCache` constructor. `RedisCache` is created at lines 59-63 and handed to `cache.SetupDistributedCache`. Both are interfaces the engine consumes (`IMemoryCache`, `IDistributedCache`), so a decorator is a drop-in. `MemoryCache` is also `IDisposable` and is owned by `CacheInstance` — the decorator must forward `Dispose` to the inner instance, and `CacheInstance` must keep receiving the *inner* disposable so ownership is unchanged.

- [ ] **Step 1: Write the failing test**

Create `tests/Caching.NET.Tests/Telemetry/LayerTelemetryTests.cs`:

```csharp
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

[Collection(MetricsCollection.Name)]
public class LayerTelemetryTests
{
    [Fact]
    public async Task MemoryProbes_EmitLayerSpansAndDurations()
    {
        using var spans = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var metrics = new MetricCollector("caching.net.layer.duration");
        using var host = TestHost.BuildNamed("layer-spans", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await host.NamedCache("layer-spans").GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));
        await host.NamedCache("layer-spans").GetOrDefaultAsync<int>("k");

        Assert.Contains(spans.Activities, a => a.OperationName == "cache.memory.get");
        Assert.Contains(
            metrics.Measurements,
            m => m.Tags[CacheTelemetryAttributes.Layer] as string == CacheLayers.Memory
                 && m.Tags[CacheTelemetryAttributes.Name] as string == "layer-spans");
    }

    [Fact]
    public async Task TelemetryDisabled_InstallsNoDecorators()
    {
        using var spans = new SpanRecorder(CacheTelemetry.ActivitySourceName);
        using var host = TestHost.BuildNamed("no-telemetry", c => c
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithTelemetry(tracing: false, metrics: false));

        await host.NamedCache("no-telemetry").GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));

        Assert.DoesNotContain(spans.Activities, a => a.OperationName.StartsWith("cache.", StringComparison.Ordinal));
    }
}
```

Match `MetricCollector`'s existing constructor and `Measurements` shape to `tests/Caching.NET.Tests/Telemetry/MetricCollector.cs`; adapt the assertions if the API differs.

Create `tests/Caching.NET.Tests.Integration/HybridLayerAttributionTests.cs`, following the fixture pattern in `tests/Caching.NET.Tests.Integration/Fixtures/CacheHost.cs`:

```csharp
// A Hybrid hit served by L2 must be attributed to cache.layer=redis. Before the layer decorators
// this was reported as cache.layer=memory, because the engine's Hit event does not name the level.
[Fact]
public async Task HybridHitServedByRedis_IsAttributedToRedis()
{
    // 1. Build two Hybrid hosts against the same Redis container, backplane disabled.
    // 2. Write a key through host A.
    // 3. Read it through host B, whose L1 has never seen it.
    // 4. Assert caching.net.hits carries cache.layer=redis for host B's cache name.
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~LayerTelemetryTests"`
Expected: FAIL — no `cache.memory.get` span is emitted.

- [ ] **Step 3: Create `src/Caching.NET/Internal/InstrumentedMemoryCache.cs`**

```csharp
using System.Diagnostics;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Caching.Memory;

namespace Caching.NET.Internal;

/// <summary>
/// Records in-process memory layer probes as Caching.NET spans and metrics.
/// </summary>
/// <remarks>
/// This is how per-layer detail is published under Caching.NET's own names. The engine's own
/// memory-level activity source and meter are never registered, so nothing here duplicates them.
/// Nothing is cached, evicted or reordered: every member forwards to the inner cache.
/// </remarks>
internal sealed class InstrumentedMemoryCache : IMemoryCache
{
    private readonly IMemoryCache _inner;
    private readonly CacheTelemetryContext _telemetry;

    private InstrumentedMemoryCache(IMemoryCache inner, CacheTelemetryContext telemetry)
    {
        _inner = inner;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Wraps <paramref name="memoryCache"/>, or returns it unchanged when neither metrics nor
    /// tracing is enabled, so a cache with telemetry off pays nothing at all.
    /// </summary>
    public static IMemoryCache Wrap(IMemoryCache memoryCache, CacheTelemetryContext telemetry)
        => telemetry.MetricsEnabled || telemetry.TracingEnabled
            ? new InstrumentedMemoryCache(memoryCache, telemetry)
            : memoryCache;

    public ICacheEntry CreateEntry(object key)
    {
        using var activity = _telemetry.StartActivity("cache.memory.set");
        var started = Stopwatch.GetTimestamp();
        var entry = _inner.CreateEntry(key);
        _telemetry.RecordLayer(CacheLayers.Memory, "set", CacheResults.Set, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return entry;
    }

    public bool TryGetValue(object key, out object? value)
    {
        using var activity = _telemetry.StartActivity("cache.memory.get");
        var started = Stopwatch.GetTimestamp();
        var found = _inner.TryGetValue(key, out value);
        var result = found ? CacheResults.Hit : CacheResults.Miss;

        activity?.SetTag(CacheTelemetryAttributes.Result, result);
        _telemetry.RecordLayer(CacheLayers.Memory, "get", result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return found;
    }

    public void Remove(object key)
    {
        using var activity = _telemetry.StartActivity("cache.memory.remove");
        var started = Stopwatch.GetTimestamp();
        _inner.Remove(key);
        _telemetry.RecordLayer(CacheLayers.Memory, "remove", CacheResults.Removed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    // Ownership is unchanged: CacheInstance disposes the inner MemoryCache directly, so this
    // decorator must not dispose it a second time.
    public void Dispose()
    {
    }
}
```

- [ ] **Step 4: Create `src/Caching.NET/Internal/InstrumentedDistributedCache.cs`**

Same shape over `Microsoft.Extensions.Caching.Distributed.IDistributedCache`, which has six members: `Get`, `GetAsync`, `Set`, `SetAsync`, `Refresh`, `RefreshAsync`, `Remove`, `RemoveAsync`. Span names `cache.redis.get`, `cache.redis.set`, `cache.redis.refresh`, `cache.redis.remove`; layer `CacheLayers.Redis`. `Get`/`GetAsync` tag `Result` from whether the returned byte array is `null`. The type is not `IDisposable`, so there is no disposal concern.

- [ ] **Step 5: Install the decorators**

In `src/Caching.NET/Internal/CacheEngineFactory.cs`, change line 42 and the `FusionCache` construction so the engine receives the wrapped memory cache while `CacheInstance` keeps the raw one:

```csharp
        var memoryCache = CreateMemoryCache(options);
        var instrumentedMemory = InstrumentedMemoryCache.Wrap(memoryCache, telemetry);
        ...
        var cache = new FusionCache(MicrosoftOptions.Create(engineOptions), instrumentedMemory, engineLogger);
```

`CacheInstance` still receives `memoryCache` in its owned-resources list, unchanged.

At line 72, wrap the distributed cache:

```csharp
            cache.SetupDistributedCache(
                InstrumentedDistributedCache.Wrap(distributedCache, telemetry),
                serializer);
```

- [ ] **Step 6: Add publish spans to the backplane**

In `src/Caching.NET/Internal/InstrumentedBackplane.cs`, change `Wrap` to also engage when tracing is enabled:

```csharp
    public static IFusionCacheBackplane Wrap(IFusionCacheBackplane backplane, CacheTelemetryContext telemetry)
        => telemetry.MetricsEnabled || telemetry.TracingEnabled
            ? new InstrumentedBackplane(backplane, telemetry)
            : backplane;
```

and wrap both `Publish` overloads in `using var activity = _telemetry.StartActivity("cache.backplane.publish");`, tagging `CacheTelemetryAttributes.BackgroundOperation` as `true`. Leave `Subscribe`, `Unsubscribe` and the receive path metrics-only: receive runs on the engine's subscription callback thread, outside any request context, and its latency is the invalidation propagation delay.

- [ ] **Step 7: Run**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~LayerTelemetryTests"`
Expected: PASS.

Run (Docker required): `dotnet test tests/Caching.NET.Tests.Integration --filter "FullyQualifiedName~HybridLayerAttributionTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A src/Caching.NET tests/Caching.NET.Tests/Telemetry tests/Caching.NET.Tests.Integration
git commit -m "feat: publish per-layer spans and durations under Caching.NET names"
```

---

## Task 11: One producer per signal

**Files:**
- Modify: `src/Caching.NET/Internal/CacheEventBridge.cs`
- Modify: `src/Caching.NET/Internal/FusionCacheService.cs`
- Modify: `src/Caching.NET/Internal/InstrumentedMemoryCache.cs`
- Modify: `src/Caching.NET/Internal/InstrumentedDistributedCache.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/NoDoubleCountingTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 9–10.
- Produces: `CacheEventBridge` subscribing to 13 events instead of 22.

**Context:** the decorators now observe L1 and L2 hits and misses that `CacheEventBridge` also observes, and the adapter observes the operation result and factory execution that the bridge also observes. Recording both double-counts inside one meter. Split the sources of truth as follows.

| Signal | Producer |
|---|---|
| `caching.net.operations`, `caching.net.hits`, `caching.net.misses` | adapter — **logical**, one record per logical operation, no `cache.layer` dimension |
| `caching.net.layer.duration` | decorators (memory, redis) and the adapter's factory wrapper — per *probe*, carries `cache.layer` |
| `caching.net.factory.executions`, `caching.net.errors` (factory), `caching.net.fail_safe.served`, `caching.net.invalidations` (eviction), `caching.net.background.operations` | event bridge |

**Two rows of this table were corrected during execution** (see the ledger's Task 11 ruling). The
original version assigned `hits`/`misses` to the decorators and `factory.executions` to the adapter.
Both were wrong, and both were measured wrong rather than argued wrong:

- **`hits`/`misses` on the decorators counts engine *probes*, not logical reads.** The engine
  double-probes L1 on a cold read, and writes emit internal `get` probes too. A measured sequence of
  3 logical reads (2 hits, 1 miss) plus one write recorded `hits=2, misses=5` — a reported hit ratio
  of 28.6% against a true 66.7%. `docs/TELEMETRY.md` ships `hits/(hits+misses)` as its "Hit ratio"
  query and documents the counters as reads, so probe semantics under those names silently breaks
  every consumer dashboard by a factor that varies with call mix. The per-layer breakdown is not
  lost: `layer.duration` already carries `cache.result=hit|miss` per layer.
- **`factory.executions` on the adapter cannot distinguish a background execution.** FusionCache
  reuses the same factory delegate for eager-refresh and background completions, and
  `FusionCacheFactoryExecutionContext<T>` exposes no background flag (`HasStaleValue` is not a
  discriminator — a foreground fail-safe-eligible refresh has one too). Measured over 60 eager-refresh
  cycles with 120 real factory invocations, the adapter recorded 120 as `background=false` while the
  bridge recorded 60 as `background=true`: 180 records for 120 executions. The engine distinguishes
  them and only the bridge can see that, so the bridge owns this counter — and with it the factory
  `errors`, whose adapter-side copy Step 3 originally introduced as a duplicate.

The adapter still owns `RecordLayer(CacheLayers.Factory, …)`: it is the only component that can time
the delegate.

- [ ] **Step 1: Write the failing test**

Create `tests/Caching.NET.Tests/Telemetry/NoDoubleCountingTests.cs`:

```csharp
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

[Collection(MetricsCollection.Name)]
public class NoDoubleCountingTests
{
    [Fact]
    public async Task OneColdGetOrSet_RecordsOneMissAndOneFactoryExecution()
    {
        using var collector = new MetricCollector(
            "caching.net.misses", "caching.net.hits", "caching.net.factory.executions", "caching.net.operations");
        using var host = TestHost.BuildNamed("count-once", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await host.NamedCache("count-once").GetOrSetAsync<int>("k", (_, _) => Task.FromResult(1));

        var mine = collector.Measurements
            .Where(m => m.Tags[CacheTelemetryAttributes.Name] as string == "count-once")
            .ToArray();

        Assert.Equal(1, Total(mine, "caching.net.factory.executions"));
        Assert.Equal(1, Total(mine, "caching.net.misses"));
        Assert.Equal(0, Total(mine, "caching.net.hits"));

        static long Total(IEnumerable<Measurement> measurements, string instrument)
            => measurements.Where(m => m.Instrument == instrument).Sum(m => m.Value);
    }

    [Fact]
    public async Task OneWarmRead_RecordsOneHit()
    {
        using var collector = new MetricCollector("caching.net.hits");
        using var host = TestHost.BuildNamed("count-hit", c => c.UseInMemory().WithApplicationPrefix("tests"));

        await host.NamedCache("count-hit").SetAsync("k", 1);
        await host.NamedCache("count-hit").GetOrDefaultAsync<int>("k");

        var hits = collector.Measurements
            .Where(m => m.Tags[CacheTelemetryAttributes.Name] as string == "count-hit")
            .Sum(m => m.Value);

        Assert.Equal(1, hits);
    }
}
```

Adapt `Measurement`, `Instrument`, `Value` and `Tags` to the shapes already in `tests/Caching.NET.Tests/Telemetry/MetricCollector.cs`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~NoDoubleCountingTests"`
Expected: FAIL — misses and hits are counted twice, once by the bridge and once by a decorator.

- [ ] **Step 3: Shrink `CacheEventBridge`**

Delete these subscriptions and their handlers from `Subscribe`, `Dispose` and the class body: `Hit`/`OnHit`, `Miss`/`OnMiss`, `Set`/`OnSet`, `Remove`/`OnRemove`, `RemoveByTag`/`OnRemoveByTag`, `Clear`/`OnClear`, `Expire`/`OnExpire`, `FactorySuccess`/`OnFactorySuccess`, `FactoryError`/`OnFactoryError`.

`OnFactoryError` also records `RecordError(CacheLayers.Factory, "FactoryError")`. The adapter's factory span already records `RecordLayer(..., CacheResults.Error, ...)` but not the error counter — add `_telemetry.RecordError(CacheLayers.Factory, "FactoryError")` to the adapter's `catch` block so the counter survives.

Keep: `FactorySyntheticTimeout`, `FailSafeActivate`, `EagerRefresh`, `BackgroundFactorySuccess`, `BackgroundFactoryError`, `Memory.Eviction`, `Distributed.SerializationError`, `Distributed.DeserializationError`, `Distributed.CircuitBreakerChange`, `Backplane.CircuitBreakerChange`, `Backplane.MessagePublished`, `Backplane.MessageReceived`. That is 12 subscriptions plus the retained `FactorySyntheticTimeout` handler's error recording.

Update the class `<remarks>` to say it now bridges only the events that no other Caching.NET component can observe.

- [ ] **Step 4: Move hit and miss counting into the decorators**

In `InstrumentedMemoryCache.TryGetValue`, after computing `result`:

```csharp
        if (found)
        {
            _telemetry.RecordHit("get", CacheLayers.Memory);
        }
        else
        {
            _telemetry.RecordMiss("get", CacheLayers.Memory);
        }
```

`RecordMiss` currently takes only an operation. Add a `layer` parameter to `CacheTelemetryContext.RecordMiss` and tag it, matching `RecordHit`. Update the one existing call site.

Do the same in `InstrumentedDistributedCache.Get`/`GetAsync` with `CacheLayers.Redis`.

- [ ] **Step 5: Record operation and invalidation counters from the adapter**

In `FusionCacheService`, alongside each span:

- `GetOrSetAsync`/`GetOrSet`: `_telemetry.RecordFactoryExecution(succeeded, background: false)` in the factory wrapper, and `_telemetry.RecordOperation("get_or_set", result)` after.
- `SetAsync`/`Set`: `_telemetry.RecordSet("set")`.
- `RemoveAsync`, `ExpireAsync`, `RemoveByTagAsync`, `ClearAsync` and their sync twins: `_telemetry.RecordInvalidation("remove" | "expire" | "remove_by_tag" | "clear")`.

Add `RecordOperation(string operation, string result)` to `CacheTelemetryContext` — the same body as `RecordSet` with a caller-supplied result. It must NOT carry a `cache.layer` dimension: the operation is not attributable to one layer, and per-layer truth lives on `caching.net.layer.duration`.

- [ ] **Step 5a: Stop the operation span claiming a layer it cannot know (added during execution)**

`FusionCacheService.ResolveHitLayer()` (added in Task 9) resolves the hit layer from the cache
*mode*: `_telemetry.Mode == nameof(CacheMode.Redis) ? CacheLayers.Redis : CacheLayers.Memory`. It was
written to mirror `CacheEventBridge.OnHit`, which Step 3 of this task deletes.

That expression is **wrong in Hybrid mode**. A Hybrid hit that misses L1 and is served by L2 is tagged
`cache.layer=memory` — wrong in exactly the case an operator investigates (cold instance, post-deploy,
post-eviction, short L1 TTL). The engine's top-level `Hit` event args carry only `Key`/`IsStale`, and
while the per-level event hubs do expose the level, they also fire for FusionCache's internal
tag/clear-marker lookups (one logical read was observed producing 2 extra `MEM Miss` and 2 extra
`DIST Miss`), so the level cannot be attributed to the logical operation by counting those events.

Reporting `memory` when Redis answered is worse than reporting nothing. Change `ResolveHitLayer` so
that on a **hit**:

- `InMemory` mode → `CacheLayers.Memory` (tautologically correct)
- `Redis` mode → `CacheLayers.Redis` (tautologically correct)
- `Hybrid` mode → return `null`, and skip the `cache.layer` tag entirely

Leave the factory case alone: `factoryExecuted` → `CacheLayers.Factory` is correct in every mode.
Per-layer truth for Hybrid lives on the decorator-owned `caching.net.layer.duration` and on the child
`cache.memory.*` / `cache.redis.*` spans, which Task 10 attributes correctly.

Add to `NoDoubleCountingTests` (or `OperationSpanTests`) an assertion that a Hybrid-mode hit's
operation span carries **no** `cache.layer` tag, and that an InMemory-mode hit still carries
`cache.layer=memory`.

- [ ] **Step 6: Run**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~NoDoubleCountingTests|FullyQualifiedName~CacheTelemetryTests|FullyQualifiedName~FailSafeMetricTests|FullyQualifiedName~GuardViolationMetricTests"`
Expected: PASS. `CacheTelemetryTests` asserts the dimension allow-list — extend it for `caching.net.layer.duration` if it fails on the new instrument.

- [ ] **Step 7: Commit**

```bash
git add -A src/Caching.NET tests/Caching.NET.Tests/Telemetry
git commit -m "refactor: one producer per telemetry signal, no double counting"
```

---

## Task 12: Migrate call sites

**Files:**
- Modify: every remaining file in `tests/`, `samples/`, `benchmark/`, `aot/` that references the engine.

**Interfaces:**
- Consumes: everything above. Produces nothing new.

- [ ] **Step 1: List what is left**

Run:

```bash
grep -rl "ZiggyCreatures" --include="*.cs" tests/ samples/ benchmark/ aot/ | grep -v "/obj/\|/bin/"
```

Expected at this point: `benchmark/*` (5 files), `aot/Caching.NET.AotSmoke/Program.cs`, `tests/Caching.NET.Tests.Pod/Program.cs`, `tests/Caching.NET.Tests.Integration/*` (6 files), `tests/Caching.NET.Tests.Chaos/*` (2 files), and any unit-test file not yet touched.

`samples/Caching.NET.Sample/Controllers/ProductCatalogController.cs` is **already migrated** — it moved into Task 7 during execution, because rebinding `CacheExtensions` to `ICacheService` broke the sample's compile and would otherwise have left the solution build red from Task 7 through Task 12.

Two unit tests are **deletions, not migrations** — see the ledger:

- `InMemoryCacheTests.OverlongKey_IsRejectedByTheEngineLevelGuard` asserts the behaviour of the engine hook deleted in Task 6; `CacheServiceTests.KeyGuard_FiresEvenWhenOverridesArePassed` covers the replacement.
- `RegistrationTests.DisabledCache_RunsTheFactoryEveryTimeAndCachesNothing` exactly duplicates two `NullCacheServiceTests` cases.

While migrating, audit every test that pairs a short `WithDefaultExpiration` with a fixed `Task.Delay`: `CacheEntryOptions.JitterMaxDuration` defaults to **2 seconds**, so such tests are silently flaky unless they also set `.WithJitter(TimeSpan.Zero)`.

`tests/Caching.NET.Tests/Internal/EngineKeyRedactionTests.cs` and `tests/Caching.NET.Tests/Analyzers/CacheEntryOptionsAnalyzerTests.cs` are handled in Task 13, not here.

- [ ] **Step 2: Apply the substitutions, file by file**

| From | To |
|---|---|
| `using ZiggyCreatures.Caching.Fusion;` | delete, add `using Caching.NET.Options;` if overrides are used |
| `IFusionCache` | `ICacheService` |
| `MaybeValue<T>` | `CacheValue<T>` |
| `new FusionCacheEntryOptions { … }` | `new CacheEntryOverrides { … }` |
| `cache.CreateEntryOptions(o => …)` | `new CacheEntryOverrides { … }` |
| `Duration = x` | `DistributedExpiration = x` (or `LocalExpiration`, per intent) |
| `IsFailSafeEnabled = x` | `FailSafe = x` |
| `MemoryCacheDuration = x` | `LocalExpiration = x` |
| `DistributedCacheDuration = x` | `DistributedExpiration = x` |
| `GetOrSetAsync(key, async _ => v)` | `GetOrSetAsync(key, async (_, _) => v)` — the factory now takes a context |

Do not batch-edit with `sed`: the factory-signature change and the `Duration` split both need a human decision per call site.

- [ ] **Step 3: Build everything**

Run: `dotnet build`
Expected: success, zero warnings — `TreatWarningsAsErrors` is on.

- [ ] **Step 4: Run the unit and property suites**

Run: `dotnet test tests/Caching.NET.Tests tests/Caching.NET.Tests.Properties`
Expected: PASS.

- [ ] **Step 5: Run the Docker suites**

Run: `dotnet test tests/Caching.NET.Tests.Integration tests/Caching.NET.Tests.Chaos`
Expected: PASS. These include the cross-process `Caching.NET.Tests.Pod` backplane suite and the chaos backplane-loss and restart tests. If any backplane test fails, stop — the design says backplane behaviour is unchanged, so a failure here is a real regression, not a migration artifact.

- [ ] **Step 6: Run the AOT smoke test**

Run: `dotnet publish aot/Caching.NET.AotSmoke -c Release` then execute the produced binary.
Expected: success.

- [ ] **Step 7: Commit**

```bash
git add -A tests samples benchmark aot
git commit -m "refactor: migrate every call site to ICacheService"
```

---

## Task 13: Add the SkipBackplaneNotification cross-process test

**Files:**
- Modify: `tests/Caching.NET.Tests.Pod/Program.cs`
- Test: `tests/Caching.NET.Tests.Integration/BackplaneSuppressionTests.cs`

**Interfaces:**
- Consumes: `CacheEntryOverrides.SkipBackplaneNotification` from Task 1.

- [ ] **Step 1: Write the test**

Create `tests/Caching.NET.Tests.Integration/BackplaneSuppressionTests.cs`, following the existing pod-launching pattern:

```csharp
// A write carrying SkipBackplaneNotification must not invalidate another process's L1 copy.
// Without this, bulk warm-up publishes one invalidation per entry to every instance.
[Fact]
public async Task SkipBackplaneNotification_LeavesTheOtherProcessL1Intact()
{
    // 1. Start two Hybrid pods against the same Redis container, backplane enabled.
    // 2. Pod A writes "k"=1. Pod B reads it, warming B's L1.
    // 3. Pod A writes "k"=2 with new CacheEntryOverrides { SkipBackplaneNotification = true }.
    // 4. Poll pod B: it must still read 1 from its L1.
    // 5. Pod A writes "k"=3 with no overrides.
    // 6. Poll pod B until it reads 3, proving the backplane still works.
}
```

Step 6 is what makes the test meaningful — without it, a broken backplane would pass step 4.

- [ ] **Step 2: Extend the pod protocol if needed**

`tests/Caching.NET.Tests.Pod/Program.cs` accepts commands over its existing channel. Add a `set-nobackplane <key> <value>` command that calls `SetAsync` with `SkipBackplaneNotification = true`, mirroring the existing `set` command.

- [ ] **Step 3: Run**

Run: `dotnet test tests/Caching.NET.Tests.Integration --filter "FullyQualifiedName~BackplaneSuppressionTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/Caching.NET.Tests.Integration/BackplaneSuppressionTests.cs tests/Caching.NET.Tests.Pod/Program.cs
git commit -m "test: cross-process coverage for SkipBackplaneNotification"
```

---

## Task 14: Repurpose the analyzer

**Files:**
- Rename: `src/Caching.NET.Analyzers/CacheEntryOptionsAnalyzer.cs` → `src/Caching.NET.Analyzers/EngineTypeAnalyzer.cs`
- Modify: `src/Caching.NET.Analyzers/AnalyzerReleases.Unshipped.md`
- Rewrite: `tests/Caching.NET.Tests/Analyzers/CacheEntryOptionsAnalyzerTests.cs` → `EngineTypeAnalyzerTests.cs`
- Delete: `tests/Caching.NET.Tests/Internal/EngineKeyRedactionTests.cs` if it asserts engine span behaviour that no longer exists — read it first and decide.

**Interfaces:**
- Produces: `CACHENET001` retitled *"Caching.NET engine type referenced directly"*, flagging any symbol whose containing namespace starts with `ZiggyCreatures.Caching.Fusion` or `StackExchange.Redis`.

- [ ] **Step 1: Write the failing test**

Rewrite the analyzer test file to assert the new diagnostic. Keep the existing harness setup; replace the sources under test:

```csharp
    [Fact]
    public async Task EngineTypeInConsumerCode_IsReported()
    {
        const string source = """
            using ZiggyCreatures.Caching.Fusion;

            public class Consumer
            {
                public void Use(IFusionCache cache) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 1);
    }

    [Fact]
    public async Task RedisTypeInConsumerCode_IsReported()
    {
        const string source = """
            using StackExchange.Redis;

            public class Consumer
            {
                public void Use(ConfigurationOptions options) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 1);
    }

    [Fact]
    public async Task CachingNetTypes_AreNotReported()
    {
        const string source = """
            using Caching.NET;
            using Caching.NET.Options;

            public class Consumer
            {
                public void Use(ICacheService cache)
                    => cache.Set("k", 1, new CacheEntryOverrides { Size = 1 });
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 0);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~EngineTypeAnalyzerTests"`
Expected: FAIL.

- [ ] **Step 3: Rewrite the analyzer**

Rename the class to `EngineTypeAnalyzer`, keep `DiagnosticId = "CACHENET001"`, and replace the descriptor and analysis:

```csharp
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        title: "Caching.NET engine type referenced directly",
        messageFormat:
            "'{0}' is an implementation detail of Caching.NET, not part of its API. "
            + "Use Caching.NET's own types — ICacheService, CacheEntryOverrides, CachingBuilder, CachingOptions — "
            + "so this code keeps compiling if the internal cache engine changes.",
        category: "Caching.NET",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Caching.NET owns its whole public surface so the internal cache engine can be replaced without a "
            + "source change in consuming applications. Referencing an engine type directly forfeits that guarantee.",
        helpLinkUri: "https://github.com/baps-apps/caching-net/blob/main/docs/ARCHITECTURE.md");

    private static readonly string[] s_bannedNamespacePrefixes =
    [
        "ZiggyCreatures.Caching.Fusion",
        "StackExchange.Redis"
    ];
```

Register a symbol-agnostic node action over `SyntaxKind.IdentifierName` and `SyntaxKind.QualifiedName`, resolve the symbol, walk to its containing namespace, and report when the fully-qualified namespace starts with a banned prefix. Skip nodes inside the `Caching.NET` assembly itself — the analyzer ships to consumers, and `src/Caching.NET` legitimately references both namespaces. Gate on `compilationContext.Compilation.AssemblyName` not being `Caching.NET` or starting with `Caching.NET.Tests`.

- [ ] **Step 4: Update the release notes**

In `src/Caching.NET.Analyzers/AnalyzerReleases.Unshipped.md`, update the `CACHENET001` row's title to match. Confirm it is not in `AnalyzerReleases.Shipped.md`:

Run: `grep -n "CACHENET001" src/Caching.NET.Analyzers/AnalyzerReleases.Shipped.md`
Expected: no match. If there is one, add a `Removed` entry and allocate `CACHENET002` for the new rule instead.

- [ ] **Step 5: Run**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~EngineTypeAnalyzerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/Caching.NET.Analyzers tests/Caching.NET.Tests/Analyzers tests/Caching.NET.Tests/Internal
git commit -m "feat!: repurpose CACHENET001 to flag engine types in consumer code"
```

---

## Task 14b: Context-free `GetOrSet` overload (added during execution)

**Why this exists.** Task 12 found that `GetOrSetAsync` cannot infer `TValue` from the factory lambda:
`TValue` appears only inside `CacheFactoryContext<TValue>`, which is a lambda *parameter* type, so C#
cannot bind the lambda before `TValue` is known and every call site fails with `CS0411` unless it
writes `GetOrSetAsync<Payload>(…)` explicitly. No reordering, delegate-type change or `Func` shuffle
fixes this while the context stays generic, and the context's generic parameter is load-bearing —
`StaleValue`, `NotModified()` and `Fail()` all return `TValue`. The user decided to add a context-free
overload. **This must land before Task 15 regenerates `PublicApi.approved.txt`.**

**Files:**
- Modify: `src/Caching.NET/ICacheService.cs`
- Modify: `src/Caching.NET/Internal/FusionCacheService.cs`
- Modify: `src/Caching.NET/Internal/NullCacheService.cs`
- Test: `tests/Caching.NET.Tests/Caching/CacheServiceTests.cs`

**Interfaces:**
- Produces: two new `ICacheService` members, taking the surface from 17 to 19.

```csharp
ValueTask<TValue?> GetOrSetAsync<TValue>(
    string key,
    Func<CancellationToken, Task<TValue?>> factory,
    CacheEntryOverrides? options = null,
    IEnumerable<string>? tags = null,
    CancellationToken token = default);

TValue? GetOrSet<TValue>(
    string key,
    Func<CancellationToken, TValue?> factory,
    CacheEntryOverrides? options = null,
    IEnumerable<string>? tags = null);
```

The cancellation parameter is named `token`, matching all 17 pre-existing `ICacheService` members.
(An earlier revision of this section said `cancellationToken`; that was an error, caught in review and
corrected before Task 15 froze the baseline. Parameter names are part of the public surface because
callers can pass them as named arguments.)

Different arity from the context-taking overloads, so **real lambdas** bind unambiguously. This is not
an absolute guarantee: a literal `null` factory carries no arity information and converts to either
delegate type, so `GetOrSetAsync<int>("k", factory: null!, options: null)` is `CS0121` ambiguous. That
is a compile-time error on a call no caller should make (both implementations
`ArgumentNullException.ThrowIfNull` the factory anyway), so it is accepted rather than designed around.

Both must go through the
same guards, spans and counters as the existing overloads — the simplest correct implementation
delegates to the context-taking overload with a factory that ignores the context, so there is exactly
one code path and no second place for the telemetry rules to drift.

- [ ] **Step 1: Write the failing test**

In `tests/Caching.NET.Tests/Caching/CacheServiceTests.cs`, a test that does not name the type:

```csharp
[Fact]
public async Task ContextFreeFactory_InfersTheValueType()
{
    using var host = TestHost.BuildInMemory();

    var value = await host.Cache().GetOrSetAsync("Order:1", async _ => await Task.FromResult(41) + 1);

    Assert.Equal(42, value);
}
```

The point of the test is that it **compiles** without a type argument; the value assertion is
secondary. Add the sync twin.

- [ ] **Step 2: Run to verify it fails**

Expected: `CS0411` before the overload exists.

- [ ] **Step 3: Add the two members** to `ICacheService` with full XML docs, then implement in
  `FusionCacheService` and `NullCacheService`.

- [ ] **Step 4: Confirm the guards and telemetry still fire on the new path**

Add a test that the key guard rejects an overlong key through the context-free overload, and one that
a cold call through it records exactly one miss and one `caching.net.operations{get_or_set}` — the
one-producer-per-signal rule from Task 11 must hold on both overloads.

- [ ] **Step 5: Update the docs examples**

`README.md` and any doc showing `GetOrSetAsync<T>(…)` for a factory that ignores the context should
show the short form as the default, with the explicit type argument kept only on the adaptive path.

- [ ] **Step 6: Commit**

```bash
git add -A src/Caching.NET tests/Caching.NET.Tests
git commit -m "feat: add context-free GetOrSet overloads so TValue infers"
```

---

## Task 15: Approve the API and enforce the boundary

**Files:**
- Modify: `tests/Caching.NET.Tests/Api/PublicApiTests.cs`
- Regenerate: `tests/Caching.NET.Tests/Api/PublicApi.approved.txt`

**Interfaces:**
- Consumes: the whole public surface.
- Produces: a test that fails the build if an engine type reappears in the public API.

- [ ] **Step 1: Write the failing test**

Add to `tests/Caching.NET.Tests/Api/PublicApiTests.cs`:

```csharp
    [Theory]
    [InlineData("ZiggyCreatures")]
    [InlineData("StackExchange")]
    [InlineData("Microsoft.Extensions.Caching.Memory")]
    public void ApprovedApi_NamesNoImplementationDetail(string bannedNamespace)
    {
        var approved = File.ReadAllText(ApprovedApiPath);

        Assert.DoesNotContain(
            bannedNamespace,
            approved,
            StringComparison.Ordinal);
    }
```

Reuse whatever the file already uses to locate the approved text; if it has no such member, add:

```csharp
    private static string ApprovedApiPath =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Api", "PublicApi.approved.txt");
```

and confirm the approved file is copied to output or resolved from the source tree the same way `PublicApiTests` already does it.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~PublicApiTests"`
Expected: FAIL on the approval comparison (the surface changed) and on all three theory cases.

- [ ] **Step 3: Regenerate the approved surface**

Run:

```bash
CACHINGNET_APPROVE_API=1 dotnet test tests/Caching.NET.Tests -f net10.0 --filter PublicApiTests
```

- [ ] **Step 4: Review the diff as the breaking-change review**

Run: `git diff tests/Caching.NET.Tests/Api/PublicApi.approved.txt`

Confirm, line by line:
- `ICacheService` with 16 members is present.
- `CacheValue<TValue>`, `CacheFactoryContext<TValue>`, `CacheEntryOverrides`, `CacheEntryPriority` are present.
- `ICacheProvider` returns `ICacheService` on three members.
- `CacheExtensions` takes `ICacheService` and `CacheEntryOverrides`.
- `CacheSecurityOptions.AllowRawKeysInTelemetry` and `CacheObservabilityOptions.EnableLayerMetrics` are present.
- `CacheTelemetry` has no `Engine*` member.
- `RedisOptions.ConfigureConnection` is gone; `CachingBuilder` has no `ConfigurationOptions` overload.
- No line contains `ZiggyCreatures`, `StackExchange` or `Microsoft.Extensions.Caching.Memory`.

Anything unexpected in that diff is a leak to fix, not a line to approve.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS, including the Docker suites.

- [ ] **Step 6: Commit**

```bash
git add tests/Caching.NET.Tests/Api
git commit -m "test: approve the engine-free public API and enforce the boundary"
```

---

## Task 16: Benchmarks

**Files:**
- Create: `benchmark/Caching.NET.Benchmark/TelemetryTierBenchmarks.cs`
- Modify: `benchmark/Caching.NET.Benchmark/CacheHostFactory.cs`
- Modify: `docs/BENCHMARKS.md`

**Interfaces:**
- Consumes: `ICacheService`, `CachingBuilder.WithTelemetry`.

**Gates**, from the spec. Absolute nanoseconds are published alongside each.

| Path | Gate |
|---|---|
| `GetOrSet` hit, InMemory, telemetry off | ≤2%, zero added allocations |
| `GetOrSet` hit, InMemory, metrics on | ≤10% |
| `GetOrSet` hit and miss, Redis and Hybrid | ≤2% |
| Tracing enabled, all modes | measured and published, no gate |

- [ ] **Step 1: Record the baseline before measuring the change**

```bash
git stash
cd benchmark/Caching.NET.Benchmark && dotnet run -c Release -- --filter '*InMemoryBenchmarks*'
# save the results to /tmp/baseline-inmemory.md
git stash pop
```

If the working tree is already fully migrated, take the baseline from the last commit before Task 1 instead:

```bash
git worktree add /tmp/caching-baseline <sha-before-task-1>
cd /tmp/caching-baseline/benchmark/Caching.NET.Benchmark && dotnet run -c Release -- --filter '*InMemoryBenchmarks*'
```

- [ ] **Step 2: Create `benchmark/Caching.NET.Benchmark/TelemetryTierBenchmarks.cs`**

Three hosts built once in `[GlobalSetup]` — telemetry off, metrics on, metrics and tracing on with an `ActivityListener` attached and sampling everything. One `[Benchmark]` per tier calling `GetOrSetAsync` on a pre-warmed key, and one per tier on a cold key. Follow the existing `InMemoryBenchmarks` structure and its `[MemoryDiagnoser]` attribute.

- [ ] **Step 3: Run all suites**

```bash
cd benchmark/Caching.NET.Benchmark
dotnet run -c Release -- --filter '*InMemoryBenchmarks*'
dotnet run -c Release -- --filter '*TelemetryTierBenchmarks*'
dotnet run -c Release -- --filter '*SerializationBenchmarks*'
CACHINGNET_BENCH_REDIS="127.0.0.1:63790,abortConnect=false" dotnet run -c Release -- --filter '*RedisBenchmarks*'
```

- [ ] **Step 4: Compare against the gates**

If the telemetry-off hit path exceeds 2% or allocates, stop and investigate before continuing. The most likely cause is a decorator being installed when it should not be — check `InstrumentedMemoryCache.Wrap` and the `CacheEngineFactory` call site.

- [ ] **Step 5: Update `docs/BENCHMARKS.md`**

Add a section with the three tiers, the absolute nanoseconds, the allocation columns, and the before/after delta for each gate.

- [ ] **Step 6: Commit**

```bash
git add benchmark docs/BENCHMARKS.md
git commit -m "perf: measure and publish the three telemetry tiers"
```

---

## Task 17: Documentation

**Files:**
- Modify: `CLAUDE.md`, `README.md`, `CHANGELOG.md`
- Modify: `docs/ARCHITECTURE.md`, `docs/TELEMETRY.md`, `docs/SECURITY.md`, `docs/OPERATIONS.md`, `docs/MIGRATION-V2-TO-V3.md`
- Untouched: `docs/MIGRATION-V1-TO-V2.md`, `docs/V2.0.0-RELEASE-IMPACT.md` — historical, carry a banner saying so

- [ ] **Step 1: `CLAUDE.md`**

Invert API design rule #1:

```markdown
1. **Never expose the cache engine.** No engine type appears in any public signature — not the
   operation contract, not per-call options, not telemetry names, not connection configuration.
   `ICacheService` is the API. `Internal/FusionCacheService` is the only type that calls an engine
   operation; `Internal/CacheEngineFactory` is the only type that configures one.
2. **The contract is eight verbs, permanently.** A new engine capability lands as a `CachingOptions`
   knob or a `CacheEntryOverrides` field, never a ninth verb on `ICacheService`.
```

Update the public-surface table: `ICacheService` replaces the `IFusionCache` row; add `CacheValue<T>`, `CacheFactoryContext<T>`, `CacheEntryOverrides`, `CacheEntryPriority`. Update the "Adding a feature" section — a new telemetry signal now needs a producer chosen from the one-producer-per-signal table.

- [ ] **Step 2: `docs/ARCHITECTURE.md`**

- §1: layer diagram gains `FusionCacheService`, `InstrumentedMemoryCache`, `InstrumentedDistributedCache`; the application row lists `ICacheService` instead of `IFusionCache`.
- §2: the resolution-cycle rationale stands, retyped to `ICacheService`.
- §3: delete the final paragraph (lines 91-99) about per-call options escaping the skip flags and the `CACHENET001` build-time patch. Replace with a paragraph explaining that `CacheEntryOverrides` is additive by construction.
- §6: rewrite the telemetry pipeline — spans now come from the adapter and decorators, not the engine.
- §7: the guard table's two "application-invoked" rows become "every call, in `FusionCacheService`".
- §8: update the feature-addition guidance.

- [ ] **Step 3: `docs/TELEMETRY.md`**

Substantial rewrite. One source, one meter. Remove every instruction to register engine sources and every warning about them. Add: the span catalogue from Task 9 and Task 10, `caching.net.layer.duration`, `EnableLayerMetrics`, `AllowRawKeysInTelemetry`, and worked trace examples for InMemory, Redis and Hybrid on both a cold miss and a warm hit.

- [ ] **Step 4: `docs/SECURITY.md`**

Guard coverage table now shows key and tag guards enforced on every call. New section on `AllowRawKeysInTelemetry`: what it exports, where it lands, and why the default is off. Note that fingerprints are xxHash64 and not a defence against brute force over a small key space.

- [ ] **Step 4a: `docs/HEALTH-CHECKS.md`**

Added to this task's scope during execution — the Task 6 review found it documents probe internals that changed. Verify each of these against the code as it stands, and correct anything still false:

- the rows for `SkipMemoryCacheRead`, `ReThrowDistributedCacheExceptions` and `EagerRefreshThreshold` in the probe-options table
- "The three distributed-layer overrides are conditional on `IFusionCache.HasDistributedCache`" — the type name is now `ICacheService`, and the conditional lives inside `FusionCacheService`'s internal probe helpers
- "a Redis outage surfaces as `Degraded` in both Redis and Hybrid modes" — true again only because Task 6's fix restored the probe's L1 bypass; confirm before leaving it in
- any reference to `IFusionCache` in an example

- [ ] **Step 5: `docs/OPERATIONS.md`**

Dashboards and alerts rewritten against branded instruments only. Remove any panel that references a `fusioncache.*` instrument.

- [ ] **Step 6: `README.md` and `docs/MIGRATION-V2-TO-V3.md`**

Every example rewritten against `ICacheService` and `CacheEntryOverrides`. The migration guide's v2→v3 mapping table now maps `ICacheService` (v2) to `ICacheService` (v3) with a shape change, rather than to `IFusionCache`.

- [ ] **Step 7: `CHANGELOG.md`**

Rewrite the 3.0.0 entry. Drop the "plugins, events" claim from the Added section. Add the telemetry rework, the guard enforcement, the additive overrides, and the corrected Hybrid layer attribution.

- [ ] **Step 8: Verify no stale engine references remain in prose**

Run:

```bash
grep -rn "IFusionCache\|FusionCacheEntryOptions\|fusioncache\." --include="*.md" . | grep -v "docs/superpowers\|MIGRATION-V1-TO-V2\|V2.0.0-RELEASE-IMPACT\|CHANGELOG"
```

Expected: only `docs/ARCHITECTURE.md` explaining the internal composition, which is correct — that file is for people changing Caching.NET.

- [ ] **Step 9: Commit**

```bash
git add -A CLAUDE.md README.md CHANGELOG.md docs
git commit -m "docs: rewrite for the engine-agnostic contract and branded telemetry"
```

---

## Task 18: Release gate

**Files:**
- Create: `docs/audits/<today>-v3.0.0-production-readiness-review.md`

The existing audit at `docs/audits/2026-08-08-v3.0.0-production-readiness-review.md` measured the *previous* surface. It is re-run, not edited.

- [ ] **Step 1: Full verification**

```bash
dotnet build
dotnet test
dotnet publish aot/Caching.NET.AotSmoke -c Release
dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs
```

All four must succeed. Record the actual output — do not claim a pass without it.

- [ ] **Step 2: Verify the packed artifact**

```bash
unzip -l nupkgs/Caching.NET.3.0.0.nupkg | grep -i "analyzers\|dll"
```

Confirm `analyzers/dotnet/cs/Caching.NET.Analyzers.dll` is present and that `Caching.NET.Analyzers` is not packed as a separate package.

- [ ] **Step 3: Confirm every "Done means" item from the spec**

Walk the list in `docs/superpowers/specs/2026-08-08-engine-agnostic-cache-contract-design.md`, recording evidence for each:

- approved API contains none of the three banned namespaces
- an application registering only the branded source and meter sees operation spans in all three modes, per-layer spans and durations, and no duplicated instrument
- a Hybrid L2 hit is `cache.layer=redis` in span and metric
- no span carries a key unless `AllowRawKeysInTelemetry` is set
- `dotnet test` green including Docker suites and the cross-process pod suite
- AOT smoke passes
- every performance gate met, numbers in `docs/BENCHMARKS.md`

- [ ] **Step 4: Write the audit and commit**

```bash
git add docs/audits
git commit -m "docs: v3.0.0 release-gate review against the engine-agnostic surface"
```

---

## Self-Review Notes

**Spec coverage.** Every spec section maps to a task: public surface → 1, 3; additive overrides → 2; adapter and guards → 4, 6; null cache → 5; composition and DI → 6; extensions → 7; leak scrub → 8; telemetry surface, spans, key policy → 9; layer decorators and backplane spans → 10; one producer per signal and the Hybrid fix → 10, 11; call sites → 12; backplane suppression → 13; analyzer → 14; enforcement → 15; performance gates → 16; documentation → 17; done-means → 18.

**Task 5 ordering.** `NullCacheServiceTests` cannot compile until Task 6 changes `CacheInstance.Cache` to `ICacheService`. That is called out in Task 5 Step 1; re-run that test class first thing after Task 6 Step 10.

**Risks carried into execution — verify these, do not assume them.**

1. **`FusionCacheFactoryExecutionContext.Options` may be get-only.** The plan mutates it in place, which works either way. If a future engine version makes it settable, do not switch to assignment — in-place mutation is what the engine's own idiom expects.
2. **`ConfigurationOptions.CertificateValidation` multicast semantics** (Task 8 Step 5). If only the last handler's return value wins, appending the application's callback would *override* Caching.NET's validation rather than tighten it, silently weakening TLS. Confirm before wiring, and chain explicitly if so.
3. **Span duration on non-awaited tasks** (Task 9). Every verb that opens a span must `await` the engine call rather than return its task, or the span closes early and records the wrong duration. Applies to all sixteen verbs.
4. **`caching.net.hits` / `misses` semantics change** (Task 11). They move from the engine's operation-level `Hit`/`Miss` events to the decorators' layer-level probes. On Hybrid, one `GetOrSet` that misses L1 and hits L2 now records one miss *and* one hit, where it previously recorded one hit. That is more accurate but it is a dashboard-visible change — call it out in `CHANGELOG.md` and `docs/OPERATIONS.md`, and check whether any alert rule assumes hits and misses sum to operations.
