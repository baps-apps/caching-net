# Caching.NET v2 — P2 API Expansion Implementation Plan

> **Status: COMPLETE — all tasks implemented and verified as of 2026-05-06**

> **Audit follow-up (same branch):** `HybridCacheService.GetAsync` uses a **value-type wrapper** to avoid caching `default(T)` for structs; **`ExistsAsync`** uses **`IDistributedCache`** when available; **`StaleEntryTracker`** now **prunes** on a cadence/size cap; **`RoutingCacheService`** implements **`IAsyncDisposable`**; **`InMemoryCacheService`** batch paths use **sync** `IMemoryCache` loops. See [INTERNALS.md](../../INTERNALS.md) and [MIGRATION-V1-TO-V2.md](../../MIGRATION-V1-TO-V2.md).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land all P2 deliverables from `docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md` §4–§5 + §12: batch APIs (`GetMany/SetMany/RemoveMany`), single-key extras (`GetAsync/RefreshAsync/ExistsAsync`), per-call expiration controls (sliding + jitter + AllowStaleFor), `CacheKeyBuilder`, `MessagePackCacheSerializer`, stale-while-revalidate orchestrator, `RequireTagSupport()` builder gate.

**Architecture:** Every new operation lands on `ICacheService` (v2 interface contract from spec §4). Concrete services (`InMemoryCacheService`, `RedisCacheService`, `HybridCacheService`) implement directly; `RoutingCacheService` prefixes keys, applies TTL jitter, dispatches per-call mode overrides, and orchestrates stale-while-revalidate (InMemory + Redis only — Hybrid manages internally and is a no-op for SwR). Tag operations are functional only in Hybrid mode (native HybridCache tags); other modes are documented no-ops, with `CachingBuilder.RequireTagSupport()` failing startup when Mode≠Hybrid. SwR uses an in-process side channel: an internal `IStaleEntryTracker` maps prefixed key → `(absoluteExpiresAt, staleUntil)`. Background refreshes run inside a `StaleRefreshThrottle` semaphore (size = `CacheOptions.StaleRefreshConcurrency`) and acquire the same stripe lock so only one refresh per key. Batch ops are client-side fan-out (parallel single-key calls); server-side Redis MGET/MSET is deferred to P3 (requires breaking out of `IDistributedCache`).

**Tech Stack:** .NET 8/9/10 multi-target, `Microsoft.Extensions.Caching.Distributed`, `Microsoft.Extensions.Caching.Hybrid` (HybridCacheEntryOptions tags), `MessagePack 3.x`, xUnit + Moq. Builds on P0 (`StripedLockManager`, Polly pipeline) and P1 (`PayloadEnvelope`, `CacheInstruments`).

**Pre-flight (one-time):**
- Branch: continue on `vpatel/v2`.
- Verify P1 baseline: `dotnet test` → 119 (×3 TFM) + 4 analyzer pass before starting Task 1.
- Commit-message form: `feat(p2): <area> — <action>` so v2.0.0 changelog can grep.

**Scope clarifications (vs literal spec):**
- Spec §4 ICacheService drops the `localExpiration` parameter from `GetOrCreateAsync`/`SetAsync`. **This plan keeps `localExpiration`** because P0 already stabilised it and three current implementations rely on it. Sliding expiration arrives via `CacheCallOptions.SlidingExpiration`, not a new positional argument.
- Spec §4 shows `RemoveByTagAsync(string tag)` only. Existing v2 contract has both `string` and `IEnumerable<string>` overloads. Keep both — they cost nothing and the IEnumerable form is just a fan-out.
- Tag effects on InMemory/Redis are **no-op** unless `RequireTagSupport()` is configured (which startup-validates Hybrid mode).
- Server-side Redis MGET/MSET pipelining is **deferred to P3**. P2 batch ops are client-side fan-out via `Task.WhenAll`.
- Stale-while-revalidate is **InMemory + Redis only**. Hybrid is a no-op (HybridCache does not expose stale-window semantics).

**Doc / behavior sync (2026-05-06):** Fluent **`Enable()`**, **`UseDevelopmentDefaults()` / `UseProductionDefaults()`**, **`WithKeyValidator` / `WithKeyTransformer`**; startup validation **`KeyPrefix` + `MaximumKeyLength` user-key budget** (≥32 chars for user segment); see [MIGRATION-V1-TO-V2.md](../../MIGRATION-V1-TO-V2.md).

---

## File Structure

**Create:**
- `src/Caching.NET/Keys/CacheKey.cs` — public `static class CacheKey` with `For<T>(object id)` factory.
- `src/Caching.NET/Keys/CacheKeyBuilder.cs` — public `sealed class CacheKeyBuilder` with `WithVariant`, `WithSegment`, `Build`.
- `src/Caching.NET/Internal/TtlJitter.cs` — internal `static class TtlJitter` with `Apply(TimeSpan, double)`.
- `src/Caching.NET/Internal/StaleEntryTracker.cs` — internal in-process tracker (`ConcurrentDictionary<string, StaleMetadata>`).
- `src/Caching.NET/Internal/StaleRefreshThrottle.cs` — internal `SemaphoreSlim` wrapper bounding background refresh fan-out.
- `src/Caching.NET/Serialization/MessagePackCacheSerializer.cs` — public `sealed class` implementing `ICacheSerializer` with `FormatId="msgpack"`.
- `tests/Caching.NET.Tests/Keys/CacheKeyBuilderTests.cs`
- `tests/Caching.NET.Tests/Internal/TtlJitterTests.cs`
- `tests/Caching.NET.Tests/Serialization/MessagePackCacheSerializerTests.cs`
- `tests/Caching.NET.Tests/Services/StaleWhileRevalidateTests.cs`

**Modify:**
- `Directory.Packages.props` — keep MessagePack at 3.x (already present from P0).
- `src/Caching.NET/Options/CacheCallOptions.cs` — add `FactoryTimeout`, `AbsoluteExpiration`, `SlidingExpiration`, `AllowStaleFor`, `Tags`, `JitterPercentage`.
- `src/Caching.NET/Abstractions/ICacheService.cs` — add `ExistsAsync`, `GetAsync<T>`, `RefreshAsync<T>`, `GetManyAsync<T>`, `SetManyAsync<T>`, `RemoveManyAsync`.
- `src/Caching.NET/Services/InMemoryCacheService.cs` — implement new members, sliding-expiration, jitter wiring.
- `src/Caching.NET/Services/RedisCacheService.cs` — implement new members, sliding-expiration, jitter wiring; `_swr` side-key for stale metadata.
- `src/Caching.NET/Services/HybridCacheService.cs` — implement new members; tags wired into `HybridCacheEntryOptions`; SwR is no-op.
- `src/Caching.NET/Services/RoutingCacheService.cs` — orchestrate SwR, prefix keys for new ops, dispatch per-call overrides, apply jitter pre-dispatch.
- `src/Caching.NET/Extensions/CacheServiceCallExtensions.cs` — overloads for new ops accepting `CacheCallOptions`.
- `src/Caching.NET/CachingBuilder.cs` — `WithTtlJitter`, `WithStaleRefreshConcurrency`, `RequireTagSupport`, `WithMessagePackSerializer` convenience.
- `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs` — register `IStaleEntryTracker`, `StaleRefreshThrottle`; honour `RequireTagSupport()` validator.
- `src/Caching.NET/Validation/CacheOptionsValidator.cs` — gate `RequireTagSupport()` (assert Mode==Hybrid when set).
- `src/Caching.NET/PublicAPI.Unshipped.txt` — add new public surface.
- `samples/Caching.NET.Sample/Program.cs` — demonstrate `WithTtlJitter`, `CacheKey.For<T>(id)`.
- `docs/MIGRATION-V1-TO-V2.md` (created later in P3 docs phase) — note that P2 deliverables are already covered.

---

## Task 1: Expand CacheCallOptions per spec §4

Add the missing per-call properties so every later task can rely on them.

**Files:**
- Modify: `src/Caching.NET/Options/CacheCallOptions.cs`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`
- Test: `tests/Caching.NET.Tests/Options/CacheCallOptionsTests.cs` (create if missing)

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Caching.NET.Tests/Options/CacheCallOptionsTests.cs
using Caching.NET.Options;
using Xunit;

namespace Caching.NET.Tests.Options;

public class CacheCallOptionsTests
{
    [Fact]
    public void Defaults_match_spec()
    {
        var o = new CacheCallOptions();
        Assert.Null(o.Mode);
        Assert.False(o.BypassCache);
        Assert.False(o.ForceRefresh);
        Assert.True(o.CoalesceConcurrent);
        Assert.Null(o.FactoryTimeout);
        Assert.Null(o.AbsoluteExpiration);
        Assert.Null(o.SlidingExpiration);
        Assert.Null(o.AllowStaleFor);
        Assert.Null(o.Tags);
        Assert.Null(o.JitterPercentage);
    }

    [Fact]
    public void All_properties_round_trip_via_init()
    {
        var tags = new[] { "tenant:a", "kind:order" };
        var o = new CacheCallOptions
        {
            Mode = CacheMode.Redis,
            BypassCache = true,
            ForceRefresh = true,
            CoalesceConcurrent = false,
            FactoryTimeout = TimeSpan.FromSeconds(5),
            AbsoluteExpiration = TimeSpan.FromMinutes(10),
            SlidingExpiration = TimeSpan.FromMinutes(2),
            AllowStaleFor = TimeSpan.FromMinutes(1),
            Tags = tags,
            JitterPercentage = 0.05,
        };

        Assert.Equal(CacheMode.Redis, o.Mode);
        Assert.True(o.BypassCache);
        Assert.True(o.ForceRefresh);
        Assert.False(o.CoalesceConcurrent);
        Assert.Equal(TimeSpan.FromSeconds(5), o.FactoryTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), o.AbsoluteExpiration);
        Assert.Equal(TimeSpan.FromMinutes(2), o.SlidingExpiration);
        Assert.Equal(TimeSpan.FromMinutes(1), o.AllowStaleFor);
        Assert.Equal(tags, o.Tags);
        Assert.Equal(0.05, o.JitterPercentage);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheCallOptionsTests`
Expected: FAIL — properties do not exist.

- [ ] **Step 3: Add properties to CacheCallOptions**

Append to `src/Caching.NET/Options/CacheCallOptions.cs` inside the class (after `CoalesceConcurrent`):

```csharp
    /// <summary>
    /// Per-call factory timeout. When null, the application-level
    /// <see cref="CacheOptions.FactoryTimeout"/> applies.
    /// </summary>
    public TimeSpan? FactoryTimeout { get; init; }

    /// <summary>
    /// Per-call absolute expiration override. When null, the call-site
    /// <c>expiration</c> argument or the application default applies.
    /// </summary>
    public TimeSpan? AbsoluteExpiration { get; init; }

    /// <summary>
    /// Per-call sliding expiration. Resets the entry's TTL on each access.
    /// Honoured by InMemory and Redis modes; ignored by Hybrid (HybridCache
    /// does not expose sliding-expiration semantics).
    /// </summary>
    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>
    /// Stale-while-revalidate window: after the absolute expiration the entry
    /// continues to serve for up to this duration while a single background
    /// refresh runs. Honoured by InMemory and Redis modes; ignored by Hybrid.
    /// </summary>
    public TimeSpan? AllowStaleFor { get; init; }

    /// <summary>
    /// Tag identifiers associated with this entry. Tags are honoured only when
    /// <see cref="CacheOptions.Mode"/> is <see cref="CacheMode.Hybrid"/>;
    /// in other modes they are ignored unless <c>CachingBuilder.RequireTagSupport()</c>
    /// has been called (which fails startup if Mode is not Hybrid).
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Per-call jitter override (0.0 disables; 0.50 = ±50%). Range 0.0–0.5.
    /// When null, <see cref="CacheOptions.TtlJitterPercentage"/> applies.
    /// </summary>
    public double? JitterPercentage { get; init; }
```

- [ ] **Step 4: Update PublicAPI.Unshipped.txt**

Append to `src/Caching.NET/PublicAPI.Unshipped.txt` (alphabetical position):

```
Caching.NET.Options.CacheCallOptions.AbsoluteExpiration.get -> System.TimeSpan?
Caching.NET.Options.CacheCallOptions.AbsoluteExpiration.init -> void
Caching.NET.Options.CacheCallOptions.AllowStaleFor.get -> System.TimeSpan?
Caching.NET.Options.CacheCallOptions.AllowStaleFor.init -> void
Caching.NET.Options.CacheCallOptions.FactoryTimeout.get -> System.TimeSpan?
Caching.NET.Options.CacheCallOptions.FactoryTimeout.init -> void
Caching.NET.Options.CacheCallOptions.JitterPercentage.get -> double?
Caching.NET.Options.CacheCallOptions.JitterPercentage.init -> void
Caching.NET.Options.CacheCallOptions.SlidingExpiration.get -> System.TimeSpan?
Caching.NET.Options.CacheCallOptions.SlidingExpiration.init -> void
Caching.NET.Options.CacheCallOptions.Tags.get -> System.Collections.Generic.IReadOnlyList<string!>?
Caching.NET.Options.CacheCallOptions.Tags.init -> void
```

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheCallOptionsTests`
Expected: PASS — 2 tests.

```bash
git add src/Caching.NET/Options/CacheCallOptions.cs src/Caching.NET/PublicAPI.Unshipped.txt tests/Caching.NET.Tests/Options/CacheCallOptionsTests.cs
git commit -m "feat(p2): expand CacheCallOptions with per-call expiration, tags, jitter"
```

---

## Task 2: ExistsAsync, GetAsync<T>, RefreshAsync<T>

Three single-key reads/writes the spec adds.

**Files:**
- Modify: `src/Caching.NET/Abstractions/ICacheService.cs`
- Modify: `src/Caching.NET/Services/InMemoryCacheService.cs`
- Modify: `src/Caching.NET/Services/RedisCacheService.cs`
- Modify: `src/Caching.NET/Services/HybridCacheService.cs`
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`
- Test: `tests/Caching.NET.Tests/Services/CacheServiceCoreApiTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Caching.NET.Tests/Services/CacheServiceCoreApiTests.cs
using Caching.NET.Abstractions;
using Caching.NET.Options;
using Caching.NET.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Services;

public class CacheServiceCoreApiTests
{
    private static InMemoryCacheService BuildInMemory()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var opts = Options.Create(new CacheOptions { KeyPrefix = "core-api" });
        return new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);
    }

    [Fact]
    public async Task GetAsync_returns_default_for_missing_key()
    {
        ICacheService svc = BuildInMemory();
        var v = await svc.GetAsync<string>("missing");
        Assert.Null(v);
    }

    [Fact]
    public async Task GetAsync_returns_value_after_SetAsync()
    {
        ICacheService svc = BuildInMemory();
        await svc.SetAsync("k", "v");
        var v = await svc.GetAsync<string>("k");
        Assert.Equal("v", v);
    }

    [Fact]
    public async Task ExistsAsync_returns_false_for_missing_then_true_after_set()
    {
        ICacheService svc = BuildInMemory();
        Assert.False(await svc.ExistsAsync("k"));
        await svc.SetAsync("k", "v");
        Assert.True(await svc.ExistsAsync("k"));
    }

    [Fact]
    public async Task RefreshAsync_runs_factory_and_overwrites_existing_entry()
    {
        ICacheService svc = BuildInMemory();
        await svc.SetAsync("k", "old");
        await svc.RefreshAsync("k", _ => Task.FromResult("new"));
        Assert.Equal("new", await svc.GetAsync<string>("k"));
    }

    [Fact]
    public async Task RefreshAsync_writes_value_when_key_absent()
    {
        ICacheService svc = BuildInMemory();
        await svc.RefreshAsync("k", _ => Task.FromResult("first"));
        Assert.Equal("first", await svc.GetAsync<string>("k"));
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheServiceCoreApiTests`
Expected: FAIL — `GetAsync<T>`, `ExistsAsync`, `RefreshAsync<T>` do not exist.

- [ ] **Step 3: Add to ICacheService**

Append to `src/Caching.NET/Abstractions/ICacheService.cs` inside the interface:

```csharp
    /// <summary>
    /// Reads a value from the cache without invoking a factory. Returns <c>default(T)</c>
    /// when the key is absent. Implementations must not throw on miss.
    /// </summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Returns <c>true</c> when the cache contains an entry for <paramref name="key"/>.
    /// Implementations should use the cheapest existence check available
    /// (e.g. Redis EXISTS; IMemoryCache TryGetValue).
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Always invokes <paramref name="factory"/> and writes the result into the cache,
    /// overwriting any existing entry. Use to refresh stale data without removing the
    /// key first.
    /// </summary>
    Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull;
```

- [ ] **Step 4: Implement in InMemoryCacheService**

Append to `src/Caching.NET/Services/InMemoryCacheService.cs`:

```csharp
    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (cache.TryGetValue(key, out T? cached))
        {
            CacheInstruments.RecordHit(Mode, "get");
            return Task.FromResult<T?>(cached);
        }
        CacheInstruments.RecordMiss(Mode, "get", "NotFound");
        return Task.FromResult<T?>(default);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        var present = cache.TryGetValue(key, out _);
        if (present) CacheInstruments.RecordHit(Mode, "exists");
        else CacheInstruments.RecordMiss(Mode, "exists", "NotFound");
        return Task.FromResult(present);
    }

    /// <inheritdoc />
    public async Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        var value = await factory(cancellationToken);
        await SetAsync(key, value, expiration, localExpiration, cancellationToken);
    }
```

- [ ] **Step 5: Implement in RedisCacheService**

Append to `src/Caching.NET/Services/RedisCacheService.cs`:

```csharp
    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (ExceedsKeyLimit(key, nameof(GetAsync)))
        {
            CacheInstruments.RecordMiss(Mode, "get", "KeyTooLong");
            return default;
        }
        try
        {
            using var cts = CreateOpCts(cancellationToken);
            byte[]? bytes = await _readPipeline.ExecuteAsync(
                async ct => await _cache.GetAsync(key, ct),
                cts.Token);
            if (bytes is null or { Length: 0 })
            {
                CacheInstruments.RecordMiss(Mode, "get", "NotFound");
                return default;
            }
            var expectedFormat = ResolveFormatId(_serializer.FormatId);
            var expectedSchema = StableTypeHash.Compute<T>();
            var status = PayloadEnvelope.TryRead(bytes, expectedFormat, expectedSchema, out var payload);
            if (status == PayloadEnvelopeReadResult.Ok)
            {
                var value = _serializer.Deserialize<T>(payload);
                if (value != null)
                {
                    CacheInstruments.RecordHit(Mode, "get");
                    CacheInstruments.RecordPayloadBytes(Mode, "get", payload.Length);
                    return value;
                }
                CacheInstruments.RecordMiss(Mode, "get", "SerializationFailed");
                return default;
            }
            CacheInstruments.RecordMiss(Mode, "get", "EnvelopeInvalid");
            return default;
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            _logger.RedisGetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "get", ClassifyError(ex));
            return default;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (ExceedsKeyLimit(key, nameof(ExistsAsync))) return false;
        try
        {
            using var cts = CreateOpCts(cancellationToken);
            var bytes = await _readPipeline.ExecuteAsync(
                async ct => await _cache.GetAsync(key, ct),
                cts.Token);
            var present = bytes is { Length: > 0 };
            if (present) CacheInstruments.RecordHit(Mode, "exists");
            else CacheInstruments.RecordMiss(Mode, "exists", "NotFound");
            return present;
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            _logger.RedisGetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "exists", ClassifyError(ex));
            return false;
        }
    }

    /// <inheritdoc />
    public async Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        var value = await factory(cancellationToken);
        await SetAsync(key, value, expiration, localExpiration, cancellationToken);
    }
```

- [ ] **Step 6: Implement in HybridCacheService**

Append to `src/Caching.NET/Services/HybridCacheService.cs`:

```csharp
    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (!options.Value.Enabled || cache == null)
        {
            CacheInstruments.RecordMiss(Mode, "get", "Disabled");
            return default;
        }
        // HybridCache has no native "peek"; emulate via a sentinel factory that throws
        // and recover the value when present. Cheap miss-path: factory just returns default.
        try
        {
            T value = await cache.GetOrCreateAsync<T>(
                key,
                static _ => ValueTask.FromResult(default(T)!),
                options: null,
                tags: null,
                cancellationToken);
            if (value is null)
            {
                CacheInstruments.RecordMiss(Mode, "get", "NotFound");
                return default;
            }
            CacheInstruments.RecordHit(Mode, "get");
            return value;
        }
        catch (Exception ex)
        {
            logger.HybridGetFailed(TruncateKey(key), ex);
            CacheInstruments.RecordError(Mode, "get", ClassifyError(ex));
            return default;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var v = await GetAsync<object>(key, cancellationToken);
        return v != null;
    }

    /// <inheritdoc />
    public async Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (!options.Value.Enabled || cache == null) return;
        try
        {
            var entry = BuildEntryOptions(expiration, localExpiration);
            T value = await factory(cancellationToken);
            await cache.SetAsync(key, value, entry, tags: null, cancellationToken);
            CacheInstruments.RecordSet(Mode);
        }
        catch (Exception ex)
        {
            logger.HybridSetFailed(TruncateKey(key), ex);
            CacheInstruments.RecordError(Mode, "refresh", ClassifyError(ex));
        }
    }
```

The Hybrid `GetAsync` shortcut treats a `default(T)` from the sentinel factory as a miss; this is deliberately approximate — HybridCache doesn't expose a real peek. The behavioural cost is that `GetAsync<T>` for value types where `default` is a valid stored value (e.g. `int 0`) will report a miss. Document on the method: callers needing strict semantics on value types should wrap in `Nullable<T>` or use `GetOrCreateAsync`.

Adjust the existing class — the sentinel factory in `GetAsync` may need a differently-shaped helper if `T : struct`. Since the v2 `ICacheService` constrains all generic methods to `T : notnull`, and this method is also `T : notnull`, the sentinel `default(T)!` compiles for both reference and non-nullable value types; runtime behaviour matches the documented caveat.

- [ ] **Step 7: Implement in RoutingCacheService**

Add to `src/Caching.NET/Services/RoutingCacheService.cs` inside the class:

```csharp
    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (IsDisabled)
        {
            CacheInstruments.RecordMiss(Mode, "get", "Disabled");
            return Task.FromResult<T?>(default);
        }
        return ResolveService(modeOverride: null).GetAsync<T>(PrependPrefix(key), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (IsDisabled) return Task.FromResult(false);
        return ResolveService(modeOverride: null).ExistsAsync(PrependPrefix(key), cancellationToken);
    }

    /// <inheritdoc />
    public Task RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (IsDisabled) return Task.CompletedTask;
        return ResolveService(modeOverride: null)
            .RefreshAsync(PrependPrefix(key), factory, expiration, localExpiration, cancellationToken);
    }
```

- [ ] **Step 8: Update PublicAPI.Unshipped.txt**

Append:

```
Caching.NET.Abstractions.ICacheService.ExistsAsync(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<bool>!
Caching.NET.Abstractions.ICacheService.GetAsync<T>(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<T?>!
Caching.NET.Abstractions.ICacheService.RefreshAsync<T>(string! key, System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>!>! factory, System.TimeSpan? expiration = null, System.TimeSpan? localExpiration = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
```

- [ ] **Step 9: Run tests + commit**

Run: `dotnet test`
Expected: PASS — 5 new tests + all P1 baseline.

```bash
git add src/Caching.NET tests/Caching.NET.Tests/Services/CacheServiceCoreApiTests.cs
git commit -m "feat(p2): add ExistsAsync/GetAsync/RefreshAsync to ICacheService"
```

---

## Task 3: GetManyAsync, SetManyAsync, RemoveManyAsync

Batch overloads. Implementation is client-side fan-out (`Task.WhenAll`); server-side Redis MGET/MSET deferred to P3. Replace existing `RemoveAsync(IEnumerable<string>)` with `RemoveManyAsync` (breaking — v2 baseline).

**Files:**
- Modify: `src/Caching.NET/Abstractions/ICacheService.cs`
- Modify: all four service classes
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`
- Test: `tests/Caching.NET.Tests/Services/CacheServiceBatchTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Caching.NET.Tests/Services/CacheServiceBatchTests.cs
using Caching.NET.Abstractions;
using Caching.NET.Options;
using Caching.NET.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Services;

public class CacheServiceBatchTests
{
    private static InMemoryCacheService BuildInMemory()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var opts = Options.Create(new CacheOptions { KeyPrefix = "batch" });
        return new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);
    }

    [Fact]
    public async Task SetMany_then_GetMany_returns_all_values()
    {
        ICacheService svc = BuildInMemory();
        var items = new Dictionary<string, string>
        {
            ["a"] = "1",
            ["b"] = "2",
            ["c"] = "3",
        };
        await svc.SetManyAsync(items);

        var results = await svc.GetManyAsync<string>(new[] { "a", "b", "c", "missing" });

        Assert.Equal(4, results.Count);
        Assert.Equal("1", results["a"]);
        Assert.Equal("2", results["b"]);
        Assert.Equal("3", results["c"]);
        Assert.Null(results["missing"]);
    }

    [Fact]
    public async Task RemoveMany_removes_all_listed_keys()
    {
        ICacheService svc = BuildInMemory();
        await svc.SetManyAsync(new Dictionary<string, string>
        {
            ["x"] = "1",
            ["y"] = "2",
            ["z"] = "3",
        });

        await svc.RemoveManyAsync(new[] { "x", "y" });

        Assert.False(await svc.ExistsAsync("x"));
        Assert.False(await svc.ExistsAsync("y"));
        Assert.True(await svc.ExistsAsync("z"));
    }

    [Fact]
    public async Task GetMany_with_empty_input_returns_empty_dictionary()
    {
        ICacheService svc = BuildInMemory();
        var results = await svc.GetManyAsync<string>(Array.Empty<string>());
        Assert.Empty(results);
    }

    [Fact]
    public async Task SetMany_with_null_throws_ArgumentNullException()
    {
        ICacheService svc = BuildInMemory();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            svc.SetManyAsync<string>(items: null!));
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheServiceBatchTests`
Expected: FAIL.

- [ ] **Step 3: Add to ICacheService**

Append to `src/Caching.NET/Abstractions/ICacheService.cs`:

```csharp
    /// <summary>
    /// Reads multiple values from the cache. The returned dictionary contains an entry
    /// for every key in <paramref name="keys"/> — missing keys map to <c>default(T)</c>.
    /// Order is preserved using the input enumeration order.
    /// </summary>
    Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Writes multiple values to the cache. All entries share the same expiration arguments.
    /// </summary>
    Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>
    /// Removes multiple keys. <c>null</c>/empty/whitespace keys are skipped.
    /// </summary>
    Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);
```

Also remove the existing v2 line:
```csharp
    Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);
```
…and delete its implementations in all four service files. Replace any existing call site of `RemoveAsync(IEnumerable<string>)` (in extension methods, samples, and tests) with `RemoveManyAsync`.

- [ ] **Step 4: Implement in InMemoryCacheService**

Append:

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        var dict = new Dictionary<string, T?>();
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            dict[k] = await GetAsync<T>(k, cancellationToken);
        }
        return dict;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var kvp in items)
        {
            await SetAsync(kvp.Key, kvp.Value, expiration, localExpiration, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys is null) return;
        foreach (var k in keys)
        {
            if (!string.IsNullOrWhiteSpace(k))
                await RemoveAsync(k, cancellationToken);
        }
    }
```

Then delete the existing `RemoveAsync(IEnumerable<string>)` method from this class.

- [ ] **Step 5: Implement in RedisCacheService**

Append:

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (keyList.Length == 0) return new Dictionary<string, T?>();

        var tasks = new Task<T?>[keyList.Length];
        for (int i = 0; i < keyList.Length; i++)
            tasks[i] = GetAsync<T>(keyList[i], cancellationToken);
        var values = await Task.WhenAll(tasks);

        var dict = new Dictionary<string, T?>(keyList.Length);
        for (int i = 0; i < keyList.Length; i++) dict[keyList[i]] = values[i];
        return dict;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        var tasks = new List<Task>(items.Count);
        foreach (var kvp in items)
            tasks.Add(SetAsync(kvp.Key, kvp.Value, expiration, localExpiration, cancellationToken));
        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys is null) return;
        var tasks = new List<Task>();
        foreach (var k in keys)
            if (!string.IsNullOrWhiteSpace(k))
                tasks.Add(RemoveAsync(k, cancellationToken));
        await Task.WhenAll(tasks);
    }
```

Then delete the existing `RemoveAsync(IEnumerable<string>)` method from this class.

- [ ] **Step 6: Implement in HybridCacheService**

Append:

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        var dict = new Dictionary<string, T?>();
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            dict[k] = await GetAsync<T>(k, cancellationToken);
        }
        return dict;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var kvp in items)
            await SetAsync(kvp.Key, kvp.Value, expiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys is null) return;
        foreach (var k in keys)
            if (!string.IsNullOrWhiteSpace(k))
                await RemoveAsync(k, cancellationToken);
    }
```

Then delete the existing `RemoveAsync(IEnumerable<string>)` method from this class.

- [ ] **Step 7: Implement in RoutingCacheService**

Append:

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (IsDisabled)
            return new Dictionary<string, T?>();

        var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (keyList.Length == 0) return new Dictionary<string, T?>();

        var prefixed = new string[keyList.Length];
        for (int i = 0; i < keyList.Length; i++) prefixed[i] = PrependPrefix(keyList[i]);

        var inner = await ResolveService(modeOverride: null)
            .GetManyAsync<T>(prefixed, cancellationToken);

        var dict = new Dictionary<string, T?>(keyList.Length);
        for (int i = 0; i < keyList.Length; i++)
            dict[keyList[i]] = inner.TryGetValue(prefixed[i], out var v) ? v : default;
        return dict;
    }

    /// <inheritdoc />
    public Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        if (IsDisabled) return Task.CompletedTask;
        var prefixed = new Dictionary<string, T>(items.Count);
        foreach (var kvp in items) prefixed[PrependPrefix(kvp.Key)] = kvp.Value;
        return ResolveService(modeOverride: null)
            .SetManyAsync(prefixed, expiration, localExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (IsDisabled || keys is null) return Task.CompletedTask;
        var prefixed = new List<string>();
        foreach (var k in keys) if (!string.IsNullOrWhiteSpace(k)) prefixed.Add(PrependPrefix(k));
        return ResolveService(modeOverride: null).RemoveManyAsync(prefixed, cancellationToken);
    }
```

Then delete the existing `RemoveAsync(IEnumerable<string>)` method from this class.

- [ ] **Step 8: Update CacheServiceCallExtensions**

Find and replace the existing `RemoveAsync(IEnumerable<string>)` extension (if present) with the new `RemoveManyAsync` name. Search for usages: `grep -rn "RemoveAsync(IEnumerable" src/ samples/ tests/` and rename mechanically.

- [ ] **Step 9: Update PublicAPI.Unshipped.txt**

Append:

```
Caching.NET.Abstractions.ICacheService.GetManyAsync<T>(System.Collections.Generic.IEnumerable<string!>! keys, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyDictionary<string!, T?>!>!
Caching.NET.Abstractions.ICacheService.RemoveManyAsync(System.Collections.Generic.IEnumerable<string!>! keys, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
Caching.NET.Abstractions.ICacheService.SetManyAsync<T>(System.Collections.Generic.IReadOnlyDictionary<string!, T>! items, System.TimeSpan? expiration = null, System.TimeSpan? localExpiration = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
```

Remove the old `RemoveAsync(IEnumerable<string>)` line from `PublicAPI.Unshipped.txt` if present.

- [ ] **Step 10: Run tests + commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add -A
git commit -m "feat(p2): add GetMany/SetMany/RemoveMany batch APIs (replaces RemoveAsync(IEnumerable))"
```

---

## Task 4: TTL jitter + sliding expiration

Apply jitter and sliding expiration in the routing layer (jitter) and concrete services (sliding).

**Files:**
- Create: `src/Caching.NET/Internal/TtlJitter.cs`
- Create: `tests/Caching.NET.Tests/Internal/TtlJitterTests.cs`
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs` (jitter wiring), `InMemoryCacheService.cs` + `RedisCacheService.cs` (sliding wiring)

- [ ] **Step 1: Write the TtlJitter test**

```csharp
// tests/Caching.NET.Tests/Internal/TtlJitterTests.cs
using Caching.NET.Internal;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class TtlJitterTests
{
    [Fact]
    public void Apply_with_zero_percentage_returns_input()
    {
        var ttl = TimeSpan.FromMinutes(10);
        Assert.Equal(ttl, TtlJitter.Apply(ttl, 0.0));
    }

    [Fact]
    public void Apply_with_negative_percentage_returns_input()
    {
        var ttl = TimeSpan.FromMinutes(10);
        Assert.Equal(ttl, TtlJitter.Apply(ttl, -0.10));
    }

    [Fact]
    public void Apply_with_ten_percent_jitter_stays_within_bounds_over_many_calls()
    {
        var ttl = TimeSpan.FromMinutes(10);
        var lower = ttl - TimeSpan.FromMinutes(1);
        var upper = ttl + TimeSpan.FromMinutes(1);

        for (int i = 0; i < 1000; i++)
        {
            var jittered = TtlJitter.Apply(ttl, 0.10);
            Assert.InRange(jittered, lower, upper);
        }
    }

    [Fact]
    public void Apply_clamps_percentage_at_0_5()
    {
        var ttl = TimeSpan.FromSeconds(100);
        var lower = TimeSpan.FromSeconds(50);
        var upper = TimeSpan.FromSeconds(150);

        for (int i = 0; i < 1000; i++)
        {
            var jittered = TtlJitter.Apply(ttl, 1.0); // > 0.5 should clamp
            Assert.InRange(jittered, lower, upper);
        }
    }

    [Fact]
    public void Apply_returns_at_least_one_millisecond_when_jitter_would_zero_ttl()
    {
        var jittered = TtlJitter.Apply(TimeSpan.FromMilliseconds(2), 0.50);
        Assert.True(jittered >= TimeSpan.FromMilliseconds(1));
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~TtlJitterTests`
Expected: FAIL.

- [ ] **Step 3: Implement TtlJitter**

```csharp
// src/Caching.NET/Internal/TtlJitter.cs
namespace Caching.NET.Internal;

/// <summary>
/// Applies symmetric jitter to a TTL window: result ∈ [ttl·(1−p), ttl·(1+p)] for p ∈ [0, 0.5].
/// Used by RoutingCacheService to spread cache-expiry storms.
/// </summary>
internal static class TtlJitter
{
    public static TimeSpan Apply(TimeSpan ttl, double percentage)
    {
        if (percentage <= 0) return ttl;
        var p = Math.Min(percentage, 0.5);
        // factor ∈ [-1, +1]
        var factor = (Random.Shared.NextDouble() * 2.0) - 1.0;
        var ticks = (long)(ttl.Ticks * (1.0 + p * factor));
        if (ticks < TimeSpan.FromMilliseconds(1).Ticks)
            ticks = TimeSpan.FromMilliseconds(1).Ticks;
        return TimeSpan.FromTicks(ticks);
    }
}
```

- [ ] **Step 4: Wire jitter into RoutingCacheService**

In `RoutingCacheService.cs`, add a private helper:

```csharp
    private TimeSpan? ApplyJitter(TimeSpan? expiration, double? perCallPercentage)
    {
        if (expiration is not { } ttl) return expiration;
        var pct = perCallPercentage ?? _optionsMonitor.CurrentValue.TtlJitterPercentage;
        return TtlJitter.Apply(ttl, pct);
    }
```

In `GetOrCreateAsync(...callOptions)` and the new write paths (`SetAsync`, `RefreshAsync`, `SetManyAsync`), apply jitter to `expiration` before forwarding to the concrete service. Update the existing call sites in `GetOrCreateAsync` and `SetAsync`:

```csharp
            var jittered = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage);
            // pass jittered to inner service.SetAsync / GetOrCreateAsync
```

For `SetManyAsync` apply jitter once per batch (`var jittered = ApplyJitter(expiration, perCallPct);`).

- [ ] **Step 5: Wire sliding expiration into InMemoryCacheService**

Modify `InMemoryCacheService.SetAsync` to honour sliding expiration. Since `SetAsync` doesn't currently receive `CacheCallOptions`, push sliding through a new internal overload OR widen the existing one.

Cleanest approach: add an internal overload accepting `MemoryCacheEntryOptions`:

```csharp
    internal Task SetAsync<T>(string key, T value, MemoryCacheEntryOptions entry, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration { EvictionCallback = s_evictionCallback });
        cache.Set(key, value, entry);
        CacheInstruments.RecordSet(Mode);
        return Task.CompletedTask;
    }
```

In `RoutingCacheService.SetAsync` (when Mode is InMemory and `SlidingExpiration` is set), build a `MemoryCacheEntryOptions` with both AbsoluteExpiration and SlidingExpiration and call this internal overload. Use a small dispatch helper:

```csharp
    private Task SetWithExpirationAsync<T>(
        ICacheService service, string prefixedKey, T value,
        TimeSpan? expiration, TimeSpan? sliding, TimeSpan? localExpiration,
        CancellationToken ct) where T : notnull
    {
        if (sliding is null) return service.SetAsync(prefixedKey, value, expiration, localExpiration, ct);
        if (service is InMemoryCacheService inMem)
        {
            var entry = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration,
                SlidingExpiration = sliding,
            };
            return inMem.SetAsync(prefixedKey, value, entry, ct);
        }
        if (service is RedisCacheService redis)
            return redis.SetAsync(prefixedKey, value, expiration, sliding, ct);
        // Hybrid does not support sliding — drop silently.
        return service.SetAsync(prefixedKey, value, expiration, localExpiration, ct);
    }
```

The `RedisCacheService.SetAsync` overload that takes `(key, value, expiration, sliding, ct)` does not yet exist; add it:

```csharp
    internal Task SetAsync<T>(string key, T value, TimeSpan? expiration, TimeSpan? sliding, CancellationToken cancellationToken) where T : notnull
        => SetAsyncCore(key, value, expiration, sliding, cancellationToken);

    private async Task SetAsyncCore<T>(string key, T value, TimeSpan? expiration, TimeSpan? sliding, CancellationToken cancellationToken) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        if (ExceedsKeyLimit(key, nameof(SetAsync))) return;

        var expirationSpan = expiration ?? _options.Value.GetDefaultExpiration() ?? DefaultExpiration;
        byte[] payload;
        try { payload = _serializer.Serialize(value); }
        catch (Exception ex)
        {
            _logger.RedisSerializationFailed(FormatKey(key), ex);
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            CacheInstruments.RecordError(Mode, "serialize", "Serialization");
            return;
        }
        if (_options.Value.MaximumPayloadBytes > 0 && payload.Length > _options.Value.MaximumPayloadBytes)
        {
            _logger.RedisPayloadTooLarge(FormatKey(key), payload.Length);
            return;
        }

        byte formatId = ResolveFormatId(_serializer.FormatId);
        ulong schemaHash = StableTypeHash.Compute<T>();
        byte[] wire = PayloadEnvelope.Write(payload, formatId, schemaHash);

        try
        {
            using var cts = CreateOpCts(cancellationToken);
            var entryOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expirationSpan,
                SlidingExpiration = sliding,
            };
            await _writePipeline.ExecuteAsync(
                async ct => await _cache.SetAsync(key, wire, entryOptions, ct),
                cts.Token);
            CacheInstruments.RecordSet(Mode);
            CacheInstruments.RecordPayloadBytes(Mode, "set", payload.Length);
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            _logger.RedisSetFailed(FormatKey(key), ex);
            CacheInstruments.RecordError(Mode, "set", ClassifyError(ex));
        }
    }
```

…and refactor the existing public `SetAsync` so it calls `SetAsyncCore(key, value, expiration, sliding: null, ct)`. This avoids duplication and keeps the existing shape compatible.

- [ ] **Step 6: Add a sliding-expiration test**

```csharp
// Append to tests/Caching.NET.Tests/Services/CacheServiceCoreApiTests.cs

[Fact]
public async Task Sliding_expiration_keeps_entry_alive_on_each_access_in_memory()
{
    var memory = new MemoryCache(new MemoryCacheOptions());
    var opts = Options.Create(new CacheOptions { KeyPrefix = "sliding", TtlJitterPercentage = 0 });
    var inMem = new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);
    var entry = new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(500),
        SlidingExpiration = TimeSpan.FromMilliseconds(200),
    };
    await inMem.SetAsync("k", "v", entry);

    for (int i = 0; i < 4; i++)
    {
        await Task.Delay(100);
        Assert.True(await inMem.ExistsAsync("k"));
    }
}
```

(Imports: `using Microsoft.Extensions.Caching.Memory;`)

- [ ] **Step 7: Run tests + commit**

Run: `dotnet test`
Expected: PASS — 5 jitter tests + 1 sliding test + all baselines.

```bash
git add -A
git commit -m "feat(p2): apply TTL jitter in routing layer; wire sliding expiration in InMemory + Redis"
```

---

## Task 5: CacheKey + CacheKeyBuilder

Helper for consumer code to build canonical keys.

**Files:**
- Create: `src/Caching.NET/Keys/CacheKey.cs`
- Create: `src/Caching.NET/Keys/CacheKeyBuilder.cs`
- Test: `tests/Caching.NET.Tests/Keys/CacheKeyBuilderTests.cs`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Caching.NET.Tests/Keys/CacheKeyBuilderTests.cs
using Caching.NET.Keys;
using Xunit;

namespace Caching.NET.Tests.Keys;

public class CacheKeyBuilderTests
{
    private sealed class Order { }

    [Fact]
    public void For_with_id_builds_TypeName_id()
    {
        var key = CacheKey.For<Order>(12345).Build();
        Assert.Equal("Order:12345", key);
    }

    [Fact]
    public void WithVariant_appends_variant_segment()
    {
        var key = CacheKey.For<Order>(12345).WithVariant("v2").Build();
        Assert.Equal("Order:12345:v2", key);
    }

    [Fact]
    public void WithSegment_appends_arbitrary_segment()
    {
        var key = CacheKey.For<Order>(12345).WithSegment("region-eu").Build();
        Assert.Equal("Order:12345:region-eu", key);
    }

    [Fact]
    public void Multiple_segments_chain()
    {
        var key = CacheKey.For<Order>(12345).WithVariant("v2").WithSegment("eu").Build();
        Assert.Equal("Order:12345:v2:eu", key);
    }

    [Fact]
    public void Build_throws_when_id_contains_colon_or_whitespace()
    {
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>("bad:id").Build());
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>("bad id").Build());
    }

    [Fact]
    public void Build_throws_when_segment_contains_colon_or_whitespace()
    {
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>(1).WithSegment("a b").Build());
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>(1).WithSegment("a:b").Build());
    }

    [Fact]
    public void Build_throws_when_total_length_exceeds_256()
    {
        var huge = new string('x', 260);
        Assert.Throws<ArgumentException>(() => CacheKey.For<Order>(huge).Build());
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheKeyBuilderTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement CacheKey + CacheKeyBuilder**

```csharp
// src/Caching.NET/Keys/CacheKey.cs
namespace Caching.NET.Keys;

/// <summary>
/// Canonical key-builder factory. Produces keys of the form
/// <c>{TypeName}:{id}[:{variant}][:{segment}]…</c>. Does NOT prepend the
/// configured <c>KeyPrefix</c> — the routing layer adds that.
/// </summary>
public static class CacheKey
{
    /// <summary>Begin a key for type <typeparamref name="T"/> with the given id.</summary>
    public static CacheKeyBuilder For<T>(object id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new CacheKeyBuilder(typeof(T).Name, id.ToString() ?? string.Empty);
    }
}
```

```csharp
// src/Caching.NET/Keys/CacheKeyBuilder.cs
namespace Caching.NET.Keys;

/// <summary>
/// Builds canonical, validated cache keys. Each segment is checked for whitespace
/// and the reserved <c>:</c> separator. <c>Build()</c> caps the post-prefix length
/// at 256 characters; the routing layer's <c>KeyPrefix</c> adds further headroom
/// up to <see cref="Options.CacheOptions.MaximumKeyLength"/>.
/// </summary>
public sealed class CacheKeyBuilder
{
    private readonly string _typeName;
    private readonly string _id;
    private readonly List<string> _segments = new(2);

    internal CacheKeyBuilder(string typeName, string id)
    {
        _typeName = typeName;
        _id = id;
    }

    /// <summary>Append a variant segment (e.g. version, view-shape).</summary>
    public CacheKeyBuilder WithVariant(string variant) => WithSegment(variant);

    /// <summary>Append an arbitrary segment.</summary>
    public CacheKeyBuilder WithSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        _segments.Add(segment);
        return this;
    }

    /// <summary>
    /// Build the final key. Throws <see cref="ArgumentException"/> when any segment
    /// contains whitespace or <c>:</c>, or when the total length exceeds 256.
    /// </summary>
    public string Build()
    {
        ValidateSegment(_typeName);
        ValidateSegment(_id);
        foreach (var s in _segments) ValidateSegment(s);

        var totalLen = _typeName.Length + 1 + _id.Length;
        for (int i = 0; i < _segments.Count; i++) totalLen += 1 + _segments[i].Length;
        if (totalLen > 256) throw new ArgumentException($"Cache key length ({totalLen}) exceeds 256 characters.");

        var sb = new System.Text.StringBuilder(totalLen);
        sb.Append(_typeName).Append(':').Append(_id);
        foreach (var s in _segments) sb.Append(':').Append(s);
        return sb.ToString();
    }

    private static void ValidateSegment(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ':' || char.IsWhiteSpace(c))
                throw new ArgumentException($"Cache key segment '{s}' contains a forbidden character ('{c}'). Use only ASCII letters/digits/dot/underscore/dash.");
        }
    }
}
```

- [ ] **Step 4: Update PublicAPI.Unshipped.txt**

Append:

```
Caching.NET.Keys.CacheKey
Caching.NET.Keys.CacheKeyBuilder
Caching.NET.Keys.CacheKeyBuilder.Build() -> string!
Caching.NET.Keys.CacheKeyBuilder.WithSegment(string! segment) -> Caching.NET.Keys.CacheKeyBuilder!
Caching.NET.Keys.CacheKeyBuilder.WithVariant(string! variant) -> Caching.NET.Keys.CacheKeyBuilder!
static Caching.NET.Keys.CacheKey.For<T>(object! id) -> Caching.NET.Keys.CacheKeyBuilder!
```

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheKeyBuilderTests`
Expected: PASS — 7 tests.

```bash
git add src/Caching.NET/Keys src/Caching.NET/PublicAPI.Unshipped.txt tests/Caching.NET.Tests/Keys
git commit -m "feat(p2): add CacheKey + CacheKeyBuilder helper"
```

---

## Task 6: MessagePackCacheSerializer

Pluggable serializer; ships in main package; opt-in via `WithSerializer`.

**Files:**
- Create: `src/Caching.NET/Serialization/MessagePackCacheSerializer.cs`
- Create: `tests/Caching.NET.Tests/Serialization/MessagePackCacheSerializerTests.cs`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Caching.NET.Tests/Serialization/MessagePackCacheSerializerTests.cs
using Caching.NET.Serialization;
using MessagePack;
using Xunit;

namespace Caching.NET.Tests.Serialization;

public class MessagePackCacheSerializerTests
{
    [MessagePackObject]
    public sealed class Dto
    {
        [Key(0)] public int Id { get; set; }
        [Key(1)] public string? Name { get; set; }
    }

    [Fact]
    public void FormatId_is_msgpack()
    {
        var s = new MessagePackCacheSerializer();
        Assert.Equal("msgpack", s.FormatId);
    }

    [Fact]
    public void Round_trip_preserves_value()
    {
        var s = new MessagePackCacheSerializer();
        var input = new Dto { Id = 42, Name = "hello" };

        var bytes = s.Serialize(input);
        var output = s.Deserialize<Dto>(bytes);

        Assert.NotNull(output);
        Assert.Equal(42, output!.Id);
        Assert.Equal("hello", output.Name);
    }

    [Fact]
    public void Deserialize_of_garbage_bytes_returns_null_or_throws_MessagePackException()
    {
        var s = new MessagePackCacheSerializer();
        try
        {
            var v = s.Deserialize<Dto>(new byte[] { 0xFF, 0xFE, 0xFD });
            // Some inputs decode to null; that's acceptable.
            Assert.Null(v);
        }
        catch (MessagePackSerializationException) { /* also acceptable */ }
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~MessagePackCacheSerializerTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement MessagePackCacheSerializer**

```csharp
// src/Caching.NET/Serialization/MessagePackCacheSerializer.cs
using MessagePack;
using MessagePack.Resolvers;

namespace Caching.NET.Serialization;

/// <summary>
/// MessagePack-backed <see cref="ICacheSerializer"/> implementation. Uses the
/// contractless resolver by default so consumer DTOs do not need
/// <c>[MessagePackObject]</c> attributes; pass a custom <see cref="MessagePackSerializerOptions"/>
/// via the constructor when an explicit resolver chain is needed (e.g. for AOT scenarios).
/// </summary>
public sealed class MessagePackCacheSerializer : ICacheSerializer
{
    private readonly MessagePackSerializerOptions _options;

    /// <summary>Construct with the contractless resolver (no attribute requirement on DTOs).</summary>
    public MessagePackCacheSerializer()
        : this(MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance)) { }

    /// <summary>Construct with custom MessagePack options.</summary>
    public MessagePackCacheSerializer(MessagePackSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string FormatId => "msgpack";

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, _options);

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return default;
        var seq = new ReadOnlySequence<byte>(bytes.ToArray());
        return MessagePackSerializer.Deserialize<T>(seq, _options);
    }
}
```

(Adds an `using System.Buffers;` at the top.)

- [ ] **Step 4: Update PublicAPI.Unshipped.txt**

Append:

```
Caching.NET.Serialization.MessagePackCacheSerializer
Caching.NET.Serialization.MessagePackCacheSerializer.Deserialize<T>(System.ReadOnlySpan<byte> bytes) -> T?
Caching.NET.Serialization.MessagePackCacheSerializer.FormatId.get -> string!
Caching.NET.Serialization.MessagePackCacheSerializer.MessagePackCacheSerializer() -> void
Caching.NET.Serialization.MessagePackCacheSerializer.MessagePackCacheSerializer(MessagePack.MessagePackSerializerOptions! options) -> void
Caching.NET.Serialization.MessagePackCacheSerializer.Serialize<T>(T value) -> byte[]!
```

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~MessagePackCacheSerializerTests`
Expected: PASS — 3 tests.

```bash
git add src/Caching.NET/Serialization/MessagePackCacheSerializer.cs src/Caching.NET/PublicAPI.Unshipped.txt tests/Caching.NET.Tests/Serialization/MessagePackCacheSerializerTests.cs
git commit -m "feat(p2): add MessagePackCacheSerializer (opt-in via WithSerializer)"
```

---

## Task 7: Stale-while-revalidate orchestrator

In-process registry tracks `(absExpiresAt, staleUntil)` per prefixed key. Routing layer:
1. On Set with `AllowStaleFor`: register metadata; underlying TTL = `expiration + AllowStaleFor`.
2. On Get hit AND now > absExpiresAt AND now <= staleUntil: return stale value, schedule background refresh under throttle.
3. Background refresh acquires stripe lock + calls `RefreshAsync` + updates registry.

InMemory + Redis only. Hybrid is a no-op.

**Files:**
- Create: `src/Caching.NET/Internal/StaleEntryTracker.cs`
- Create: `src/Caching.NET/Internal/StaleRefreshThrottle.cs`
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs` (orchestrator)
- Modify: `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs` (register both as singletons)
- Test: `tests/Caching.NET.Tests/Services/StaleWhileRevalidateTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Caching.NET.Tests/Services/StaleWhileRevalidateTests.cs
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Caching.NET.Tests.Services;

public class StaleWhileRevalidateTests
{
    private static IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddCaching(b => b.UseInMemory().WithKeyPrefix("swr-test")))
            .Build();
    }

    [Fact]
    public async Task GetOrCreateAsync_returns_stale_value_within_window_and_schedules_refresh()
    {
        using var host = BuildHost();
        var svc = host.Services.GetRequiredService<ICacheService>();
        var calls = 0;

        // Step 1: prime cache with absolute expiry of 100 ms, stale-for 1 s
        await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v1"); },
            callOptions: new CacheCallOptions
            {
                AbsoluteExpiration = TimeSpan.FromMilliseconds(100),
                AllowStaleFor = TimeSpan.FromSeconds(1),
            });
        Assert.Equal(1, calls);

        // Step 2: wait past abs expiry but inside stale window
        await Task.Delay(150);

        // Step 3: read should serve stale value AND schedule a background refresh
        var result = await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v2"); },
            callOptions: new CacheCallOptions
            {
                AbsoluteExpiration = TimeSpan.FromMilliseconds(100),
                AllowStaleFor = TimeSpan.FromSeconds(1),
            });

        Assert.Equal("v1", result); // stale value returned
        // refresh task scheduled; allow it to run
        await Task.Delay(150);
        Assert.True(calls >= 2, $"Expected background refresh to run; calls={calls}");
    }

    [Fact]
    public async Task GetOrCreateAsync_outside_stale_window_runs_factory_directly()
    {
        using var host = BuildHost();
        var svc = host.Services.GetRequiredService<ICacheService>();
        var calls = 0;

        await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v1"); },
            callOptions: new CacheCallOptions
            {
                AbsoluteExpiration = TimeSpan.FromMilliseconds(50),
                AllowStaleFor = TimeSpan.FromMilliseconds(50),
            });

        await Task.Delay(200); // past abs + stale window

        var result = await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v2"); });

        Assert.Equal("v2", result);
        Assert.Equal(2, calls);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~StaleWhileRevalidateTests`
Expected: FAIL — `AllowStaleFor` is currently ignored.

- [ ] **Step 3: Implement StaleEntryTracker**

```csharp
// src/Caching.NET/Internal/StaleEntryTracker.cs
using System.Collections.Concurrent;

namespace Caching.NET.Internal;

internal readonly record struct StaleMetadata(long AbsExpiresAtUtcTicks, long StaleUntilUtcTicks);

/// <summary>
/// In-process registry of stale-while-revalidate metadata. NOT distributed:
/// each app instance maintains its own view, which is acceptable for v2.0
/// because background refresh is per-instance work. A distributed registry
/// is deferred beyond v2.0.0.
/// </summary>
internal sealed class StaleEntryTracker
{
    private readonly ConcurrentDictionary<string, StaleMetadata> _entries = new();

    public void Register(string prefixedKey, TimeSpan absoluteExpiration, TimeSpan staleFor)
    {
        var now = DateTime.UtcNow.Ticks;
        var meta = new StaleMetadata(
            AbsExpiresAtUtcTicks: now + absoluteExpiration.Ticks,
            StaleUntilUtcTicks:  now + absoluteExpiration.Ticks + staleFor.Ticks);
        _entries[prefixedKey] = meta;
    }

    public bool TryGet(string prefixedKey, out StaleMetadata meta) =>
        _entries.TryGetValue(prefixedKey, out meta);

    public void Forget(string prefixedKey) => _entries.TryRemove(prefixedKey, out _);
}
```

- [ ] **Step 4: Implement StaleRefreshThrottle**

```csharp
// src/Caching.NET/Internal/StaleRefreshThrottle.cs
namespace Caching.NET.Internal;

/// <summary>
/// Bounds the number of concurrent background stale-while-revalidate refreshes.
/// </summary>
internal sealed class StaleRefreshThrottle : IDisposable
{
    private readonly SemaphoreSlim _gate;

    public StaleRefreshThrottle(int maxConcurrent)
    {
        if (maxConcurrent < 1) maxConcurrent = 1;
        _gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public bool TryAcquire() => _gate.Wait(0);
    public void Release() => _gate.Release();

    public void Dispose() => _gate.Dispose();
}
```

- [ ] **Step 5: Register in DI**

In `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs` inside `AddCachingCore`, register:

```csharp
        services.AddSingleton<StaleEntryTracker>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            return new StaleRefreshThrottle(opts.StaleRefreshConcurrency);
        });
```

- [ ] **Step 6: Wire orchestrator into RoutingCacheService**

Inject `StaleEntryTracker` and `StaleRefreshThrottle` into the constructor (add as parameters and assign to private fields). In `GetOrCreateAsync(callOptions)`, before delegating to the inner service, check `callOptions?.AllowStaleFor`:

```csharp
        var staleFor = callOptions?.AllowStaleFor;
        if (staleFor is { } sw && _resolvedMode != CacheMode.Hybrid && _staleTracker.TryGet(prefixed, out var meta))
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks > meta.AbsExpiresAtUtcTicks && nowTicks <= meta.StaleUntilUtcTicks)
            {
                // Inside stale window: return cached value, schedule refresh
                var stale = await ResolveService(callOptions?.Mode)
                    .GetAsync<T>(prefixed, cancellationToken);
                if (stale is not null)
                {
                    CacheInstruments.RecordStaleServed(Mode, "get_or_create");
                    ScheduleBackgroundRefresh(prefixed, factory, callOptions, expiration, localExpiration);
                    return stale;
                }
                _staleTracker.Forget(prefixed);
            }
            else if (nowTicks > meta.StaleUntilUtcTicks)
            {
                _staleTracker.Forget(prefixed);
            }
        }
```

Where `ScheduleBackgroundRefresh` is:

```csharp
    private void ScheduleBackgroundRefresh<T>(
        string prefixedKey,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration,
        TimeSpan? localExpiration) where T : notnull
    {
        if (!_throttle.TryAcquire()) return; // skip if cap reached
        CacheInstruments.AddStaleRefreshInFlight(Mode, +1);
        _ = Task.Run(async () =>
        {
            var stripe = _lockManager.GetLock(prefixedKey);
            await stripe.WaitAsync();
            try
            {
                var value = await factory(CancellationToken.None);
                var inner = ResolveService(callOptions?.Mode);
                var abs = callOptions?.AbsoluteExpiration ?? expiration ?? _optionsMonitor.CurrentValue.DefaultExpiration;
                var staleFor = callOptions?.AllowStaleFor ?? TimeSpan.Zero;
                var ttl = abs + staleFor;
                await inner.SetAsync(prefixedKey, value, ttl, localExpiration, CancellationToken.None);
                _staleTracker.Register(prefixedKey, abs, staleFor);
            }
            catch
            {
                // refresh failed; leave stale entry in place to expire naturally
            }
            finally
            {
                stripe.Release();
                _throttle.Release();
                CacheInstruments.AddStaleRefreshInFlight(Mode, -1);
            }
        });
    }
```

For SwR-aware writes, modify the `SetAsync(callOptions)` and the inside-lock GetOrCreate write paths to register metadata when `AllowStaleFor` is set:

```csharp
        if (callOptions?.AllowStaleFor is { } swrSet)
        {
            var abs = callOptions.AbsoluteExpiration ?? expiration ?? _optionsMonitor.CurrentValue.DefaultExpiration;
            var ttl = abs + swrSet;
            await service.SetAsync(prefixed, value, ttl, localExpiration, cancellationToken);
            _staleTracker.Register(prefixed, abs, swrSet);
            CacheInstruments.RecordSet(Mode);
            return;
        }
```

The variable `_resolvedMode` referenced above is the startup `_startupOptions.Mode`; use that or pass `callOptions?.Mode ?? _startupOptions.Mode` consistently.

- [ ] **Step 7: Run tests + commit**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~StaleWhileRevalidateTests`
Expected: PASS — 2 tests.

Run: `dotnet test`
Expected: PASS overall.

```bash
git add -A
git commit -m "feat(p2): orchestrate stale-while-revalidate in routing layer (InMemory + Redis)"
```

---

## Task 8: CachingBuilder additions + RequireTagSupport startup gate

**Files:**
- Modify: `src/Caching.NET/CachingBuilder.cs`
- Modify: `src/Caching.NET/Validation/CacheOptionsValidator.cs`
- Modify: `src/Caching.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`
- Test: `tests/Caching.NET.Tests/Builder/CachingBuilderP2Tests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Caching.NET.Tests/Builder/CachingBuilderP2Tests.cs
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Builder;

public class CachingBuilderP2Tests
{
    [Fact]
    public void WithTtlJitter_writes_value_to_options()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("p2").WithTtlJitter(0.20));
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(0.20, opts.TtlJitterPercentage);
    }

    [Fact]
    public void WithStaleRefreshConcurrency_writes_value_to_options()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("p2").WithStaleRefreshConcurrency(64));
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(64, opts.StaleRefreshConcurrency);
    }

    [Fact]
    public void RequireTagSupport_with_Hybrid_succeeds()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddCaching(b => b
                .UseHybrid()
                .WithKeyPrefix("p2")
                .RequireTagSupport()))
            .Build();
        var validator = host.Services.GetRequiredService<IOptions<CacheOptions>>();
        _ = validator.Value;
    }

    [Fact]
    public void RequireTagSupport_with_InMemory_throws_at_resolution()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddCaching(b => b
                .UseInMemory()
                .WithKeyPrefix("p2")
                .RequireTagSupport()))
            .Build();
        var ex = Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<IOptions<CacheOptions>>().Value);
        Assert.Contains("RequireTagSupport", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CachingBuilderP2Tests`
Expected: FAIL — methods don't exist; `RequireTagSupport` validator missing.

- [ ] **Step 3: Add CachingBuilder methods**

Append to `src/Caching.NET/CachingBuilder.cs`:

```csharp
    /// <summary>Apply ±<paramref name="percentage"/> jitter to all entry TTLs (clamped to 0–0.5).</summary>
    public CachingBuilder WithTtlJitter(double percentage)
    {
        _services?.PostConfigure<CacheOptions>(o => o.TtlJitterPercentage = Math.Clamp(percentage, 0.0, 0.5));
        return this;
    }

    /// <summary>Cap concurrent in-flight stale-while-revalidate background refreshes.</summary>
    public CachingBuilder WithStaleRefreshConcurrency(int maxConcurrent)
    {
        _services?.PostConfigure<CacheOptions>(o => o.StaleRefreshConcurrency = maxConcurrent);
        return this;
    }

    /// <summary>
    /// Mark the application as requiring tag support. Startup validation fails
    /// when <see cref="CacheOptions.Mode"/> is not <see cref="CacheMode.Hybrid"/>.
    /// </summary>
    public CachingBuilder RequireTagSupport()
    {
        _services?.PostConfigure<CacheOptions>(o => o.RequireTagSupport = true);
        return this;
    }

    /// <summary>Use the bundled MessagePack serializer.</summary>
    public CachingBuilder WithMessagePackSerializer()
    {
        _services?.AddSingleton<Serialization.ICacheSerializer, Serialization.MessagePackCacheSerializer>();
        return this;
    }
```

- [ ] **Step 4: Add the gate flag on CacheOptions**

In `src/Caching.NET/Options/CacheOptions.cs`, add inside the class:

```csharp
    /// <summary>
    /// Set by <c>CachingBuilder.RequireTagSupport()</c>; when true the validator
    /// rejects startup if <see cref="Mode"/> is not <see cref="CacheMode.Hybrid"/>.
    /// </summary>
    public bool RequireTagSupport { get; set; }
```

- [ ] **Step 5: Update CacheOptionsValidator**

Inside `Validation/CacheOptionsValidator.cs`, in the `Validate` method, after existing checks add:

```csharp
        if (options.RequireTagSupport && options.Mode != CacheMode.Hybrid)
            failures.Add($"RequireTagSupport() was called but Mode={options.Mode}; tag support is only available in Hybrid mode.");
```

(Use whatever pattern the existing validator uses for collecting failures — match existing style.)

- [ ] **Step 6: Update PublicAPI.Unshipped.txt**

Append:

```
Caching.NET.CachingBuilder.RequireTagSupport() -> Caching.NET.CachingBuilder!
Caching.NET.CachingBuilder.WithMessagePackSerializer() -> Caching.NET.CachingBuilder!
Caching.NET.CachingBuilder.WithStaleRefreshConcurrency(int maxConcurrent) -> Caching.NET.CachingBuilder!
Caching.NET.CachingBuilder.WithTtlJitter(double percentage) -> Caching.NET.CachingBuilder!
Caching.NET.Options.CacheOptions.RequireTagSupport.get -> bool
Caching.NET.Options.CacheOptions.RequireTagSupport.set -> void
```

- [ ] **Step 7: Run tests + commit**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CachingBuilderP2Tests`
Expected: PASS — 4 tests.

Run: `dotnet test`
Expected: PASS overall.

```bash
git add -A
git commit -m "feat(p2): add WithTtlJitter / WithStaleRefreshConcurrency / RequireTagSupport / WithMessagePackSerializer"
```

---

## Task 9: PublicAPI consolidation, sample updates, final verification

**Files:**
- Modify: `samples/Caching.NET.Sample/Program.cs`, `samples/Caching.NET.Sample/Controllers/ProductCatalogController.cs`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt` (consolidate / sort)

- [ ] **Step 1: Update sample Program.cs to demo new APIs**

In `samples/Caching.NET.Sample/Program.cs`, locate the `AddCaching(...)` call. Add (chained):

```csharp
    .WithTtlJitter(0.10)
    .WithStaleRefreshConcurrency(128);
```

In a controller (e.g. `ProductCatalogController`), add a small endpoint that uses `CacheKey.For<Product>(id)` and `CacheCallOptions { AllowStaleFor = TimeSpan.FromSeconds(30) }`:

```csharp
[HttpGet("/products/{id:int}/with-swr")]
public async Task<IActionResult> GetWithSwr(int id, [FromServices] ICacheService cache, CancellationToken ct)
{
    var key = CacheKey.For<Product>(id).Build();
    var product = await cache.GetOrCreateAsync(
        key,
        async _ => await LoadProductFromDatabase(id),
        callOptions: new CacheCallOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(2),
            AllowStaleFor      = TimeSpan.FromSeconds(30),
            JitterPercentage   = 0.05,
        },
        cancellationToken: ct);
    return Ok(product);
}
```

(Adjust namespaces and method signatures to match the existing controller; the snippet shows shape, not literal code.)

- [ ] **Step 2: Run sample build**

Run: `dotnet build samples/Caching.NET.Sample`
Expected: clean build, 0 warnings.

- [ ] **Step 3: Sort PublicAPI.Unshipped.txt**

Run: `sort -o src/Caching.NET/PublicAPI.Unshipped.txt src/Caching.NET/PublicAPI.Unshipped.txt`
Then re-run `dotnet build` and confirm the analyzer is happy with the sort.

- [ ] **Step 4: Run full multi-target test suite**

Run: `dotnet test`
Expected: all tests PASS across net8/9/10. Report counts.

- [ ] **Step 5: Smoke-pack v2.0.0-alpha.4**

Run: `dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs /p:Version=2.0.0-alpha.4`
Expected: Single nupkg produced; analyzer dll embedded; no warnings.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "docs(p2): wire new APIs in sample app; sort PublicAPI surface"
```

---

## Self-Review Checklist (filled in)

**1. Spec coverage:**

| Spec §4–§5 / §12 P2 requirement | Implementing task |
|---|---|
| `GetMany` / `SetMany` / `RemoveMany` | Task 3 |
| `GetAsync` / `RefreshAsync` / `ExistsAsync` | Task 2 |
| Sliding expiration | Task 4 (Step 5) |
| Tag overloads | Task 8 (`RequireTagSupport`); Hybrid native path already in P0 |
| `MessagePackCacheSerializer` | Task 6 |
| `CacheKeyBuilder` | Task 5 |
| Stale-while-revalidate | Task 7 |
| TTL jitter | Task 4 |
| `CacheCallOptions` v2 properties | Task 1 |
| `WithTtlJitter` / `WithStaleRefreshConcurrency` / `RequireTagSupport` builder methods | Task 8 |

**Documented deviations from literal spec:**
- Server-side Redis MGET/MSET pipelining → deferred to P3 (batch ops are client-side fan-out).
- Stale-while-revalidate registry is in-process (not distributed) → distributed registry deferred beyond v2.0.0.
- Hybrid mode does not honour `SlidingExpiration` or `AllowStaleFor` (`HybridCacheEntryOptions` doesn't expose either); documented on the option properties.
- `localExpiration` parameter retained on `ICacheService` (spec §4 dropped it); kept for backwards compat with existing concrete implementations introduced in P0.
- `RemoveAsync(IEnumerable<string>)` removed in favour of `RemoveManyAsync` (rename, not addition).

**2. Placeholder scan:** Every code step contains the actual code or exact command. No "TBD"/"TODO"/"add error handling".

**3. Type consistency:**

- `CacheCallOptions` properties added in Task 1 — used by Tasks 4 (`JitterPercentage`, `AbsoluteExpiration`, `SlidingExpiration`), 7 (`AllowStaleFor`), 8 (none — builder writes `CacheOptions`).
- `ICacheService.GetAsync<T>` signature `Task<T?> GetAsync<T>(string, CancellationToken)` — used identically in Tasks 2, 3, 7.
- `ICacheService.RefreshAsync<T>` signature `Task RefreshAsync<T>(string, Func<CancellationToken, Task<T>>, TimeSpan?, TimeSpan?, CancellationToken)` — used identically in Tasks 2, 7.
- `RemoveManyAsync(IEnumerable<string>, CancellationToken)` — Task 3 declaration matches PublicAPI line.
- `CacheKey.For<T>(object id) → CacheKeyBuilder` and `CacheKeyBuilder.Build() → string` — Task 5 declarations consistent with sample usage in Task 9.
- `MessagePackCacheSerializer.FormatId == "msgpack"` — matches `RedisCacheService.ResolveFormatId` (P1 already mapped this).
- `StaleEntryTracker.Register(string, TimeSpan, TimeSpan)` and `StaleEntryTracker.TryGet(string, out StaleMetadata)` — Task 7 declarations consistent across orchestrator code.
- `StaleRefreshThrottle.TryAcquire() → bool` and `Release()` — Task 7 declaration consistent.
- `CacheOptions.RequireTagSupport` flag — Task 8 declaration matches validator usage and PublicAPI entry.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-05-06-v2-p2-api-expansion.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — fresh subagent per task, two-stage review between tasks.

**2. Inline Execution** — execute in this session with checkpoints.

**Which approach?**
