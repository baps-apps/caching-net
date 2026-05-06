# Operations

## Configuration

```jsonc
// appsettings.json
{
  "Caching": {
    "KeyPrefix": "orders-svc:v1",
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
services.AddCaching(builder.Configuration.GetSection("Caching"));
```

## AWS ElastiCache (TLS, IAM auth)

1. Create a Redis 7.x replication group with **encryption-in-transit enabled** and **IAM authentication** enabled.
2. Configure the security group to allow port 6380 from the EKS pod CIDR.
3. Generate a connection string of the form:
   `rediss://<cluster>.cache.amazonaws.com:6380,ssl=true,abortConnect=false,user=<iam-user>,password=<token>`
4. Use the AWS SDK to mint a short-lived auth token (15-min TTL) and write it to a secret.
5. Point `Caching:RedisConnectionString` at that secret. Rotate the secret periodically — the `RedisConnectionRotator` hosted service rebuilds the multiplexer when the value changes; no pod restart required.

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
        - name: Caching__KeyPrefix
          value: orders-svc:v1
        - name: Caching__Mode
          value: Hybrid
        envFrom:
        - secretRef:
            name: cache-secrets   # contains Caching__RedisConnectionString
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
2. Atomically swaps the singleton in DI.
3. Disposes the old multiplexer (existing in-flight requests complete on the old connection).

Operational checklist for rotation:
- [ ] Confirm `IConfigurationRoot.Reload()` is wired to your secret store.
- [ ] Pre-rotate: monitor `cache.errors{cache.error_kind="ConnectionFailed"}` for spikes.
- [ ] Rotate: write the new credential to the secret store.
- [ ] Post-rotate: verify `cache.hits` continues to flow at the previous rate.

## Circuit-breaker tuning

Defaults (Polly v8): 50% failure ratio over a 30-second sampling window with a 20-call minimum throughput; opens for 15s.

Watch `cache.circuit_state_changes` and `cache.errors{cache.error_kind="CircuitOpen"}` to validate the new shape.
