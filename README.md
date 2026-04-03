# [Caching.NET](http://Caching.NET)

Shared .NET caching package with three modes: **InMemory**, **Redis**, and **Hybrid** (in-memory + optional Redis with stampede protection). Exposes a single **ICacheService** abstraction.

Consumer solutions reference **Caching.NET NuGet package** (from a feed or local nupkg), not a project reference.

## Table of contents

- [Benefits](#benefits)
- [Modes](#modes)
- [Installation](#installation)
- [Registration](#registration)
- [Configuration Reference](#configuration-reference)
- [Per-call options](#per-call-options)
- [Telemetry](#telemetry)
- [Health Checks](#health-checks)
- [Operations](#operations)
- [Security](#security)

## Benefits

This package is mainly a **standardization + maintenance win**: one tested implementation, one configuration model, and one abstraction (`ICacheService`) reused everywhere.

- **Less duplicate code**: avoids re-creating the same cache glue (serialization, expirations, DI wiring, fallbacks) across multiple apps.
- **Fewer production bugs**: fixes and behavior changes happen **once** in the shared package instead of being copied (and often diverging) across repos.
- **Faster onboarding**: teams learn one API and one set of conventions; new services get caching by adding the package + configuration.
- **Safer operations**: you can disable caching (`Enabled=false`) without changing application code — takes effect immediately without restart (hot-reloadable).
- **Hybrid stampede protection**: prevents thundering-herd spikes on hot keys (many concurrent misses triggering the same expensive factory).

It also improves runtime performance when caching is effective:

- **Latency reduction on cache hits**: often **10x-100x faster** (memory/Redis lookup vs a DB/API call).
- **Code saved**: this repo's caching implementation is ~**980 lines of C#** under `src/Caching.NET` (excluding `bin/` and `obj/`). Reusing the package avoids re-writing and maintaining that caching plumbing in every application.

## Modes

| Mode         | Description |
| ------------ | ----------- |
| **InMemory** | In-process memory cache only. No distributed tier. Tag APIs are **ignored** (no-op); `localExpiration` parameters are accepted but ignored. |
| **Redis**    | Distributed Redis only. Requires `RedisConnectionString`. Serialization via System.Text.Json. Tag APIs are **ignored** (no-op); `localExpiration` parameters are accepted but ignored. |
| **Hybrid**   | In-memory + optional Redis. Uses `Microsoft.Extensions.Caching.Hybrid` for stampede protection and two-tier caching. `expiration` controls the overall (distributed) lifetime; `localExpiration` controls the in-memory tier. **Tag APIs are supported** via HybridCache. Set `RedisConnectionString` to add Redis; omit for in-memory-only Hybrid. |

### Feature support by mode

| Feature                       | InMemory        | Redis           | Hybrid    |
| ----------------------------- | --------------- | --------------- | --------- |
| `localExpiration` parameter   | Ignored         | Ignored         | Supported |
| Tag APIs (`RemoveByTagAsync`) | Ignored (no-op) | Ignored (no-op) | Supported |

When **Enabled** is false, `RoutingCacheService` short-circuits all operations: `GetOrCreateAsync` runs the factory directly, and all other methods are no-ops. `ICacheService` is always registered regardless of the `Enabled` flag, so consumers can depend on it without feature flags or null checks. The `Enabled` flag is **hot-reloadable** — flipping it in `appsettings.json` takes effect immediately without restart.

See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for detailed behavior of each mode and type.

## Installation

### Step 1: Authenticate with GitHub Packages

```bash
dotnet nuget add source https://nuget.pkg.github.com/baps-apps/index.json \
  --name github \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text
```

Create a PAT at: `https://github.com/settings/tokens` (requires `read:packages` permission).

### Step 2: Install package

```bash
dotnet add package Caching.NET --source github
```

## Registration

Caching.NET provides four `AddCaching` overloads through a fluent builder API. Pick the pattern that fits your app.

### Zero-config (prototyping / simple apps)

Hybrid mode (in-memory only), enabled, 10-minute default expiration. No appsettings section needed.

```csharp
builder.Services.AddCaching();
```

### Config-file driven

Reads from the `CacheOptions` section in `appsettings.json`.

```csharp
builder.Services.AddCaching(builder.Configuration);
```

### Fluent code-first

Programmatic configuration with IntelliSense. No config file needed.

```csharp
builder.Services.AddCaching(cache => cache
    .UseInMemory()
    .WithDefaultExpiration(TimeSpan.FromMinutes(15))
    .WithMemorySizeLimit(256)
    .WithMaximumKeyLength(512)
    .WithFactoryTimeout(TimeSpan.FromSeconds(30))
    .WithOpenTelemetry()
    .WithHealthChecks());
```

### Config-file + fluent overrides (recommended)

Base configuration from `appsettings.json`, with fluent additions. Fluent settings take precedence on conflict.

```csharp
builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithOpenTelemetry()
    .WithHealthChecks());
```

### All builder methods

| Method | Description |
|--------|-------------|
| `UseInMemory()` | Set mode to InMemory |
| `UseRedis(string connectionString)` | Set mode to Redis with connection string |
| `UseRedis(Action<ConfigurationOptions>)` | Set mode to Redis with programmatic StackExchange.Redis config |
| `UseHybrid()` | Set mode to Hybrid (in-memory only, no Redis) |
| `UseHybrid(string connectionString)` | Set mode to Hybrid with Redis backend |
| `WithDefaultExpiration(TimeSpan)` | Default TTL for cache entries |
| `WithDefaultLocalExpiration(TimeSpan)` | Default TTL for the in-memory tier in Hybrid mode |
| `WithMaximumPayloadBytes(long)` | Skip caching entries larger than this |
| `WithMaximumKeyLength(int)` | Reject keys longer than this |
| `WithMemorySizeLimit(int mb)` | Cap IMemoryCache size in MB |
| `WithFactoryTimeout(TimeSpan)` | Cancel slow factories after this duration |
| `WithInstanceName(string)` | Redis key prefix for multi-service clusters |
| `WithStrictCertificateValidation()` | Enforce strict TLS certificate validation for Redis |
| `WithOpenTelemetry()` | Enable metrics and traces via `System.Diagnostics` |
| `WithHealthChecks(string name)` | Register ASP.NET Core health check |
| `Disable()` | Explicitly disable caching (factory passthrough) |

### Complete examples

**Redis with full options:**

```csharp
builder.Services.AddCaching(cache => cache
    .UseRedis("localhost:6379,abortConnect=false")
    .WithInstanceName("myapp:")
    .WithDefaultExpiration(TimeSpan.FromMinutes(10))
    .WithMaximumPayloadBytes(5_000_000)
    .WithMaximumKeyLength(1024)
    .WithStrictCertificateValidation()
    .WithOpenTelemetry()
    .WithHealthChecks());
```

**Redis with programmatic ConfigurationOptions:**

```csharp
builder.Services.AddCaching(cache => cache
    .UseRedis(redis =>
    {
        redis.EndPoints.Add("redis-primary", 6379);
        redis.EndPoints.Add("redis-replica", 6380);
        redis.Password = Environment.GetEnvironmentVariable("REDIS_PASSWORD");
        redis.ConnectTimeout = 5000;
        redis.SyncTimeout = 3000;
        redis.AbortOnConnectFail = false;
    })
    .WithInstanceName("myapp:")
    .WithOpenTelemetry());
```

**Hybrid (production — two-tier with Redis):**

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithDefaultExpiration(TimeSpan.FromMinutes(15))
    .WithDefaultLocalExpiration(TimeSpan.FromMinutes(5))
    .WithInstanceName("myapp:")
    .WithMemorySizeLimit(128)
    .WithMaximumPayloadBytes(10_000_000)
    .WithFactoryTimeout(TimeSpan.FromSeconds(30))
    .WithOpenTelemetry()
    .WithHealthChecks());
```

**Explicitly disabled (testing / staging):**

```csharp
builder.Services.AddCaching(cache => cache.Disable());
```

`ICacheService` still resolves — all calls pass through to factories.

### Quick start

Inject and use `ICacheService` in any service or controller:

```csharp
public class MyService(ICacheService cache)
{
    public async Task<MyDto> GetAsync(string id, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(
            "item:" + id,
            async _ => await LoadFromDb(id, ct),
            expiration: TimeSpan.FromMinutes(10),
            localExpiration: TimeSpan.FromMinutes(5),
            cancellationToken: ct);
    }
}
```

### Startup validation

Call `ValidateCacheRegistration()` after building the host to fail fast on DI misconfiguration:

```csharp
var app = builder.Build();
app.Services.ValidateCacheRegistration();
```

## Configuration Reference

All options available in the `CacheOptions` section of `appsettings.json`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable/disable caching. Hot-reloadable at runtime. |
| `Mode` | `string` | `Hybrid` | `InMemory`, `Redis`, or `Hybrid` |
| `DefaultExpiration` | `string` | — | Default TTL (e.g. `"00:10:00"` for 10 min). Falls back to 10 min if unset. |
| `DefaultLocalExpiration` | `string` | — | In-memory tier TTL for Hybrid (e.g. `"00:05:00"`). Falls back to 5 min if unset. |
| `RedisConnectionString` | `string` | — | Required for Redis mode; optional for Hybrid (omit for in-memory-only). |
| `RedisInstanceName` | `string` | — | Key prefix for multi-service clusters (e.g. `"myapp:"`). |
| `MaximumPayloadBytes` | `long` | — | Skip caching entries larger than this (min 1024). |
| `MaximumKeyLength` | `int` | — | Bypass cache for keys longer than this (1–4096). |
| `MemorySizeLimitMb` | `int` | — | Cap IMemoryCache at this many MB. |
| `FactoryTimeout` | `string` | — | Cancel slow factories (e.g. `"00:00:30"`). Max 5 min. |
| `FailOpen` | `bool` | `true` | Fall back to factory on cache failure instead of throwing. |
| `ThrowOnFailure` | `bool` | `false` | Throw on cache failures (only applies when `FailOpen=false`). |
| `StrictRedisCertificateValidation` | `bool` | `false` | Reject any TLS certificate errors including hostname mismatches. |

### Example: Hybrid with Redis

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "Hybrid",
    "RedisConnectionString": "localhost:6379",
    "RedisInstanceName": "myapp:",
    "DefaultExpiration": "00:10:00",
    "DefaultLocalExpiration": "00:05:00",
    "MaximumPayloadBytes": 1048576,
    "MaximumKeyLength": 256
  }
}
```

Omit `RedisConnectionString` for Hybrid to run in-memory-only (still with stampede protection).

### Redis serialization

To use custom JSON options for Redis, register `CacheSerializerOptions` before or after `AddCaching`:

```csharp
builder.Services.Configure<Caching.NET.Options.CacheSerializerOptions>(o =>
{
    o.JsonSerializerOptions = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };
});
```

## Per-call options

Use `CacheCallOptions` with the extension overloads for per-request control:

| Option | Description |
|--------|-------------|
| **OverrideMode** | Use a different cache mode for this call (e.g. `Hybrid` → `InMemory` for a single key). |
| **BypassCache** | Skip the cache entirely — factory always runs, result is not cached. |
| **ForceRefresh** | Factory always runs; result is written back to the cache. Use to refresh stale data. |

```csharp
using Caching.NET.Extensions;
using Caching.NET.Options;

// Force a specific key to use InMemory regardless of global mode
var callOptions = new CacheCallOptions { OverrideMode = CacheMode.InMemory };
var result = await cache.GetOrCreateAsync(
    "hot:" + id,
    async _ => await LoadFromDb(id, ct),
    callOptions,
    expiration: TimeSpan.FromMinutes(5),
    cancellationToken: ct);

// Bypass cache entirely for debugging
var callOptions = new CacheCallOptions { BypassCache = true };
var result = await cache.GetOrCreateAsync(
    "item:" + id,
    async _ => await LoadFromDb(id, ct),
    callOptions,
    cancellationToken: ct);
```

## Telemetry

Caching.NET provides opt-in observability via the `ICacheTelemetry` abstraction. Enable it with one builder call:

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid()
    .WithOpenTelemetry());
```

Then register the meter and activity source in your OpenTelemetry SDK:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("Caching.NET.Cache"))
    .WithTracing(tracing => tracing.AddSource("Caching.NET.Cache"));
```

Emits four counters (`cache.requests`, `cache.hits`, `cache.misses`, `cache.failures`) and error-only traces. Without `WithOpenTelemetry()`, a zero-cost no-op is used.

For full details — metrics reference, trace tags, custom providers, dashboard queries, and alerting recommendations — see **[docs/TELEMETRY.md](docs/TELEMETRY.md)**.

## Health Checks

Caching.NET includes a built-in health check that verifies the cache pipeline is operational:

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithHealthChecks());

var app = builder.Build();
app.MapHealthChecks("/health");
```

The health probe runs a lightweight `GetOrCreateAsync` on a synthetic key. It respects `FailOpen` semantics — when `FailOpen=true` (default) and Redis is down, the probe still reports healthy because requests will succeed via factory fallback.

For full details — probe logic, FailOpen interaction, Kubernetes probes, combining with Redis health checks, and troubleshooting — see **[docs/HEALTH-CHECKS.md](docs/HEALTH-CHECKS.md)**.

## Golden path (recommended production setup)

```csharp
using Caching.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithOpenTelemetry()
    .WithHealthChecks());

builder.Services.AddControllers();

var app = builder.Build();

app.Services.ValidateCacheRegistration();
app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
```

With `appsettings.json`:

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "Hybrid",
    "RedisConnectionString": "localhost:6379",
    "RedisInstanceName": "myapp:",
    "DefaultExpiration": "00:10:00",
    "DefaultLocalExpiration": "00:05:00"
  }
}
```

This gives you config-driven mode/connection with fluent opt-in for telemetry and health checks. Disable caching at runtime by setting `Enabled=false` in appsettings — no restart required.

## Operations

For production runbooks (switching modes, disabling cache during incidents, interpreting logs, tuning), see [docs/OPERATIONS.md](docs/OPERATIONS.md).

## Security

- **Do not cache secrets** (tokens, passwords, API keys). Avoid caching PII in shared caches; the library truncates keys in logs to reduce exposure.
- **Redis key prefixing:** Use `WithInstanceName()` or `RedisInstanceName` (e.g. `"myapp:"`) so keys are namespaced per application or tenant.
- **TLS:** Use `WithStrictCertificateValidation()` in production to enforce strict Redis TLS validation.

## Versioning and compatibility

- Caching.NET follows **Semantic Versioning**:
  - **MAJOR:** breaking API/behavior changes (for example, `ICacheService` semantics).
  - **MINOR:** new features and configuration options, backwards compatible.
  - **PATCH:** bug fixes and internal improvements only.
- The library currently targets **.NET 10** (`net10.0`) for the core package, tests, and sample app.
