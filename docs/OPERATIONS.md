# Operations

## `Enabled = false` in production

You can deploy with `CacheOptions:Enabled: false` to bypass the cache without removing DI. The host **does not** register Redis, hybrid, serializers, or Polly in that configuration, and options validation is skipped — useful for blue/green or disaster “cache off” switches without invalid secrets in config. **`Enabled` is read on every call:** flipping to `false` takes effect immediately (factory-only / no-op paths). Flipping to `true` uses real backends only if they were registered at startup; **if the process cold-started with `Enabled: false`, restart** after setting `Enabled: true` so `AddCaching` runs the enabled registration path.

## Configuration

```jsonc
// appsettings.json
{
  "CacheOptions": {
    "KeyPrefix": "asm-api-prod",
    "Mode": "Hybrid",
    "RedisConnectionString": "rediss://elasticache.amzn.example:6380",
    "StrictRedisCertificateValidation": true,
    "FailOpen": true,
    "DefaultExpiration": "00:10:00",
    "TtlJitterPercentage": 0.10,
    "MaximumKeyLength": 512,
    "MaximumPayloadBytes": 1048576,
    "StaleRefreshConcurrency": 256,
    "FactoryTimeout": "00:00:30",
    "RedisOperationTimeout": "00:00:02"
  }
}
```

```csharp
services.AddCaching(builder.Configuration);
```

## AWS ElastiCache (TLS, IAM auth)

1. Create a Redis 7.x replication group with **encryption-in-transit enabled** and **IAM authentication** enabled.
2. Configure the security group to allow port 6380 from the EKS pod CIDR.
3. Generate a connection string of the form:
   `rediss://<cluster>.cache.amazonaws.com:6380,ssl=true,abortConnect=false,user=<iam-user>,password=<token>`
4. Use the AWS SDK to mint a short-lived auth token (15-min TTL) and write it to a secret.
5. Point `CacheOptions:RedisConnectionString` at that secret. Rotate the secret periodically — the `RedisConnectionRotator` hosted service rebuilds the multiplexer when the value changes; no pod restart required.

## Kubernetes deployment

```yaml
# Excerpt
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
      - name: app
        env:
        - name: CacheOptions__KeyPrefix
          value: asm-api-prod
        - name: CacheOptions__Mode
          value: Hybrid
        envFrom:
        - secretRef:
            name: cache-secrets   # contains CacheOptions__RedisConnectionString
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 8080  # caching-net health-check is wired here
```

## Sharding

Caching.NET delegates sharding to `IDistributedCache` (StackExchange.Redis). For ElastiCache cluster mode, set the connection-string `replicationGroup=<id>` so the multiplexer routes by hash slot. For self-hosted Redis Cluster use `cluster=true` in the connection string.

## Credential rotation

`RedisConnectionRotator` listens on `IOptionsMonitor<CacheOptions>`. When the Configuration provider re-reads `RedisConnectionString` (triggered by your secret reloader), the rotator:
1. Builds a new `IConnectionMultiplexer` with the new credentials.
2. Rotates the active multiplexer reference used by the cache services.
3. Disposes the old multiplexer after a **short drain delay** (100 ms) and **`await`s async disposal** when the multiplexer implements `IAsyncDisposable`, logging warnings if teardown fails. On rotation the previous connection’s disposal is scheduled without blocking the change handler; **host stop** awaits disposal through `StopAsync` / `DisposeAsync` on the rotator.

Operational checklist for rotation:
- [ ] Confirm `IConfigurationRoot.Reload()` is wired to your secret store.
- [ ] Pre-rotate: monitor `cache.errors{cache.error_kind="ConnectionFailed"}` for spikes.
- [ ] Rotate: write the new credential to the secret store.
- [ ] Post-rotate: verify `cache.hits` continues to flow at the previous rate.

## Circuit-breaker tuning

Defaults (`CacheResilienceOptions`): **2s** per-op timeout; breaker samples **30s** with **50%** failure ratio and **20** minimum calls; stays open **15s**. Retries: **2** attempts with exponential backoff, **50 ms** initial delay, **1 s** cap, jitter on. Transients include connection/timeouts, socket I/O failures, and Redis `LOADING`/`READONLY` server messages; cancellations are not retried.

Optional **Redis concurrency limiter:** set `EnableRedisConcurrencyLimiter` and permit/queue limits via `WithResilience(...)` to cap concurrent executions per pipeline during brownouts (default off; queue limit 0 = fail fast when saturated).

Override defaults:

```csharp
services.AddCaching(configuration, b => b
    .UseRedis(configuration["Redis"]!)
    .WithKeyPrefix("asm-api-prod")
    .WithResilience(r =>
    {
        r.RetryCount = 3;
        r.EnableRedisConcurrencyLimiter = true;
        r.RedisConcurrencyPermitLimit = 128;
    }));
```

Watch `cache.circuit_state_changes` and `cache.errors` with `cache.error_kind="CircuitOpen"` to validate tuning.
