# Caching.NET

Production-grade caching for high-throughput .NET services. One `ICacheService` abstraction. Three modes: **InMemory**, **Redis**, **Hybrid**. Stampede protection, Polly resilience, OpenTelemetry-native, AOT-friendly.

## Install

```bash
dotnet add package Caching.NET
```

Targets: `net8.0`, `net9.0`, `net10.0`. AOT/trim compatible when consumer supplies a `JsonSerializerContext`.

## Quickstart (zero-config)

```csharp
services.AddCaching(b => b.UseInMemory().WithKeyPrefix("orders-svc:v1"));
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
services.AddCaching(b => b.UseInMemory().WithKeyPrefix("svc:v1"));

// Redis distributed
services.AddCaching(b => b.UseRedis("localhost:6379").WithKeyPrefix("svc:v1"));

// Hybrid (Microsoft.Extensions.Caching.Hybrid: in-memory L1 + Redis L2)
services.AddCaching(b => b.UseHybrid("localhost:6379").WithKeyPrefix("svc:v1"));
```

## Production config (Amazon-scale)

```csharp
services.AddCaching(b => b
    .UseHybrid("rediss://elasticache.amzn.example:6380")
    .WithKeyPrefix("orders-svc:v1")
    .WithSerializer(new JsonCacheSerializer(MyJsonContext.Default)) // AOT/trim
    .WithTtlJitter(0.10)
    .WithStaleRefreshConcurrency(512)
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
