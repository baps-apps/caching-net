# Caching.NET Telemetry

Caching.NET includes built-in observability through the `ICacheTelemetry` abstraction. Telemetry is **opt-in** — no overhead is incurred unless you explicitly enable it.

## Quick Start

```csharp
builder.Services.AddCaching(cache => cache
    .UseHybrid()
    .WithOpenTelemetry());   // ← enables metrics + traces
```

Without `.WithOpenTelemetry()`, a no-op implementation is registered and all telemetry calls are inlined away by the JIT.

## Architecture

```
ICacheTelemetry (abstraction — 7 callback methods)
  ├── NoopCacheTelemetry        (default: empty methods, zero cost)
  └── OpenTelemetryCacheTelemetry (opt-in: System.Diagnostics metrics + traces)
```

All cache services (`RoutingCacheService`, `InMemoryCacheService`, `RedisCacheService`, `HybridCacheService`) receive `ICacheTelemetry` via constructor injection and call it during cache operations.

### Registration Flow

```
WithOpenTelemetry()
  → sets RegisterOpenTelemetry = true on CachingBuilder
    → ServiceCollectionExtensions calls:
        services.TryAddSingleton<ICacheTelemetry, OpenTelemetryCacheTelemetry>()

Without WithOpenTelemetry():
    → services.TryAddSingleton<ICacheTelemetry, NoopCacheTelemetry>()
```

`TryAddSingleton` means the **first registration wins**. This enables custom provider overrides (see below).

## Meter & ActivitySource

Both use the name **`Caching.NET.Cache`**.

Register listeners in your OpenTelemetry SDK configuration:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Caching.NET.Cache"))
    .WithTracing(tracing => tracing
        .AddSource("Caching.NET.Cache"));
```

## Metrics

Four counters are emitted:

| Counter | Description | Incremented On |
|---------|-------------|----------------|
| `cache.requests` | Total cache operations | Every hit, miss, set, remove, and remove-by-tag |
| `cache.hits` | Lookups that returned a cached value | `GetOrCreateAsync` finds value in cache |
| `cache.misses` | Lookups that invoked the factory | `GetOrCreateAsync` runs the factory |
| `cache.failures` | Operations that threw an exception | Any cache operation error |

### Tags on Every Metric

| Tag | Values | Description |
|-----|--------|-------------|
| `cache.operation` | `get_or_create`, `set`, `remove`, `remove_by_tag` | Which operation was performed |
| `cache.mode` | `InMemory`, `Redis`, `Hybrid`, `Routing` | Which cache layer handled the operation |

### Example Metric Output

```
cache_requests_total{cache_operation="get_or_create", cache_mode="Hybrid"} 10425
cache_hits_total{cache_operation="get_or_create", cache_mode="Hybrid"} 9870
cache_misses_total{cache_operation="get_or_create", cache_mode="Hybrid"} 555
cache_failures_total{cache_operation="get_or_create", cache_mode="Hybrid"} 3
```

## Traces (Activities)

Traces are emitted for **error conditions only** to keep span volume low in production:

| Activity Name | When | Tags |
|---------------|------|------|
| `cache.{operation}` | A cache operation fails with an exception | `cache.mode`, `cache.operation`, `cache.key_prefix`, `exception.type` |
| `cache.factory_timeout` | Factory exceeds configured `FactoryTimeout` | `cache.mode`, `cache.key_prefix`, `cache.factory_timeout_ms` |

All activities use `ActivityKind.Internal` and set `ActivityStatusCode.Error`.

### Key Truncation

Cache keys are **truncated to 64 characters** in trace tags to prevent high-cardinality explosions. Keys longer than 64 characters are stored as `first64chars...`.

## ICacheTelemetry Callbacks

The interface defines seven methods that cache services call at specific points:

| Method | Called When |
|--------|------------|
| `OnCacheHit(key, mode)` | Cache lookup returns a cached value |
| `OnCacheMiss(key, mode)` | Cache lookup finds no value; factory will run |
| `OnCacheSet(key, mode)` | Value successfully written to cache |
| `OnCacheRemove(key, mode)` | Single cache entry removed by key |
| `OnCacheRemoveByTag(tag, mode)` | Cache entries evicted by tag |
| `OnCacheError(operation, keyOrTag, mode, exception)` | Any cache operation fails |
| `OnFactoryTimeout(key, mode, timeout)` | Factory exceeds configured `FactoryTimeout` |

## Why NoopCacheTelemetry Exists

The null-object pattern is used instead of nullable `ICacheTelemetry?` to keep cache service code clean:

- All services take `ICacheTelemetry` as a **required** constructor parameter
- No `_telemetry?.OnCacheHit(...)` null checks scattered through hot paths
- The JIT inlines empty method bodies — zero runtime cost
- Standard .NET pattern (same approach used by `NullLogger<T>`, `NullLoggerFactory`)

## Custom Telemetry Providers

Implement `ICacheTelemetry` and register it **before** calling `AddCaching`:

```csharp
// Register your custom provider first
builder.Services.AddSingleton<ICacheTelemetry, MyCustomTelemetry>();

// AddCaching uses TryAddSingleton — your registration wins
builder.Services.AddCaching(cache => cache.UseHybrid());
```

### Example: Custom Provider

```csharp
using Caching.NET.Abstractions;

public class DataDogCacheTelemetry : ICacheTelemetry
{
    private readonly IStatsd _statsd;

    public DataDogCacheTelemetry(IStatsd statsd) => _statsd = statsd;

    public void OnCacheHit(string key, string mode)
        => _statsd.Increment("cache.hit", tags: new[] { $"mode:{mode}" });

    public void OnCacheMiss(string key, string mode)
        => _statsd.Increment("cache.miss", tags: new[] { $"mode:{mode}" });

    public void OnCacheSet(string key, string mode)
        => _statsd.Increment("cache.set", tags: new[] { $"mode:{mode}" });

    public void OnCacheRemove(string key, string mode)
        => _statsd.Increment("cache.remove", tags: new[] { $"mode:{mode}" });

    public void OnCacheRemoveByTag(string tag, string mode)
        => _statsd.Increment("cache.remove_by_tag", tags: new[] { $"mode:{mode}" });

    public void OnCacheError(string operation, string keyOrTag, string mode, Exception ex)
        => _statsd.Increment("cache.error", tags: new[] { $"mode:{mode}", $"op:{operation}" });

    public void OnFactoryTimeout(string key, string mode, TimeSpan timeout)
        => _statsd.Increment("cache.factory_timeout", tags: new[] { $"mode:{mode}" });
}
```

## Integration Examples

### Prometheus + Jaeger

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Caching.NET.Cache")
        .AddPrometheusExporter())
    .WithTracing(tracing => tracing
        .AddSource("Caching.NET.Cache")
        .AddJaegerExporter());

builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithOpenTelemetry());
```

**Prometheus** (scraped at `/metrics`):
```
cache_requests_total{cache_operation="get_or_create",cache_mode="Hybrid"} 1042
cache_hits_total{cache_operation="get_or_create",cache_mode="Hybrid"} 987
cache_misses_total{cache_operation="get_or_create",cache_mode="Hybrid"} 55
cache_failures_total{cache_operation="get_or_create",cache_mode="Hybrid"} 0
```

**Jaeger** (traces appear only on errors):
- Span `cache.get_or_create` with `exception.type=RedisConnectionException`
- Span `cache.factory_timeout` with `cache.factory_timeout_ms=30000`

### OTLP (OpenTelemetry Collector)

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Caching.NET.Cache")
        .AddOtlpExporter())
    .WithTracing(tracing => tracing
        .AddSource("Caching.NET.Cache")
        .AddOtlpExporter());

builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithOpenTelemetry());
```

### Azure Monitor / Application Insights

```csharp
builder.Services.AddOpenTelemetry()
    .UseAzureMonitor(options =>
    {
        options.ConnectionString = "<your-connection-string>";
    })
    .WithMetrics(metrics => metrics
        .AddMeter("Caching.NET.Cache"))
    .WithTracing(tracing => tracing
        .AddSource("Caching.NET.Cache"));

builder.Services.AddCaching(cache => cache
    .UseHybrid("localhost:6379")
    .WithOpenTelemetry());
```

## Dashboard Queries

### Grafana (PromQL)

**Cache hit rate:**
```promql
rate(cache_hits_total[5m]) / rate(cache_requests_total[5m])
```

**Cache miss rate by mode:**
```promql
rate(cache_misses_total[5m]) / rate(cache_requests_total[5m])
```

**Error rate:**
```promql
rate(cache_failures_total[5m])
```

**Hit rate over time (for graphs):**
```promql
sum(rate(cache_hits_total[5m])) by (cache_mode)
  / sum(rate(cache_requests_total[5m])) by (cache_mode)
```

### Azure Monitor (KQL)

**Cache hit rate:**
```kql
customMetrics
| where name == "cache.hits" or name == "cache.requests"
| summarize hits = sumif(value, name == "cache.hits"),
            total = sumif(value, name == "cache.requests")
            by bin(timestamp, 5m)
| extend hit_rate = iff(total > 0, hits / total, 0.0)
```

**Cache errors:**
```kql
customMetrics
| where name == "cache.failures"
| summarize failures = sum(value) by bin(timestamp, 5m), tostring(customDimensions["cache.mode"])
```

## Alerting Recommendations

| Metric | Threshold | Severity | Action |
|--------|-----------|----------|--------|
| Cache hit rate | < 50% for 10 min | Warning | Investigate key patterns, check TTLs |
| Cache hit rate | < 20% for 5 min | Critical | Possible cache flush or misconfiguration |
| Error rate | > 0 for 5 min | Warning | Check Redis connectivity |
| Error rate | > 10/min for 5 min | Critical | Redis likely down; verify FailOpen behavior |
| Factory timeouts | > 0 for 5 min | Warning | Slow downstream; review FactoryTimeout setting |

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| No metrics appearing | Missing `.AddMeter("Caching.NET.Cache")` | Add the meter to your OTel metrics config |
| No traces appearing | Missing `.AddSource("Caching.NET.Cache")` | Add the source to your OTel tracing config |
| Metrics show but all zeros | Cache disabled or no traffic | Check `Enabled=true` and that requests are hitting the cache |
| High cardinality warnings | Too many unique operation/mode combinations | Expected — there are only ~8 combinations total |
| `WithOpenTelemetry()` has no effect | Custom `ICacheTelemetry` registered before `AddCaching` | Your custom registration takes precedence via `TryAddSingleton` |
