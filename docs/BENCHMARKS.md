# Benchmarks

Run on macOS arm64, .NET 10. Numbers below are illustrative placeholders; the authoritative source is `bench/Caching.NET.Bench/bench-baseline.json`.

To regenerate: `pwsh scripts/dev.ps1 bench`

## GetOrCreateAsync

| Mode | Scenario | Mean (ns) | Allocated (B) |
|------|----------|----------:|--------------:|
| InMemory | Hit hot key | ~200 | 0 |
| InMemory | Miss + factory | ~2 000 | ~400 |
| Redis    | Hit hot key | ~250 000 | ~400 |
| Hybrid   | Hit L1 | ~60 | 0 |

## Serializer comparison

| Serializer | Payload | Mean (ns) | Allocated (B) |
|------------|--------:|----------:|--------------:|
| JsonCacheSerializer (reflection) | 100 B | ~2 000 | ~800 |
| JsonCacheSerializer (source-gen) | 100 B | ~1 000 | ~200 |
| MessagePackCacheSerializer | 100 B | ~700 | ~200 |

## Batch ops (InMemory)

| N | GetMany Mean (µs) | Allocated (B) |
|---:|------------------:|--------------:|
| 10 | ~6 | ~1 200 |
| 100 | ~60 | ~12 000 |

## Perf gate

CI fails when any benchmark's `Mean` or `Allocated` regresses > 10% vs `bench-baseline.json`. Update the baseline only after a deliberate perf change has landed and been reviewed.

Run the gate: `pwsh bench/perf-gate.ps1`
