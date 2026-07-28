# Benchmarks

Run on macOS arm64, .NET 10. The authoritative source is `benchmark/Caching.NET.Benchmark/bench-baseline.json`; the `GetOrCreateAsync` InMemory rows below are measured, the rest are illustrative placeholders and are marked as such.

The perf gate (`benchmark/perf-gate.ps1`) fails on a >10% regression against that baseline. A baseline of `AllocatedBytes: 0` means "must stay allocation-free" — any allocation fails.

To regenerate: `pwsh scripts/dev.ps1 bench`

## GetOrCreateAsync

| Mode | Scenario | Mean (ns) | Allocated (B) |
|------|----------|----------:|--------------:|
| InMemory | Hit hot key | ~140 | ~400 |
| InMemory | Miss + factory | ~620 | ~1 096 |
| Redis    | Hit hot key | ~250 000 | ~400 |
| Hybrid   | Hit L1 | ~60 | 0 |

The InMemory rows are measured with **no OTel pipeline attached**, which is the state `CacheCallRecorder.Start` optimizes for: with no `ActivityListener` and no `MeterListener`, it returns `null` and the call allocates nothing for telemetry — these numbers match the pre-telemetry ones. Attach a metrics or tracing pipeline and a recorder (plus the factory wrapper on `get_or_create`) is allocated per call; that is the cost of the signal, and it is only paid by consumers who asked for it.

The Redis and Hybrid rows are not backed by a benchmark in this project; treat them as illustrative until one exists.

Micro-benchmarks do not yet surface `cache.serialize.duration` / `cache.deserialize.duration`; use production metrics or ad-hoc profiling for serializer regressions.

## Serializer comparison

| Serializer | Payload | Mean (ns) | Allocated (B) |
|------------|--------:|----------:|--------------:|
| JsonCacheSerializer (reflection) | 100 B | ~2 000 | ~800 |
| JsonCacheSerializer (source-gen) | 100 B | ~1 000 | ~200 |
| MessagePackCacheSerializer | 100 B | ~700 | ~200 |

## Batch ops (InMemory)

Implementations use **synchronous** `IMemoryCache`/`MemoryCache` access in batch paths (`TryGetValue`, `Set`, `Remove`) — no per-key `await` overhead.

| N | GetMany Mean (µs) | Allocated (B) |
|---:|------------------:|--------------:|
| 10 | ~6 | ~1 200 |
| 100 | ~60 | ~12 000 |

## Perf gate

The local perf gate (`pwsh benchmark/perf-gate.ps1`, or `pwsh scripts/dev.ps1 bench:gate` after a bench run) fails when any benchmark's `Mean` or `Allocated` regresses > 10% vs `bench-baseline.json`. Update the baseline only after a deliberate perf change has landed and been reviewed.
