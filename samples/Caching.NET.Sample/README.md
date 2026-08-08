# Caching.NET sample

An ASP.NET API showing how an application consumes Caching.NET v3. Note what is **not** here: no
cache engine registration, no serializer wiring, no backplane setup, no distributed-cache adapter.
`AddCaching` and the `CacheOptions` configuration section are the whole surface.

## What it demonstrates

| File | Shows |
|---|---|
| `Program.cs` | Configuration + fluent registration, a second named cache, health-check wiring, the OpenTelemetry names |
| `appsettings.json` | The full `CacheOptions` section: Hybrid mode, Redis, backplane, resilience, serialization, security, observability |
| `appsettings.Development.json` | Development overrides: InMemory, short TTLs, fail-safe off, errors surfaced |
| `Controllers/ProductCatalogController.cs` | Get-or-set with a factory, per-entry options, tags, batch reads, invalidation, a named cache, key guards |
| `Data/ProductRepository.cs` | A slow "database" so cache hits are visible in response times |

## Running it

Development profile — InMemory, no Redis needed:

```bash
cd samples/Caching.NET.Sample
dotnet run
```

Production-shaped profile — Hybrid with Redis:

```bash
docker run -d --name caching-net-sample-redis -p 6379:6379 redis:7.4-alpine
ASPNETCORE_ENVIRONMENT=Production dotnet run
```

You should see the startup summary:

```text
Caching.NET initialized. CacheName: default Mode: Hybrid MemoryLayer: Enabled RedisLayer: Enabled
Backplane: Enabled FailSafe: Enabled Serializer: SystemTextJson Compression: Enabled
Tracing: Enabled Metrics: Enabled
```

## Endpoints

| Method | Route | Behaviour |
|---|---|---|
| `GET` | `/api/productcatalog/{sku}` | Get-or-set. First call takes ~250 ms; later calls are immediate |
| `GET` | `/api/productcatalog/category/{id}` | Tagged entries with eager refresh |
| `GET` | `/api/productcatalog/batch?skus=SKU-001,SKU-002` | Concurrent batch read |
| `DELETE` | `/api/productcatalog/{sku}` | Invalidate one entry |
| `DELETE` | `/api/productcatalog/category/{id}` | Invalidate a tag group |
| `GET` | `/api/productcatalog/quota/{clientId}` | Reads from the `short-lived` named cache |
| `GET` | `/api/productcatalog/stats` | How often the "database" was actually read |
| `GET` | `/health/live` | Liveness — no I/O |
| `GET` | `/health/ready` | Readiness — real cache round trip |

## Seeing the cache work

```bash
# Cold: ~250 ms, source loads = 1
time curl -s localhost:5000/api/productcatalog/SKU-001 > /dev/null
curl -s localhost:5000/api/productcatalog/stats

# Warm: immediate, source loads still 1
time curl -s localhost:5000/api/productcatalog/SKU-001 > /dev/null
curl -s localhost:5000/api/productcatalog/stats

# Invalidate, then cold again
curl -s -X DELETE localhost:5000/api/productcatalog/SKU-001
time curl -s localhost:5000/api/productcatalog/SKU-001 > /dev/null
```

## Multi-pod invalidation

Run two instances against the same Redis to watch the backplane work:

```bash
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5001 dotnet run &
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5002 dotnet run &

curl -s localhost:5001/api/productcatalog/SKU-001 > /dev/null   # warms pod 1's L1
curl -s localhost:5002/api/productcatalog/SKU-001 > /dev/null   # warms pod 2's L1
curl -s -X DELETE localhost:5001/api/productcatalog/SKU-001     # invalidates on both
curl -s localhost:5002/api/productcatalog/stats                 # pod 2 reloaded from source
```

## Cleanup

```bash
docker rm -f caching-net-sample-redis
```
