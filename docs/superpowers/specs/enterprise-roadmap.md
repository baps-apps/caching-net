# Caching.NET Enterprise Hardening Roadmap

**Date:** 2026-04-02
**Status:** Backlog (reference only — no implementation scheduled)

This document captures deferred improvements identified during the API ergonomics review. Each round is independent and can be prioritized based on consumer demand.

---

## Round 1: API Ergonomics (Current)

**Spec:** `2026-04-02-api-ergonomics-design.md`
**Status:** In progress

- Fluent `CachingBuilder` API with zero-config defaults
- Eliminate `NoOpCacheService` — `RoutingCacheService` always registered
- Hot-reloadable `Enabled` flag via `IOptionsMonitor`
- Config-file + fluent builder coexistence (fluent overrides config)
- Sample project demonstrating all registration patterns
- Major version bump

---

## Round 2: Robustness & Resilience

**Goal:** Make the package production-safe for environments where Redis is unreliable or experiences transient failures.

### Circuit Breaker for Redis
- Integrate Polly (or `Microsoft.Extensions.Resilience`) to wrap Redis calls
- When Redis is down, automatically fall back to InMemory for a configurable duration
- Expose via builder: `cache.UseRedis("conn").WithCircuitBreaker(options => ...)`
- Circuit states (closed/open/half-open) should be observable via telemetry

### Retry Policies
- Configurable retry with exponential backoff for transient Redis failures
- Default: 1 retry with 100ms delay (safe for latency-sensitive paths)
- Expose via builder: `cache.UseRedis("conn").WithRetry(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(100))`
- Must respect `CancellationToken` and `FactoryTimeout`

### Graceful Degradation
- When Redis connection is lost, auto-degrade Hybrid/Redis mode to InMemory
- Log degradation events at Warning level
- Auto-promote back to configured mode when Redis recovers
- Builder: `cache.WithGracefulDegradation()` (opt-in, off by default)

### Connection Pooling Configuration
- Expose `ConfigurationOptions` tuning via builder for advanced consumers
- Connection multiplexer sharing strategy (single vs pooled)
- Builder: `cache.UseRedis(redis => { redis.ConnectTimeout = ...; redis.SyncTimeout = ...; })`

---

## Round 3: Observability

**Goal:** Provide production-grade metrics and traces that integrate with standard observability stacks (Prometheus, Grafana, Aspire Dashboard).

### Latency Histograms
- Add `Histogram<double>` metrics for cache operation latency:
  - `cache.get_or_create.duration` (by mode, hit/miss)
  - `cache.set.duration` (by mode)
  - `cache.remove.duration` (by mode)
- Use recommended OpenTelemetry semantic conventions for metric names

### Cache Size Tracking
- `cache.memory.entry_count` — gauge for in-memory entry count (where trackable)
- `cache.memory.estimated_bytes` — gauge for estimated memory usage
- `cache.redis.connection_status` — gauge (0=disconnected, 1=connected)

### Hit Rate Ratio Metric
- `cache.hit_rate` — derived metric (hits / total requests) per mode
- Useful for alerting on cache effectiveness degradation

### Structured Logging Improvements
- Add correlation ID propagation through cache calls
- Structured log properties: `{CacheMode}`, `{CacheOperation}`, `{CacheKey}` (truncated), `{CacheDurationMs}`
- Log level guidelines: Debug for hits, Info for misses, Warning for degradation, Error for failures

### Sample Dashboard Configuration
- Provide sample Grafana dashboard JSON for common metrics
- Provide sample Aspire Dashboard configuration
- Document recommended alert thresholds (hit rate < 50%, error rate > 1%, p99 latency > 100ms)

---

## Round 4: Additional API Ergonomics

**Goal:** Expand the `ICacheService` surface for common enterprise patterns without breaking the existing contract.

### GetAsync<T> (Get Without Factory)
- Extension method: `cache.GetAsync<T>(key)` → returns `T?` (null on miss)
- Useful when callers want to check cache without providing a fallback
- Implemented via extension method (no change to `ICacheService`)

### Batch Operations
- `cache.GetOrCreateManyAsync<T>(keys, factory)` — batch get-or-create
- Factory receives only the missed keys
- Reduces round-trips for Redis/Hybrid modes
- Implemented via extension method

### Typed Cache Keys
- `CacheKey<T>` value object to prevent key collisions between types
- Example: `CacheKey<Product>.From("p-100")` → `"Product:p-100"`
- Optional — consumers can still use raw strings

### Tag Support for InMemory and Redis
- Currently only Hybrid mode supports tags (via `HybridCache`)
- InMemory: track tags in a `ConcurrentDictionary<string, HashSet<string>>` mapping tags to keys
- Redis: use Redis Sets to track tag-to-key mappings, `SUNIONSTORE` + `DEL` for tag removal
- Breaking: this changes tag methods from no-op to functional in InMemory/Redis modes

---

## Prioritization Guidance

| Round | Impact | Effort | Recommended When |
|-------|--------|--------|------------------|
| 1 (API Ergonomics) | High | Medium | Now — fixes production risk and modernizes DI |
| 2 (Robustness) | High | Medium | When consumers deploy with Redis in production |
| 3 (Observability) | Medium | Low-Medium | When consumers need production monitoring |
| 4 (More API) | Medium | High | When consumers request specific features |
