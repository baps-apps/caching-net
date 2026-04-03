# Caching.NET Health Checks

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
WithHealthChecks()
  → sets RegisterHealthChecks = true on CachingBuilder
    → ServiceCollectionExtensions calls:
        services.AddHealthChecks()
            .AddCachingHealthChecks(name: "caching-net")
              → registers CachingHealthCheck with ASP.NET Core
```

### Probe Logic

`CachingHealthCheck` implements `IHealthCheck` and runs the following on each health check request:

```
GET /health
  → CachingHealthCheck.CheckHealthAsync()
    ├─ Caching disabled?
    │    → Healthy("Caching is disabled via configuration.")
    │
    └─ Caching enabled?
         → GetOrCreateAsync("caching-net:health:probe", () => true, expiration: 5min)
           ├─ Success → Healthy("Caching.NET is reachable and operational.")
           └─ Exception → Unhealthy("Caching.NET health probe failed.")
```

**When caching is disabled**: Returns `Healthy` immediately. The cache is intentionally out of the request path, so there's nothing to probe.

**When caching is enabled**: Executes a real `GetOrCreateAsync` call with a synthetic key (`caching-net:health:probe`) and a trivial factory (`() => true`). This validates the entire pipeline — DI resolution, serialization, and backend connectivity (Redis, in-memory, or Hybrid).

### FailOpen Interaction

The health probe respects `FailOpen` semantics:

| `FailOpen` | Redis Down | Health Status | Why |
|---|---|---|---|
| `true` (default) | Yes | **Healthy** | Factory runs successfully — requests will still be served (just without cache) |
| `false` | Yes | **Unhealthy** | Exception propagates — requests would fail too |

This means the health check accurately reflects whether your **application** is healthy, not just whether Redis is up. With `FailOpen=true`, a Redis outage degrades performance but doesn't break functionality — so the health check correctly reports healthy.

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

### Liveness + Readiness

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
| Readiness | Yes | Remove from load balancer if cache/Redis is down (with `FailOpen=false`) |
| Startup | Optional | Gate traffic until initial cache warm-up completes |

**With `FailOpen=true`** (default): Even readiness checks report healthy when Redis is down, because the app can still serve requests via factory fallback. This is usually the right behavior — you don't want pods removed from rotation just because Redis had a blip.

**With `FailOpen=false`**: Readiness checks accurately report unhealthy when Redis is down, which removes the pod from the load balancer. Use this when your app genuinely cannot function without cache.

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

- **Probe key**: `caching-net:health:probe` with a 5-minute TTL
- **Factory**: Returns `true` — minimal allocation, no external calls
- **Error logging**: Failures are logged at `Error` level with the probe key
- **DI dependencies**: `ICacheService`, `IOptions<CacheOptions>`, `ILogger<CachingHealthCheck>`
- **Registered as**: `IHealthCheck` via `AddCheck<CachingHealthCheck>(...)`
- **Default failure status**: `HealthStatus.Unhealthy` (configurable)

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Health always returns Healthy even when Redis is down | `FailOpen=true` (default) — factory runs successfully | Expected behavior. Set `FailOpen=false` if you need strict Redis health gating |
| Health returns Unhealthy on startup | Redis not yet available | Ensure Redis is up before the app starts, or use `FailOpen=true` |
| Health check not appearing at `/health` | Missing `app.MapHealthChecks("/health")` | Add the endpoint mapping after `builder.Build()` |
| `InvalidOperationException` on startup | `WithHealthChecks()` called but `MapHealthChecks()` missing | Both registration and endpoint mapping are required |
