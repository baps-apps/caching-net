# Per-Call Cache Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every call into Caching.NET report its mode, operation, whether the value came from cache or from the source, the total duration, and the source (factory) duration — through OpenTelemetry metrics and one span per call.

**Architecture:** A new internal `CacheCallRecorder` owns the per-call record: it starts an `Activity`, times the call, wraps the caller's factory delegate to time source retrieval, and on dispose emits `cache.operation.duration` (with a `cache.served_from` tag) plus `cache.factory.duration`. `RoutingCacheService` — the single entry point every consumer call passes through — creates one recorder per call. Recording is removed from the three backend services, which eliminates the nesting/double-counting that per-service recording produces. No ambient state is needed: Routing creates the factory wrapper, so the wrapper closes over the recorder directly.

**Tech Stack:** .NET 8/9/10 multi-target, `System.Diagnostics.Metrics` (`Meter`, `Histogram<double>`), `System.Diagnostics.ActivitySource`, xUnit + Moq, Testcontainers for integration tests.

**Spec:** [docs/superpowers/specs/2026-07-28-cache-call-visibility-design.md](../specs/2026-07-28-cache-call-visibility-design.md)

## Global Constraints

- Target frameworks are `net8.0;net9.0;net10.0`. Code must compile on all three. `Stopwatch.GetElapsedTime` and `Stopwatch.GetTimestamp` are available on all three.
- `TreatWarningsAsErrors` is enabled globally via `Directory.Build.props`. Zero warnings, or the build fails.
- Central package management via `Directory.Packages.props`. This plan adds **no** new package references.
- Do **not** add members to `ICacheService`. All additions are instruments, tags, and internal types (API stability contract in `CLAUDE.md`).
- Keep the existing public `CacheInstruments.RecordDuration(string mode, string operation, double milliseconds)` 3-arg method for source/binary compatibility.
- Exact instrument name: `cache.factory.duration`, unit `ms`.
- Exact tag names: `cache.mode`, `cache.operation`, `cache.served_from`, `cache.factory_ms`, `cache.miss_reason`, `cache.hit_count`, `cache.miss_count`, `cache.coalesced`, `cache.error_kind`, `cache.key_hash`.
- Exact `cache.served_from` values: `cache`, `source`, `mixed`, `none`. Omit the tag entirely on write-shaped operations.
- Span name is `cache {operation}` (e.g. `cache get_or_create`), `ActivityKind.Internal`.
- Raw cache keys must never appear on a span. Only `StableStringHash.Compute64(key).ToString("x16")`, and only when `CacheOptions.IncludeKeyHashInTraces` is true.
- Every existing instrument and its tags stay unchanged: `cache.hits`, `cache.misses`, `cache.errors`, `cache.sets`, `cache.removes`, `cache.evictions`, `cache.stale_served`, `cache.circuit_state_changes`, `cache.schema_drift`, `cache.payload.bytes`, `cache.stale_refresh.in_flight`, `cache.tls.validation`, `cache.serialize.duration`, `cache.deserialize.duration`.
- Test commands: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0` for fast iteration; the full 3-TFM run before the final commit. Integration and Chaos projects need Docker.
- Existing test helper `tests/Caching.NET.Tests/Telemetry/MeterListenerHelpers.cs` provides `Capture<T>(instrumentName, modeTag)` and `ForCounterWithTags(instrumentName, out observed)`. Reuse it; do not duplicate it.

## Test Isolation Rules (read before writing any test)

The `Meter` and `ActivitySource` are process-wide statics, and xUnit runs test classes in parallel, so other tests emit the same instruments while your listener is attached. Two rules make assertions deterministic:

1. **Strict counts ("exactly one sample") go in `CacheCallRecorder` unit tests**, which pass a unique mode string such as `$"unit-{Guid.NewGuid():N}"`. Nothing else in the suite emits that mode, so counts are exact. This mirrors the existing pattern in `tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs`.
2. **Routing-level tests** use real mode tags (`InMemory`, `Redis`, `Hybrid`), so metric assertions must be bleed-tolerant (`Assert.Contains` / `Assert.NotEmpty`, never `Assert.Single`). When a test needs an exact per-call assertion at the Routing level, set `IncludeKeyHashInTraces = true`, use a GUID key, and filter captured activities by `cache.key_hash` — that pins the assertion to your call only.

---

## File Structure

**Create:**

- `src/Caching.NET/Telemetry/CacheCallRecorder.cs` — the per-call record. Owns span lifetime, total/factory timing, `served_from` resolution, and metric emission. Single responsibility: turning one cache call into one telemetry record. Keeps this logic out of `RoutingCacheService`, which is already ~730 lines.
- `tests/Caching.NET.Tests/Telemetry/ActivityListenerHelpers.cs` — test helper for capturing cache spans.
- `tests/Caching.NET.Tests/Telemetry/CacheCallRecorderTests.cs` — strict unit tests for the recorder.
- `tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs` — Routing wiring tests (metrics + spans).
- `tests/Caching.NET.Tests.Integration/CallVisibilityRedisTests.cs` — end-to-end against real Redis.

**Modify:**

- `src/Caching.NET/Telemetry/CacheInstruments.cs` — add the `cache.factory.duration` histogram, `RecordFactoryDuration`, and the 4-arg `RecordDuration` overload; delete `MeasureDuration` and `OperationTimer`.
- `src/Caching.NET/Services/RoutingCacheService.cs` — create a recorder per entry point; wrap the factory; mark outcomes.
- `src/Caching.NET/Services/InMemoryCacheService.cs`, `RedisCacheService.cs`, `HybridCacheService.cs` — remove all duration instrumentation.
- `tests/Caching.NET.Tests/Telemetry/OperationDurationTests.cs` — rewritten to assert the services no longer record.
- `docs/TELEMETRY.md`, `docs/features/telemetry.md`, `CLAUDE.md`, `src/Caching.NET/Caching.NET.csproj` (version).

---

## Task 1: Instrument and API additions

**Files:**
- Modify: `src/Caching.NET/Telemetry/CacheInstruments.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public static void CacheInstruments.RecordDuration(string mode, string operation, double milliseconds, string? servedFrom)`
  - `public static void CacheInstruments.RecordFactoryDuration(string mode, string operation, double milliseconds)`
  - `internal static readonly Histogram<double> CacheInstruments.FactoryDuration`

- [ ] **Step 1: Write the failing tests**

Append to `tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs`:

```csharp
    [Fact]
    public void RecordFactoryDuration_emits_histogram_with_mode_and_operation()
    {
        var modeTag = $"unit-factory-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", modeTag);
        using var _ = listener;

        CacheInstruments.RecordFactoryDuration(modeTag, "get_or_create", 187.5);

        Assert.Single(values);
        Assert.Equal(187.5, values[0].value);
        Assert.Contains(values[0].tags, t => t.Key == "cache.operation" && (string?)t.Value == "get_or_create");
    }

    [Fact]
    public void RecordDuration_with_servedFrom_adds_served_from_tag()
    {
        var modeTag = $"unit-served-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", modeTag);
        using var _ = listener;

        CacheInstruments.RecordDuration(modeTag, "get_or_create", 12.5, "source");

        Assert.Single(values);
        Assert.Contains(values[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "source");
    }

    [Fact]
    public void RecordDuration_with_null_servedFrom_omits_the_tag()
    {
        var modeTag = $"unit-noserved-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", modeTag);
        using var _ = listener;

        CacheInstruments.RecordDuration(modeTag, "set", 3.0, servedFrom: null);

        Assert.Single(values);
        Assert.DoesNotContain(values[0].tags, t => t.Key == "cache.served_from");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~CacheInstrumentsTests"`

Expected: FAIL to compile — `RecordFactoryDuration` and the 4-arg `RecordDuration` do not exist. A compile failure is the correct RED here; there is no way to reference a method that isn't defined.

- [ ] **Step 3: Add the instrument and methods**

In `src/Caching.NET/Telemetry/CacheInstruments.cs`, add the histogram next to the existing `OperationDuration` declaration:

```csharp
    internal static readonly Histogram<double> FactoryDuration =
        Meter.CreateHistogram<double>("cache.factory.duration", unit: "ms", description: "Factory (source) retrieval duration.");
```

Add these methods next to the existing `RecordDuration`:

```csharp
    /// <summary>
    /// Record a cache operation duration in milliseconds together with where the value came from.
    /// Pass <c>null</c> for <paramref name="servedFrom"/> on write-shaped operations so no
    /// meaningless tag value is emitted.
    /// </summary>
    public static void RecordDuration(string mode, string operation, double milliseconds, string? servedFrom)
    {
        if (servedFrom is null)
        {
            RecordDuration(mode, operation, milliseconds);
            return;
        }

        OperationDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation),
            new KeyValuePair<string, object?>("cache.served_from", servedFrom));
    }

    /// <summary>Record how long the caller's factory took to retrieve the value from the source.</summary>
    public static void RecordFactoryDuration(string mode, string operation, double milliseconds)
        => FactoryDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~CacheInstrumentsTests"`

Expected: PASS, all tests in the class, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Telemetry/CacheInstruments.cs tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs
git commit -m "feat(telemetry): add cache.factory.duration and served_from-tagged duration"
```

---

## Task 2: `CacheCallRecorder`

**Files:**
- Create: `src/Caching.NET/Telemetry/CacheCallRecorder.cs`
- Create: `tests/Caching.NET.Tests/Telemetry/ActivityListenerHelpers.cs`
- Create: `tests/Caching.NET.Tests/Telemetry/CacheCallRecorderTests.cs`

**Interfaces:**
- Consumes: `CacheInstruments.RecordDuration(mode, operation, ms, servedFrom)`, `CacheInstruments.RecordFactoryDuration(mode, operation, ms)`, `CacheInstruments.Activity`, `Caching.NET.Internal.StableStringHash.Compute64(string)`, `Caching.NET.Options.CacheOptions.IncludeKeyHashInTraces`.
- Produces (used by every later task):
  - `internal sealed class CacheCallRecorder : IDisposable`
  - `static CacheCallRecorder Start(string mode, string operation, CacheOptions options, string? rawKey = null)`
  - `Func<CancellationToken, Task<T>> WrapFactory<T>(Func<CancellationToken, Task<T>> factory)`
  - `void SetMode(string resolvedMode)`
  - `void MarkServedFromCache()`
  - `void MarkNotFound()`
  - `void MarkBatch(int hits, int misses)`
  - `void MarkCoalesced()`
  - `void MarkMissReason(string reason)`
  - `void MarkError(string errorKind, bool thrownToCaller)`
  - `void Dispose()`

- [ ] **Step 1: Write the span-capture test helper**

Create `tests/Caching.NET.Tests/Telemetry/ActivityListenerHelpers.cs`:

```csharp
using System.Diagnostics;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

internal static class ActivityListenerHelpers
{
    /// <summary>
    /// Captures every completed Caching.NET activity. The ActivitySource is process-wide, so filter
    /// the returned list (by tag, e.g. cache.key_hash) rather than asserting on its total count.
    /// </summary>
    public static (List<Activity> activities, ActivityListener listener) Capture()
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CacheInstruments.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (captured) captured.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return (captured, listener);
    }

    public static string? Tag(this Activity activity, string name) =>
        activity.GetTagItem(name)?.ToString();
}
```

- [ ] **Step 2: Write the failing recorder tests**

Create `tests/Caching.NET.Tests/Telemetry/CacheCallRecorderTests.cs`. Every test uses a unique mode string, so counts are exact and bleed-free:

```csharp
using System.Diagnostics;
using Caching.NET.Options;
using Caching.NET.Telemetry;

namespace Caching.NET.Tests.Telemetry;

public class CacheCallRecorderTests
{
    private static CacheOptions Options(bool keyHash = false) =>
        new() { KeyPrefix = "t", IncludeKeyHashInTraces = keyHash };

    [Fact]
    public void Dispose_records_exactly_one_total_sample()
    {
        var mode = $"unit-rec-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (CacheCallRecorder.Start(mode, "get", Options())) { }

        Assert.Single(values);
        Assert.True(values[0].value >= 0);
    }

    [Fact]
    public void Double_dispose_records_once()
    {
        var mode = $"unit-dd-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        var rec = CacheCallRecorder.Start(mode, "get", Options());
        rec.Dispose();
        rec.Dispose();

        Assert.Single(values);
    }

    [Fact]
    public void Read_that_found_a_value_is_served_from_cache()
    {
        var mode = $"unit-hit-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get", Options()))
            rec.MarkServedFromCache();

        Assert.Contains(values[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "cache");
    }

    [Fact]
    public void Read_that_found_nothing_is_served_from_none()
    {
        var mode = $"unit-none-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get", Options()))
            rec.MarkNotFound();

        Assert.Contains(values[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "none");
    }

    [Fact]
    public async Task Factory_that_ran_marks_source_and_records_factory_duration()
    {
        var mode = $"unit-src-{Guid.NewGuid():N}";
        var (totals, totalListener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        var (factories, factoryListener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = totalListener;
        using var __ = factoryListener;

        using (var rec = CacheCallRecorder.Start(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(async ct =>
            {
                await Task.Delay(25, ct);
                return "v";
            });
            Assert.Equal("v", await wrapped(CancellationToken.None));
        }

        Assert.Contains(totals[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "source");
        Assert.Single(factories);
        Assert.True(factories[0].value >= 20, $"expected >= 20ms, got {factories[0].value}");
        Assert.True(totals[0].value >= factories[0].value);
    }

    [Fact]
    public void No_factory_means_no_factory_duration_sample()
    {
        var mode = $"unit-nofac-{Guid.NewGuid():N}";
        var (factories, listener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get_or_create", Options()))
            rec.MarkServedFromCache();

        Assert.Empty(factories);
    }

    [Fact]
    public async Task Factory_invoked_twice_accumulates_into_one_sample()
    {
        var mode = $"unit-twice-{Guid.NewGuid():N}";
        var (factories, listener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(async ct => { await Task.Delay(20, ct); return "v"; });
            await wrapped(CancellationToken.None);
            await wrapped(CancellationToken.None);
        }

        Assert.Single(factories);
        Assert.True(factories[0].value >= 35, $"expected accumulated >= 35ms, got {factories[0].value}");
    }

    [Fact]
    public async Task Factory_that_throws_still_records_both_samples()
    {
        var mode = $"unit-throw-{Guid.NewGuid():N}";
        var (totals, totalListener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        var (factories, factoryListener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", mode);
        using var _ = totalListener;
        using var __ = factoryListener;

        using (var rec = CacheCallRecorder.Start(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(_ => throw new InvalidOperationException("boom"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrapped(CancellationToken.None));
        }

        Assert.Single(totals);
        Assert.Single(factories);
        Assert.Contains(totals[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == "source");
    }

    [Theory]
    [InlineData(3, 2, "mixed")]
    [InlineData(3, 0, "cache")]
    [InlineData(0, 2, "none")]
    [InlineData(0, 0, "none")]
    public void Batch_outcome_resolves_served_from(int hits, int misses, string expected)
    {
        var mode = $"unit-batch-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get_many", Options()))
            rec.MarkBatch(hits, misses);

        Assert.Contains(values[0].tags, t => t.Key == "cache.served_from" && (string?)t.Value == expected);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("set_many")]
    [InlineData("remove")]
    [InlineData("remove_many")]
    [InlineData("remove_by_tag")]
    [InlineData("clear")]
    public void Write_operations_omit_served_from(string operation)
    {
        var mode = $"unit-write-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", mode);
        using var _ = listener;

        using (CacheCallRecorder.Start(mode, operation, Options())) { }

        Assert.DoesNotContain(values[0].tags, t => t.Key == "cache.served_from");
    }

    [Fact]
    public void SetMode_overrides_the_mode_tag()
    {
        var resolved = $"unit-resolved-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", resolved);
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start("Routing", "get", Options()))
            rec.SetMode(resolved);

        Assert.Single(values);
    }

    [Fact]
    public void Span_carries_operation_mode_and_served_from()
    {
        var mode = $"unit-span-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get_or_create", Options()))
            rec.MarkServedFromCache();

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.Equal("cache get_or_create", span.DisplayName);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal("cache", span.Tag("cache.served_from"));
    }

    [Fact]
    public async Task Span_carries_factory_ms_when_the_factory_ran()
    {
        var mode = $"unit-spanfac-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get_or_create", Options()))
        {
            var wrapped = rec.WrapFactory<string>(async ct => { await Task.Delay(20, ct); return "v"; });
            await wrapped(CancellationToken.None);
        }

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.NotNull(span.Tag("cache.factory_ms"));
        Assert.True(double.Parse(span.Tag("cache.factory_ms")!) >= 15);
    }

    [Fact]
    public void Span_carries_batch_counts_and_coalesced_and_miss_reason()
    {
        var mode = $"unit-spantags-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get_many", Options()))
        {
            rec.MarkBatch(3, 2);
            rec.MarkCoalesced();
            rec.MarkMissReason("NotFound");
        }

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.Equal("3", span.Tag("cache.hit_count"));
        Assert.Equal("2", span.Tag("cache.miss_count"));
        Assert.Equal("True", span.Tag("cache.coalesced"));
        Assert.Equal("NotFound", span.Tag("cache.miss_reason"));
    }

    [Fact]
    public void Escaping_error_sets_span_status_error()
    {
        var mode = $"unit-err-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get", Options()))
            rec.MarkError("Timeout", thrownToCaller: true);

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Timeout", span.Tag("cache.error_kind"));
    }

    [Fact]
    public void Swallowed_error_keeps_span_ok_but_tags_error_kind()
    {
        var mode = $"unit-failopen-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get_or_create", Options()))
            rec.MarkError("Timeout", thrownToCaller: false);

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Timeout", span.Tag("cache.error_kind"));
    }

    [Fact]
    public void Cancellation_is_tagged_but_not_an_error()
    {
        var mode = $"unit-cancel-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (var rec = CacheCallRecorder.Start(mode, "get", Options()))
            rec.MarkError("Canceled", thrownToCaller: true);

        var span = activities.Single(a => a.Tag("cache.mode") == mode);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Canceled", span.Tag("cache.error_kind"));
    }

    [Fact]
    public void Key_hash_tag_only_when_opted_in()
    {
        var optedIn = $"unit-kh-on-{Guid.NewGuid():N}";
        var optedOut = $"unit-kh-off-{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        using (CacheCallRecorder.Start(optedIn, "get", Options(keyHash: true), "member:42")) { }
        using (CacheCallRecorder.Start(optedOut, "get", Options(keyHash: false), "member:42")) { }

        var withHash = activities.Single(a => a.Tag("cache.mode") == optedIn);
        var withoutHash = activities.Single(a => a.Tag("cache.mode") == optedOut);
        Assert.Equal(16, withHash.Tag("cache.key_hash")!.Length);
        Assert.DoesNotContain("member:42", withHash.Tag("cache.key_hash")!);
        Assert.Null(withoutHash.Tag("cache.key_hash"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~CacheCallRecorderTests"`

Expected: FAIL to compile — `CacheCallRecorder` does not exist.

- [ ] **Step 4: Implement `CacheCallRecorder`**

Create `src/Caching.NET/Telemetry/CacheCallRecorder.cs`:

```csharp
using System.Diagnostics;
using Caching.NET.Internal;
using Caching.NET.Options;

namespace Caching.NET.Telemetry;

/// <summary>
/// One telemetry record for one call into <see cref="Services.RoutingCacheService"/>: starts a span,
/// times the call end to end, times the caller's factory when one runs, and on dispose emits
/// <c>cache.operation.duration</c> plus (when a factory ran) <c>cache.factory.duration</c>.
/// <para>
/// Not thread-safe by design: one instance belongs to one logical call, whose factory invocations are
/// sequential. Concurrent calls each get their own recorder, so no ambient state is involved.
/// </para>
/// </summary>
internal sealed class CacheCallRecorder : IDisposable
{
    internal const string ServedFromCache = "cache";
    internal const string ServedFromSource = "source";
    internal const string ServedFromMixed = "mixed";
    internal const string ServedFromNone = "none";

    private readonly string _operation;
    private readonly bool _readShaped;
    private readonly long _startTimestamp;
    private readonly Activity? _activity;

    private string _mode;
    private long _factoryTicks;
    private bool _factoryRan;
    private bool _servedFromCache;
    private bool _batch;
    private int _hits;
    private int _misses;
    private bool _coalesced;
    private string? _missReason;
    private string? _errorKind;
    private bool _errorThrownToCaller;
    private bool _disposed;

    private CacheCallRecorder(string mode, string operation, Activity? activity)
    {
        _mode = mode;
        _operation = operation;
        _readShaped = IsReadShaped(operation);
        _activity = activity;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Starts a record. <paramref name="rawKey"/> is used only to derive a hashed key tag when
    /// <see cref="CacheOptions.IncludeKeyHashInTraces"/> is set; the raw value never reaches the span.
    /// </summary>
    public static CacheCallRecorder Start(string mode, string operation, CacheOptions options, string? rawKey = null)
    {
        var activity = CacheInstruments.Activity.StartActivity($"cache {operation}", ActivityKind.Internal);
        if (activity is not null && options.IncludeKeyHashInTraces && !string.IsNullOrEmpty(rawKey))
            activity.SetTag("cache.key_hash", StableStringHash.Compute64(rawKey).ToString("x16"));
        return new CacheCallRecorder(mode, operation, activity);
    }

    // Write-shaped operations serve nothing, so they carry no served_from tag at all rather than a
    // meaningless value that would split Prometheus series for no benefit.
    private static bool IsReadShaped(string operation) => operation is
        "get" or "get_many" or "get_or_create" or "exists" or "refresh" or "stale_refresh";

    /// <summary>Replaces the mode tag once routing has resolved which backend handles the call.</summary>
    public void SetMode(string resolvedMode) => _mode = resolvedMode;

    /// <summary>
    /// Wraps the caller's factory so each invocation is timed. Elapsed time accumulates, because some
    /// paths invoke the factory more than once per call (a failed read that falls open to the factory).
    /// Exceptions propagate unchanged, and their elapsed time is still counted.
    /// </summary>
    public Func<CancellationToken, Task<T>> WrapFactory<T>(Func<CancellationToken, Task<T>> factory)
        => async ct =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await factory(ct);
            }
            finally
            {
                _factoryTicks += Stopwatch.GetTimestamp() - started;
                _factoryRan = true;
            }
        };

    public void MarkServedFromCache() => _servedFromCache = true;

    public void MarkNotFound() => _servedFromCache = false;

    public void MarkBatch(int hits, int misses)
    {
        _batch = true;
        _hits = hits;
        _misses = misses;
    }

    /// <summary>Marks that this call waited on a stripe lock another call held.</summary>
    public void MarkCoalesced() => _coalesced = true;

    public void MarkMissReason(string reason) => _missReason = reason;

    /// <summary>
    /// Records a backend error. <paramref name="thrownToCaller"/> false means the failure was swallowed
    /// (fail-open) and the span keeps an unset status — a Redis blip must not paint a successful
    /// consumer request as failed.
    /// </summary>
    public void MarkError(string errorKind, bool thrownToCaller)
    {
        _errorKind = errorKind;
        _errorThrownToCaller = thrownToCaller;
    }

    private string? ResolveServedFrom()
    {
        if (!_readShaped) return null;
        if (_factoryRan) return ServedFromSource;
        if (_batch)
        {
            if (_hits > 0 && _misses > 0) return ServedFromMixed;
            return _hits > 0 ? ServedFromCache : ServedFromNone;
        }
        return _servedFromCache ? ServedFromCache : ServedFromNone;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var totalMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var servedFrom = ResolveServedFrom();
        CacheInstruments.RecordDuration(_mode, _operation, totalMs, servedFrom);

        double? factoryMs = null;
        if (_factoryRan)
        {
            factoryMs = _factoryTicks * 1000.0 / Stopwatch.Frequency;
            CacheInstruments.RecordFactoryDuration(_mode, _operation, factoryMs.Value);
        }

        if (_activity is null) return;

        _activity.SetTag("cache.mode", _mode);
        _activity.SetTag("cache.operation", _operation);
        if (servedFrom is not null) _activity.SetTag("cache.served_from", servedFrom);
        if (factoryMs is { } f) _activity.SetTag("cache.factory_ms", Math.Round(f, 3));
        if (_missReason is not null) _activity.SetTag("cache.miss_reason", _missReason);
        if (_batch)
        {
            _activity.SetTag("cache.hit_count", _hits);
            _activity.SetTag("cache.miss_count", _misses);
        }
        if (_coalesced) _activity.SetTag("cache.coalesced", true);
        if (_errorKind is not null)
        {
            _activity.SetTag("cache.error_kind", _errorKind);
            // Cancellation is a caller decision, not a fault.
            if (_errorThrownToCaller && _errorKind is not "Canceled" and not "Cancelled")
                _activity.SetStatus(ActivityStatusCode.Error, _errorKind);
        }

        _activity.Dispose();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~CacheCallRecorderTests"`

Expected: PASS, every test. If `Span_carries_factory_ms` is flaky on a loaded machine, the delay/threshold pair (25ms delay, 15ms assertion) already has margin — do not weaken the assertion to `>= 0`, that would stop testing anything.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Telemetry/CacheCallRecorder.cs tests/Caching.NET.Tests/Telemetry/ActivityListenerHelpers.cs tests/Caching.NET.Tests/Telemetry/CacheCallRecorderTests.cs
git commit -m "feat(telemetry): add CacheCallRecorder for per-call cache visibility"
```

---

## Task 3: Remove duration recording from the backend services

Recording must leave the services **before** Routing starts recording, otherwise both fire and every sample is double counted.

**Files:**
- Modify: `src/Caching.NET/Services/InMemoryCacheService.cs`
- Modify: `src/Caching.NET/Services/RedisCacheService.cs`
- Modify: `src/Caching.NET/Services/HybridCacheService.cs`
- Modify: `src/Caching.NET/Telemetry/CacheInstruments.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/OperationDurationTests.cs` (replace contents)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `CacheInstruments.MeasureDuration` and the nested `CacheInstruments.OperationTimer` type no longer exist. Nothing outside `CacheInstruments` may reference them after this task.

- [ ] **Step 1: Replace the test file with one asserting services do not record**

Replace the entire contents of `tests/Caching.NET.Tests/Telemetry/OperationDurationTests.cs`:

```csharp
using Caching.NET.Options;
using Caching.NET.Resilience;
using Caching.NET.Serialization;
using Caching.NET.Services;
using Caching.NET.Tests.Fakes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// cache.operation.duration is recorded once per call at the routing layer. A backend service invoked
/// directly must record nothing, otherwise composite operations nest and every dashboard that sums
/// across operations double counts.
/// </summary>
public class OperationDurationTests
{
    private static RedisCacheService BuildRedis(IDistributedCache distributed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(distributed);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CacheOptions { KeyPrefix = "dur" }));
        services.AddSingleton<ICacheSerializer>(new JsonCacheSerializer());
        services.AddSingleton(CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(5), retryCount: 0));
        services.AddSingleton<RedisCacheService>();
        return services.BuildServiceProvider().GetRequiredService<RedisCacheService>();
    }

    [Fact]
    public async Task Redis_service_called_directly_records_no_operation_duration()
    {
        var cache = BuildRedis(new FakeDistributedCache());
        var key = $"k:{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", "Redis");
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v"));
        await cache.GetAsync<string>(key);
        await cache.SetAsync(key, "v2");
        await cache.RemoveAsync(key);
        listener.Dispose();

        Assert.Empty(values);
    }
}
```

Note: this asserts on mode `Redis` while other test classes may run in parallel against Redis-mode caches through routing, which does record. Keep this test's scope to the direct-service calls above and run it as written; the `RedisCacheService` built here is standalone (no routing wrapper), and Routing-based tests in the suite use `Hybrid`/`InMemory` fixtures for their duration assertions. If this proves flaky in CI, move it to its own xUnit collection rather than weakening the assertion.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~OperationDurationTests"`

Expected: FAIL — `Assert.Empty() Failure: Collection was not empty`, because the services still record.

- [ ] **Step 3: Strip instrumentation from `InMemoryCacheService`**

Delete every `using var timer = CacheInstruments.MeasureDuration(...)` line, and restore `GetOrCreateAsync` to not pass a timer. The two methods return to:

```csharp
    /// <inheritdoc />
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        _ = localExpiration;

        if (cache.TryGetValue(key, out T? cached))
        {
            CacheInstruments.RecordHit(Mode, "get_or_create");
            return Task.FromResult(cached!);
        }

        return GetOrCreateSlowAsync(key, factory, expiration, cancellationToken);
    }

    private async Task<T> GetOrCreateSlowAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration,
        CancellationToken cancellationToken) where T : notnull
    {
        CacheInstruments.RecordMiss(Mode, "get_or_create", "NotFound");
        T value = await factory(cancellationToken);
        // Never cache a null factory result; treat it as a non-cacheable miss so the next call re-runs the factory.
        if (value is null) return value!;
        var expirationSpan = expiration ?? options.Value.GetDefaultExpiration() ?? FallbackExpiration;
        var entryOpts = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
        entryOpts.PostEvictionCallbacks.Add(s_evictionRegistration);
        cache.Set(key, value, entryOpts);
        CacheInstruments.RecordSet(Mode);
        return value;
    }
```

All `RecordHit`/`RecordMiss`/`RecordSet`/`RecordRemove`/`RecordEviction` calls stay exactly as they are.

- [ ] **Step 4: Strip instrumentation from `RedisCacheService` and `HybridCacheService`**

Delete every `using var timer = CacheInstruments.MeasureDuration(...)` line in both files. Do not touch `SerializeTimed`/`DeserializeTimed` — `cache.serialize.duration` and `cache.deserialize.duration` stay.

Verify none remain:

```bash
grep -rn "MeasureDuration" src/ || echo "clean"
```

Expected: `clean`.

- [ ] **Step 5: Delete the now-unused helper from `CacheInstruments`**

Remove the `MeasureDuration` method and the entire `OperationTimer` struct. Keep `RecordDuration` (both overloads), `RecordFactoryDuration`, and every other member.

- [ ] **Step 6: Run the full unit suite**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0`

Expected: PASS. Zero warnings — an unused `using System.Diagnostics;` left behind in a service file will fail the build under `TreatWarningsAsErrors`.

- [ ] **Step 7: Commit**

```bash
git add src/Caching.NET/Services src/Caching.NET/Telemetry/CacheInstruments.cs tests/Caching.NET.Tests/Telemetry/OperationDurationTests.cs
git commit -m "refactor(telemetry): stop recording operation duration in backend services"
```

---

## Task 4: Route `GetOrCreateAsync` through the recorder

**Files:**
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs` (create)

**Interfaces:**
- Consumes: `CacheCallRecorder.Start/WrapFactory/SetMode/MarkMissReason/MarkCoalesced/MarkError/Dispose` from Task 2.
- Produces:
  - `private static string RoutingCacheService.ModeNameOf(ICacheService service)` → `"InMemory"` / `"Redis"` / `"Hybrid"` / `"Routing"`
  - `private async Task<T> GetOrCreateCoreAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheCallOptions? callOptions, TimeSpan? expiration, TimeSpan? localExpiration, CacheCallRecorder recorder, CancellationToken cancellationToken)` — the previous body of the public overload, used by Task 7 as well.

- [ ] **Step 1: Write the failing tests**

Create `tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs`:

```csharp
using System.Diagnostics;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Telemetry;

public class RoutingCallVisibilityTests
{
    // Key-hash tagging is enabled so each assertion can be pinned to its own call via
    // cache.key_hash — the ActivitySource is process-wide and other tests emit spans too.
    private static ICacheService BuildCache(string mode, bool enabled = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = enabled ? "true" : "false",
            ["CacheOptions:Mode"] = mode,
            ["CacheOptions:KeyPrefix"] = "vis",
            ["CacheOptions:IncludeKeyHashInTraces"] = "true",
        };
        if (mode == "Hybrid") settings["CacheOptions:RedisConnectionString"] = "localhost:6379";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        return services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    private static Activity SpanFor(List<Activity> activities, string operation, string keyHash) =>
        activities.Single(a => a.Tag("cache.operation") == operation && a.Tag("cache.key_hash") == keyHash);

    private static string HashOf(string key) =>
        Caching.NET.Internal.StableStringHash.Compute64(key).ToString("x16");

    [Fact]
    public async Task Miss_then_hit_reports_source_then_cache()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, async ct => { await Task.Delay(30, ct); return "v"; });
        var missSpan = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", missSpan.Tag("cache.served_from"));
        Assert.Equal("InMemory", missSpan.Tag("cache.mode"));
        Assert.True(double.Parse(missSpan.Tag("cache.factory_ms")!) >= 20);

        activities.Clear();
        await cache.GetOrCreateAsync(key, _ => Task.FromResult("other"));
        var hitSpan = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("cache", hitSpan.Tag("cache.served_from"));
        Assert.Null(hitSpan.Tag("cache.factory_ms"));
    }

    [Fact]
    public async Task Miss_emits_one_total_and_one_factory_metric_sample()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (totals, totalListener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", "InMemory");
        var (factories, factoryListener) = MeterListenerHelpers.Capture<double>("cache.factory.duration", "InMemory");
        using var _ = totalListener;
        using var __ = factoryListener;

        await cache.GetOrCreateAsync(key, async ct => { await Task.Delay(25, ct); return "v"; });
        totalListener.Dispose();
        factoryListener.Dispose();

        Assert.Contains(totals, t =>
            t.tags.Any(x => x.Key == "cache.operation" && (string?)x.Value == "get_or_create") &&
            t.tags.Any(x => x.Key == "cache.served_from" && (string?)x.Value == "source"));
        Assert.Contains(factories, f =>
            f.tags.Any(x => x.Key == "cache.operation" && (string?)x.Value == "get_or_create") && f.value >= 20);
    }

    [Fact]
    public async Task Disabled_cache_still_reports_the_call_and_times_the_source()
    {
        var cache = BuildCache("InMemory", enabled: false);
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, async ct => { await Task.Delay(25, ct); return "v"; });

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("Routing", span.Tag("cache.mode"));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.Equal("Disabled", span.Tag("cache.miss_reason"));
        Assert.True(double.Parse(span.Tag("cache.factory_ms")!) >= 20);
    }

    [Fact]
    public async Task Bypass_reports_source_with_bypass_reason()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v"), new CacheCallOptions { BypassCache = true });

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.Equal("Bypass", span.Tag("cache.miss_reason"));
    }

    [Fact]
    public async Task Force_refresh_reports_source()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "old");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("new"), new CacheCallOptions { ForceRefresh = true });

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.NotNull(span.Tag("cache.factory_ms"));
    }

    [Fact]
    public async Task Rejected_key_reports_source_with_key_rejected_reason()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "vis",
            ["CacheOptions:IncludeKeyHashInTraces"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, b => b.WithKeyValidator(_ => false));
        var cache = services.BuildServiceProvider().GetRequiredService<ICacheService>();
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v"));

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.Equal("KeyRejected", span.Tag("cache.miss_reason"));
    }

    [Fact]
    public async Task Coalesced_waiter_is_tagged_and_reports_cache()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var gate = new TaskCompletionSource();
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        var winner = cache.GetOrCreateAsync(key, async _ =>
        {
            await gate.Task;
            return "v";
        });
        // Give the winner time to take the stripe lock before the waiter arrives.
        await Task.Delay(100);
        var waiter = cache.GetOrCreateAsync(key, _ => Task.FromResult("unused"));
        gate.SetResult();
        await Task.WhenAll(winner, waiter);

        var spans = activities.Where(a =>
            a.Tag("cache.operation") == "get_or_create" && a.Tag("cache.key_hash") == HashOf(key)).ToList();
        Assert.Equal(2, spans.Count);
        var coalesced = Assert.Single(spans, s => s.Tag("cache.coalesced") == "True");
        Assert.Equal("cache", coalesced.Tag("cache.served_from"));
        Assert.Null(coalesced.Tag("cache.factory_ms"));
    }

    [Fact]
    public async Task Factory_exception_marks_span_error_and_still_records()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync<string>(key, _ => throw new InvalidOperationException("boom")));

        var span = SpanFor(activities, "get_or_create", HashOf(key));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Unknown", span.Tag("cache.error_kind"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~RoutingCallVisibilityTests"`

Expected: FAIL — no spans exist yet, so `Single(...)` throws "The collection was empty".

- [ ] **Step 3: Add the mode-name helper**

In `RoutingCacheService`, next to `ResolveService`:

```csharp
    // The mode tag reports which backend actually handled the call. Short-circuit paths that never
    // reach a backend keep the "Routing" value, matching the existing counter behavior.
    private static string ModeNameOf(ICacheService service) => service switch
    {
        InMemoryCacheService => "InMemory",
        RedisCacheService => "Redis",
        HybridCacheService => "Hybrid",
        _ => Mode,
    };
```

- [ ] **Step 4: Split the public `GetOrCreateAsync` overload into recorder + core**

Rename the existing `public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheCallOptions? callOptions, ...)` body to a private core method that takes the recorder, and add the thin public wrapper. Only the signature line and the `ArgumentException` guard move; the body is otherwise untouched at this step:

```csharp
    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        using var recorder = CacheCallRecorder.Start(
            Mode, "get_or_create", _optionsMonitor.CurrentValue, key);
        try
        {
            return await GetOrCreateCoreAsync(
                key, factory, callOptions, expiration, localExpiration, recorder, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }

    private async Task<T> GetOrCreateCoreAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheCallOptions? callOptions,
        TimeSpan? expiration,
        TimeSpan? localExpiration,
        CacheCallRecorder recorder,
        CancellationToken cancellationToken)
        where T : notnull
    {
        // ... existing body, edited in the next step ...
    }
```

- [ ] **Step 5: Wire the recorder into the core body**

Inside `GetOrCreateCoreAsync`, apply these edits.

First, wrap the factory once, keeping the original for the background-refresh path (which outlives this recorder):

```csharp
        var originalFactory = factory;
        factory = recorder.WrapFactory(factory);
```

Then mark the short-circuit paths (each already records a counter — add the recorder call beside it):

```csharp
        if (IsDisabled)
        {
            CacheInstruments.RecordMiss(Mode, "get_or_create", "Disabled");
            recorder.MarkMissReason("Disabled");
            return await factory(cancellationToken);
        }

        if (!TryPreparePrefixedKey(key, "get_or_create", out var prefixed))
        {
            CacheInstruments.RecordMiss(Mode, "get_or_create", "KeyRejected");
            recorder.MarkMissReason("KeyRejected");
            return await factory(cancellationToken);
        }

        if ((callOptions?.BypassCache ?? false))
        {
            CacheInstruments.RecordMiss(Mode, "get_or_create", "Bypass");
            recorder.MarkMissReason("Bypass");
            var ct = ApplyFactoryTimeout(cancellationToken, out var cts);
            try
            {
                return await factory(ct);
            }
            finally
            {
                cts?.Dispose();
            }
        }
```

After the service is resolved, set the resolved mode:

```csharp
        var service = ResolveService(callOptions?.Mode);
        recorder.SetMode(ModeNameOf(service));
```

In the stale-serve branch, pass the **unwrapped** factory to the background refresh so its timing belongs to that refresh's own recorder (Task 7), not to this call:

```csharp
                var stale = await service.GetAsync<T>(prefixed, cancellationToken);
                if (stale is not null)
                {
                    CacheInstruments.RecordStaleServed(Mode, "get_or_create");
                    recorder.MarkServedFromCache();
                    ScheduleBackgroundRefresh(prefixed, originalFactory, callOptions, expiration, localExpiration);
                    return stale;
                }
```

Replace the stripe-lock acquisition so contention is detected exactly — an uncontended `WaitAsync` returns an already-completed task:

```csharp
            var semaphore = _lockManager.GetLock(prefixed);
            var waitTask = semaphore.WaitAsync(cancellationToken);
            if (!waitTask.IsCompleted) recorder.MarkCoalesced();
            await waitTask;
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~RoutingCallVisibilityTests"`

Expected: PASS. `Coalesced_waiter_is_tagged_and_reports_cache` is the one to watch — if it fails claiming both spans are uncoalesced, the 100ms delay was not enough for the winner to take the lock on a loaded machine; raise it to 250ms rather than removing the assertion.

- [ ] **Step 7: Run the whole unit suite for regressions**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0`

Expected: PASS, zero warnings.

- [ ] **Step 8: Commit**

```bash
git add src/Caching.NET/Services/RoutingCacheService.cs tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs
git commit -m "feat(telemetry): record per-call visibility for GetOrCreateAsync at routing layer"
```

---

## Task 5: Route the single-key operations through the recorder

**Files:**
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs` (append)

**Interfaces:**
- Consumes: `CacheCallRecorder` (Task 2), `ModeNameOf` (Task 4).
- Produces: nothing new for later tasks.

Operations covered, with the exact recorder calls each needs:

| Method | `operation` | Outcome marking |
| ---- | ---- | ---- |
| `GetAsync<T>` | `get` | `MarkServedFromCache()` when the returned value is non-null, else `MarkNotFound()` |
| `GetAsync(string, Type)` | `get` | same, on the returned `object?` |
| `ExistsAsync` | `exists` | `MarkServedFromCache()` when true, else `MarkNotFound()` |
| `RefreshAsync<T>` | `refresh` | factory wrapped; no explicit marking (factory always runs ⇒ `source`) |
| `SetAsync<T>` (per-call overload) | `set` | none (write-shaped, tag omitted) |
| `RemoveAsync` | `remove` | none |
| `RemoveByTagAsync(string)` | `remove_by_tag` | none |
| `RemoveByTagAsync(IEnumerable<string>)` | `remove_by_tag` | none |
| `ClearAsync` | `clear` | none |

All of these are currently non-`async` methods returning `Task` directly. Each becomes `async` so the recorder can be disposed after the inner task completes. That adds one state machine per call — acceptable and required for the measurement to include the awaited work.

- [ ] **Step 1: Write the failing tests**

Append to `RoutingCallVisibilityTests`:

```csharp
    [Fact]
    public async Task Get_hit_and_miss_report_cache_and_none()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var missingKey = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetAsync<string>(key);
        await cache.GetAsync<string>(missingKey);

        Assert.Equal("cache", SpanFor(activities, "get", HashOf(key)).Tag("cache.served_from"));
        Assert.Equal("none", SpanFor(activities, "get", HashOf(missingKey)).Tag("cache.served_from"));
    }

    [Fact]
    public async Task Exists_reports_cache_when_present_and_none_when_absent()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var missingKey = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        Assert.True(await cache.ExistsAsync(key));
        Assert.False(await cache.ExistsAsync(missingKey));

        Assert.Equal("cache", SpanFor(activities, "exists", HashOf(key)).Tag("cache.served_from"));
        Assert.Equal("none", SpanFor(activities, "exists", HashOf(missingKey)).Tag("cache.served_from"));
    }

    [Fact]
    public async Task Refresh_reports_source_and_times_the_factory()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.RefreshAsync(key, async ct => { await Task.Delay(25, ct); return "v"; });

        var span = SpanFor(activities, "refresh", HashOf(key));
        Assert.Equal("source", span.Tag("cache.served_from"));
        Assert.True(double.Parse(span.Tag("cache.factory_ms")!) >= 20);
    }

    [Fact]
    public async Task Write_operations_emit_a_span_without_served_from()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.SetAsync(key, "v");
        await cache.RemoveAsync(key);

        var setSpan = SpanFor(activities, "set", HashOf(key));
        Assert.Null(setSpan.Tag("cache.served_from"));
        Assert.Equal("InMemory", setSpan.Tag("cache.mode"));
        Assert.Null(SpanFor(activities, "remove", HashOf(key)).Tag("cache.served_from"));
    }

    [Fact]
    public async Task Clear_emits_a_span()
    {
        var cache = BuildCache("InMemory");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await ((Caching.NET.Services.IRoutingCacheService)cache).ClearAsync();

        Assert.Contains(activities, a => a.Tag("cache.operation") == "clear");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~RoutingCallVisibilityTests"`

Expected: the five new tests FAIL with an empty-collection error from `Single`. The Task 4 tests still pass.

- [ ] **Step 3: Convert `GetAsync<T>` and `GetAsync(string, Type)`**

```csharp
    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        using var recorder = CacheCallRecorder.Start(Mode, "get", _optionsMonitor.CurrentValue, key);
        try
        {
            if (IsDisabled)
            {
                CacheInstruments.RecordMiss(Mode, "get", "Disabled");
                recorder.MarkMissReason("Disabled");
                return default;
            }
            if (!TryPreparePrefixedKey(key, "get", out var prefixed))
            {
                CacheInstruments.RecordMiss(Mode, "get", "KeyRejected");
                recorder.MarkMissReason("KeyRejected");
                return default;
            }
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            var value = await service.GetAsync<T>(prefixed, cancellationToken);
            if (value is not null) recorder.MarkServedFromCache();
            else recorder.MarkNotFound();
            return value;
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<object?> GetAsync(string key, Type type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        ArgumentNullException.ThrowIfNull(type);
        using var recorder = CacheCallRecorder.Start(Mode, "get", _optionsMonitor.CurrentValue, key);
        try
        {
            if (IsDisabled)
            {
                CacheInstruments.RecordMiss(Mode, "get", "Disabled");
                recorder.MarkMissReason("Disabled");
                return null;
            }
            if (!TryPreparePrefixedKey(key, "get", out var prefixed))
            {
                CacheInstruments.RecordMiss(Mode, "get", "KeyRejected");
                recorder.MarkMissReason("KeyRejected");
                return null;
            }
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            var value = await service.GetAsync(prefixed, type, cancellationToken);
            if (value is not null) recorder.MarkServedFromCache();
            else recorder.MarkNotFound();
            return value;
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }
```

- [ ] **Step 4: Convert `ExistsAsync` and `RefreshAsync`**

```csharp
    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        using var recorder = CacheCallRecorder.Start(Mode, "exists", _optionsMonitor.CurrentValue, key);
        try
        {
            if (IsDisabled)
            {
                recorder.MarkMissReason("Disabled");
                return false;
            }
            if (!TryPreparePrefixedKey(key, "exists", out var prefixed))
            {
                recorder.MarkMissReason("KeyRejected");
                return false;
            }
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            var present = await service.ExistsAsync(prefixed, cancellationToken);
            if (present) recorder.MarkServedFromCache();
            else recorder.MarkNotFound();
            return present;
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
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
        using var recorder = CacheCallRecorder.Start(Mode, "refresh", _optionsMonitor.CurrentValue, key);
        try
        {
            if (IsDisabled)
            {
                recorder.MarkMissReason("Disabled");
                return;
            }
            if (!TryPreparePrefixedKey(key, "refresh", out var prefixed))
            {
                recorder.MarkMissReason("KeyRejected");
                return;
            }
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            await service.RefreshAsync(prefixed, recorder.WrapFactory(factory), expiration, localExpiration, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }
```

- [ ] **Step 5: Convert the write operations**

```csharp
    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        CacheCallOptions? callOptions,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));
        using var recorder = CacheCallRecorder.Start(Mode, "set", _optionsMonitor.CurrentValue, key);
        try
        {
            if (IsDisabled) return;
            if ((callOptions?.BypassCache ?? false)) return;
            if (!TryPreparePrefixedKey(key, "set", out var prefixed)) return;
            var service = ResolveService(callOptions?.Mode);
            recorder.SetMode(ModeNameOf(service));
            var jitteredExpiration = ApplyJitter(callOptions?.AbsoluteExpiration ?? expiration, callOptions?.JitterPercentage);
            await RoutingCacheService.SetWithExpirationAsync(
                service, prefixed, value, jitteredExpiration, callOptions?.SlidingExpiration,
                localExpiration, callOptions?.Tags, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        using var recorder = CacheCallRecorder.Start(Mode, "remove", _optionsMonitor.CurrentValue, key);
        try
        {
            if (IsDisabled) return;
            if (!TryPreparePrefixedKey(key, "remove", out var prefixed)) return;
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            await service.RemoveAsync(prefixed, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        using var recorder = CacheCallRecorder.Start(Mode, "remove_by_tag", _optionsMonitor.CurrentValue);
        try
        {
            if (IsDisabled) return;
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            await service.RemoveByTagAsync(tag, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        using var recorder = CacheCallRecorder.Start(Mode, "remove_by_tag", _optionsMonitor.CurrentValue);
        try
        {
            if (IsDisabled) return;
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            await service.RemoveByTagAsync(tags, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }
```

`ClearAsync` keeps its `switch` but becomes async. The XML doc comment above it stays unchanged:

```csharp
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var recorder = CacheCallRecorder.Start(Mode, "clear", _optionsMonitor.CurrentValue);
        try
        {
            if (IsDisabled) return;
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            switch (service)
            {
                case InMemoryCacheService inMemory:
                    await inMemory.ClearAsync(cancellationToken);
                    break;
                case HybridCacheService hybrid:
                    await hybrid.ClearAsync(cancellationToken);
                    break;
                case RedisCacheService redis:
                    await redis.ClearAsync(EscapeGlob(_keyPrefix) + "*", cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0`

Expected: PASS, whole suite, zero warnings. `CS1998` (async without await) would be an error here — every converted method awaits, so if you hit it, a `return` path was left non-awaiting.

- [ ] **Step 7: Commit**

```bash
git add src/Caching.NET/Services/RoutingCacheService.cs tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs
git commit -m "feat(telemetry): record per-call visibility for single-key operations"
```

---

## Task 6: Route the batch operations through the recorder

**Files:**
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs` (append)

**Interfaces:**
- Consumes: `CacheCallRecorder.MarkBatch(int hits, int misses)` (Task 2), `ModeNameOf` (Task 4).
- Produces: nothing new for later tasks.

`GetManyAsync` counts a hit per non-null value and a miss per null value **including keys rejected by the validator/transformer**, since from the caller's view those keys returned nothing. `SetManyAsync` and `RemoveManyAsync` are write-shaped: span only, no `served_from`.

- [ ] **Step 1: Write the failing tests**

Append to `RoutingCallVisibilityTests`:

```csharp
    [Fact]
    public async Task Get_many_with_some_hits_reports_mixed_with_counts()
    {
        var cache = BuildCache("InMemory");
        var hitKey1 = $"k:{Guid.NewGuid():N}";
        var hitKey2 = $"k:{Guid.NewGuid():N}";
        var missKey = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(hitKey1, "v1");
        await cache.SetAsync(hitKey2, "v2");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetManyAsync<string>(new[] { hitKey1, hitKey2, missKey });

        var span = Assert.Single(activities, a => a.Tag("cache.operation") == "get_many");
        Assert.Equal("mixed", span.Tag("cache.served_from"));
        Assert.Equal("2", span.Tag("cache.hit_count"));
        Assert.Equal("1", span.Tag("cache.miss_count"));
    }

    [Fact]
    public async Task Get_many_all_hits_reports_cache()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "v");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetManyAsync<string>(new[] { key });

        var span = Assert.Single(activities, a => a.Tag("cache.operation") == "get_many");
        Assert.Equal("cache", span.Tag("cache.served_from"));
    }

    [Fact]
    public async Task Get_many_all_misses_reports_none()
    {
        var cache = BuildCache("InMemory");
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.GetManyAsync<string>(new[] { $"k:{Guid.NewGuid():N}", $"k:{Guid.NewGuid():N}" });

        var span = Assert.Single(activities, a => a.Tag("cache.operation") == "get_many");
        Assert.Equal("none", span.Tag("cache.served_from"));
        Assert.Equal("0", span.Tag("cache.hit_count"));
        Assert.Equal("2", span.Tag("cache.miss_count"));
    }

    [Fact]
    public async Task Batch_writes_emit_spans_without_served_from()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;

        await cache.SetManyAsync(new Dictionary<string, string> { [key] = "v" });
        await cache.RemoveManyAsync(new[] { key });

        var setMany = Assert.Single(activities, a => a.Tag("cache.operation") == "set_many");
        Assert.Null(setMany.Tag("cache.served_from"));
        Assert.Contains(activities, a => a.Tag("cache.operation") == "remove_many");
    }
```

These use `Assert.Single(activities, predicate)` rather than a key-hash filter because batch operations have no single key to hash; each test builds its own provider and captures a short window, so only its own batch span matches.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~RoutingCallVisibilityTests"`

Expected: the four new tests FAIL — no `get_many` / `set_many` / `remove_many` spans exist.

- [ ] **Step 3: Wire `GetManyAsync`**

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var recorder = CacheCallRecorder.Start(Mode, "get_many", _optionsMonitor.CurrentValue);
        try
        {
            if (IsDisabled)
            {
                recorder.MarkBatch(0, 0);
                recorder.MarkMissReason("Disabled");
                return new Dictionary<string, T?>();
            }

            var keyList = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            if (keyList.Length == 0)
            {
                recorder.MarkBatch(0, 0);
                return new Dictionary<string, T?>();
            }

            var dict = new Dictionary<string, T?>(keyList.Length);
            var okKeys = new List<string>(keyList.Length);
            var okPrefixed = new List<string>(keyList.Length);
            foreach (var k in keyList)
            {
                if (TryPreparePrefixedKey(k, "get_many", out var p))
                {
                    okKeys.Add(k);
                    okPrefixed.Add(p);
                }
                else
                {
                    dict[k] = default;
                }
            }

            if (okPrefixed.Count == 0)
            {
                recorder.MarkBatch(0, dict.Count);
                recorder.MarkMissReason("KeyRejected");
                return dict;
            }

            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            var inner = await service.GetManyAsync<T>(okPrefixed, cancellationToken);

            for (int i = 0; i < okKeys.Count; i++)
                dict[okKeys[i]] = inner.TryGetValue(okPrefixed[i], out var v) ? v : default;

            // A key rejected before dispatch returned nothing to the caller, so it counts as a miss.
            var hits = dict.Values.Count(v => v is not null);
            recorder.MarkBatch(hits, dict.Count - hits);
            return dict;
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }
```

- [ ] **Step 4: Wire `SetManyAsync` and `RemoveManyAsync`**

```csharp
    /// <inheritdoc />
    public async Task SetManyAsync<T>(
        IReadOnlyDictionary<string, T> items,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        using var recorder = CacheCallRecorder.Start(Mode, "set_many", _optionsMonitor.CurrentValue);
        try
        {
            if (IsDisabled || items.Count == 0) return;
            var jitteredExpiration = ApplyJitter(expiration, null);
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            var prefixed = new Dictionary<string, T>(items.Count);
            foreach (var kvp in items)
            {
                if (!TryPreparePrefixedKey(kvp.Key, "set_many", out var p)) continue;
                prefixed[p] = kvp.Value;
            }
            if (prefixed.Count == 0) return;
            await service.SetManyAsync(prefixed, jitteredExpiration, localExpiration, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys is null) return;
        using var recorder = CacheCallRecorder.Start(Mode, "remove_many", _optionsMonitor.CurrentValue);
        try
        {
            if (IsDisabled) return;
            var prefixed = new List<string>();
            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (TryPreparePrefixedKey(k, "remove_many", out var p)) prefixed.Add(p);
            }
            if (prefixed.Count == 0) return;
            var service = ResolveService(modeOverride: null);
            recorder.SetMode(ModeNameOf(service));
            await service.RemoveManyAsync(prefixed, cancellationToken);
        }
        catch (Exception ex)
        {
            recorder.MarkError(ClassifyError(ex), thrownToCaller: true);
            throw;
        }
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0`

Expected: PASS, whole suite, zero warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Services/RoutingCacheService.cs tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs
git commit -m "feat(telemetry): record per-call visibility for batch operations"
```

---

## Task 7: Background stale refresh gets its own record

**Files:**
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`
- Test: `tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs` (append)

**Interfaces:**
- Consumes: `CacheCallRecorder` (Task 2), `ModeNameOf` (Task 4), the unwrapped `originalFactory` passed to `ScheduleBackgroundRefresh` (Task 4, Step 5).
- Produces: nothing new for later tasks.

The refresh runs on its own `Task.Run` after the triggering call has returned, so it needs its own recorder with `operation=stale_refresh`. Its errors are logged and swallowed, so `thrownToCaller` is false and the span stays un-errored.

- [ ] **Step 1: Write the failing test**

Append to `RoutingCallVisibilityTests`:

```csharp
    [Fact]
    public async Task Background_stale_refresh_records_its_own_span()
    {
        var cache = BuildCache("InMemory");
        var key = $"k:{Guid.NewGuid():N}";
        var callOptions = new CacheCallOptions { AllowStaleFor = TimeSpan.FromMinutes(5) };

        // Seed with a 200ms TTL plus the stale window, then wait for it to go stale.
        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v1"), callOptions, TimeSpan.FromMilliseconds(200));
        await Task.Delay(400);

        var (activities, listener) = ActivityListenerHelpers.Capture();
        using var _ = listener;
        var refreshed = new TaskCompletionSource();

        // Stale read: returns the stale value immediately and schedules the background refresh.
        var served = await cache.GetOrCreateAsync(key, async ct =>
        {
            await Task.Delay(20, ct);
            refreshed.TrySetResult();
            return "v2";
        }, callOptions, TimeSpan.FromMinutes(1));
        Assert.Equal("v1", served);

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Let the refresh task finish its write and dispose its recorder.
        await Task.Delay(200);

        var refreshSpan = Assert.Single(activities, a => a.Tag("cache.operation") == "stale_refresh");
        Assert.Equal("source", refreshSpan.Tag("cache.served_from"));
        Assert.NotNull(refreshSpan.Tag("cache.factory_ms"));
        Assert.NotEqual(ActivityStatusCode.Error, refreshSpan.Status);

        var staleSpan = Assert.Single(activities, a =>
            a.Tag("cache.operation") == "get_or_create" && a.Tag("cache.key_hash") == HashOf(key));
        Assert.Equal("cache", staleSpan.Tag("cache.served_from"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~Background_stale_refresh_records_its_own_span"`

Expected: FAIL — no `stale_refresh` span exists.

- [ ] **Step 3: Wire the recorder into `ScheduleBackgroundRefresh`**

Inside the `Task.Run` body, open a recorder right after the disposal check, wrap the factory with it, and mark swallowed errors. The existing counter calls and lock handling stay exactly as they are:

```csharp
        var refreshTask = Task.Run(async () =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            using var recorder = CacheCallRecorder.Start(
                Mode, "stale_refresh", _optionsMonitor.CurrentValue, prefixedKey);
            CacheInstruments.AddStaleRefreshInFlight(Mode, +1);
            var lockStripe = _lockManager.GetLock(prefixedKey);
            // Bound the wait so a stuck stripe-holder cannot pin a throttle slot indefinitely.
            var lockTimeout = _optionsMonitor.CurrentValue.GetFactoryTimeout() ?? TimeSpan.FromSeconds(30);
            bool lockAcquired = false;
            try
            {
                lockAcquired = await lockStripe.WaitAsync(lockTimeout, shutdownToken);
                if (!lockAcquired)
                {
                    _logger.StaleRefreshLockTimeout(prefixedKey, lockTimeout.TotalMilliseconds);
                    CacheInstruments.RecordError(Mode, "stale_refresh", "Timeout");
                    recorder.MarkError("Timeout", thrownToCaller: false);
                    return;
                }
                var factoryCt = ApplyFactoryTimeout(shutdownToken, out var cts);
                T value;
                try
                {
                    value = await recorder.WrapFactory(factory)(factoryCt);
                }
                finally
                {
                    cts?.Dispose();
                }
                var inner = ResolveService(callOptions?.Mode);
                recorder.SetMode(ModeNameOf(inner));
                var abs = callOptions?.AbsoluteExpiration ?? expiration ?? _optionsMonitor.CurrentValue.DefaultExpiration;
                var staleFor = callOptions?.AllowStaleFor ?? TimeSpan.Zero;
                var ttl = abs + staleFor;
                await inner.SetAsync(prefixedKey, value, ttl, localExpiration, shutdownToken);
                if (staleFor > TimeSpan.Zero)
                    _staleTracker.Register(prefixedKey, abs, staleFor);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Swallow expected shutdown cancellation.
                recorder.MarkError("Canceled", thrownToCaller: false);
            }
            catch (Exception ex)
            {
                _logger.StaleRefreshFailed(prefixedKey, ex);
                CacheInstruments.RecordError(Mode, "stale_refresh", ClassifyError(ex));
                recorder.MarkError(ClassifyError(ex), thrownToCaller: false);
            }
            finally
            {
                if (lockAcquired) lockStripe.Release();
                _throttle.Release();
                CacheInstruments.AddStaleRefreshInFlight(Mode, -1);
                _backgroundRefreshes.TryRemove(refreshId, out _);
            }
        });
```

Note `recorder.WrapFactory(factory)` is called once, inline, because the factory is invoked exactly once here.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj -f net10.0 --filter "FullyQualifiedName~RoutingCallVisibilityTests"`

Expected: PASS. If the `stale_refresh` span is missing, the trailing `Task.Delay(200)` was too short for the refresh task to dispose its recorder — raise it, do not drop the assertion.

- [ ] **Step 5: Run the whole unit suite on all three TFMs**

Run: `dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj`

Expected: PASS on net8.0, net9.0, net10.0.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Services/RoutingCacheService.cs tests/Caching.NET.Tests/Telemetry/RoutingCallVisibilityTests.cs
git commit -m "feat(telemetry): give background stale refresh its own per-call record"
```

---

## Task 8: Integration coverage against real Redis

**Files:**
- Create: `tests/Caching.NET.Tests.Integration/CallVisibilityRedisTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–7.
- Produces: nothing.

**Requires Docker** (Testcontainers starts Redis).

- [ ] **Step 1: Inspect the existing integration fixture**

Run: `ls tests/Caching.NET.Tests.Integration/` and read whichever file sets up the Redis container. Reuse that fixture and its connection-string plumbing exactly; do not start a second container.

- [ ] **Step 2: Write the failing tests**

Create `tests/Caching.NET.Tests.Integration/CallVisibilityRedisTests.cs`. Replace `RedisFixture` / `Connection` below with the fixture type and property names found in Step 1, and copy the span-capture helper inline because the Integration project does not reference the unit-test project:

```csharp
using System.Diagnostics;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Integration;

[Collection("redis")]
public class CallVisibilityRedisTests(RedisFixture fixture)
{
    private static (List<Activity> activities, ActivityListener listener) CaptureSpans()
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CacheInstruments.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (captured) captured.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        return (captured, listener);
    }

    private ICacheService BuildCache(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = mode,
            ["CacheOptions:KeyPrefix"] = "vis",
            ["CacheOptions:IncludeKeyHashInTraces"] = "true",
            ["CacheOptions:RedisConnectionString"] = fixture.Connection,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        return services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    [Theory]
    [InlineData("Redis")]
    [InlineData("Hybrid")]
    public async Task Miss_then_hit_reports_source_then_cache_end_to_end(string mode)
    {
        var cache = BuildCache(mode);
        var key = $"vis:{Guid.NewGuid():N}";
        var keyHash = Caching.NET.Internal.StableStringHash.Compute64(key).ToString("x16");
        var (activities, listener) = CaptureSpans();
        using var _ = listener;

        var first = await cache.GetOrCreateAsync(key, async ct =>
        {
            await Task.Delay(120, ct);
            return "v";
        });
        Assert.Equal("v", first);

        var second = await cache.GetOrCreateAsync(key, _ => Task.FromResult("other"));
        Assert.Equal("v", second);

        var spans = activities
            .Where(a => a.GetTagItem("cache.operation")?.ToString() == "get_or_create"
                     && a.GetTagItem("cache.key_hash")?.ToString() == keyHash)
            .OrderBy(a => a.StartTimeUtc)
            .ToList();
        Assert.Equal(2, spans.Count);

        Assert.Equal("source", spans[0].GetTagItem("cache.served_from")?.ToString());
        Assert.Equal(mode, spans[0].GetTagItem("cache.mode")?.ToString());
        var factoryMs = double.Parse(spans[0].GetTagItem("cache.factory_ms")!.ToString()!);
        Assert.True(factoryMs >= 100, $"factory_ms should reflect the 120ms delay, got {factoryMs}");
        // The source call's total must cover the factory plus the cache work around it.
        Assert.True(spans[0].Duration.TotalMilliseconds >= factoryMs);

        Assert.Equal("cache", spans[1].GetTagItem("cache.served_from")?.ToString());
        Assert.Null(spans[1].GetTagItem("cache.factory_ms"));
    }
}
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj -f net10.0 --filter "FullyQualifiedName~CallVisibilityRedisTests"`

Expected: PASS for both `Redis` and `Hybrid`. These assert behavior built in Tasks 1–7, so they should pass immediately — that is expected for integration coverage layered on top of already-tested units. If either fails, the failure is a real defect in the wiring, not a missing feature.

- [ ] **Step 4: Run the full integration and chaos suites**

Run:
```bash
dotnet test tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj -f net10.0
dotnet test tests/Caching.NET.Tests.Chaos/Caching.NET.Tests.Chaos.csproj -f net10.0
```

Expected: PASS both.

- [ ] **Step 5: Commit**

```bash
git add tests/Caching.NET.Tests.Integration/CallVisibilityRedisTests.cs
git commit -m "test(telemetry): end-to-end per-call visibility against real Redis"
```

---

## Task 9: Documentation and version

**Files:**
- Modify: `docs/TELEMETRY.md`
- Modify: `docs/features/telemetry.md`
- Modify: `CLAUDE.md`
- Modify: `src/Caching.NET/Caching.NET.csproj`

**Interfaces:**
- Consumes: the final tag and instrument names from Tasks 1–7.
- Produces: nothing.

- [ ] **Step 1: Rewrite the duration section in `docs/TELEMETRY.md`**

Replace the whole `### cache.operation.duration` section (added in the previous change, describing per-service recording and intentional nesting — both now false) with:

```markdown
### `cache.operation.duration` and `cache.factory.duration`

Both are recorded once per call by the routing layer, in a `finally`, so failed and timed-out calls are timed too. Wall time in milliseconds, measured with `Stopwatch.GetTimestamp()`.

- `cache.operation.duration` — the whole call, tagged `cache.mode`, `cache.operation`, and `cache.served_from` on read-shaped operations.
- `cache.factory.duration` — time inside the caller's factory (source retrieval), tagged `cache.mode` and `cache.operation`. Emitted only when a factory actually ran in that call.

Cache-side cost on a miss is `cache.operation.duration` − `cache.factory.duration` for the same call, exact in the span and derivable from the histograms at matching quantiles.

There is exactly **one** sample per call: no nesting, so summing across operations does not double count. A call made directly against a backend service rather than through `ICacheService` records nothing — dependency injection always registers the routing layer, so this affects tests only.

`cache.served_from` values:

| Value | Meaning |
| ---- | ---- |
| `cache` | served from the cache without running a factory |
| `source` | a factory ran (normal miss, force refresh, bypass, caching disabled, backend error fallback) |
| `mixed` | batch read where some keys hit and some missed |
| `none` | nothing was served (a `get` miss, `exists` false) |

Write-shaped operations (`set`, `set_many`, `remove`, `remove_many`, `remove_by_tag`, `clear`) omit the tag rather than carry a meaningless value.

Paths that reach no backend — caching disabled, per-call bypass, a key rejected by the validator/transformer, a Redis key over `MaximumKeyLength` — are still recorded, tagged `cache.mode=Routing`, with the factory timed. Background stale refreshes record under `cache.operation=stale_refresh`.

A caller that waits on a stripe lock another call holds is tagged `cache.coalesced=true` on the span and reports `served_from=cache`: it performs a real cache read of the value the winner wrote, and runs no factory of its own. `HybridCache`'s internal stampede handling is invisible to the library, so Hybrid waiters carry no `coalesced` tag.

Not measured: L1-vs-L2 attribution inside Hybrid mode. `HybridCache` never tells the caller which tier served a value, so a cache-served Hybrid call does not say whether it came from local memory or Redis. In **Redis** mode this is not a gap — a cache-served call does nothing but talk to Redis, so the total *is* Redis latency.
```

- [ ] **Step 2: Replace the Activities paragraph in `docs/TELEMETRY.md`**

Replace the sentence stating that v2 does not start Activities and that `IncludeKeyHashInTraces` is unused with:

```markdown
## Tracing

One `Activity` per cache call, from `ActivitySource` `Caching.NET` (`CacheInstruments.ActivitySourceName`). Span name is `cache {operation}` — e.g. `cache get_or_create` — with `ActivityKind.Internal`. The call's total duration is the span's own duration.

| Tag | When |
| ---- | ---- |
| `cache.mode`, `cache.operation` | always |
| `cache.served_from` | read-shaped operations |
| `cache.factory_ms` | a factory ran in this call |
| `cache.miss_reason` | a miss reason was determined |
| `cache.hit_count`, `cache.miss_count` | batch operations |
| `cache.coalesced` | the call waited on another caller's stripe lock |
| `cache.error_kind` | a backend error occurred, including swallowed ones |
| `cache.key_hash` | `CacheOptions.IncludeKeyHashInTraces=true`, single-key operations |

Span status is `Error` only when an exception escapes to the caller. Fail-open failures that the library swallowed keep an unset status and carry `cache.error_kind`, so a Redis blip does not mark a successful consumer request as failed. Cancellation is tagged, never marked `Error`.

`cache.key_hash` is `StableStringHash.Compute64(key)` as 16 hex characters. Raw keys never appear on a span, regardless of `IncludeRawKeyInLogs`.

Span volume is the consumer's sampler's business: `StartActivity` returns null when nothing is listening, so the cost with tracing off is a null check.
```

- [ ] **Step 3: Update the instrument tables and tag vocabulary**

In `docs/TELEMETRY.md`, add to the Instruments table:

```markdown
| `cache.factory.duration` | Histogram | `ms` | factory (source) retrieval time; only when a factory ran |
```

and add to the allowed-tags list:

```markdown
- `cache.served_from` ∈ {`cache`, `source`, `mixed`, `none`} — read-shaped operations only
- `cache.coalesced` — `true` when the call waited on another caller's stripe lock (span only)
```

In `docs/features/telemetry.md`, update the `cache.operation.duration` row and add a `cache.factory.duration` row:

```markdown
| `cache.operation.duration` | histogram (ms) | Total per call, one sample, tagged `cache.served_from` ([details](../TELEMETRY.md#cacheoperationduration-and-cachefactoryduration)) |
| `cache.factory.duration` | histogram (ms) | Source (factory) retrieval time, when a factory ran |
```

- [ ] **Step 4: Update the Telemetry section of `CLAUDE.md`**

Replace it with:

```markdown
### Telemetry

Static `CacheInstruments` (`Meter` / `ActivitySource`) — subscribe with `AddMeter(CacheInstruments.MeterName)` / `AddSource(CacheInstruments.ActivitySourceName)`; both names are **`Caching.NET`**. `WithOpenTelemetry()` remains an API-compatibility hook for apps that already call it.

Per-call visibility lives in `RoutingCacheService`, not the backend services: it creates one `CacheCallRecorder` per call, which starts the span, times the call, wraps the caller's factory to time source retrieval, and on dispose emits exactly one `cache.operation.duration` sample (tagged `cache.served_from`) plus `cache.factory.duration` when a factory ran. Backend services must **not** record durations — doing so reintroduces nested double counting. See [docs/TELEMETRY.md](docs/TELEMETRY.md).
```

- [ ] **Step 5: Bump the package version**

In `src/Caching.NET/Caching.NET.csproj`, change `<Version>2.2.0</Version>` to `<Version>2.3.0</Version>`.

- [ ] **Step 6: Verify docs match the code**

Run:
```bash
grep -rn "does not start Activities" docs/ || echo "stale claim removed"
grep -rn "MeasureDuration\|OperationTimer" src/ docs/ || echo "no stale references"
```

Expected: both echo their success message.

- [ ] **Step 7: Full verification before the final commit**

Run:
```bash
dotnet build src/Caching.NET/Caching.NET.csproj
dotnet test tests/Caching.NET.Tests/Caching.NET.Tests.csproj
dotnet test tests/Caching.NET.Tests.Properties/Caching.NET.Tests.Properties.csproj
dotnet test tests/Caching.NET.Tests.Integration/Caching.NET.Tests.Integration.csproj -f net10.0
dotnet test tests/Caching.NET.Tests.Chaos/Caching.NET.Tests.Chaos.csproj -f net10.0
```

Expected: build with 0 warnings / 0 errors; all four suites pass. Note `dotnet build` on the whole solution currently fails on `samples/Caching.NET.Sample` with `NU1903` (`Microsoft.OpenApi` 2.0.0 vulnerability advisory promoted to an error). That failure predates this work and is unrelated — do not attempt to fix it here, and do not treat it as a regression.

- [ ] **Step 8: Commit**

```bash
git add docs/TELEMETRY.md docs/features/telemetry.md CLAUDE.md src/Caching.NET/Caching.NET.csproj
git commit -m "docs(telemetry): document per-call visibility, bump to 2.3.0"
```

---

## Plan Self-Review

**Spec coverage:**

| Spec requirement | Task |
| ---- | ---- |
| `cache.factory.duration` instrument | 1 |
| `cache.served_from` tag + 4-arg `RecordDuration` | 1 |
| `CacheCallRecorder` with the full method contract | 2 |
| `served_from` resolution table (all four values, write omission) | 2 |
| Span name/kind/tags/status rules, key-hash opt-in | 2 |
| Factory accumulation across multiple invocations | 2 |
| Double-dispose safety | 2 |
| Delete `MeasureDuration`/`OperationTimer` | 3 |
| Remove recording from all three backend services | 3 |
| Recording consolidated at Routing for `get_or_create` | 4 |
| `cache.mode` = resolved backend, `Routing` on short-circuits | 4 (`ModeNameOf`) |
| Disabled / bypass / rejected key / force refresh coverage | 4 |
| Coalesced detection via incomplete `WaitAsync` | 4 |
| Escaping-exception span status | 4 (and every entry point in 5, 6) |
| `get`, `get(Type)`, `exists`, `refresh`, `set`, `remove`, `remove_by_tag`, `clear` | 5 |
| `get_many` counts, `set_many`, `remove_many` | 6 |
| Background stale refresh with `operation=stale_refresh`, swallowed errors | 7 |
| Redis + Hybrid end-to-end | 8 |
| Docs (TELEMETRY, features, CLAUDE) + version bump | 9 |
| Non-goal: Hybrid L1/L2 tier | documented in Task 9, Step 1 |

Two spec items are intentionally **not** separate tasks: the `MaximumKeyLength` path is covered by the `KeyRejected` marking in `TryPreparePrefixedKey` (Routing applies the length cap there, so no extra code is needed), and the nested-cache-call-inside-a-factory case needs no implementation — each call creates its own recorder, and the recorder unit tests plus `Miss_then_hit_reports_source_then_cache` exercise the mechanism.

**Placeholder scan:** No "TBD"/"TODO"/"handle edge cases"/"similar to Task N". Every code step carries the actual code. Task 8 Step 1 asks the implementer to read the existing fixture and substitute its real type name — that is a deliberate lookup, not a placeholder, because inventing a fixture name would produce code that does not compile.

**Type consistency:** `CacheCallRecorder` method names are identical everywhere they appear (Task 2 definition, Tasks 4–7 usage): `Start`, `WrapFactory`, `SetMode`, `MarkServedFromCache`, `MarkNotFound`, `MarkBatch`, `MarkCoalesced`, `MarkMissReason`, `MarkError`, `Dispose`. `ModeNameOf` is defined in Task 4 and used in Tasks 5–7. `CacheInstruments.RecordFactoryDuration` and the 4-arg `RecordDuration` match their Task 1 signatures. `ActivityListenerHelpers.Capture()` and the `Tag(this Activity, string)` extension are defined in Task 2 and used in Tasks 4–7; Task 8 inlines its own copy because the Integration project does not reference the unit-test project.
