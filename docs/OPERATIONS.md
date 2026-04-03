# Caching.NET operational runbook

This guide helps operations and support teams run Caching.NET in production. For configuration options, see the [README](../README.md#configuration).

## Runtime configuration changes

Mode is determined by `CacheOptions.Mode` (InMemory, Redis, Hybrid). Change the configuration (e.g., `appsettings.json`, environment variables, or your config provider) and restart the application.

**Hot-reloadable (no restart):**

- `Enabled` flag — takes effect immediately via IOptionsMonitor

**Restart required:**

- `Mode`, `RedisConnectionString`, `RedisInstanceName`, and all other settings

**Safe ordering:** Prefer switching to a mode that does not add new dependencies (e.g., from Redis to InMemory) to avoid new failure modes. When moving from InMemory to Redis or Hybrid, ensure Redis is reachable and connection strings are correct before deploying.

**Hybrid without Redis:** For Hybrid mode, omitting `RedisConnectionString` runs in-memory-only (with stampede protection). You can "disable Redis" by removing or blanking `RedisConnectionString` and restarting, so the app keeps caching locally without Redis.

## Disabling caching

1. Set `CacheOptions:Enabled` to `false` in configuration.
2. Configuration takes effect immediately (hot-reloadable) — no restart required.
3. Verify: all `GetOrCreateAsync` calls now run the factory directly; `SetAsync` / `Remove*` are no-op.
4. To re-enable: set `Enabled` back to `true`.

Use this when:

- Cache or Redis is causing errors and you want to reduce impact while you fix the backend.
- You need to rule out cache-related bugs (stale data, serialization, etc.).

## Health checks

For full health check details — probe logic, FailOpen interaction, Kubernetes probes, combining with Redis health checks, and troubleshooting — see [HEALTH-CHECKS.md](HEALTH-CHECKS.md).

### Startup validation

After building the service provider, call **`serviceProvider.ValidateCacheRegistration()`** (e.g., in host startup) to ensure `ICacheService` resolves and the configured mode's backing services are registered. This fails fast on DI misconfiguration; it does **not** probe Redis or the cache backend.

### Quick setup

```csharp
builder.Services.AddCaching(builder.Configuration, cache => cache
    .WithHealthChecks());

var app = builder.Build();
app.Services.ValidateCacheRegistration();
app.MapHealthChecks("/health");
```

## Interpreting logs

- **InMemory / Redis tag calls:** If you see debug messages like *"RemoveByTagAsync is not supported in InMemory/Redis mode"*, the app is calling tag APIs in a mode that does not support them. This is a no-op; switch to **Hybrid** if you need tag-based invalidation.
- **Redis failures (fail-open):** With default `FailOpen=true`, Redis get/set/remove failures are logged (warning/error) and the operation falls back to the factory (get) or is skipped (set/remove). Look for messages such as *"Redis get failed for key ...; executing factory (fail-open)"* or *"Redis set failed for key ..."*. These indicate Redis connectivity or serialization issues; the app continues to work without caching.
- **Hybrid failures:** Similar messages for Hybrid (e.g., *"Error getting or creating cache entry for key ...; executing factory (fail-open)"*) mean the hybrid layer caught an exception and ran the factory instead. Check Redis and in-memory tier health.
- **Key/payload limits:** Warnings like *"Key length ... exceeds MaximumKeyLength"* or *"Payload for key ... exceeds MaximumPayloadBytes"* mean the entry was not cached. Consider shortening keys or reducing payload size, or increasing limits if appropriate.
- **Cache disabled/unavailable:** Debug messages like *"Cache disabled or unavailable - executing factory for key ..."* (Hybrid) indicate caching is off or the cache instance is null; the factory is executed every time.

## Tuning

- **DefaultExpiration / DefaultLocalExpiration:** Shorter TTLs reduce stale data and memory use but increase load on the source (e.g., database). For high-churn or volatile data, use shorter values (e.g., 1-5 minutes). For stable reference data, longer values (e.g., 30-60 minutes) are fine. When you do not configure these options explicitly, Caching.NET uses sensible internal defaults (currently 10 minutes for `DefaultExpiration` and 5 minutes for `DefaultLocalExpiration` in Hybrid) so entries are not unbounded.
- **MemorySizeLimitMb:** When set, the in-memory cache uses a size limit (see [INTERNALS.md](INTERNALS.md#configuration-deep-dive) for behavior). Use this in production to cap memory; tune based on instance size and number of keys.
- **MaximumPayloadBytes / MaximumKeyLength:** When left unset, Caching.NET does not impose extra limits beyond the underlying caches. For large-scale systems, set these (for example, payloads capped in the low megabytes and keys capped in the low hundreds to ~1k characters) to avoid storing very large keys or values and to protect memory and network.

## Incident playbooks

### Redis instability

- **Symptoms:** Logs show repeated messages like *"Redis get failed for key ...; executing factory (fail-open)"* or *"Redis set failed for key ..."*. Upstream databases see increased load as cache falls back to the factory.
- **Actions:**
  1. Confirm `FailOpen=true` so requests continue to succeed.
  2. Temporarily switch to `Mode=InMemory` or set `Enabled=false` in `CacheOptions` for affected services and redeploy, to remove Redis from the critical path.
  3. Investigate Redis separately (connectivity, CPU, memory, slowlog).
  4. Once Redis is stable, switch mode back to Redis or Hybrid and redeploy.
- **Resolution:** Redis log messages stop; cache hit rates return to normal; upstream database load decreases.

### Suspected stale or corrupted cached data

- **Symptoms:** Users see outdated responses even after underlying data is updated. Logs do not show factory exceptions, but cache hits are frequent.
- **Actions:**
  1. Use `CacheCallOptions.ForceRefresh` for the affected keys to refresh entries without flushing the entire cache.
  2. If using Hybrid with tags, call `RemoveByTagAsync` for relevant tags to invalidate groups of keys.
  3. Temporarily reduce `DefaultExpiration` (and `DefaultLocalExpiration` for Hybrid) to accelerate natural expiration.
  4. Review serialization changes or DTO versioning that may have caused incompatibilities.
- **Resolution:** Responses reflect current data; cache miss rate temporarily increases then stabilizes.

## Security

For security guidance (secrets, PII, key namespacing, Redis TLS), see the [README security section](../README.md#security) and [INTERNALS.md](INTERNALS.md#security-and-tls).
