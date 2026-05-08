# Caching.NET Health Checks

> **v2 note:** The builder API is unchanged. Use `WithKeyPrefix(...)` (not `WithInstanceName`) when configuring the cache mode alongside health checks. See [MIGRATION-V1-TO-V2.md](MIGRATION-V1-TO-V2.md) for the full v2 change list.

> **Encapsulation:** The concrete `IHealthCheck` implementations (`CachingHealthCheck`, `CachingLivenessHealthCheck`) are **assembly-internal** types. Consumers register them only via `WithHealthChecks()` / `AddCachingHealthChecks(...)` — do not reference or construct those types from application code.

Caching.NET includes a built-in health check that verifies the cache pipeline is operational. It integrates with ASP.NET Core's standard health check system.

## Quick Start

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithHealthChecks());           // ← registers CachingHealthCheck

var app = builder.Build();
app.MapHealthChecks("/health");     // ← exposes at /health endpoint
```

That's it. Hit `GET /health` and you'll get a status for your cache pipeline.

## How It Works

### Registration Flow

```
WithHealthChecks() / WithHealthChecks(splitLivenessReadiness: true)
  → sets RegisterHealthChecks = true on CachingBuilder
    → ServiceCollectionExtensions calls:
        services.AddHealthChecks()
            .AddCachingHealthChecks(name: "caching-net", splitLivenessReadiness: …)
              → registers CachingHealthCheck only (default), or
                 CachingLivenessHealthCheck + CachingHealthCheck with tags (when split)
```

### Probe Logic

`CachingHealthCheck` implements `IHealthCheck` and runs the following on each health check request:

```
GET /health
  → CachingHealthCheck.CheckHealthAsync()
    ├─ Caching disabled?
    │    → Healthy("Caching is disabled via configuration.")
    │       (no backend registration; multiplexer may be absent — still healthy)
    │
    └─ Caching enabled?
         ├─ Mode is Redis or Hybrid?
         │    ├─ IConnectionMultiplexer missing? → Unhealthy("Redis multiplexer is unavailable…")
         │    ├─ !IsConnected? → Unhealthy("Redis multiplexer is disconnected.")
         │    └─ await PING
         │
         └─ ExistsAsync(probeKey)
              ├─ true  → Healthy("Caching.NET is reachable and operational.")
              └─ false → GetOrCreateAsync(probeKey, () => true, expiration: 5min)
                        ├─ Success → Healthy("Caching.NET is reachable and operational.")
                        └─ Exception → Unhealthy("Caching.NET health probe failed.")
```

**When caching is disabled**: Returns `Healthy` immediately. No Redis ping or cache write runs (backends are not registered in that configuration).

**When caching is enabled (InMemory)**: Skips multiplexer checks; the probe exercises `GetOrCreateAsync` through the in-memory stack.

**When caching is enabled (Redis / Hybrid)**: Requires a connected `IConnectionMultiplexer` from DI (normal `AddCaching` path). The check **PINGs Redis** before calling `GetOrCreateAsync`, so a dead or partitioned primary fails the check **before** `FailOpen` could mask Redis errors on the cache path.

**Probe key**: `caching-net:health:probe:{MachineName}:{ProcessId}` — avoids cross-replica contention on a single Redis key.

### FailOpen Interaction

For **Redis and Hybrid**, reachability is gated by **multiplexer connection state + PING**, not by whether `GetOrCreateAsync` would fall back when `FailOpen=true`. If Redis is down, the check is typically **Unhealthy** regardless of `FailOpen`.

For **InMemory**, the probe only runs through `ICacheService`; `FailOpen` affects backend errors on real keys the same way as in app code, but the synthetic probe rarely hits that path.

## Configuration Options

### Custom Health Check Name

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid()
    .WithHealthChecks("my-cache-check"));   // custom name instead of "caching-net"
```

### Custom Failure Status

If you register health checks manually instead of through the builder:

```csharp
builder.Services.AddCaching(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddCachingHealthChecks(
        name: "caching-net",
        failureStatus: HealthStatus.Degraded);   // Degraded instead of Unhealthy
```

### Standalone Registration

`AddCachingHealthChecks` is a public extension method on `IHealthChecksBuilder`, so you can use it independently from the fluent builder:

```csharp
builder.Services.AddCaching(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddCachingHealthChecks();
```

## Combining with Redis Health Checks

`CachingHealthCheck` is intentionally lightweight — it validates the Caching.NET pipeline, not Redis infrastructure. For production, combine it with a dedicated Redis health check:

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithHealthChecks());

// Add dedicated Redis connectivity check (requires AspNetCore.HealthChecks.Redis package)
builder.Services.AddHealthChecks()
    .AddRedis(
        redisConnectionString: "localhost:6379",
        name: "redis");

var app = builder.Build();
app.MapHealthChecks("/health");
```

This gives you two health checks:
- **`caching-net`**: Is the Caching.NET pipeline working end-to-end?
- **`redis`**: Is the Redis server reachable and responding to PING?

## Kubernetes Probes

### Built-in liveness + readiness split

Opt in when registering caching:

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithHealthChecks(splitLivenessReadiness: true));   // registers two checks — see below
```

This registers:

| Check name (default prefix `caching-net`) | Tag | Behavior |
|-------------------------------------------|-----|----------|
| `caching-net-liveness` | `liveness` | Disabled cache → Healthy. InMemory → Healthy. Redis/Hybrid → multiplexer present and `IsConnected` only (no PING, no probe write). |
| `caching-net-readiness` | `readiness` | Same as the single-check path: PING + `GetOrCreateAsync` probe when enabled. |

Map endpoints using tags:

```csharp
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("liveness"),
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("readiness") || r.Name == "redis",
});
```

Manual registration without the fluent flag:

```csharp
builder.Services.AddHealthChecks()
    .AddCachingHealthChecks(splitLivenessReadiness: true);
```

### Liveness + Readiness (single combined check)

If you use the default **one** `CachingHealthCheck` (readiness-style), you can still split probes at the HTTP layer:

```csharp
// Liveness: is the app running?
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false   // no checks — just confirms the app responds
});

// Readiness: is the app ready to serve traffic?
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Name == "caching-net" || check.Name == "redis"
});
```

```yaml
# kubernetes deployment
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  periodSeconds: 5
```

### When to Use Each Probe

| Probe | Includes Cache Check? | Purpose |
|---|---|---|
| Liveness | No | Restart if the process is hung |
| Readiness | Yes | Remove from load balancer when `caching-net` is Unhealthy (Redis/Hybrid includes PING; InMemory probes the local cache path) |
| Startup | Optional | Gate traffic until initial cache warm-up completes |

**With `FailOpen=true`** (default): Application requests may still succeed when Redis errors are swallowed, but **`CachingHealthCheck` still fails when Redis/Hybrid cannot connect or PING** (see above). Combine with a dedicated Redis health check if you need finer-grained signals.

**With `FailOpen=false`**: Cache operations can throw on backend failure; align readiness with your SLOs and the Redis probe outcome.

## Health Check Response

### Healthy Response (HTTP 200)

```json
{
  "status": "Healthy",
  "entries": {
    "caching-net": {
      "status": "Healthy",
      "description": "Caching.NET is reachable and operational."
    }
  }
}
```

### Unhealthy Response (HTTP 503)

```json
{
  "status": "Unhealthy",
  "entries": {
    "caching-net": {
      "status": "Unhealthy",
      "description": "Caching.NET health probe failed.",
      "exception": "StackExchange.Redis.RedisConnectionException: ..."
    }
  }
}
```

### Disabled Response (HTTP 200)

```json
{
  "status": "Healthy",
  "entries": {
    "caching-net": {
      "status": "Healthy",
      "description": "Caching is disabled via configuration."
    }
  }
}
```

> Note: To get detailed JSON responses, configure the health check endpoint with a custom `ResponseWriter`. The default ASP.NET Core response is just the status text.

## Implementation Details

- **Probe key**: `caching-net:health:probe:{MachineName}:{ProcessId}` with a 5-minute TTL
- **Factory**: Returns `true` — minimal allocation, no external calls beyond Redis PING when applicable
- **Error logging**: Failures are logged at `Error` level with the probe key
- **DI dependencies**: `ICacheService`, `IOptions<CacheOptions>`, `ILogger<CachingHealthCheck>`, optional `IConnectionMultiplexer` (resolved when registered; required for Redis/Hybrid probes)
- **Registered as**: `IHealthCheck` via `AddCheck<CachingHealthCheck>(...)`
- **Default failure status**: `HealthStatus.Unhealthy` (configurable)

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Health still Unhealthy when `Enabled=false` | Unexpected — check you are not filtering a different check | When disabled, description should mention *Caching is disabled via configuration* |
| Redis down but check Healthy | Unlikely for Redis/Hybrid — multiplexer disconnected path should fail | Verify mode, DI registration, and that you are hitting `CachingHealthCheck` |
| Health returns Unhealthy on startup | Redis not yet available for Redis/Hybrid | Wait for Redis before marking ready, relax readiness predicate, or fix networking |
| Health check not appearing at `/health` | Missing `app.MapHealthChecks("/health")` | Add the endpoint mapping after `builder.Build()` |
| Endpoint returns 404 | `app.MapHealthChecks(...)` missing | Add endpoint mapping after `builder.Build()` |
