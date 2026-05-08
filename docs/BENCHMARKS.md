# Benchmarks

Run on macOS arm64, .NET 10. Numbers below are illustrative placeholders; the authoritative source is `benchmark/Caching.NET.Benchmark/bench-baseline.json`.

To regenerate: `pwsh scripts/dev.ps1 bench`

## GetOrCreateAsync

| Mode | Scenario | Mean (ns) | Allocated (B) |
|------|----------|----------:|--------------:|
| InMemory | Hit hot key | ~200 | 0 |
| InMemory | Miss + factory | ~2 000 | ~400 |
| Redis    | Hit hot key | ~250 000 | ~400 |
| Hybrid   | Hit L1 | ~60 | 0 |

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
