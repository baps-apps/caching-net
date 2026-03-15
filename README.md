# [Caching.NET](http://Caching.NET)

Shared .NET caching package with three modes: **InMemory**, **Redis**, and **Hybrid** (in-memory + optional Redis with stampede protection). Exposes a single **ICacheService** abstraction.

Consumer solutions reference **Caching.NET NuGet package** (from a feed or local nupkg), not a project reference.

## Table of contents

- [Benefits](#benefits)
- [Modes](#modes)
- [Installation](#installation)
- [Configuration](#configuration)
- [Per-call options](#per-call-options)
- [Telemetry](#telemetry)
- [Operations](#operations)
- [Security](#security)

## Benefits

This package is mainly a **standardization + maintenance win**: one tested implementation, one configuration model, and one abstraction (`ICacheService`) reused everywhere.

- **Less duplicate code**: avoids re-creating the same cache glue (serialization, expirations, DI wiring, fallbacks) across multiple apps.
- **Fewer production bugs**: fixes and behavior changes happen **once** in the shared package instead of being copied (and often diverging) across repos.
- **Faster onboarding**: teams learn one API and one set of conventions; new services get caching by adding the package + configuration.
- **Safer operations**: you can disable caching (`Enabled=false`) without changing application code, which helps during incidents and troubleshooting.
- **Hybrid stampede protection**: prevents thundering-herd spikes on hot keys (many concurrent misses triggering the same expensive factory).

It also improves runtime performance when caching is effective:

- **Latency reduction on cache hits**: often **10×–100× faster** (memory/Redis lookup vs a DB/API call).
- **Code saved**: this repo’s caching implementation is ~**980 lines of C#** under `src/Caching.NET` (excluding `bin/` and `obj/`). Reusing the package avoids re-writing and maintaining that caching plumbing in every application.

## Modes


| Mode         | Description                                                                                                                                                                                                                                                                                                                                         |
| ------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **InMemory** | In-process memory cache only. No distributed tier. Tag APIs are **ignored** (no-op); `localExpiration` parameters are accepted but ignored.                                                                                                                                                                                                         |
| **Redis**    | Distributed Redis only. Requires `RedisConnectionString`. Serialization via System.Text.Json. Tag APIs are **ignored** (no-op); `localExpiration` parameters are accepted but ignored.                                                                                                                                                              |
| **Hybrid**   | In-memory + optional Redis. Uses `Microsoft.Extensions.Caching.Hybrid` for stampede protection and two-tier caching. `expiration` controls the overall (distributed) lifetime; `localExpiration` controls the in-memory tier. **Tag APIs are supported** via HybridCache. Set `RedisConnectionString` to add Redis; omit for in-memory-only Hybrid. |


### Feature support by mode


| Feature                       | InMemory        | Redis           | Hybrid    |
| ----------------------------- | --------------- | --------------- | --------- |
| `localExpiration` parameter   | Ignored         | Ignored         | Supported |
| Tag APIs (`RemoveByTagAsync`) | Ignored (no-op) | Ignored (no-op) | Supported |


When **Enabled** is false (the default), a no-op implementation (`NoOpCacheService`) is registered: `GetOrCreateAsync` always runs the factory, and `SetAsync` / `Remove`* are no-op. This allows consumers to keep depending on `ICacheService` without feature flags or null checks, and it means simply adding the package + `AddCaching` will not break applications that have not yet configured caching.

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

Consumers reference the **Caching.NET NuGet package**, not a project reference.

## Quick Start

### 1. Configure `appsettings.json`

Minimal Hybrid configuration with sensible defaults (explicitly enabling caching):

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

> For InMemory/Redis-only examples and advanced options (e.g., `MaximumPayloadBytes`, `MaximumKeyLength`), see the **Configuration** section below.

### 2. Add to `Program.cs`

```csharp
using Caching.NET.Abstractions;
using Caching.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register Caching.NET using configuration-bound CacheOptions
builder.Services.AddCaching(builder.Configuration);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
```

Inject and use `ICacheService` in your application services or controllers:

```csharp
using Caching.NET.Abstractions;

public class MyService(ICacheService cache)
{
    public async Task<MyDto> GetAsync(string id, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(
            "item:" + id,
            async _ => await LoadFromDb(id, ct),
            expiration: TimeSpan.FromMinutes(10),          // overall expiration
            localExpiration: TimeSpan.FromMinutes(5),      // for Hybrid: in-memory tier expiration (ignored by InMemory/Redis)
            cancellationToken: ct);
    }
}
```

### 3. Run and verify

```bash
dotnet run
```

### 4. Redis-focused sample wiring

The `Caching.NET.Sample` project shows a typical ASP.NET Core app configured for Hybrid (in-memory + Redis):

- `samples/Caching.NET.Sample/appsettings.json` contains a `CacheOptions` section with:
  - `Mode = "Hybrid"`
  - `RedisConnectionString = "localhost:6379"` (replace with your connection string, ideally from secrets)
  - `RedisInstanceName = "sampleapp:"`
- `Program.cs` registers:
  - `AddCaching(builder.Configuration)`
  - `AddHealthChecks().AddCachingHealthChecks("caching-net")`
  - `MapHealthChecks("/health")`
- `WeatherForecastController` demonstrates `ICacheService.GetOrCreateAsync` with both `expiration` and `localExpiration`, which is especially useful in Hybrid mode (in-memory + Redis).

## Configuration

Use the **CacheOptions** section (constant: `Caching.NET.Configuration.CacheConfigurationKeys.CacheOptions`). Caching is **opt-in**: `Enabled` defaults to `false`, so you must explicitly set `"Enabled": true` in configuration to turn the pipeline on.

### Example: InMemory

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "InMemory",
    "DefaultExpiration": "00:10:00"
  }
}
```

### Example: Redis

```json
{
  "CacheOptions": {
    "Enabled": true,
    "Mode": "Redis",
    "RedisConnectionString": "localhost:6379",
    "DefaultExpiration": "00:10:00"
  }
}
```

### Example: Hybrid (in-memory + Redis)

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

Optional: `MaximumPayloadBytes`, `MaximumKeyLength` (for Hybrid).

### Per-call options

Use **`CacheCallOptions`** with the extension overloads for enterprise patterns:

| Option | Description |
|--------|-------------|
| **OverrideMode** | Use a different cache mode for this call (e.g. `Hybrid` → `InMemory` for a single key). |
| **BypassCache** | When `true`, the cache is not read or written; the factory is always run and the result returned without caching. Use for debugging or emergency "cache off" at a callsite. |
| **ForceRefresh** | When `true`, the factory is always run; the result is then written to the cache and returned. Use to refresh stale data without removing the key first. |

Example: override mode and bypass cache

```csharp
using Caching.NET.Extensions;
using Caching.NET.Options;

public class MyService(ICacheService cache)
{
    public Task<MyDto> GetHotLocalOnlyAsync(string id, CancellationToken ct)
    {
        var callOptions = new CacheCallOptions { OverrideMode = CacheMode.InMemory };
        return cache.GetOrCreateAsync(
            "hot:" + id,
            async _ => await LoadFromDb(id, ct),
            callOptions,
            expiration: TimeSpan.FromMinutes(5),
            localExpiration: TimeSpan.FromMinutes(5),
            cancellationToken: ct);
    }

    public Task<MyDto> GetBypassingCacheAsync(string id, CancellationToken ct)
    {
        var callOptions = new CacheCallOptions { BypassCache = true };
        return cache.GetOrCreateAsync(
            "item:" + id,
            async _ => await LoadFromDb(id, ct),
            callOptions,
            expiration: TimeSpan.FromMinutes(10),
            cancellationToken: ct);
    }
}
```

### Startup validation (optional)

After building the service provider (e.g. in `Program.cs`), call **`app.Services.ValidateCacheRegistration()`** to ensure `ICacheService` resolves and the configured mode is available. Fails fast on DI misconfiguration; does not probe Redis. See [docs/OPERATIONS.md](docs/OPERATIONS.md).

### Redis serialization

To use custom JSON options for Redis, register **`CacheSerializerOptions`** before or after `AddCaching`:

```csharp
builder.Services.Configure<Caching.NET.Options.CacheSerializerOptions>(o =>
{
    o.JsonSerializerOptions = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
});
builder.Services.AddCaching(builder.Configuration);
```

## Telemetry

Caching.NET does not take a hard dependency on any specific telemetry stack, but it provides a small telemetry abstraction and a default implementation:

- `ICacheTelemetry` – abstraction for cache hits, misses, errors, and factory timeouts.
- `NoopCacheTelemetry` – default no-op implementation registered by `AddCaching`.
- `OpenTelemetryCacheTelemetry` – optional implementation that uses `Meter` + `ActivitySource` so your existing OpenTelemetry.NET setup can export cache metrics and spans.

To enable OpenTelemetry-style telemetry:

```csharp
using Caching.NET.Abstractions;
using Caching.NET.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ICacheTelemetry, OpenTelemetryCacheTelemetry>();
builder.Services.AddCaching(builder.Configuration);
```

Then configure your OpenTelemetry `MeterProvider` / `TracerProvider` to listen to:

- Meter name: `Caching.NET.Cache`
- Activity source: `Caching.NET.Cache`

See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for full details on emitted metrics, tags, and spans.

## Golden path integration (recommended pattern)

A typical ASP.NET Core service using Caching.NET, health checks, and optional telemetry:

```csharp
using Caching.NET.Abstractions;
using Caching.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Bind and register CacheOptions + ICacheService (InMemory, Redis, or Hybrid)
builder.Services.AddCaching(builder.Configuration);

// Optional: Redis health check (requires AspNetCore HealthChecks Redis package)
builder.Services.AddHealthChecks()
    .AddRedis(
        redisConnectionString: builder.Configuration["CacheOptions:RedisConnectionString"]!,
        name: "redis");

// Optional: add your own OpenTelemetry decorator around ICacheService (see docs/IMPLEMENTATION.md)

builder.Services.AddControllers();

var app = builder.Build();

// Fail fast if cache registration is misconfigured
app.Services.ValidateCacheRegistration();

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
```

In a controller or service:

```csharp
public class WeatherService(ICacheService cache)
{
    public Task<WeatherDto[]> GetFiveDayAsync(CancellationToken ct)
    {
        return cache.GetOrCreateAsync(
            "weather:5day",
            async _ => await LoadFromApiAsync(ct),
            expiration: TimeSpan.FromMinutes(5),
            localExpiration: TimeSpan.FromMinutes(2),
            cancellationToken: ct);
    }
}
```

## Operations

For production runbooks (switching modes, disabling cache during incidents, interpreting logs, tuning), see [docs/OPERATIONS.md](docs/OPERATIONS.md).

## Security

- **Do not cache secrets** (tokens, passwords, API keys). Avoid caching PII in shared caches; the library truncates keys in logs to reduce exposure.
- **Redis key prefixing:** Use **`RedisInstanceName`** (e.g. `myservice:`) so keys are namespaced per application or tenant. See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) and [docs/OPERATIONS.md](docs/OPERATIONS.md).

## Versioning and compatibility

- Caching.NET follows **Semantic Versioning**:
  - **MAJOR:** breaking API/behavior changes (for example, `ICacheService` semantics).
  - **MINOR:** new features and configuration options, backwards compatible.
  - **PATCH:** bug fixes and internal improvements only.
- The library currently targets **.NET 10** (`net10.0`) for the core package, tests, and sample app. Future versions may multi-target additional TFMs; see [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for details.