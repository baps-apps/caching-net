# Caching.NET v2 — P1 Observability & Envelope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land all P1 deliverables from `docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md` §6 (envelope) + §7 (observability) — full OTel instrument set, miss-reason taxonomy, IMemoryCache eviction listener, payload histogram, `PayloadEnvelope` with schema-drift detection, `LoggerMessage` source-gen, cardinality Roslyn analyzer.

**Architecture:** Layer onto P0 (already shipped on branch `vpatel/v2`). All Redis writes wrap payload in a 17-byte `PayloadEnvelope` (magic + format-id + 64-bit type hash + length + bytes). On read, magic / format / schema mismatches return miss with a typed `cache.miss_reason` tag and bump `cache.schema_drift`. `CacheInstruments` gains `Evictions`, `StaleServed`, `CircuitStateChanges`, `SchemaDrift`, `PayloadBytes`, `StaleRefreshInFlight`. `InMemoryCacheService` wires `PostEvictionCallbacks`. Polly circuit-state callbacks wire to telemetry. All logging migrates to `LoggerMessage` source-gen with stable `EventId` ranges. A new `Caching.NET.Analyzers` project ships as analyzer-only ref, blocking `cache.key`/`tenant`/`user_id` tag adds at compile time.

**Tech Stack:** .NET 8/9/10 multi-target, `System.Diagnostics.Metrics`, `System.IO.Hashing.XxHash64`, `Microsoft.Extensions.Logging.Abstractions` source-gen, `Microsoft.CodeAnalysis.CSharp` for the analyzer, xUnit + Moq.

**Pre-flight (one-time, not per-task):**
- Branch: `vpatel/v2` (continue on existing P0 branch)
- Verify P0 baseline: `dotnet test` → all 88 tests pass before starting Task 1.
- All commit subjects use the form `feat(p1): <area> — <action>` or `fix(p1): …` so the v2.0.0 changelog can grep them.

**Doc / behavior sync (2026-05-06):** Beyond the original P1 instrument set, production code now includes **`cache.serialize.duration` / `cache.deserialize.duration`** (tag `cache.format`), **`PayloadEnvelope.TryRead` strict length** (trailing bytes → invalid), **`Write(..., IBufferWriter<byte>)`**, and **`DriftLogSampler`** for envelope/schema drift **logs**. See [TELEMETRY.md](../../TELEMETRY.md) and [INTERNALS.md](../../INTERNALS.md).

---

## File Structure

**Create:**
- `src/Caching.NET/Internal/StableTypeHash.cs` — 64-bit xxHash64 of an assembly-qualified type name (envelope schema-hash source).
- `src/Caching.NET/Serialization/PayloadEnvelope.cs` — public static `Write`/`TryRead` helpers; envelope wire format.
- `src/Caching.NET/Serialization/PayloadEnvelopeReadResult.cs` — public enum `Ok | EnvelopeInvalid | FormatDrift | SchemaDrift`.
- `src/Caching.NET/Internal/CacheLogMessages.cs` — partial static class with `[LoggerMessage]` definitions; source-gen produces zero-alloc loggers.
- `src/Caching.NET.Analyzers/Caching.NET.Analyzers.csproj` — netstandard2.0 analyzer project.
- `src/Caching.NET.Analyzers/CardinalityAnalyzer.cs` — `DiagnosticAnalyzer` that flags forbidden tag-name string literals.
- `src/Caching.NET.Analyzers/AnalyzerReleases.Shipped.md` — release-tracking file (empty list).
- `src/Caching.NET.Analyzers/AnalyzerReleases.Unshipped.md` — release-tracking file (CN0001 entry).
- `tests/Caching.NET.Tests.Analyzers/Caching.NET.Tests.Analyzers.csproj` — analyzer test project.
- `tests/Caching.NET.Tests.Analyzers/CardinalityAnalyzerTests.cs` — Microsoft.CodeAnalysis.CSharp.Analyzer.Testing harness tests.
- `tests/Caching.NET.Tests/Internal/StableTypeHashTests.cs`
- `tests/Caching.NET.Tests/Serialization/PayloadEnvelopeTests.cs`
- `tests/Caching.NET.Tests/Services/InMemoryEvictionTests.cs`
- `tests/Caching.NET.Tests/Resilience/CircuitStateChangeTests.cs`

**Modify:**
- `Directory.Packages.props` — add `Microsoft.CodeAnalysis.CSharp 4.11.0`, `Microsoft.CodeAnalysis.CSharp.Workspaces 4.11.0`, `Microsoft.CodeAnalysis.Analyzer.Testing 1.1.2`, `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing 1.1.2`, `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit 1.1.2`, `System.IO.Hashing 8.0.0`.
- `Caching.NET.sln` — add `Caching.NET.Analyzers` and `Caching.NET.Tests.Analyzers` projects.
- `src/Caching.NET/Caching.NET.csproj` — reference analyzer project as `Analyzer` asset, add `System.IO.Hashing` PackageReference.
- `src/Caching.NET/Telemetry/CacheInstruments.cs` — add Evictions / StaleServed / CircuitStateChanges / SchemaDrift / PayloadBytes counters + StaleRefreshInFlight UpDownCounter + PayloadBytes histogram + Record helpers.
- `src/Caching.NET/Options/CacheOptions.cs` — `MissReasons` constants reference (no schema change).
- `src/Caching.NET/Internal/CacheLogEvents.cs` — renumber per spec ranges (1000–1099 info, 1100–1199 warn, 1200–1299 error); add envelope events.
- `src/Caching.NET/Internal/StableStringHash.cs` — add `Compute64(string)` returning `ulong` for type-hash usage.
- `src/Caching.NET/Services/InMemoryCacheService.cs` — `PostEvictionCallbacks` wiring → `CacheInstruments.RecordEviction`; switch logger to source-gen.
- `src/Caching.NET/Services/RedisCacheService.cs` — wrap Set in envelope, decode on Get, emit `RecordPayloadBytes` + drift counters with explicit miss reasons; switch to source-gen logger.
- `src/Caching.NET/Services/HybridCacheService.cs` — switch to source-gen logger.
- `src/Caching.NET/Services/RoutingCacheService.cs` — pass explicit miss reasons (`Bypass`, `Disabled`, `NotFound`) consistently; switch to source-gen logger.
- `src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs` — wire `OnOpened`/`OnClosed`/`OnHalfOpened` callbacks → `CacheInstruments.RecordCircuitStateChange`.
- `src/Caching.NET/PublicAPI.Unshipped.txt` — register new public surface (`PayloadEnvelope`, `PayloadEnvelopeReadResult`, new `CacheInstruments.Record*` methods).
- `tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs` — extend with new-instrument assertions.
- `tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs` — assert envelope round-trip + drift miss reasons.

---

## Task 1: 64-bit stable hash for type names

**Files:**
- Modify: `src/Caching.NET/Internal/StableStringHash.cs`
- Create: `src/Caching.NET/Internal/StableTypeHash.cs`
- Test: `tests/Caching.NET.Tests/Internal/StableTypeHashTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Caching.NET.Tests/Internal/StableTypeHashTests.cs
using Caching.NET.Internal;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class StableTypeHashTests
{
    [Fact]
    public void Compute_for_same_type_returns_same_hash()
    {
        Assert.Equal(StableTypeHash.Compute<string>(), StableTypeHash.Compute<string>());
    }

    [Fact]
    public void Compute_for_different_types_returns_different_hashes()
    {
        Assert.NotEqual(StableTypeHash.Compute<string>(), StableTypeHash.Compute<int>());
    }

    [Fact]
    public void Compute_uses_assembly_qualified_name()
    {
        // The goal is stability across runs — assembly-qualified name is the input.
        var expected = StableStringHash.Compute64(typeof(string).AssemblyQualifiedName!);
        Assert.Equal(expected, StableTypeHash.Compute<string>());
    }

    [Fact]
    public void Compute64_for_empty_string_is_deterministic()
    {
        Assert.Equal(StableStringHash.Compute64(""), StableStringHash.Compute64(""));
    }

    [Fact]
    public void Compute64_for_different_inputs_differs()
    {
        Assert.NotEqual(StableStringHash.Compute64("alpha"), StableStringHash.Compute64("beta"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~StableTypeHashTests -f net10.0`
Expected: FAIL — `StableStringHash.Compute64` and `StableTypeHash.Compute<T>` do not exist.

- [ ] **Step 3: Add Compute64 to StableStringHash**

Append to `src/Caching.NET/Internal/StableStringHash.cs` inside the existing `internal static partial class StableStringHash` (or `internal static class` — match what the file already declares):

```csharp
    /// <summary>
    /// 64-bit deterministic hash of a UTF-8 string. Backed by <see cref="System.IO.Hashing.XxHash64"/>.
    /// Stable across processes/runtimes (NOT randomized like <see cref="string.GetHashCode()"/>).
    /// </summary>
    public static ulong Compute64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return System.IO.Hashing.XxHash64.HashToUInt64(ReadOnlySpan<byte>.Empty);
        Span<byte> buf = value.Length <= 256
            ? stackalloc byte[System.Text.Encoding.UTF8.GetMaxByteCount(value.Length)]
            : new byte[System.Text.Encoding.UTF8.GetByteCount(value)];
        int written = System.Text.Encoding.UTF8.GetBytes(value, buf);
        return System.IO.Hashing.XxHash64.HashToUInt64(buf[..written]);
    }
```

If `StableStringHash` is currently `internal static class` (not partial), keep the existing `Compute(string)` method intact and add `Compute64` next to it.

- [ ] **Step 4: Add System.IO.Hashing dependency**

Edit `Directory.Packages.props` — inside the existing `<ItemGroup>` add:

```xml
<PackageVersion Include="System.IO.Hashing" Version="8.0.0" />
```

Edit `src/Caching.NET/Caching.NET.csproj` — inside the existing `<ItemGroup>` of package references add:

```xml
<PackageReference Include="System.IO.Hashing" />
```

- [ ] **Step 5: Create StableTypeHash**

```csharp
// src/Caching.NET/Internal/StableTypeHash.cs
namespace Caching.NET.Internal;

/// <summary>
/// Deterministic 64-bit hash of a CLR type's assembly-qualified name.
/// Used by the payload envelope to detect schema drift across deploys.
/// </summary>
internal static class StableTypeHash
{
    public static ulong Compute<T>() => Cache<T>.Value;

    private static class Cache<T>
    {
        public static readonly ulong Value = StableStringHash.Compute64(typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name);
    }
}
```

- [ ] **Step 6: Run tests to verify pass**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~StableTypeHashTests`
Expected: PASS — 5 tests.

- [ ] **Step 7: Run full suite to confirm no regression**

Run: `dotnet test`
Expected: PASS — 88 + 5 = 93 tests.

- [ ] **Step 8: Commit**

```bash
git add src/Caching.NET/Internal/StableStringHash.cs src/Caching.NET/Internal/StableTypeHash.cs src/Caching.NET/Caching.NET.csproj Directory.Packages.props tests/Caching.NET.Tests/Internal/StableTypeHashTests.cs
git commit -m "feat(p1): add 64-bit type hash for envelope schema-drift detection"
```

---

## Task 2: PayloadEnvelope wire format (encode/decode)

**Files:**
- Create: `src/Caching.NET/Serialization/PayloadEnvelopeReadResult.cs`
- Create: `src/Caching.NET/Serialization/PayloadEnvelope.cs`
- Test: `tests/Caching.NET.Tests/Serialization/PayloadEnvelopeTests.cs`

Wire format (matches spec §6):

```
Offset 0–3   : magic "CN20" (ASCII bytes 0x43 0x4E 0x32 0x30)
Offset 4     : FormatId byte — 0x01=json, 0x02=msgpack, 0xFF=custom (followed by 1-byte length + UTF-8 string)
Offset 5–12  : SchemaHash uint64 little-endian
Offset 13–16 : PayloadLen uint32 little-endian
Offset 17+   : Payload bytes
```

Total fixed header for built-in formats: 17 bytes.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Caching.NET.Tests/Serialization/PayloadEnvelopeTests.cs
using Caching.NET.Internal;
using Caching.NET.Serialization;
using Xunit;

namespace Caching.NET.Tests.Serialization;

public class PayloadEnvelopeTests
{
    private const byte FormatJson = 0x01;
    private const byte FormatMsgPack = 0x02;
    private const byte FormatCustom = 0xFF;

    [Fact]
    public void Write_emits_magic_format_hash_length_payload()
    {
        ReadOnlySpan<byte> payload = stackalloc byte[] { 1, 2, 3, 4 };
        var schema = StableTypeHash.Compute<int>();

        byte[] wire = PayloadEnvelope.Write(payload, FormatJson, schema);

        Assert.Equal((byte)'C', wire[0]);
        Assert.Equal((byte)'N', wire[1]);
        Assert.Equal((byte)'2', wire[2]);
        Assert.Equal((byte)'0', wire[3]);
        Assert.Equal(FormatJson, wire[4]);
        Assert.Equal(schema, BitConverter.ToUInt64(wire, 5));
        Assert.Equal((uint)payload.Length, BitConverter.ToUInt32(wire, 13));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, wire[17..]);
    }

    [Fact]
    public void Roundtrip_returns_payload_for_matching_format_and_schema()
    {
        ReadOnlySpan<byte> payload = stackalloc byte[] { 9, 8, 7 };
        var schema = StableTypeHash.Compute<string>();
        byte[] wire = PayloadEnvelope.Write(payload, FormatJson, schema);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, schema, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.Ok, result);
        Assert.Equal(new byte[] { 9, 8, 7 }, decoded.ToArray());
    }

    [Fact]
    public void TryRead_with_bad_magic_returns_EnvelopeInvalid()
    {
        var wire = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        var result = PayloadEnvelope.TryRead(wire, 0x01, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_short_buffer_returns_EnvelopeInvalid()
    {
        var wire = new byte[] { (byte)'C', (byte)'N', (byte)'2', (byte)'0' }; // header only, no rest

        var result = PayloadEnvelope.TryRead(wire, 0x01, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_format_mismatch_returns_FormatDrift()
    {
        byte[] wire = PayloadEnvelope.Write(new byte[] { 1 }, FormatJson, schemaHash: 7UL);

        var result = PayloadEnvelope.TryRead(wire, FormatMsgPack, expectedSchemaHash: 7UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.FormatDrift, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_schema_mismatch_returns_SchemaDrift()
    {
        byte[] wire = PayloadEnvelope.Write(new byte[] { 1 }, FormatJson, schemaHash: 7UL);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, expectedSchemaHash: 99UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.SchemaDrift, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_payload_length_overflow_returns_EnvelopeInvalid()
    {
        // header claims 1 GiB but buffer has 17+0 bytes
        var wire = new byte[17];
        "CN20"u8.CopyTo(wire);
        wire[4] = FormatJson;
        BitConverter.GetBytes(0UL).CopyTo(wire, 5);
        BitConverter.GetBytes(1_073_741_824u).CopyTo(wire, 13);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.EnvelopeInvalid, result);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void TryRead_with_zero_length_payload_returns_Ok_empty()
    {
        byte[] wire = PayloadEnvelope.Write(ReadOnlySpan<byte>.Empty, FormatJson, schemaHash: 0UL);

        var result = PayloadEnvelope.TryRead(wire, FormatJson, 0UL, out var decoded);

        Assert.Equal(PayloadEnvelopeReadResult.Ok, result);
        Assert.True(decoded.IsEmpty);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~PayloadEnvelopeTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create PayloadEnvelopeReadResult enum**

```csharp
// src/Caching.NET/Serialization/PayloadEnvelopeReadResult.cs
namespace Caching.NET.Serialization;

/// <summary>
/// Outcome of <see cref="PayloadEnvelope.TryRead"/>. All non-Ok results indicate the wire bytes are unusable
/// and the consumer should treat the read as a miss with the corresponding miss-reason tag.
/// </summary>
public enum PayloadEnvelopeReadResult
{
    /// <summary>Envelope decoded; payload is valid for the expected format and schema.</summary>
    Ok = 0,

    /// <summary>Magic bytes wrong, header truncated, or payload-length larger than buffer.</summary>
    EnvelopeInvalid = 1,

    /// <summary>FormatId in envelope differs from caller's expected format (e.g. cached as MessagePack, reading as JSON).</summary>
    FormatDrift = 2,

    /// <summary>SchemaHash differs from expected — the cached DTO type changed since the entry was written.</summary>
    SchemaDrift = 3,
}
```

- [ ] **Step 4: Create PayloadEnvelope encode/decode**

```csharp
// src/Caching.NET/Serialization/PayloadEnvelope.cs
using System.Buffers.Binary;

namespace Caching.NET.Serialization;

/// <summary>
/// Wire-level wrapper for cached payloads. Layout: 4-byte magic "CN20" + 1-byte FormatId
/// + 8-byte little-endian schema hash + 4-byte little-endian payload length + payload bytes.
/// Used by Redis-backed services so schema drift, format swaps, and corrupt entries surface
/// as misses rather than runtime exceptions.
/// </summary>
public static class PayloadEnvelope
{
    /// <summary>ASCII "CN20" — magic prefix identifying a Caching.NET v2 envelope.</summary>
    public static ReadOnlySpan<byte> Magic => "CN20"u8;

    /// <summary>Fixed header size in bytes for built-in (non-custom) format ids.</summary>
    public const int HeaderSize = 17;

    /// <summary>FormatId for the built-in JSON serializer.</summary>
    public const byte FormatIdJson = 0x01;

    /// <summary>FormatId for the built-in MessagePack serializer.</summary>
    public const byte FormatIdMsgPack = 0x02;

    /// <summary>Allocate a wire-format byte[] containing the envelope and payload.</summary>
    /// <param name="payload">Serialized payload bytes (empty allowed).</param>
    /// <param name="formatId">Caller's serializer FormatId byte.</param>
    /// <param name="schemaHash">64-bit type-stable hash (typically <see cref="Caching.NET.Internal.StableTypeHash.Compute{T}"/>).</param>
    public static byte[] Write(ReadOnlySpan<byte> payload, byte formatId, ulong schemaHash)
    {
        var wire = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(wire);
        wire[4] = formatId;
        BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(5, 8), schemaHash);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(13, 4), (uint)payload.Length);
        if (!payload.IsEmpty)
            payload.CopyTo(wire.AsSpan(HeaderSize));
        return wire;
    }

    /// <summary>
    /// Validate magic + format + schema and return the inner payload.
    /// On any mismatch returns a non-Ok result and an empty span — callers MUST treat that as a miss.
    /// </summary>
    public static PayloadEnvelopeReadResult TryRead(
        ReadOnlySpan<byte> wire,
        byte expectedFormatId,
        ulong expectedSchemaHash,
        out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (wire.Length < HeaderSize) return PayloadEnvelopeReadResult.EnvelopeInvalid;
        if (!wire[..4].SequenceEqual(Magic)) return PayloadEnvelopeReadResult.EnvelopeInvalid;

        var len = BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(13, 4));
        if (len > (uint)(wire.Length - HeaderSize)) return PayloadEnvelopeReadResult.EnvelopeInvalid;

        var format = wire[4];
        if (format != expectedFormatId) return PayloadEnvelopeReadResult.FormatDrift;

        var schema = BinaryPrimitives.ReadUInt64LittleEndian(wire.Slice(5, 8));
        if (schema != expectedSchemaHash) return PayloadEnvelopeReadResult.SchemaDrift;

        payload = wire.Slice(HeaderSize, (int)len);
        return PayloadEnvelopeReadResult.Ok;
    }
}
```

- [ ] **Step 5: Add public surface to PublicAPI.Unshipped.txt**

Append to `src/Caching.NET/PublicAPI.Unshipped.txt` (alphabetical position):

```
Caching.NET.Serialization.PayloadEnvelope
Caching.NET.Serialization.PayloadEnvelopeReadResult
Caching.NET.Serialization.PayloadEnvelopeReadResult.EnvelopeInvalid = 1 -> Caching.NET.Serialization.PayloadEnvelopeReadResult
Caching.NET.Serialization.PayloadEnvelopeReadResult.FormatDrift = 2 -> Caching.NET.Serialization.PayloadEnvelopeReadResult
Caching.NET.Serialization.PayloadEnvelopeReadResult.Ok = 0 -> Caching.NET.Serialization.PayloadEnvelopeReadResult
Caching.NET.Serialization.PayloadEnvelopeReadResult.SchemaDrift = 3 -> Caching.NET.Serialization.PayloadEnvelopeReadResult
const Caching.NET.Serialization.PayloadEnvelope.FormatIdJson = 1 -> byte
const Caching.NET.Serialization.PayloadEnvelope.FormatIdMsgPack = 2 -> byte
const Caching.NET.Serialization.PayloadEnvelope.HeaderSize = 17 -> int
static Caching.NET.Serialization.PayloadEnvelope.Magic.get -> System.ReadOnlySpan<byte>
static Caching.NET.Serialization.PayloadEnvelope.TryRead(System.ReadOnlySpan<byte> wire, byte expectedFormatId, ulong expectedSchemaHash, out System.ReadOnlySpan<byte> payload) -> Caching.NET.Serialization.PayloadEnvelopeReadResult
static Caching.NET.Serialization.PayloadEnvelope.Write(System.ReadOnlySpan<byte> payload, byte formatId, ulong schemaHash) -> byte[]!
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~PayloadEnvelopeTests`
Expected: PASS — 8 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Caching.NET/Serialization/PayloadEnvelope.cs src/Caching.NET/Serialization/PayloadEnvelopeReadResult.cs src/Caching.NET/PublicAPI.Unshipped.txt tests/Caching.NET.Tests/Serialization/PayloadEnvelopeTests.cs
git commit -m "feat(p1): add PayloadEnvelope wire format with drift detection"
```

---

## Task 3: Expand CacheInstruments with new instruments

Add the missing instruments from spec §7: `Evictions`, `StaleServed`, `CircuitStateChanges`, `SchemaDrift`, `PayloadBytes` (Histogram<long>), `StaleRefreshInFlight` (UpDownCounter<long>). Add `Record*` helpers.

**Files:**
- Modify: `src/Caching.NET/Telemetry/CacheInstruments.cs`
- Modify: `src/Caching.NET/PublicAPI.Unshipped.txt`
- Test: `tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/Caching.NET.Tests/Telemetry/CacheInstrumentsTests.cs` (the file already exists from P0). Use the existing `MeterListenerHelpers` from P0:

```csharp
    [Fact]
    public void RecordEviction_emits_evictions_counter_with_reason_tag()
    {
        var modeTag = $"unit-evict-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.evictions", modeTag);
        using var _ = listener;

        CacheInstruments.RecordEviction(modeTag, "Capacity");

        Assert.Single(values);
        Assert.Equal(1L, values[0].value);
        Assert.Contains(values[0].tags, t => t.Key == "cache.eviction_reason" && (string?)t.Value == "Capacity");
    }

    [Fact]
    public void RecordStaleServed_emits_stale_served_counter()
    {
        var modeTag = $"unit-stale-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.stale_served", modeTag);
        using var _ = listener;

        CacheInstruments.RecordStaleServed(modeTag, "get_or_create");

        Assert.Single(values);
    }

    [Fact]
    public void RecordCircuitStateChange_emits_counter_with_state_tag()
    {
        var modeTag = $"unit-cb-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.circuit_state_changes", modeTag);
        using var _ = listener;

        CacheInstruments.RecordCircuitStateChange(modeTag, "cache.redis.read", "open");

        Assert.Single(values);
        Assert.Contains(values[0].tags, t => t.Key == "cache.circuit_state" && (string?)t.Value == "open");
    }

    [Fact]
    public void RecordSchemaDrift_emits_counter_with_kind_tag()
    {
        var modeTag = $"unit-drift-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.schema_drift", modeTag);
        using var _ = listener;

        CacheInstruments.RecordSchemaDrift(modeTag, "schema_drift");

        Assert.Single(values);
        Assert.Contains(values[0].tags, t => t.Key == "cache.drift_kind" && (string?)t.Value == "schema_drift");
    }

    [Fact]
    public void RecordPayloadBytes_emits_histogram()
    {
        var modeTag = $"unit-bytes-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.CaptureHistogram<long>("cache.payload.bytes", modeTag);
        using var _ = listener;

        CacheInstruments.RecordPayloadBytes(modeTag, "set", 4096);

        Assert.Single(values);
        Assert.Equal(4096L, values[0].value);
    }

    [Fact]
    public void TrackStaleRefresh_emits_updowncounter_increments_and_decrements()
    {
        var modeTag = $"unit-srf-{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.CaptureUpDownCounter<long>("cache.stale_refresh.in_flight", modeTag);
        using var _ = listener;

        CacheInstruments.AddStaleRefreshInFlight(modeTag, 1);
        CacheInstruments.AddStaleRefreshInFlight(modeTag, -1);

        Assert.Equal(2, values.Count);
        Assert.Equal(1L, values[0].value);
        Assert.Equal(-1L, values[1].value);
    }
```

If `MeterListenerHelpers` does not yet expose `CaptureUpDownCounter<T>`, extend it with this method (alongside the existing `CaptureCounter`/`CaptureHistogram`):

```csharp
public static (List<(T value, KeyValuePair<string, object?>[] tags)> values, MeterListener listener)
    CaptureUpDownCounter<T>(string instrumentName, string modeTag) where T : struct
{
    var values = new List<(T value, KeyValuePair<string, object?>[] tags)>();
    var listener = new MeterListener
    {
        InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "Caching.NET" && instr.Name == instrumentName)
                l.EnableMeasurementEvents(instr);
        }
    };
    listener.SetMeasurementEventCallback<T>((instr, value, tags, _) =>
    {
        foreach (var t in tags)
            if (t.Key == "cache.mode" && (string?)t.Value == modeTag)
            {
                values.Add((value, tags.ToArray()));
                return;
            }
    });
    listener.Start();
    return (values, listener);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheInstrumentsTests`
Expected: FAIL — `RecordEviction`, `RecordStaleServed`, `RecordCircuitStateChange`, `RecordSchemaDrift`, `RecordPayloadBytes`, `AddStaleRefreshInFlight` do not exist.

- [ ] **Step 3: Add new instruments and Record helpers**

Edit `src/Caching.NET/Telemetry/CacheInstruments.cs`. After the existing `OperationDuration` declaration add:

```csharp
    internal static readonly Counter<long> EvictionsCounter =
        Meter.CreateCounter<long>("cache.evictions", unit: "{entry}", description: "Cache entry evictions.");
    internal static readonly Counter<long> StaleServedCounter =
        Meter.CreateCounter<long>("cache.stale_served", unit: "{op}", description: "Stale entries served while a background refresh ran.");
    internal static readonly Counter<long> CircuitStateChangesCounter =
        Meter.CreateCounter<long>("cache.circuit_state_changes", unit: "{event}", description: "Polly circuit-breaker state transitions.");
    internal static readonly Counter<long> SchemaDriftCounter =
        Meter.CreateCounter<long>("cache.schema_drift", unit: "{event}", description: "Envelope/format/schema drift events on read.");

    internal static readonly Histogram<long> PayloadBytesHistogram =
        Meter.CreateHistogram<long>("cache.payload.bytes", unit: "By", description: "Serialized payload size in bytes.");

    internal static readonly UpDownCounter<long> StaleRefreshInFlightCounter =
        Meter.CreateUpDownCounter<long>("cache.stale_refresh.in_flight", unit: "{task}", description: "Background stale-refresh tasks in flight.");
```

After the existing `RecordDuration` method add:

```csharp
    /// <summary>Record an IMemoryCache eviction with a reason tag (Expired/Capacity/Replaced/Removed/TokenExpired).</summary>
    public static void RecordEviction(string mode, string evictionReason)
        => EvictionsCounter.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.eviction_reason", evictionReason));

    /// <summary>Record a stale entry served while a background refresh ran.</summary>
    public static void RecordStaleServed(string mode, string operation)
        => StaleServedCounter.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Record a Polly circuit-breaker state transition (state ∈ open|half-open|closed).</summary>
    public static void RecordCircuitStateChange(string mode, string pipeline, string circuitState)
        => CircuitStateChangesCounter.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.pipeline", pipeline),
            new KeyValuePair<string, object?>("cache.circuit_state", circuitState));

    /// <summary>Record an envelope/format/schema drift event observed during a read (driftKind ∈ envelope_invalid|format_drift|schema_drift).</summary>
    public static void RecordSchemaDrift(string mode, string driftKind)
        => SchemaDriftCounter.Add(1,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.drift_kind", driftKind));

    /// <summary>Record a serialized payload size on read or write.</summary>
    public static void RecordPayloadBytes(string mode, string operation, long bytes)
        => PayloadBytesHistogram.Record(bytes,
            new KeyValuePair<string, object?>("cache.mode", mode),
            new KeyValuePair<string, object?>("cache.operation", operation));

    /// <summary>Increment or decrement the in-flight stale-refresh counter.</summary>
    public static void AddStaleRefreshInFlight(string mode, long delta)
        => StaleRefreshInFlightCounter.Add(delta,
            new KeyValuePair<string, object?>("cache.mode", mode));
```

- [ ] **Step 4: Update PublicAPI.Unshipped.txt**

Append to `src/Caching.NET/PublicAPI.Unshipped.txt`:

```
static Caching.NET.Telemetry.CacheInstruments.AddStaleRefreshInFlight(string! mode, long delta) -> void
static Caching.NET.Telemetry.CacheInstruments.RecordCircuitStateChange(string! mode, string! pipeline, string! circuitState) -> void
static Caching.NET.Telemetry.CacheInstruments.RecordEviction(string! mode, string! evictionReason) -> void
static Caching.NET.Telemetry.CacheInstruments.RecordPayloadBytes(string! mode, string! operation, long bytes) -> void
static Caching.NET.Telemetry.CacheInstruments.RecordSchemaDrift(string! mode, string! driftKind) -> void
static Caching.NET.Telemetry.CacheInstruments.RecordStaleServed(string! mode, string! operation) -> void
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheInstrumentsTests`
Expected: PASS — 6 new tests + existing tests.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Telemetry/CacheInstruments.cs src/Caching.NET/PublicAPI.Unshipped.txt tests/Caching.NET.Tests/Telemetry/
git commit -m "feat(p1): add evictions, stale, circuit, drift, payload-bytes instruments"
```

---

## Task 4: Wire envelope into RedisCacheService write path + payload-bytes histogram

Wrap every Redis `Set` in `PayloadEnvelope.Write`. Emit `RecordPayloadBytes` after successful write.

**Files:**
- Modify: `src/Caching.NET/Services/RedisCacheService.cs`
- Test: `tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs`:

```csharp
    [Fact]
    public async Task SetAsync_writes_envelope_wrapped_payload_to_distributed_cache()
    {
        var distributed = new Mock<IDistributedCache>();
        byte[]? captured = null;
        distributed
            .Setup(d => d.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, b, _, _) => captured = b)
            .Returns(Task.CompletedTask);

        var service = BuildService(distributed.Object); // existing test helper

        await service.SetAsync("orders-svc:Order:1", new TestDto { Id = 1 });

        Assert.NotNull(captured);
        Assert.True(captured!.Length >= PayloadEnvelope.HeaderSize);
        Assert.True(captured.AsSpan(0, 4).SequenceEqual("CN20"u8));
        Assert.Equal(PayloadEnvelope.FormatIdJson, captured[4]);
    }

    private sealed class TestDto { public int Id { get; init; } }
```

If `BuildService` does not exist, use the helpers already present in the file. Match the pattern of existing tests.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~RedisCacheServiceTests.SetAsync_writes_envelope_wrapped_payload`
Expected: FAIL — payload is bare bytes, magic prefix missing.

- [ ] **Step 3: Modify SetAsync to write envelope + emit payload-bytes histogram**

In `src/Caching.NET/Services/RedisCacheService.cs`, change `SetAsync<T>` (around line 124–164). Replace the `bytes = _serializer.Serialize(value);` block and the subsequent write with:

```csharp
        byte[] payload;
        try
        {
            payload = _serializer.Serialize(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(CacheLogEvents.RedisSerializationFailed, ex, "Serialization failed for key {Key}.", TruncateKey(key));
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            CacheInstruments.RecordError(Mode, "serialize", "Serialization");
            return;
        }

        if (_options.Value.MaximumPayloadBytes > 0 && payload.Length > _options.Value.MaximumPayloadBytes)
        {
            _logger.LogWarning(CacheLogEvents.RedisPayloadTooLarge, "Payload for key {Key} exceeds MaximumPayloadBytes ({Size} bytes); not caching.", TruncateKey(key), payload.Length);
            return;
        }

        byte formatId = ResolveFormatId(_serializer.FormatId);
        ulong schemaHash = StableTypeHash.Compute<T>();
        byte[] wire = PayloadEnvelope.Write(payload, formatId, schemaHash);

        try
        {
            using var cts = CreateOpCts(cancellationToken);
            var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
            await _writePipeline.ExecuteAsync(
                async ct => await _cache.SetAsync(key, wire, entryOptions, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            CacheInstruments.RecordSet(Mode);
            CacheInstruments.RecordPayloadBytes(Mode, "set", payload.Length);
        }
        catch (Exception ex)
        {
            if (_options.Value.ThrowOnFailure && !_options.Value.FailOpen) throw;
            _logger.LogError(CacheLogEvents.RedisSetFailed, ex, "Redis set failed for key {Key}.", TruncateKey(key));
            CacheInstruments.RecordError(Mode, "set", ClassifyError(ex));
        }
```

Add the helper near the bottom of the class:

```csharp
    private static byte ResolveFormatId(string formatId) => formatId switch
    {
        "json"    => PayloadEnvelope.FormatIdJson,
        "msgpack" => PayloadEnvelope.FormatIdMsgPack,
        _         => 0xFF,
    };
```

Add the missing usings at the top of the file:

```csharp
using Caching.NET.Internal;
using Caching.NET.Serialization;
```

(Both should already exist — verify.)

- [ ] **Step 4: Run test to verify pass**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~RedisCacheServiceTests.SetAsync_writes_envelope_wrapped_payload`
Expected: PASS.

- [ ] **Step 5: Run full RedisCacheService tests**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~RedisCacheServiceTests`
Expected: PASS — existing roundtrip tests will fail at this point because the read path still expects bare bytes. **Expected failures here.** Note the failures and continue to Task 5.

- [ ] **Step 6: Commit (with Task 5 tests still red)**

```bash
git add src/Caching.NET/Services/RedisCacheService.cs tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs
git commit -m "feat(p1): wrap Redis writes in PayloadEnvelope and emit payload-bytes histogram"
```

---

## Task 5: Decode envelope on Redis read path with miss-reason taxonomy

Read path: try-decode wire bytes; on `EnvelopeInvalid`/`FormatDrift`/`SchemaDrift` emit drift counter + miss-reason and treat as miss.

**Files:**
- Modify: `src/Caching.NET/Services/RedisCacheService.cs`
- Test: `tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs`:

```csharp
    [Fact]
    public async Task GetOrCreateAsync_returns_factory_value_when_envelope_magic_is_invalid()
    {
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

        var service = BuildService(distributed.Object);
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 42 }));

        Assert.Equal(42, result.Id);
    }

    [Fact]
    public async Task GetOrCreateAsync_treats_schema_drift_as_miss_and_runs_factory()
    {
        var oldHash = 0x1234_5678_9ABC_DEF0UL; // intentionally not the real TestDto hash
        var stalePayload = "{\"Id\":99}"u8.ToArray();
        byte[] staleWire = PayloadEnvelope.Write(stalePayload, PayloadEnvelope.FormatIdJson, oldHash);
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleWire);

        var service = BuildService(distributed.Object);
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 7 }));

        Assert.Equal(7, result.Id);
    }

    [Fact]
    public async Task GetOrCreateAsync_returns_cached_value_when_envelope_matches()
    {
        var realHash = StableTypeHash.Compute<TestDto>();
        byte[] wire = PayloadEnvelope.Write("{\"Id\":99}"u8, PayloadEnvelope.FormatIdJson, realHash);
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(wire);

        var service = BuildService(distributed.Object);
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 0 }));

        Assert.Equal(99, result.Id);
    }
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~RedisCacheServiceTests.GetOrCreateAsync"`
Expected: FAIL — read path passes raw bytes through `_serializer.Deserialize` instead of envelope decode.

- [ ] **Step 3: Update read path in RedisCacheService.GetOrCreateAsync**

Replace the inner `if (bytes is { Length: > 0 })` block (around lines 90–98) with:

```csharp
            if (bytes is { Length: > 0 })
            {
                var expectedFormat = ResolveFormatId(_serializer.FormatId);
                var expectedSchema = StableTypeHash.Compute<T>();
                var status = PayloadEnvelope.TryRead(bytes, expectedFormat, expectedSchema, out var payload);
                switch (status)
                {
                    case PayloadEnvelopeReadResult.Ok:
                        var value = _serializer.Deserialize<T>(payload);
                        if (value != null)
                        {
                            CacheInstruments.RecordHit(Mode, "get_or_create");
                            CacheInstruments.RecordPayloadBytes(Mode, "get_or_create", payload.Length);
                            return value;
                        }
                        CacheInstruments.RecordMiss(Mode, "get_or_create", "SerializationFailed");
                        break;
                    case PayloadEnvelopeReadResult.EnvelopeInvalid:
                        _logger.LogWarning(CacheLogEvents.RedisEnvelopeInvalid, "Envelope invalid for key {Key}; treating as miss.", TruncateKey(key));
                        CacheInstruments.RecordMiss(Mode, "get_or_create", "EnvelopeInvalid");
                        CacheInstruments.RecordSchemaDrift(Mode, "envelope_invalid");
                        break;
                    case PayloadEnvelopeReadResult.FormatDrift:
                        _logger.LogWarning(CacheLogEvents.RedisFormatDrift, "Format drift for key {Key}; treating as miss.", TruncateKey(key));
                        CacheInstruments.RecordMiss(Mode, "get_or_create", "EnvelopeInvalid");
                        CacheInstruments.RecordSchemaDrift(Mode, "format_drift");
                        break;
                    case PayloadEnvelopeReadResult.SchemaDrift:
                        _logger.LogWarning(CacheLogEvents.RedisSchemaDrift, "Schema drift for key {Key}; treating as miss.", TruncateKey(key));
                        CacheInstruments.RecordMiss(Mode, "get_or_create", "EnvelopeInvalid");
                        CacheInstruments.RecordSchemaDrift(Mode, "schema_drift");
                        break;
                }
            }
```

Add the new EventIds to `src/Caching.NET/Internal/CacheLogEvents.cs`:

```csharp
    public static readonly EventId RedisEnvelopeInvalid = new(1106, nameof(RedisEnvelopeInvalid));
    public static readonly EventId RedisFormatDrift = new(1107, nameof(RedisFormatDrift));
    public static readonly EventId RedisSchemaDrift = new(1108, nameof(RedisSchemaDrift));
```

- [ ] **Step 4: Run RedisCacheService tests**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~RedisCacheServiceTests`
Expected: PASS — all envelope round-trip + drift tests green.

- [ ] **Step 5: Run full suite**

Run: `dotnet test`
Expected: PASS across net8/9/10.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Services/RedisCacheService.cs src/Caching.NET/Internal/CacheLogEvents.cs tests/Caching.NET.Tests/Services/RedisCacheServiceTests.cs
git commit -m "feat(p1): decode PayloadEnvelope on Redis reads with miss-reason taxonomy"
```

---

## Task 6: IMemoryCache eviction listener

Wire `MemoryCacheEntryOptions.PostEvictionCallbacks` so capacity / expiration / replaced events emit `cache.evictions`.

**Files:**
- Modify: `src/Caching.NET/Services/InMemoryCacheService.cs`
- Test: `tests/Caching.NET.Tests/Services/InMemoryEvictionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Caching.NET.Tests/Services/InMemoryEvictionTests.cs
using Caching.NET.Options;
using Caching.NET.Services;
using Caching.NET.Telemetry;
using Caching.NET.Tests.Telemetry; // MeterListenerHelpers
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Services;

public class InMemoryEvictionTests
{
    [Fact]
    public async Task Removing_entry_emits_eviction_with_reason_Removed()
    {
        var modeTag = "InMemory";
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.evictions", modeTag);
        using var _ = listener;

        using var memory = new MemoryCache(new MemoryCacheOptions());
        var opts = Options.Create(new CacheOptions { KeyPrefix = "evict-test" });
        var service = new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);

        await service.SetAsync("k", "v");
        await service.RemoveAsync("k");

        // PostEvictionCallback runs on a queued work item; pump the thread pool briefly.
        await Task.Delay(50);

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.eviction_reason" && (string?)t.Value == "Removed"));
    }

    [Fact]
    public async Task Expiring_entry_emits_eviction_with_reason_Expired()
    {
        var modeTag = "InMemory";
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.evictions", modeTag);
        using var _ = listener;

        using var memory = new MemoryCache(new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) });
        var opts = Options.Create(new CacheOptions { KeyPrefix = "evict-test" });
        var service = new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);

        await service.SetAsync("k", "v", expiration: TimeSpan.FromMilliseconds(20));
        await Task.Delay(120);
        // Trigger expiration scan with another op
        memory.TryGetValue("k", out _);
        await Task.Delay(50);

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.eviction_reason" && (string?)t.Value == "Expired"));
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~InMemoryEvictionTests`
Expected: FAIL — no eviction callback wired.

- [ ] **Step 3: Wire PostEvictionCallbacks in InMemoryCacheService**

Edit `src/Caching.NET/Services/InMemoryCacheService.cs`. Add a static field for a single shared callback to avoid per-Set allocation:

```csharp
    private static readonly PostEvictionDelegate s_evictionCallback = OnEvicted;

    private static void OnEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        CacheInstruments.RecordEviction(Mode, reason.ToString());
    }
```

Replace each `cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan });` with:

```csharp
        var entry = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expirationSpan };
        entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration { EvictionCallback = s_evictionCallback });
        cache.Set(key, value, entry);
```

There are two such call-sites (in `GetOrCreateAsync` and `SetAsync`). Update both.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~InMemoryEvictionTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Services/InMemoryCacheService.cs tests/Caching.NET.Tests/Services/InMemoryEvictionTests.cs
git commit -m "feat(p1): emit cache.evictions counter from IMemoryCache post-eviction callback"
```

---

## Task 7: Miss-reason taxonomy across RoutingCacheService

Pass explicit `miss_reason` for `Bypass` and `Disabled` paths; pass-through `NotFound` from inner services.

**Files:**
- Modify: `src/Caching.NET/Services/RoutingCacheService.cs`
- Test: `tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs`:

```csharp
    [Fact]
    public async Task GetOrCreateAsync_records_Bypass_miss_when_BypassCache_is_set()
    {
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.misses", "InMemory");
        using var _ = listener;

        var routing = BuildRouting(); // existing helper
        await routing.GetOrCreateAsync(
            "k", _ => Task.FromResult("v"),
            callOptions: new CacheCallOptions { BypassCache = true });

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.miss_reason" && (string?)t.Value == "Bypass"));
    }

    [Fact]
    public async Task GetOrCreateAsync_records_Disabled_miss_when_Enabled_is_false()
    {
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.misses", "InMemory");
        using var _ = listener;

        var routing = BuildRouting(enabled: false);
        await routing.GetOrCreateAsync("k", _ => Task.FromResult("v"));

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.miss_reason" && (string?)t.Value == "Disabled"));
    }
```

If `BuildRouting` does not exist, add a small helper at the top of the test class that constructs `RoutingCacheService` against an in-memory `MemoryCache` per the existing patterns in the file.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter "FullyQualifiedName~RoutingCacheServiceTests.GetOrCreateAsync_records"`
Expected: FAIL — counter emitted with default `NotFound` reason or no reason.

- [ ] **Step 3: Update RoutingCacheService.GetOrCreateAsync**

Edit `src/Caching.NET/Services/RoutingCacheService.cs`. In the `GetOrCreateAsync` body, locate where `Enabled == false` short-circuits and where `BypassCache` short-circuits. Add explicit miss reasons:

```csharp
        if (!_optionsMonitor.CurrentValue.Enabled)
        {
            CacheInstruments.RecordMiss(_resolvedMode.ToString(), "get_or_create", "Disabled");
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        if (callOptions?.BypassCache == true)
        {
            CacheInstruments.RecordMiss(_resolvedMode.ToString(), "get_or_create", "Bypass");
            return await factory(cancellationToken).ConfigureAwait(false);
        }
```

(Method/property names may differ slightly from the snippet — use the actual identifiers in the file. The substantive change is the explicit `"Disabled"` and `"Bypass"` reason strings on the RecordMiss calls.)

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~RoutingCacheServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Services/RoutingCacheService.cs tests/Caching.NET.Tests/Services/RoutingCacheServiceTests.cs
git commit -m "feat(p1): record Bypass and Disabled miss reasons in RoutingCacheService"
```

---

## Task 8: Polly circuit-state-change instrumentation

Hook Polly v8 `CircuitBreakerStrategyOptions.OnOpened` / `OnClosed` / `OnHalfOpened` callbacks → `CacheInstruments.RecordCircuitStateChange` + WARN log.

**Files:**
- Modify: `src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs`
- Test: `tests/Caching.NET.Tests/Resilience/CircuitStateChangeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Caching.NET.Tests/Resilience/CircuitStateChangeTests.cs
using Caching.NET.Resilience;
using Caching.NET.Telemetry;
using Caching.NET.Tests.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Xunit;

namespace Caching.NET.Tests.Resilience;

public class CircuitStateChangeTests
{
    [Fact]
    public async Task Tripping_circuit_emits_open_then_closed_state_changes()
    {
        var modeTag = "Redis";
        var (values, listener) = MeterListenerHelpers.CaptureCounter<long>("cache.circuit_state_changes", modeTag);
        using var _ = listener;

        var registry = CacheResiliencePipelineBuilder.Build(
            new ResiliencePipelineRegistryOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(2),
                BreakDuration = TimeSpan.FromMilliseconds(100),
                RetryCount = 0
            },
            NullLoggerFactory.Instance);

        var pipeline = registry.GetPipeline(ResiliencePipelineNames.RedisRead);

        // Trip the breaker: 2 failed throws above failure-ratio
        for (int i = 0; i < 2; i++)
        {
            try { await pipeline.ExecuteAsync<int>(_ => throw new RedisConnectionException(ConnectionFailureType.None, "fail")); }
            catch { /* expected */ }
        }

        // Sleep past break-duration then make a probe call to close it
        await Task.Delay(200);
        try { await pipeline.ExecuteAsync<int>(_ => ValueTask.FromResult(1)); } catch { }

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.circuit_state" && (string?)t.Value == "open"));
    }
}
```

If `RedisConnectionException` requires StackExchange.Redis namespace, add `using StackExchange.Redis;` at the top.

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CircuitStateChangeTests`
Expected: FAIL — no instrument increment.

- [ ] **Step 3: Wire callbacks in CacheResiliencePipelineBuilder**

Edit `src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs`. Locate the `AddCircuitBreaker(...)` call. Inside the `CircuitBreakerStrategyOptions` instance add the three callbacks (these properties exist on Polly v8 `CircuitBreakerStrategyOptions`):

```csharp
    OnOpened = args =>
    {
        CacheInstruments.RecordCircuitStateChange("Redis", pipelineName, "open");
        return default;
    },
    OnClosed = args =>
    {
        CacheInstruments.RecordCircuitStateChange("Redis", pipelineName, "closed");
        return default;
    },
    OnHalfOpened = args =>
    {
        CacheInstruments.RecordCircuitStateChange("Redis", pipelineName, "half-open");
        return default;
    },
```

`pipelineName` is whatever the local builder method already names the pipeline — pass the exact registry key (`cache.redis.read`, `cache.redis.write`, `cache.redis.delete`). If the existing builder structure does not expose the name to the inner closure, capture it explicitly: `var name = pipelineName;` before the `AddCircuitBreaker` call.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CircuitStateChangeTests`
Expected: PASS — at least one `open` event observed.

- [ ] **Step 5: Run full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Caching.NET/Resilience/CacheResiliencePipelineBuilder.cs tests/Caching.NET.Tests/Resilience/CircuitStateChangeTests.cs
git commit -m "feat(p1): wire Polly circuit-breaker state changes to telemetry"
```

---

## Task 9: LoggerMessage source-gen for hot-path logs

Replace `_logger.LogWarning/LogError` calls in services with source-gen `[LoggerMessage]` partial methods. Renumber EventIds per spec ranges (1000–1099 info, 1100–1199 warn, 1200–1299 error).

**Files:**
- Modify: `src/Caching.NET/Internal/CacheLogEvents.cs` (delete — replaced by source-gen attributes)
- Create: `src/Caching.NET/Internal/CacheLogMessages.cs`
- Modify: `src/Caching.NET/Services/RedisCacheService.cs`, `HybridCacheService.cs`, `InMemoryCacheService.cs`, `RoutingCacheService.cs`

- [ ] **Step 1: Create CacheLogMessages with source-gen**

```csharp
// src/Caching.NET/Internal/CacheLogMessages.cs
using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Source-generated logger messages. EventId ranges:
/// 1000–1099 info, 1100–1199 warn, 1200–1299 error.
/// </summary>
internal static partial class CacheLogMessages
{
    // 1100–1199 warnings
    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "Redis get failed for key {KeyHash}; executing factory (fail-open).")]
    public static partial void RedisGetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Redis set failed for key {KeyHash}.")]
    public static partial void RedisSetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "Redis remove failed for key {KeyHash}.")]
    public static partial void RedisRemoveFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning, Message = "Key length ({Length}) exceeds MaximumKeyLength ({Max}); skipping cache for {Operation}.")]
    public static partial void RedisKeyTooLong(this ILogger logger, int length, int max, string operation);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Payload for key {KeyHash} exceeds MaximumPayloadBytes ({Size} bytes); not caching.")]
    public static partial void RedisPayloadTooLarge(this ILogger logger, string keyHash, int size);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning, Message = "Hybrid get failed for key {KeyHash}; executing factory (fail-open).")]
    public static partial void HybridGetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning, Message = "Envelope invalid for key {KeyHash}; treating as miss.")]
    public static partial void RedisEnvelopeInvalid(this ILogger logger, string keyHash);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Warning, Message = "Format drift for key {KeyHash}; treating as miss.")]
    public static partial void RedisFormatDrift(this ILogger logger, string keyHash);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Warning, Message = "Schema drift for key {KeyHash}; treating as miss.")]
    public static partial void RedisSchemaDrift(this ILogger logger, string keyHash);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Warning, Message = "Caching option {OptionName} changed at runtime but is startup-only. Restart required.")]
    public static partial void StartupOnlyOptionChanged(this ILogger logger, string optionName);

    // 1200–1299 errors
    [LoggerMessage(EventId = 1200, Level = LogLevel.Error, Message = "Serialization failed for key {KeyHash}.")]
    public static partial void RedisSerializationFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Error, Message = "Hybrid set failed for key {KeyHash}.")]
    public static partial void HybridSetFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error, Message = "Hybrid remove failed for key {KeyHash}.")]
    public static partial void HybridRemoveFailed(this ILogger logger, string keyHash, Exception ex);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Error, Message = "Hybrid tag-remove failed for tag {Tag}.")]
    public static partial void HybridTagRemoveFailed(this ILogger logger, string tag, Exception ex);

    // 1000–1099 info
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Cache disabled or unavailable — executing factory for key {KeyHash}.")]
    public static partial void HybridCacheDisabled(this ILogger logger, string keyHash);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "RemoveByTagAsync is not supported in this mode; no-op for tag {Tag}. Use Hybrid mode for tag support.")]
    public static partial void TagNotSupported(this ILogger logger, string tag);
}
```

- [ ] **Step 2: Add KeyHash helper to RedisCacheService and others**

In `src/Caching.NET/Services/RedisCacheService.cs` replace `TruncateKey` with:

```csharp
    private string FormatKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        if (_options.Value.IncludeRawKeyInLogs)
            return key.Length <= 64 ? key : key[..64] + "...";
        return StableStringHash.Compute64(key).ToString("x16");
    }
```

Apply the same replacement in `HybridCacheService.cs` and `InMemoryCacheService.cs` (for the existing `TruncateKey` usages — if a service does not currently have one, leave it unless logging in this task adds one).

- [ ] **Step 3: Replace logger calls with source-gen**

In `src/Caching.NET/Services/RedisCacheService.cs`, replace each existing `_logger.LogWarning(CacheLogEvents.X, ex, "...", TruncateKey(key))` with the source-gen equivalent, e.g.:

```csharp
_logger.RedisGetFailed(FormatKey(key), ex);
```

Apply consistently for Redis get/set/remove/serialization/key-too-long/payload-too-large/envelope-invalid/format-drift/schema-drift events. Repeat the pattern in `HybridCacheService.cs`, `InMemoryCacheService.cs`, `RoutingCacheService.cs`.

- [ ] **Step 4: Delete CacheLogEvents.cs**

```bash
git rm src/Caching.NET/Internal/CacheLogEvents.cs
```

Remove any remaining `using` of the deleted namespace + struct from the modified service files.

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: No warnings, no errors. Source-gen produces the partial method bodies.

- [ ] **Step 6: Run full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(p1): migrate hot-path logs to LoggerMessage source-gen with stable EventIds"
```

---

## Task 10: Cardinality Roslyn analyzer (CN0001)

Block compile-time additions of forbidden tag keys (`key`, `tenant`, `user_id`) to OTel instruments.

**Files:**
- Create: `src/Caching.NET.Analyzers/Caching.NET.Analyzers.csproj`
- Create: `src/Caching.NET.Analyzers/CardinalityAnalyzer.cs`
- Create: `src/Caching.NET.Analyzers/AnalyzerReleases.Shipped.md`
- Create: `src/Caching.NET.Analyzers/AnalyzerReleases.Unshipped.md`
- Create: `tests/Caching.NET.Tests.Analyzers/Caching.NET.Tests.Analyzers.csproj`
- Create: `tests/Caching.NET.Tests.Analyzers/CardinalityAnalyzerTests.cs`
- Modify: `Directory.Packages.props`, `Caching.NET.sln`

- [ ] **Step 1: Add analyzer-related package versions**

Edit `Directory.Packages.props` — append to the existing `<ItemGroup>`:

```xml
<PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.11.0" />
<PackageVersion Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.11.0" />
<PackageVersion Include="Microsoft.CodeAnalysis.Analyzer.Testing" Version="1.1.2" />
<PackageVersion Include="Microsoft.CodeAnalysis.CSharp.Analyzer.Testing" Version="1.1.2" />
<PackageVersion Include="Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit" Version="1.1.2" />
```

- [ ] **Step 2: Create analyzer csproj**

```xml
<!-- src/Caching.NET.Analyzers/Caching.NET.Analyzers.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <DevelopmentDependency>true</DevelopmentDependency>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="AnalyzerReleases.Shipped.md" />
    <AdditionalFiles Include="AnalyzerReleases.Unshipped.md" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create AnalyzerReleases files**

```markdown
<!-- src/Caching.NET.Analyzers/AnalyzerReleases.Shipped.md -->
; Shipped analyzer releases.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
```

```markdown
<!-- src/Caching.NET.Analyzers/AnalyzerReleases.Unshipped.md -->
; Unshipped analyzer release.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CN0001  | Caching.Cardinality | Error | High-cardinality tag added to a Caching.NET OTel instrument
```

- [ ] **Step 4: Implement the analyzer**

```csharp
// src/Caching.NET.Analyzers/CardinalityAnalyzer.cs
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Caching.NET.Analyzers;

/// <summary>
/// CN0001: flags high-cardinality tag-key string literals on OTel instrument calls.
/// Forbidden keys: "key", "tenant", "user_id". Severity: Error.
/// Triggers on calls to Counter&lt;T&gt;.Add, Histogram&lt;T&gt;.Record, UpDownCounter&lt;T&gt;.Add
/// when any KeyValuePair&lt;string,object?&gt; argument's Key string literal matches a forbidden value.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CardinalityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CN0001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "High-cardinality tag added to a Caching.NET OTel instrument",
        messageFormat: "Tag key '{0}' is high-cardinality and forbidden on Caching.NET instruments. Use a histogram or remove the tag.",
        category: "Caching.Cardinality",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Adding key/tenant/user_id as a tag to OTel counters or histograms causes time-series explosion. Use the cache.payload.bytes histogram for size, and never tag with the raw key.",
        helpLinkUri: "https://github.com/baps-apps/caching-net/blob/main/docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md#7-observability-otel-first");

    private static readonly ImmutableHashSet<string> ForbiddenKeys =
        ImmutableHashSet.Create("key", "tenant", "user_id", "cache.key", "cache.tenant", "cache.user_id");

    private static readonly ImmutableHashSet<string> InstrumentMethodNames =
        ImmutableHashSet.Create("Add", "Record");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
        if (!InstrumentMethodNames.Contains(member.Name.Identifier.Text)) return;

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null) return;

        var containingTypeName = symbol.ContainingType?.OriginalDefinition?.ToDisplayString();
        if (containingTypeName is null) return;
        if (!IsInstrumentType(containingTypeName)) return;

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var literalKey = ExtractTagKeyLiteral(arg.Expression);
            if (literalKey is null) continue;
            if (!ForbiddenKeys.Contains(literalKey)) continue;
            context.ReportDiagnostic(Diagnostic.Create(Rule, arg.GetLocation(), literalKey));
        }
    }

    private static bool IsInstrumentType(string typeName) =>
        typeName == "System.Diagnostics.Metrics.Counter<T>" ||
        typeName == "System.Diagnostics.Metrics.Histogram<T>" ||
        typeName == "System.Diagnostics.Metrics.UpDownCounter<T>" ||
        typeName == "System.Diagnostics.Metrics.ObservableCounter<T>" ||
        typeName == "System.Diagnostics.Metrics.ObservableUpDownCounter<T>";

    private static string? ExtractTagKeyLiteral(ExpressionSyntax expr)
    {
        // Looks for: new KeyValuePair<string, object?>("key", ...)
        // or:        new("key", ...)  with target-typed new
        if (expr is ObjectCreationExpressionSyntax obj && obj.ArgumentList?.Arguments.Count >= 1)
            return TryGetStringLiteral(obj.ArgumentList.Arguments[0].Expression);

        if (expr is ImplicitObjectCreationExpressionSyntax imp && imp.ArgumentList?.Arguments.Count >= 1)
            return TryGetStringLiteral(imp.ArgumentList.Arguments[0].Expression);

        return null;
    }

    private static string? TryGetStringLiteral(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText
            : null;
}
```

- [ ] **Step 5: Create analyzer test project csproj**

```xml
<!-- tests/Caching.NET.Tests.Analyzers/Caching.NET.Tests.Analyzers.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Analyzer.Testing" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Caching.NET.Analyzers\Caching.NET.Analyzers.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write analyzer tests**

```csharp
// tests/Caching.NET.Tests.Analyzers/CardinalityAnalyzerTests.cs
using Caching.NET.Analyzers;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<
    Caching.NET.Analyzers.CardinalityAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Caching.NET.Tests.Analyzers;

public class CardinalityAnalyzerTests
{
    private const string Preamble = @"
using System.Collections.Generic;
using System.Diagnostics.Metrics;

class Test
{
    static readonly Meter M = new(""x"");
    static readonly Counter<long> C = M.CreateCounter<long>(""x"");
}
";

    [Fact]
    public async Task Forbidden_key_tag_reports_CN0001()
    {
        var src = Preamble + @"
class Caller
{
    static void X()
    {
        Test.C.Add(1, [|new KeyValuePair<string, object?>(""key"", ""abc"")|]);
    }
}";
        await new Verifier { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task Forbidden_tenant_tag_reports_CN0001()
    {
        var src = Preamble + @"
class Caller
{
    static void X()
    {
        Test.C.Add(1, [|new KeyValuePair<string, object?>(""tenant"", ""acme"")|]);
    }
}";
        await new Verifier { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task Allowed_mode_tag_does_not_report()
    {
        var src = Preamble + @"
class Caller
{
    static void X()
    {
        Test.C.Add(1, new KeyValuePair<string, object?>(""cache.mode"", ""Redis""));
    }
}";
        await new Verifier { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task Non_instrument_method_does_not_report()
    {
        var src = @"
using System.Collections.Generic;
class Other
{
    public void Add(int x, KeyValuePair<string, object?> p) { }
}
class Caller
{
    static void X()
    {
        new Other().Add(1, new KeyValuePair<string, object?>(""key"", ""abc""));
    }
}";
        await new Verifier { TestCode = src }.RunAsync();
    }
}
```

- [ ] **Step 7: Add projects to solution**

```bash
dotnet sln /Users/vishalpatel/Projects/caching-net/Caching.NET.sln add /Users/vishalpatel/Projects/caching-net/src/Caching.NET.Analyzers/Caching.NET.Analyzers.csproj /Users/vishalpatel/Projects/caching-net/tests/Caching.NET.Tests.Analyzers/Caching.NET.Tests.Analyzers.csproj
```

- [ ] **Step 8: Build and run analyzer tests**

Run: `dotnet build src/Caching.NET.Analyzers/Caching.NET.Analyzers.csproj`
Expected: Build succeeds.

Run: `dotnet test tests/Caching.NET.Tests.Analyzers`
Expected: 4 tests PASS.

- [ ] **Step 9: Commit**

```bash
git add Directory.Packages.props Caching.NET.sln src/Caching.NET.Analyzers tests/Caching.NET.Tests.Analyzers
git commit -m "feat(p1): add CN0001 cardinality Roslyn analyzer + tests"
```

---

## Task 11: Ship analyzer with main package

Reference the analyzer project from `Caching.NET.csproj` as an analyzer-only asset so consumers get CN0001 automatically.

**Files:**
- Modify: `src/Caching.NET/Caching.NET.csproj`

- [ ] **Step 1: Add ProjectReference with analyzer assets**

Append a new `<ItemGroup>` to `src/Caching.NET/Caching.NET.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\Caching.NET.Analyzers\Caching.NET.Analyzers.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false"
                    PrivateAssets="all" />
</ItemGroup>
```

Then ensure the analyzer dll lands under `analyzers/dotnet/cs/` in the produced .nupkg by adding to the same csproj:

```xml
<Target Name="PackAnalyzer" AfterTargets="Build" Condition="'$(GeneratePackageOnBuild)' == 'true' OR '$(IsPacking)' == 'true'">
  <ItemGroup>
    <None Include="$(OutputPath)..\..\..\Caching.NET.Analyzers\$(Configuration)\netstandard2.0\Caching.NET.Analyzers.dll"
          Pack="true"
          PackagePath="analyzers/dotnet/cs/"
          Visible="false" />
  </ItemGroup>
</Target>
```

- [ ] **Step 2: Verify analyzer self-flags violations on the library itself**

Add this temporary test code to the bottom of `src/Caching.NET/Telemetry/CacheInstruments.cs` and verify the build fails with CN0001:

```csharp
// TEMP — verify analyzer wires.
// internal static void _DeleteMe() => Hits.Add(1, new KeyValuePair<string, object?>("key", "x"));
```

Uncomment that line, run `dotnet build`, expected: ERROR `CN0001`. Then re-comment / delete the line.

- [ ] **Step 3: Run full suite**

Run: `dotnet test`
Expected: PASS — analyzer not triggering on production code paths.

- [ ] **Step 4: Pack and inspect**

Run: `dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs /p:Version=2.0.0-alpha.2`
Expected: `nupkgs/Caching.NET.2.0.0-alpha.2.nupkg` produced.

Run: `unzip -l nupkgs/Caching.NET.2.0.0-alpha.2.nupkg | grep analyzers`
Expected: line shows `analyzers/dotnet/cs/Caching.NET.Analyzers.dll`.

- [ ] **Step 5: Commit**

```bash
git add src/Caching.NET/Caching.NET.csproj
git commit -m "feat(p1): ship CN0001 analyzer inside main Caching.NET nupkg"
```

---

## Task 12: Sample app + spec touch-ups + final verification

**Files:**
- Modify: `samples/Caching.NET.Sample/Program.cs` (only if a deprecated symbol references the deleted `CacheLogEvents`).
- Modify: `docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md` (no content change required; this task is verification only).

- [ ] **Step 1: Confirm sample app builds**

Run: `dotnet build samples/Caching.NET.Sample/Caching.NET.Sample.csproj`
Expected: Build succeeds with no warnings.

- [ ] **Step 2: Verify all CacheInstruments tests still target unique mode tags**

Run: `dotnet test tests/Caching.NET.Tests --filter FullyQualifiedName~CacheInstrumentsTests`
Expected: PASS — no cross-test bleed (every test uses GUID-suffixed mode tag).

- [ ] **Step 3: Run full multi-target suite**

Run: `dotnet test`
Expected: PASS across net8.0, net9.0, net10.0. All counters/histograms emit measurable signal.

- [ ] **Step 4: Smoke-pack v2.0.0-alpha.3**

Run: `dotnet pack src/Caching.NET/Caching.NET.csproj -c Release -o nupkgs /p:Version=2.0.0-alpha.3`
Expected: Single nupkg produced; analyzer dll embedded; no warnings.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/plans/2026-05-05-v2-p1-observability-envelope.md
git commit -m "docs(p1): mark P1 plan complete; ready for P2 plan"
```

---

## Self-Review Checklist (filled in)

**1. Spec coverage:**

| Spec §7 / §6 requirement | Implementing task |
|---|---|
| All OTel instruments (Hits/Misses/Errors/Sets/Removes already in P0; +Evictions/StaleServed/CircuitStateChanges/SchemaDrift/PayloadBytes/StaleRefreshInFlight) | Task 3 |
| Eviction listener on IMemoryCache | Task 6 |
| Payload histogram | Task 3 instrument + Task 4 emission |
| PayloadEnvelope + drift | Tasks 1, 2, 4, 5 |
| Schema-drift counter | Tasks 3, 5 |
| Miss-reason taxonomy | Tasks 5 (envelope reasons), 7 (Bypass/Disabled) |
| Circuit state change counter + log | Task 8 |
| LoggerMessage source-gen | Task 9 |
| Cardinality analyzer | Tasks 10, 11 |
| KeyHash in logs (default) | Task 9 (`FormatKey`) |
| Stable EventId ranges | Task 9 (`CacheLogMessages`) |

`StaleServed` instrument and `StaleRefreshInFlight` UpDownCounter exist in this plan but are **not yet emitted** — stale-while-revalidate logic is P2. They land here so OTel pipelines can subscribe to them ahead of time. P2 will wire emission.

**2. Placeholder scan:** Every code step contains the actual code or exact command. No "TBD"/"TODO". Two judgment-call lines flag where existing identifier names may differ from the snippet (Task 7 `BuildRouting`, Task 8 `pipelineName`); each gives explicit instructions for the engineer to bridge the gap.

**3. Type consistency:**

- `PayloadEnvelopeReadResult` enum members: `Ok | EnvelopeInvalid | FormatDrift | SchemaDrift` — used identically in Task 2 (declaration) and Task 5 (consumption).
- `PayloadEnvelope.Write(ReadOnlySpan<byte>, byte, ulong) → byte[]` — Task 2 declaration matches Task 4 call sites.
- `PayloadEnvelope.TryRead(ReadOnlySpan<byte>, byte, ulong, out ReadOnlySpan<byte>) → PayloadEnvelopeReadResult` — Task 2 declaration matches Task 5 + Task 2 tests.
- `StableTypeHash.Compute<T>() → ulong` and `StableStringHash.Compute64(string) → ulong` — Task 1 declaration matches Tasks 2/4/5/9 call sites.
- `CacheInstruments.RecordEviction(string mode, string evictionReason)` — Task 3 declaration matches Task 6 callback (`reason.ToString()`).
- `CacheInstruments.RecordCircuitStateChange(string mode, string pipeline, string circuitState)` — Task 3 declaration matches Task 8 callbacks.
- `CacheInstruments.RecordPayloadBytes(string, string, long)` — Task 3 matches Tasks 4 & 5 call sites.
- `CacheInstruments.RecordSchemaDrift(string mode, string driftKind)` — Task 3 declaration matches the three drift sites in Task 5 (`envelope_invalid`, `format_drift`, `schema_drift`).
- LoggerMessage extension methods (`logger.RedisGetFailed(string keyHash, Exception ex)` etc.) — Task 9 declarations match Task 9 call-site replacements.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-05-05-v2-p1-observability-envelope.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — fresh subagent per task, two-stage review between tasks.

**2. Inline Execution** — execute in this session with checkpoints.

**Which approach?**
