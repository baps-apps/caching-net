# Caching.NET operational runbook

This guide helps operations and support teams run Caching.NET in production: switching modes, disabling cache during incidents, and interpreting logs.

## Switching cache modes in production

- **Configuration-driven:** Mode is determined by `CacheOptions.Mode` (InMemory, Redis, Hybrid). Change the configuration (e.g., `appsettings.json`, environment variables, or your config provider) and restart the application (or use a hot-reload mechanism if your host supports it).
- **Safe order:** Prefer switching to a mode that does not add new dependencies (e.g., from Redis to InMemory) to avoid new failure modes. When moving from InMemory to Redis or Hybrid, ensure Redis is reachable and connection strings are correct before deploying.
- **Hybrid ↔ InMemory-only Hybrid:** For Hybrid mode, omitting `RedisConnectionString` runs in-memory-only (with stampede protection). You can “disable Redis” by removing or blanking `RedisConnectionString` and restarting, so the app keeps caching locally without Redis.

## Disabling caching during incidents

- Set **`CacheOptions:Enabled`** to **`false`** and restart (or use your deployment/configuration process). The application will register a no-op cache: every `GetOrCreateAsync` runs the factory and no data is read from or written to the cache. No application code changes are required.
- Use this when:
  - Cache or Redis is causing errors and you want to reduce impact while you fix the backend.
  - You need to rule out cache-related bugs (stale data, serialization, etc.).
- Re-enable by setting **`Enabled`** back to **`true`** and restarting.

## Interpreting logs

- **InMemory / Redis tag calls:** If you see debug messages like *"RemoveByTagAsync is not supported in InMemory/Redis mode"*, the app is calling tag APIs in a mode that does not support them. This is a no-op; switch to **Hybrid** if you need tag-based invalidation.
- **Redis failures (fail-open):** With default `FailOpen=true`, Redis get/set/remove failures are logged (warning/error) and the operation falls back to the factory (get) or is skipped (set/remove). Look for messages such as *"Redis get failed for key ...; executing factory (fail-open)"* or *"Redis set failed for key ..."*. These indicate Redis connectivity or serialization issues; the app continues to work without caching.
- **Hybrid failures:** Similar messages for Hybrid (e.g., *"Error getting or creating cache entry for key ...; executing factory (fail-open)"*) mean the hybrid layer caught an exception and ran the factory instead. Check Redis and in-memory tier health.
- **Key/payload limits:** Warnings like *"Key length ... exceeds MaximumKeyLength"* or *"Payload for key ... exceeds MaximumPayloadBytes"* mean the entry was not cached. Consider shortening keys or reducing payload size, or increasing limits if appropriate.
- **Cache disabled/unavailable:** Debug messages like *"Cache disabled or unavailable - executing factory for key ..."* (Hybrid) indicate caching is off or the cache instance is null; the factory is executed every time.

## Tuning expirations, limits, and memory at scale

- **DefaultExpiration / DefaultLocalExpiration:** Shorter TTLs reduce stale data and memory use but increase load on the source (e.g., database). For high-churn or volatile data, use shorter values (e.g., 1–5 minutes). For stable reference data, longer values (e.g., 30–60 minutes) are fine. When you do not configure these options explicitly, Caching.NET uses sensible internal defaults (currently 10 minutes for `DefaultExpiration` and 5 minutes for `DefaultLocalExpiration` in Hybrid) so entries are not unbounded.
- **MemorySizeLimitMb:** When set, the in-memory cache uses a size limit (see [IMPLEMENTATION.md](IMPLEMENTATION.md) for behavior). Use this in production to cap memory; tune based on instance size and number of keys.
- **MaximumPayloadBytes / MaximumKeyLength:** When left unset, Caching.NET does not impose extra limits beyond the underlying caches. For large-scale systems, set these (for example, payloads capped in the low megabytes and keys capped in the low hundreds to ~1k characters) to avoid storing very large keys or values and to protect memory and network.

## Health checks and startup validation

- **Startup validation:** After building the service provider, call **`serviceProvider.ValidateCacheRegistration()`** (e.g., in host startup) to ensure `ICacheService` resolves and the configured mode’s backing services are registered. This fails fast on DI misconfiguration; it does **not** probe Redis or the cache backend.
- **Caching.NET pipeline health:** Use the built-in `CachingHealthCheck` to verify that the cache pipeline (routing, options, and backing services) is operational:

  ```csharp
  builder.Services.AddCaching(builder.Configuration);

  builder.Services.AddHealthChecks()
      .AddCachingHealthChecks(name: "caching-net");
  ```

  - When caching is disabled (`CacheOptions.Enabled=false`), the health check reports **Healthy** with a description indicating that caching is intentionally disabled.
  - In Redis or Hybrid modes, the health check performs a very cheap `GetOrCreateAsync` on a synthetic key. When `FailOpen=true` (default), Redis failures are treated as healthy from the perspective of this check because requests will still succeed by falling back to the factory; combine this with a dedicated Redis health check (see below) to monitor Redis availability explicitly.
- **Connectivity:** Use your existing health check stack to monitor Redis (e.g., ping, PUSH/GET of a probe key). Caching.NET does not ship a built-in Redis health check; add one in your app if needed.

Example using ASP.NET Core health checks:

```csharp
builder.Services.AddHealthChecks()
    .AddRedis(
        redisConnectionString: builder.Configuration.GetConnectionString("Redis") 
            ?? builder.Configuration["CacheOptions:RedisConnectionString"] 
            ?? throw new InvalidOperationException("Redis connection missing for health check."),
        name: "redis",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
```

Then map the health endpoint:

```csharp
app.MapHealthChecks("/health");
```

## Security and multi-tenant considerations

- **Secrets and PII:** Do not cache secrets (tokens, passwords, API keys). Be cautious caching PII; ensure key naming and logging do not expose sensitive data (keys are truncated in library logs).
- **Redis key prefixing:** Use **`RedisInstanceName`** (e.g., `"myservice:"`) so keys are namespaced per application or tenant. For multi-tenant shared Redis, use a prefix that includes tenant id where appropriate.
- **Redis TLS and certificates:** When using Redis over TLS, the default (non-strict) certificate validation in Caching.NET allows hostname mismatches (`RemoteCertificateNameMismatch`) but rejects all other SSL policy errors and logs a warning. To enforce a strict TLS posture in any environment, set `StrictRedisCertificateValidation=true` in `CacheOptions` (or override the certificate validation callback entirely) so that any SSL policy error, including hostname mismatches, causes the connection to be rejected.

### Example operational playbooks

**Scenario: Redis instability**

- Symptoms:
  - Logs show repeated messages like *"Redis get failed for key ...; executing factory (fail-open)"* or *"Redis set failed for key ..."*.
  - Upstream databases see increased load as cache falls back to the factory.
- Actions:
  - Confirm `FailOpen=true` so requests continue to succeed.
  - Temporarily switch to **`Mode=InMemory`** or set **`Enabled=false`** in `CacheOptions` for affected services and redeploy, to remove Redis from the critical path.
  - Investigate Redis separately (connectivity, CPU, memory, slowlog).
  - Once Redis is stable, switch mode back to **Redis** or **Hybrid** and redeploy.

**Scenario: suspected stale or corrupted cached data**

- Symptoms:
  - Users see outdated responses even after underlying data is updated.
  - Logs do not show factory exceptions, but cache hits are frequent.
- Actions:
  - Use **`CacheCallOptions.ForceRefresh`** for the affected keys to refresh entries without flushing the entire cache.
  - If using Hybrid with tags, call `RemoveByTagAsync` for relevant tags to invalidate groups of keys.
  - Temporarily reduce `DefaultExpiration` (and `DefaultLocalExpiration` for Hybrid) to accelerate natural expiration.
  - Review serialization changes or DTO versioning that may have caused incompatibilities.
