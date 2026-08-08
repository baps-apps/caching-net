# Benchmarks

Measured numbers for Caching.NET v3.0.0. Nothing in this repository claims a performance improvement
without a run behind it.

## How to reproduce

```bash
cd benchmark/Caching.NET.Benchmark

# Redis-free suites
dotnet run -c Release -- --filter '*InMemoryBenchmarks*'
dotnet run -c Release -- --filter '*SerializationBenchmarks*'
dotnet run -c Release -- --filter '*TelemetryOverheadBenchmarks*'

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

| Method | Concurrency | Mean | Allocated |
|---|---:|---:|---:|
| L1 hit | 1 | 284.8 ns | 512 B |
| L1 miss (no factory) | 1 | 234.4 ns | 511 B |
| Factory execution | 1 | 2,246.2 ns | 4,272 B |
| Concurrent get-or-set on one key | 1 | 415.4 ns | 720 B |
| L1 hit | 64 | 288.4 ns | 512 B |
| L1 miss (no factory) | 64 | 231.6 ns | 512 B |
| Factory execution | 64 | 2,288.7 ns | 4,272 B |
| Concurrent get-or-set on one key | 64 | 25,001.2 ns | 44,672 B |

Reading: a hit costs ~285 ns and does not degrade from 1 to 64 concurrent callers. The
"concurrent get-or-set" row fans out N awaits over one already-cached key, so its cost scales with N
(64 × ~390 ns) — it measures the fan-out, not lock contention.

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

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Hit, telemetry disabled | 115.5 ns | 1.00 | 192 B |
| Hit, metrics enabled, no trace listener | 305.3 ns | 2.64 | 543 B |
| Hit, trace listener attached | 476.3 ns | 4.12 | 544 B |

Telemetry is **not** free on the in-memory hit path: metrics cost roughly 190 ns and 350 B per hit,
and an attached trace listener roughly 360 ns.

An earlier revision of this table showed all three arms within noise of each other. That was an
artefact: the event-bridge subscription was installed regardless of `Observability.EnableMetrics`, so
the "disabled" arm still paid for the engine building event arguments and queueing a dispatch on
every operation. Turning metrics off now skips the subscription entirely, which is where the
difference comes from.

`EnableTracing` and `EnableMetrics` still default to `true` — 190 ns against a cache hit that saves a
database round trip is the right default for a service, and the cost is bounded and predictable. Turn
metrics off for a cache on a genuinely hot inner loop where the hit itself is the workload.

`ActivitySource.HasListeners()` and the metrics-enabled flag are still checked before any attribute
value is built, so no *attribute* is allocated when nobody is listening; the residual cost is the
engine's event dispatch, not Caching.NET's recording.

## Redis and Hybrid

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Hybrid L1 hit | 870.0 ns | 1.00 | 1.56 KB |
| Redis mode miss | 105,923.9 ns | 121.8 | 2.98 KB |
| Redis mode hit | 114,680.6 ns | 131.9 | 6.70 KB |
| Hybrid L2 hit (L1 bypassed) | 108,064.1 ns | 124.3 | 7.79 KB |
| Hybrid full miss + factory | 132,713.5 ns | 152.7 | 12.95 KB |

The headline: **a Hybrid L1 hit is ~124× faster than reaching Redis** even over loopback, where the
network cost is near zero. On a real cluster the gap widens. This is the entire argument for Hybrid
mode in a multi-pod deployment, and the reason Redis mode is documented as "correctness over
latency" rather than presented as a general-purpose default.

Redis-mode hit and Hybrid L2 hit are equivalent, as expected — the same round trip and the same
deserialization.

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
