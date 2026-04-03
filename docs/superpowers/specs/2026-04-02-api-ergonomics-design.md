# Caching.NET API Ergonomics Redesign

**Date:** 2026-04-02
**Status:** Approved
**Scope:** Fluent builder API, zero-config defaults, NoOpCacheService elimination, hot-reloadable Enabled flag, sample project updates, doc updates
**Breaking:** Yes (major version bump required)

---

## Problem

1. When `CacheOptions.Enabled=false`, `NoOpCacheService` is registered instead of `RoutingCacheService`. Consumers that directly reference `NoOpCacheService` (e.g., `is NoOpCacheService` checks) are coupled to an implementation detail. More critically, in consuming projects the conditional DI registration caused startup crashes: `ValidateCacheRegistration()` would throw when `ICacheService` wasn't registered, and any controller/service injecting `ICacheService` would fail with a 500.

2. The only DI entry point is `AddCaching(IConfiguration)`, which requires a config-file section. There's no zero-config path and no fluent builder for code-first scenarios (tests, simple apps, programmatic configuration).

3. The `Enabled` flag cannot be toggled at runtime — it's read once at startup. For incident response, this means a restart is required to disable caching.

---

## Design

### 1. DI Registration & CachingBuilder API

A new `CachingBuilder` class provides the fluent API. Each builder method returns `CachingBuilder` (i.e., `return this;`) to enable fluent chaining. All `AddCaching` overloads on `IServiceCollection` return `IServiceCollection` (not the builder) to maintain the standard .NET chaining pattern. The builder is used internally via a lambda.

#### Entry Points

```csharp
// Zero-config: InMemory, enabled, 10min default expiration
services.AddCaching();

// Config-file only (existing consumers — signature preserved)
services.AddCaching(configuration);

// Code-first via fluent builder
services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithDefaultExpiration(TimeSpan.FromMinutes(15))
    .WithOpenTelemetry());

// Config-file base + fluent overrides (fluent wins on conflict)
services.AddCaching(configuration, cache => cache
    .WithOpenTelemetry()
    .WithHealthChecks());
```

#### Builder Methods

**Mode selection (mutually exclusive — last call wins):**

| Method | Effect |
|--------|--------|
| `UseInMemory()` | Sets Mode = InMemory |
| `UseRedis(string connectionString)` | Sets Mode = Redis + connection string |
| `UseRedis(Action<ConfigurationOptions> configure)` | Sets Mode = Redis + programmatic StackExchange.Redis config |
| `UseHybrid()` | Sets Mode = Hybrid (in-memory only, no Redis backend) |
| `UseHybrid(string connectionString)` | Sets Mode = Hybrid + Redis backend |

**Configuration:**

| Method | Effect |
|--------|--------|
| `WithDefaultExpiration(TimeSpan)` | Sets default cache entry expiration |
| `WithDefaultLocalExpiration(TimeSpan)` | Sets default local (in-memory) expiration for Hybrid |
| `WithMaximumPayloadBytes(long)` | Sets max payload size limit |
| `WithMaximumKeyLength(int)` | Sets max cache key length |
| `WithMemorySizeLimit(int megabytes)` | Sets IMemoryCache size limit |
| `WithFactoryTimeout(TimeSpan)` | Sets factory execution timeout |
| `WithInstanceName(string)` | Sets Redis key prefix |

**Features:**

| Method | Effect |
|--------|--------|
| `WithOpenTelemetry()` | Registers `OpenTelemetryCacheTelemetry` as `ICacheTelemetry` |
| `WithHealthChecks(string name = "caching-net")` | Registers `CachingHealthCheck` |
| `WithStrictCertificateValidation()` | Enables strict Redis TLS validation |
| `Disable()` | Sets Enabled = false (equivalent to config `"Enabled": false`) |

#### Internal Flow

1. `AddCaching` overload creates a `CachingBuilder` instance
2. If `IConfiguration` is provided, binds `CacheOptions` section first
3. If fluent lambda is provided, executes it (overriding config values)
4. Builder applies defaults for any unset values (InMemory, Enabled=true, 10min expiration)
5. Builder registers all required services based on resolved mode
6. `RoutingCacheService` is **always** registered as `ICacheService` singleton
7. Options validation runs at startup via `ValidateOnStart()`

### 2. RoutingCacheService Changes

#### Always Registered

`RoutingCacheService` is the **only** implementation ever registered as `ICacheService`. There is no conditional branching based on `Enabled`.

#### Hot-Reloadable Enabled Flag

- Constructor takes `IOptionsMonitor<CacheOptions>` (replacing `IOptions<CacheOptions>`)
- On every call, reads `_optionsMonitor.CurrentValue.Enabled`
- When `Enabled=false`:
  - `GetOrCreateAsync` → executes factory directly, returns result
  - `SetAsync` / `RemoveAsync` / `RemoveByTagAsync` → returns `Task.CompletedTask`

`IOptionsMonitor.CurrentValue` reads from an in-memory cached object. The cost is a single field read + boolean check per call — negligible compared to the async state machine, dictionary lookups, and telemetry calls already happening.

#### Startup-Only Settings

Mode, RedisConnectionString, and all other infrastructure settings remain read from `IOptions<CacheOptions>` at startup. Only `Enabled` is hot-reloaded. Changing Mode or connection strings at runtime would be unsafe (Redis connections, HybridCache registration, etc. are startup-bound).

#### NoOpCacheService Deletion

`NoOpCacheService` is deleted from the codebase entirely. `RoutingCacheService` subsumes its responsibility.

### 3. Configuration Precedence & Defaults

**Layering order (last wins):**

1. **Built-in defaults** — InMemory, Enabled=true, 10min expiration, 5min local expiration
2. **Config-file binding** — `CacheOptions` section from `IConfiguration` (when provided)
3. **Fluent builder** — explicit `Use*`/`With*` calls override anything from config

**Zero-config behavior:**

```csharp
services.AddCaching();
// Result: InMemory mode, Enabled=true, 10min default expiration
// No appsettings section needed, no exceptions, just works
```

**Conflict resolution:** Fluent always wins. If appsettings says `Mode=Redis` and fluent says `UseHybrid()`, the result is Hybrid.

**Validation (unchanged rules, new timing):**
- Validation runs after all layers are merged, at startup via `ValidateOnStart()`
- Redis mode without connection string → `InvalidOperationException` at startup
- Hybrid mode without connection string → valid (in-memory-only hybrid)
- Invalid TimeSpan formats → validation error at startup

### 4. Breaking Changes & Migration

#### Breaking Changes

1. **`NoOpCacheService` deleted** — consumers referencing `NoOpCacheService` directly (e.g., `new NoOpCacheService()` in tests, `is NoOpCacheService` checks) will get compile errors
2. **`AddCaching(IConfiguration)` behavior change** — when `Enabled=false`, registers `RoutingCacheService` (not `NoOpCacheService`). Functionally identical for `ICacheService` consumers.

#### What Won't Break

- Injecting `ICacheService` — works exactly as before
- `AddCaching(configuration)` signature — preserved
- `CacheCallOptions` / extension methods — unchanged
- Config-file schema — same property names, same section name
- Health checks — unchanged
- Telemetry — unchanged

#### Migration Guide

| Before | After |
|--------|-------|
| `new NoOpCacheService()` in tests | `services.AddCaching(cache => cache.Disable())` or mock `ICacheService` |
| `if (service is NoOpCacheService)` | Check `IOptionsMonitor<CacheOptions>.CurrentValue.Enabled` |
| `services.AddCaching(configuration)` | Works unchanged |

#### Versioning

Major version bump (semver) required due to `NoOpCacheService` removal.

### 5. Test Strategy

#### Tests to Update

- **`NoOpCacheServiceTests`** — delete entirely
- **`RoutingCacheServiceTests`** — add disabled-state cases (short-circuit to factory, Set/Remove return CompletedTask)
- **`RoutingCacheServiceConcurrencyTests`** — add disabled-state case (skips coalescing)
- **`CacheRegistrationValidationTests`** — verify `ICacheService` always resolves regardless of `Enabled`

#### New Tests

- **Builder tests** — each `AddCaching` overload registers `RoutingCacheService`:
  - `AddCaching()` → InMemory, enabled
  - `AddCaching(configuration)` → reads from config
  - `AddCaching(cache => cache.UseRedis(...))` → Redis mode
  - `AddCaching(configuration, cache => ...)` → fluent overrides config
  - `AddCaching(cache => cache.Disable())` → disabled, still resolves
- **Precedence tests** — config says Redis, fluent says Hybrid → Hybrid wins
- **Hot-reload test** — change `Enabled` via `IOptionsMonitor`, verify short-circuit
- **Builder validation tests** — `UseRedis()` without connection string throws at startup

### 6. Sample Project Updates

Update `samples/Caching.NET.Sample` to demonstrate all registration patterns:

**Program.cs** — show multiple commented examples:

```csharp
// Example 1: Zero-config (InMemory, enabled, sensible defaults)
// builder.Services.AddCaching();

// Example 2: Config-file driven (current approach)
// builder.Services.AddCaching(builder.Configuration);

// Example 3: Fluent code-first
// builder.Services.AddCaching(cache => cache
//     .UseHybrid("localhost:6379")
//     .WithDefaultExpiration(TimeSpan.FromMinutes(15))
//     .WithOpenTelemetry()
//     .WithHealthChecks());

// Example 4: Config-file + fluent overrides (recommended for production)
builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithOpenTelemetry()
    .WithHealthChecks());

// Example 5: Explicitly disabled (for testing/staging)
// builder.Services.AddCaching(cache => cache.Disable());
```

The active (uncommented) example should be Example 4 as the recommended production pattern.

### 7. Documentation Updates

- **CLAUDE.md** — update Architecture section to reflect: RoutingCacheService always registered, no NoOpCacheService, builder API, hot-reload of Enabled flag
- **XML doc comments** — update `ServiceCollectionExtensions` and `RoutingCacheService` class-level docs
- **scripts/README.md** — no changes needed (publishing flow unchanged)

---

## Out of Scope

Deferred to future rounds (see `enterprise-roadmap.md`):
- Circuit breakers / retry policies (Round 2: Robustness)
- Latency histograms / cache size tracking (Round 3: Observability)
- `GetAsync<T>`, batch operations, typed keys (Round 4: API Ergonomics)
