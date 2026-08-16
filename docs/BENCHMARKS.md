# Benchmarks

Measured numbers for Caching.NET v3.1.0. Nothing in this repository claims a performance improvement
without a run behind it.

## How to reproduce

```bash
cd benchmark/Caching.NET.Benchmark

# Redis-free suites
dotnet run -c Release -- --filter '*InMemoryBenchmarks*'
dotnet run -c Release -- --filter '*SerializationBenchmarks*'
dotnet run -c Release -- --filter '*TelemetryOverheadBenchmarks*'
dotnet run -c Release -- --filter '*LayerDecoratorBenchmarks*'
dotnet run -c Release -- --filter '*BackplaneDispatchBenchmarks*'

# Redis and Hybrid suites
docker run -d --name bench-redis -p 63790:6379 redis:7.4-alpine
CACHINGNET_BENCH_REDIS="127.0.0.1:63790,abortConnect=false" \
  dotnet run -c Release -- --filter '*RedisBenchmarks*'
docker rm -f bench-redis
```

## Environment

```text
BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100, .NET 10.0.0, Arm64 RyuJIT armv8.0-a
IterationCount=8  WarmupCount=3
Redis 7.4-alpine in Docker on the same host (loopback)
```

A developer laptop over loopback Docker. Absolute Redis latencies are **not** representative of a
production cluster; the useful signal is the *ratio* between layers, which is the thing the mode
choice turns on.

---

## In-memory mode

Measured as a **paired run** (`IterationCount=8`, `WarmupCount=3`, Gen1 and Gen2 zero throughout): a
worktree at v3.0.0 and the 3.1.0 tree, same harness, back to back on one machine. The v3.0.0 column
is a fresh measurement of that tree rather than a quote of the release-gate run, so the two columns
differ only by the code under test — the point of the table is the comparison, and figures carried
across sessions cannot support one.

| Method | Concurrency | v3.0.0 | v3.1.0 | Gen0 | Allocated |
|---|---:|---:|---:|---:|---:|
| L1 hit | 1 | 145.6 ns | 140.9 ns | 0.0014 | 160 B |
| L1 miss (no factory) | 1 | 103.5 ns | 102.5 ns | 0.0017 | 176 B |
| Factory execution | 1 | 3,295.0 ns | 3,128.2 ns | 0.0076 | 4,208 B |
| Concurrent get-or-set on one key | 1 | 172.7 ns | 176.4 ns | 0.0036 | 384 B |
| L1 hit | 64 | 142.9 ns | 140.6 ns | 0.0014 | 160 B |
| L1 miss (no factory) | 64 | 103.0 ns | 104.0 ns | 0.0017 | 176 B |
| Factory execution | 64 | 2,673.8 ns | 2,544.5 ns | 0.0076 | 4,208 B |
| Concurrent get-or-set on one key | 64 | 10,983.1 ns | 10,912.9 ns | 0.2136 | 23,136 B |

Gen0 and allocation are identical column to column, byte for byte. Every timing row is inside its
error bars, which is the intended result rather than a pleasant surprise: 3.1.0 changes *when a span
is created*, and nothing on this path creates one.

**The two `Factory execution` rows carry ±1,300 ns in both trees and should not be read as a 5%
improvement** — they measure a user-supplied delegate, not cache machinery, and at that error they
cannot support a claim in either direction. They are shown because omitting the slowest rows from a
regression table is how a regression gets missed, not because their difference means anything.

Reading: a hit costs ~141 ns and **does not degrade from 1 to 64 concurrent callers** (140.9 ns →
140.6 ns), so there is no contention on the read path. The "concurrent get-or-set" row fans out N
awaits over one already-cached key, so its cost scales with N (64 × ~171 ns, the same per-caller cost
as the single-caller row) — it measures the fan-out, not lock contention.

These numbers are roughly **2× faster and 2–3× lower-allocating** than the table that stood here
before, which was carried over from an earlier task rather than re-measured. Re-runs of this suite on
this machine have shown swings up to 2× between otherwise-identical sessions, so treat the relative
shape (hit and miss flat from 1 to 64 callers, factory execution dominated by the delegate, concurrent
get-or-set scaling with N) as the durable signal and the specific nanosecond values as one machine's
snapshot rather than a number to plan capacity against.

## Redis mode: the cost of authoritative tag markers

`RemoveByTag` and `Clear` are marker-based, and Redis mode keeps markers out of the memory layer so an
invalidation cannot be hidden by a local copy (see
[ARCHITECTURE §3.1](ARCHITECTURE.md#31-the-mode-also-has-to-reach-the-tag-markers)). That costs Redis
round trips on every read that **finds** something.

### The command model

**A read that hits costs `3 + n` Redis commands for an entry with `n` tags** — the entry itself, plus
the two reserved `Clear` markers, plus one marker per tag. **A read that misses costs 1**: with no
entry there is nothing for a marker to invalidate, so no marker is fetched. This is the durable,
hardware-independent capacity fact; every latency figure below is one machine's expression of it.

Two independent measurements agree on it.

**Direct: `cmdstat_*` diffs**, over 2 000 samples per row against a loopback Redis, with the marker
rule toggled off and on and nothing else changed:

| Read | Redis commands | Mean | p50 | p95 | p99 | ops/s |
|---|---:|---:|---:|---:|---:|---:|
| Untagged entry, markers cached locally | 1 `hmget` | 124.0 µs | 120.2 µs | 162.4 µs | 205.4 µs | 8,067 |
| Untagged entry, markers authoritative | 3 `hmget` | 381.9 µs | 368.4 µs | 467.7 µs | 658.7 µs | 2,619 |
| Entry with 2 tags, markers cached locally | 1 `hmget` | 129.9 µs | 123.2 µs | 180.6 µs | 301.1 µs | 7,700 |
| Entry with 2 tags, markers authoritative | 5 `hmget` | 583.6 µs | 564.5 µs | 715.7 µs | 910.8 µs | 1,714 |

**Indirect: latency is linear in command count.** In the `RedisBenchmarks` table below, a Redis-mode
miss — known to be exactly one command — costs 109.6 µs, so one loopback command is ≈110 µs. Dividing
the other rows by that figure recovers the command counts without counting any commands:

| Row | Mean | ÷ 109.6 µs | Commands implied | Commands expected |
|---|---:|---:|---:|---:|
| Redis mode miss | 109.6 µs | 1.00 | 1.00 | 1 |
| Redis mode hit | 338.1 µs | 3.08 | 3.08 | 3 |
| Redis mode hit, 2 tags | 537.0 µs | 4.90 | 4.90 | 5 |

Two methods, one model. That is why the `3 + n` rule, rather than any microsecond figure on this page,
is what Redis-mode capacity should be planned against.

### What it costs

Against the pre-fix figures in the [baseline comparison](#baseline-comparison-the-engine-agnostic-contracts-cost)
below, an untagged Redis-mode hit went from 109.1 µs to 338.1 µs (**×3.10**) and a two-tag hit to
537.0 µs (**×4.92**). Allocation roughly doubled as well, 6.08 KB → 12.2 KB untagged and 19.0 KB with
two tags, because each marker probe carries its own response through the serializer.

**Misses are unchanged** (107.9 µs → 109.6 µs), which makes the blended cost depend entirely on the
hit ratio: a Redis-mode cache running at a 20% hit ratio pays ≈1.4× the old Redis command volume,
while one at 95% pays ≈2.9×. Read the amplification against the *hit* rate, not the request rate.

**`Hybrid` and `InMemory` are unaffected on the read path.** A Hybrid L1 hit measured 421.8 ns against
410.4 ns before the rule — inside this machine's noise — because Hybrid may serve markers from memory.
Tagging a Hybrid entry costs CPU only, not a round trip: 740.6 ns for a two-tag L1 hit, still three
orders of magnitude below any Redis-mode read. If a service is in Redis mode for latency rather than
for consistency, it wants `Hybrid`.

## Serialization (distributed layer only)

| Method | Payload | Mean | Allocated |
|---|---:|---:|---:|
| JSON serialize | 256 B | 168.1 ns | 728 B |
| JSON deserialize | 256 B | 277.4 ns | 1,096 B |
| MessagePack serialize | 256 B | 155.6 ns | 704 B |
| MessagePack deserialize | 256 B | 193.6 ns | 960 B |
| JSON + Brotli serialize | 256 B | 2,370.7 ns | 992 B |
| JSON + Brotli deserialize | 256 B | 3,112.5 ns | 83,776 B |
| JSON serialize | 64 KiB | 9,268.0 ns | 131,288 B |
| JSON deserialize | 64 KiB | 17,653.7 ns | 196,946 B |
| MessagePack serialize | 64 KiB | 7,119.6 ns | 131,264 B |
| MessagePack deserialize | 64 KiB | 15,919.6 ns | 196,810 B |
| JSON + Brotli serialize | 64 KiB | 15,619.0 ns | 98,920 B |
| JSON + Brotli deserialize | 64 KiB | 73,983.3 ns | 344,907 B |

Conclusions that drove the defaults:

- **MessagePack is ~8–23% faster than JSON** and allocates slightly less. It is opt-in rather than
  the default because JSON payloads are inspectable in Redis, which is worth more than 20% of a
  sub-microsecond operation for most services.
- **Compression is a bad default.** At 256 B it is 14× slower for no size benefit — which is why the
  codec only compresses above `Compression.ThresholdBytes` and keeps the raw form when compression
  does not shrink the payload. At 64 KiB it is still 1.7× slower to write and 8× slower to read.
  Enable it only when Redis memory or network transfer is the actual constraint.
- The 83 KiB allocation on the 256 B Brotli deserialize is `BrotliStream`'s internal buffer, paid
  once per call. Another reason not to compress small values.

## Telemetry overhead

Measured at `IterationCount=15  WarmupCount=5`, on a quiescent machine.

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Hit, telemetry disabled | 137.6 ns | 1.00 | 192 B |
| Hit, metrics enabled, no trace listener | 154.7 ns | 1.12 | 192 B |
| Hit, trace listener attached, no parent span | 483.1 ns | 3.51 | 1,464 B |
| Hit, trace listener attached, under parent span | 479.5 ns | 3.48 | 1,520 B |
| Miss, telemetry disabled | 90.6 ns | 0.66 | 200 B |
| Miss, metrics enabled, no trace listener | 105.7 ns | 0.77 | 184 B |
| Miss, trace listener attached, no parent span | 466.5 ns | 3.39 | 1,456 B |
| Miss, trace listener attached, under parent span | 467.9 ns | 3.40 | 1,512 B |

Hit and miss are measured separately (a pre-warmed key vs. a key that is never populated) because
they build different metric/trace attributes and can regress independently. On this run, metrics-only
cost is small — roughly 17 ns and no extra allocation on a hit — and a live trace listener is the
expensive tier, at ~330–380 ns and a bit over 1 KB, because it is the only tier that actually walks
the span's tag list.

**The two listener rows are the same measurement within error, and that is the point.**
`Observability.LayerTracing` defaults to suppressing a layer span that has no parent, and a cache verb
never produces one: Caching.NET's own operation span sits above every probe the verb issues, whether
or not the application has a request span. So the caller's path costs what it always cost. The setting
only reaches probes the engine issues on its own threads, which no end-to-end row can exercise — see
the decorator table below.

`EnableTracing` and `EnableMetrics` still default to `true`. `ActivitySource.HasListeners()` and the
metrics-enabled flag are checked before any attribute value is built, so no *attribute* is allocated
when nobody is listening.

**This table is substantially cheaper than earlier revisions, and that is a real architectural
change, not noise.** Before this plan (see the baseline comparison below), turning metrics on cost
roughly 190 ns over telemetry-off, because the old engine-level event bridge subscribed to FusionCache's
own hit/miss events and built `EventArgs` on every operation regardless of whether metrics were
enabled. `FusionCacheService` now calls `CacheTelemetryContext.RecordHit`/`RecordMiss` directly —
one producer per signal, gated on `MetricsEnabled` — so the "metrics enabled, no listener" tier is
now only ~20 ns over telemetry-off instead of ~190 ns.

### Layer-decorator probe cost

The numbers above measure whole `GetOrDefaultAsync` calls, where a few nanoseconds inside the L1
probe disappear under ~100–300 ns of engine and adapter work. This table isolates the memory-layer
decorator against a bare `MemoryCache` probe, because that is where a regression hides.

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Raw `MemoryCache.TryGetValue` | 17.34 ns | 1.00 | 0 B |
| `InstrumentedMemoryCache.TryGetValue`, no listener | 19.32 ns | 1.11 | 0 B |
| `InstrumentedMemoryCache.TryGetValue`, `MeterListener` attached | 57.96 ns | 3.34 | 0 B |
| `InstrumentedMemoryCache.TryGetValue`, no trace listener | 19.62 ns | 1.13 | 0 B |
| `InstrumentedMemoryCache.TryGetValue`, `ActivityListener` attached, no parent span | 20.91 ns | 1.21 | 0 B |
| `InstrumentedMemoryCache.TryGetValue`, `ActivityListener` attached, under parent span | 151.50 ns | 8.74 | 600 B |
| `InstrumentedMemoryCache.TryGetValue`, `ActivityListener` attached, ended parent span | 22.83 ns | 1.32 | 0 B |
| `InstrumentedMemoryCache.TryGetValue`, `ActivityListener` attached, ended parent span, `LayerTracing=Always` | 159.51 ns | 9.20 | 600 B |
| `InstrumentedMemoryCache.TryGetValue`, `ActivityListener` attached, no parent span, `LayerTracing=Always` | 141.23 ns | 8.15 | 616 B |

Instrumentation costs about **2 ns per probe when nothing is listening**, and allocates nothing. The
engine issues several probes per logical operation, so that per-probe figure is what matters rather
than the ratio.

### What suppressing an unparented layer span is worth

The **ended parent span** pair is the whole case for `Observability.LayerTracing`, measured against one
baseline in one run. `Always` is exactly the pre-3.1 behaviour, so those two rows are the before and
after of the same probe:

| Probe with an `ActivityListener` attached | Mean | Allocated |
|---|---:|---:|
| Ended parent, `LayerTracing=Always` (pre-3.1) | 159.51 ns | 600 B |
| Ended parent, `LayerTracing=WhenParented` (default) | **22.83 ns** | **0 B** |
| Under a live parent (unchanged either way) | 151.50 ns | 600 B |

A suppressed probe costs **7× less and allocates nothing** — and at 22.83 ns it is within a couple of
nanoseconds of the 19.62 ns "no trace listener" row, the remainder being the `ExecutionContext` restore
the row pays to set the scenario up. That is the real statement: a probe whose span is suppressed
costs what it would cost if nobody were listening at all. A traced probe still costs its ~130 ns and
600 B whenever it has a live parent, which is every probe on a caller's path.

**Why the parent is *ended* rather than absent, and why that is the row that matters.** Background
work does not begin from a blank ambient context — trace context flows with the `ExecutionContext`
captured when the work was scheduled, so an engine callback typically runs holding the span of the
request that triggered it, which has since finished. The "no parent span" rows are the easier
scenario; the "ended parent span" rows are the one production actually produces, and a gate written as
`Activity.Current is null` would emit a span on every one of them. Setting this up requires a captured
context: assigning a finished activity to `Activity.Current` is rejected by the runtime outright
(measured at ~7.6 µs and 192 B for the rejected assignment), so a benchmark or test written that way
silently measures the parentless case instead. `TracingScope.RunUnderStaleParent` and
`LayerSpanParentingTests.EndedAmbientSpan_CountsAsNoParent` both go through
`ExecutionContext.Run` for that reason — ~11 ns and no allocation, paid equally by both rows.

This is per *probe*, and the engine issues several per operation — but only on its own threads does
the suppression apply, so the saving is proportional to background and backplane activity rather than
to request traffic. The larger win is not CPU: it is the exporter batches, ingest volume and
trace-store cardinality that those single-span root traces were consuming.

### Backplane message dispatch

Delivering one incoming backplane message through `InstrumentedBackplane`, which wraps the engine's
handler in the `cache.backplane.receive` span. The handler here only increments a counter, so these
rows are delivery cost alone — the eviction work a real message triggers is the table above.

| Method | Before | After | Allocated (after) |
|---|---:|---:|---:|
| Incoming message dispatch, no trace listener | 1.909 ns | 3.353 ns | 0 B |
| Incoming message dispatch, trace listener attached | 2.147 ns | 145.390 ns | 640 B |
| Incoming message dispatch (async), no trace listener | 9.473 ns | 9.938 ns | 72 B |
| Incoming message dispatch (async), trace listener attached | 9.455 ns | 149.250 ns | 712 B |

The span costs ~145 ns and ~640 B per *received message* — not per operation. Backplane traffic is
invalidations across the cluster, orders of magnitude below cache-call volume, and it buys the thing
the evictions underneath it had no way to get: a parent. With tracing enabled but nothing listening
the cost is the wrapper delegate alone — 1.4 ns on the sync path, and 0.5 ns on the async one.

The async wrapper is deliberately not an `async` method: it starts the span, and when there is no
listener it returns the engine's own `ValueTask` straight through rather than awaiting it, so no state
machine is built on a path that will never produce a span. Its 72 B is the engine's `ValueTask`, not
the wrapper's — the same 72 B the undecorated path allocates. Written as `async`, that row measured
21.7 ns and carried a state machine on every received message regardless.

With `Observability.EnableTracing: false` the subscription is passed through unrebuilt and the wrapper
does not exist at all.

This benchmark exists because the decorator regressed here once and nothing noticed. An early
revision took two `Stopwatch` timestamps and built a `TagList` on every probe regardless of whether a
listener existed; the fix hoisted a per-instance configuration check and added a live
`CacheTelemetry.LayerDuration.Enabled` check. A reviewer later reverted that fix outright and **the
entire unit suite was byte-identical** — the optimisation had no automated enforcement of any kind.

The row is load-bearing: reverting the live listener check (`ShouldRecordDuration` returning
`_layerMetricsConfigured` alone) moves the no-listener arm from **18.53 ns to 48.05 ns**, a 2.6×
regression far outside the ±1 ns error bars. A benchmark cannot fail a build, so this is visibility
rather than a gate — but the loss is now measurable instead of invisible.

## Baseline comparison: the engine-agnostic contract's cost

The engine-agnostic-cache-contract plan replaced the public surface with `ICacheService` and routed
every call through `FusionCacheService` (guard validation, tag materialisation, span/counter
recording) instead of exposing the engine's own `IFusionCache` directly. The plan's spec set explicit
gates for that change, measured against **`329d8f4`**, the commit immediately before the plan's first
task — i.e. the last commit where `IFusionCache` was still the public contract. Both sides were built
`Release` and run with BenchmarkDotNet back-to-back, same machine, same job settings, to keep
inter-run drift as small as this environment allows (see the noise note below).

| Gate | Threshold | Baseline (`329d8f4`) | Current | Δ | Result |
|---|---|---:|---:|---:|---|
| `GetOrSet` hit, InMemory, telemetry off | ≤2% latency, zero added allocations | 116.0 ns / 192 B | 133.2 ns / 192 B | **+14.9%** / +0 B | **latency gate missed**; allocation gate met |
| `GetOrSet` hit, InMemory, metrics on | ≤10% | 304.4 ns | 153.4 ns | **−49.6%** | met (large margin) |
| Redis hit | ≤2% | 115,827 ns | 109,120 ns | −5.8% | met at the time; **superseded** — see below |
| Redis miss | ≤2% | 115,913 ns | 107,856 ns | −7.0% | met (still holds: 109,608 ns today) |
| Hybrid full miss + factory | ≤2% | 138,538 ns | 129,588 ns | −6.5% | met (135,466 ns today, inside the error bars) |
| Tracing enabled, all modes | measured and published, no gate | 310.2 ns (hit) | 439.0 ns (hit) | — | published above and in Redis/Hybrid section |

**The `Redis hit` row is superseded and must not be read as a current result.** It was measured before
the tag-marker fix, when a Redis-mode read could be answered by a locally cached marker — i.e. while
`RemoveByTag` and `Clear` could be silently lost across instances. Measured against that same
115,827 ns baseline today it is **338,110 ns, or +192%**, and the gate is deliberately not met.

**Do not read that +192% as the contract's cost.** It is a composite of two independent changes that
move in opposite directions, and only one of them is what this gate table set out to measure:

| Step | Reading | Δ | What changed |
|---|---:|---:|---|
| Baseline `329d8f4` | 115,827 ns | — | engine exposed, markers local |
| …after the engine-agnostic contract | 109,120 ns | **−5.8%** | what this gate table measures |
| …after the tag-marker fix | 338,110 ns | **×3.10** | `3 + n` commands per hit |
| Net vs. baseline | 338,110 ns | **+192%** | both, compounded |

So the contract change made this row *faster*, and the marker fix is what multiplied it. **The
controlled measurement of the fix is the ×3.10, not the +192%** — same benchmark, same machine, same
job settings, one rule toggled.

The two readings come from different benchmark sessions, and this page warns elsewhere that sessions
on this machine can drift by 2×. Two rows untouched by the fix act as controls and rule that out:

| Control row | Pre-fix session | Today | Drift |
|---|---:|---:|---:|
| Redis mode miss | 107,856 ns | 109,608 ns | +1.6% |
| Hybrid full miss + factory | 129,588 ns | 135,466 ns | +4.5% |

Both are inside their own error bars, so the machine did not move between sessions and the hit row's
jump is the fix rather than drift. The ±6.3% error on the 338,110 ns reading puts the net figure
between +173% and +210%; the sign and the order of magnitude are not in question. The row is left in
the table rather than deleted because that 109,120 ns → 338,110 ns delta is the cleanest measurement
of what the fix cost.

**The other gate this run did not meet: the telemetry-off in-memory hit path is ~15% slower than
before the contract change**, not the ≤2% the spec asked for. Allocations did not change (192 B both
sides), so the cost is pure CPU: `ICacheService.GetOrDefaultAsync` now runs through
`FusionCacheService` — key/tag guard validation, `CacheEntryOverrides` resolution, and
`CacheTelemetryContext.StartOperation`'s early-exit check — on every call, where the old surface
called `IFusionCache.GetOrDefaultAsync` directly. This is reported as a finding, not fixed here: per
this task's own constraints, a performance fix is a separate, reviewed change, not a ride-along in a
benchmarking task. Whoever owns the plan should decide whether ~17 ns of guard/adapter overhead on
every telemetry-off in-memory hit is an accepted trade for the engine-agnostic contract, or worth a
follow-up optimisation pass.

**The metrics-on gate passed by a wide margin, and for a specific, verifiable reason**: the old
surface's "metrics on" cost included the engine-level event-bridge dispatch (~190 ns, see the
telemetry-overhead narrative above), which this plan's Task 10/11 work replaced with direct
`RecordHit`/`RecordMiss` calls. That saving (~150 ns) outweighs the ~17 ns of added adapter overhead,
so a cache with metrics on — the shipping default — is faster in absolute terms today than it was
before this plan, even though the isolated telemetry-off path got slower.

**Not included in the gate table, and why:** `InMemoryBenchmarks` (L1 hit/miss, factory execution,
concurrent get-or-set) and the `Hybrid L1 hit` row were also run against both commits, but the
readings were not stable enough on this machine to publish as a regression comparison. Concrete
evidence:

- `InMemoryBenchmarks.'L1 hit'` (Concurrency=1) read 279.6 ns on one baseline run and, in different
  isolated sessions, 140.8 ns and 238–288 ns on current-commit runs of the *identical* code path —
  swings in both directions, including current-commit readings faster than baseline, which is not
  physically consistent with an adapter that can only add work on top of the same engine call.
- `Hybrid L1 hit` read 870 ns in the table published before this task, 1,040 ns on today's baseline
  run, and 410 ns on today's current-commit run — again spanning more than 2× with no consistent
  direction.
- `InMemoryBenchmarks.'Factory execution'` at Concurrency=1 could not be measured on the current
  commit in three attempts: BenchmarkDotNet's pilot stage kept escalating the operations-per-iteration
  count (up to 4,194,304) as the run progressed, and each attempt either exceeded a 5–10 minute budget
  or was manually terminated. The same benchmark completed normally (~2 s) on the `329d8f4` baseline
  and on the current commit at Concurrency=64. This is very likely this specific benchmark's design —
  every invocation inserts a new, never-expiring, never-evicted key into an unbounded `MemoryCache`,
  so total live entries grow without bound over a run that can total millions of operations — rather
  than a regression in cache code, since the identical design exists unchanged on both commits and the
  instability was not consistently reproducible. It is flagged here, unresolved, rather than silently
  omitted or patched.

The `TelemetryOverheadBenchmarks` and `RedisBenchmarks`/Hybrid-hit-and-miss readings used for the gate
table above are trusted because they were low-variance *within* each run (StdDev under 1% of the mean
in most rows) and were captured immediately back-to-back to minimise the drift window — but the
swings documented in this section mean any absolute nanosecond figure from this machine should be
read with the same skepticism the rest of this document already applies to Redis latencies: the
*direction and rough magnitude* of a well-isolated, low-variance comparison is trustworthy; a single
absolute reading on a noisy shared laptop is not.

## Redis and Hybrid

Re-baselined after the tag-marker fix. This is the current `RedisBenchmarks` suite; the superseded
pre-fix readings are kept in the [baseline comparison](#baseline-comparison-the-engine-agnostic-contracts-cost)
section for the delta.

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Hybrid L1 hit | 421.8 ns | 1.00 | 624 B |
| Hybrid L1 hit, 2 tags | 740.6 ns | 1.76 | 1,232 B |
| Redis mode miss | 109,608.1 ns | 259.9 | 3.02 KB |
| Hybrid full miss + factory | 135,466.3 ns | 321.2 | 13.5 KB |
| Redis mode hit | 338,109.9 ns | 801.8 | 12.2 KB |
| Redis mode hit, 2 tags | 537,043.0 ns | 1,273.5 | 19.0 KB |

Note the ordering, which is new and counter-intuitive: **a Redis-mode hit is now three times more
expensive than a Redis-mode miss**, and more expensive than a full Hybrid miss that runs a factory.
That inversion is the `3 + n` command model showing through — a miss reads one key and stops, a hit
reads the entry and then has to prove no marker invalidates it.

The headline: **a Hybrid L1 hit is 260–800× faster than reaching Redis** even over loopback, where the
network cost is near zero — and the multiplier is now 800× on the *hit* path specifically, because
that is the path the marker rule taxes. On a real cluster the gap widens. This is the entire argument
for Hybrid mode in a multi-pod deployment, and the reason Redis mode is documented as "correctness
over latency" rather than presented as a general-purpose default.

`Hybrid L1 hit`'s absolute figure is the least trustworthy number on this page: it read 870 ns in a
table published before this task, 1,040 ns on a same-day baseline (`329d8f4`) run, 410 ns on the
pre-fix run, and 421.8 ns here — more than a 2× spread with no consistent direction, consistent with
the L1-probe-scale noise discussed in the baseline-comparison section above. The orders-of-magnitude
gap to Redis is the durable signal; the exact nanosecond figure for the memory-only side is not. The
useful reading of the 410 → 421.8 ns pair is not "1.03× slower" but "indistinguishable", which is the
claim that matters: the marker rule did not touch the Hybrid read path.

There is no separate "Hybrid L2 hit" row. Producing one meant asking the cache to skip its memory
layer for a single call — something Caching.NET's additive per-call overrides deliberately cannot
express, because an override must never escape its cache's mode. The only way to issue that read is a
Redis-mode cache, which is what **Redis mode hit** already measures.

### Hybrid under a trace listener

Hybrid is the only mode that runs a backplane, so it is the mode where the parented/parentless
distinction exists at all. Measured in a separate session from the table above — read the rows against
each other, not against it:

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Hybrid L1 hit | 433.4 ns | 1.00 | 624 B |
| Hybrid L1 hit, trace listener attached, no parent span | 1,028.1 ns | 2.37 | 3,096 B |
| Hybrid L1 hit, trace listener attached, under parent span | 1,061.6 ns | 2.45 | 3,152 B |

The two listener rows are one measurement: both go through a cache verb, so both are fully parented by
the operation span whatever the caller's ambient context is. A live trace listener costs a Hybrid L1
hit roughly 600 ns and ~2.5 KB, at either setting. What `LayerTracing` changes is not visible from a
cache verb by construction — see the decorator table.

An earlier revision of this table reported these two rows as 1,281 ns and 1,631 ns with a ±541 ns
interval on the second, and flagged that no mechanism explained the gap. Re-measured on a quiet
machine they land 33 ns apart with intervals of ±18 and ±26 ns, which is what the absence of a
mechanism should look like. The earlier pair was measurement noise, not behaviour.

## Comparison with v2

**Not measured.** v2's `ICacheService` no longer exists in this repository, so a same-process A/B is
not possible without reconstructing it. The v2 benchmark artifacts that were in
`benchmark/BenchmarkDotNet.Artifacts` measured different operations against a different API on
unknown hardware, so comparing against them would be misleading. If a v2/v3 comparison is needed,
run both packages from a separate harness on identical hardware.

## Not yet benchmarked

- Tag invalidation and `RemoveByTagAsync` throughput.
- Named-cache resolution (`ICacheProvider.GetCache`) — a frozen-dictionary probe; expected to be
  negligible, but unmeasured.
- Backplane publish/receive latency under load.
- Behaviour under sustained multi-pod load against a real Redis cluster.
