# Caching.NET Sample (Redis via Docker Compose)

Run the sample against a real Redis instance:

From the sample directory:

```bash
cd samples/Caching.NET.Sample
```

1. Start Redis:

```bash
make sample-redis-up
```

2. Run the sample app:

```bash
make sample-run
```

3. Validate Redis round-trip:

```bash
make sample-redis-validate
```

Expected response shape:

```json
{
  "key": "sample-api-dev:redis:validation:<guid>",
  "probeId": "<guid>",
  "roundTripMatches": true,
  "removed": true
}
```

`roundTripMatches = true` confirms the read returned the value written to Redis, and `removed = true` confirms cleanup succeeded.

Cleanup:

```bash
make sample-redis-down
```
