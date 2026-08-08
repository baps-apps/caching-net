# Security

What Caching.NET v3 protects against, how, and where the gaps are.

## 1. Threat model

A cache sits between an application and its data, holds copies of that data, and — in Redis and
Hybrid modes — persists them somewhere other processes can reach. The exposures that follow from
that are:

| Threat | Mitigation |
|---|---|
| One application reading or invalidating another's entries on a shared Redis database | Mandatory `ApplicationPrefix`, plus optional environment/tenant/cache-name segments, on every key and on the backplane channel |
| A caller-supplied identifier forging an extra key segment (`id = "1:admin"`) to hit another entity's entry | `CacheKeyBuilder` rejects `':'`, whitespace and control characters in every segment |
| Unbounded keys exhausting memory or blowing up telemetry cardinality | `Security.MaximumKeyLength`, enforced inside the cache on every operation using the configured defaults |
| Oversized values exhausting Redis or process memory | `Serialization.MaximumPayloadBytes`, enforced on write **and** on read |
| Cache poisoning — a rewritten Redis value deserialized as trusted input | One-byte format header validated before any deserialization; unrecognised header → treated as a miss |
| Decompression bomb — a small compressed value expanding without bound | Bounded read loop against `Compression.MaximumDecompressedBytes` |
| Polymorphic deserialization / type confusion | System.Text.Json with no type-name handling; MessagePack contractless. No type name is ever written to or read from a payload |
| Credentials leaking through logs, traces or crash dumps | Connection strings redacted; only exception *types* in health output; validation messages never echo the connection string |
| Personal data leaking through telemetry | Caching.NET metrics, logs and spans carry no keys, values or tags; a non-reversible fingerprint is available instead. **Registering the engine activity sources exports raw cache keys** — see §9 |
| A man-in-the-middle on the Redis connection | TLS with strict certificate validation by default; permissive mode rejected at startup unless TLS is actually on |

## 2. Isolation

```text
logical:  {ApplicationPrefix}[:{EnvironmentPrefix}][:{TenantPrefix}][:{CacheName}]:{caller key}
physical: [{Redis.InstancePrefix}]v2:{ApplicationPrefix}[:{EnvironmentPrefix}][:{TenantPrefix}][:{CacheName}]:{caller key}
```

`v2` is the engine's wire-format segment; it is constant across applications and contributes nothing
to isolation. Everything that does the isolating sits after it.

- `ApplicationPrefix` is **required**; startup fails without it.
- No prefix segment may contain `':'` — that character is the delimiter, and allowing it would let
  configuration collapse two namespaces into one.
- Named caches append their name, so two caches in one application cannot share a key space.
- The backplane channel prefix defaults to the same prefix, so applications sharing a Redis instance
  never receive each other's invalidations.
- `ClearAsync` is scoped to the application's own prefix. Caching.NET never issues `FLUSHDB`.

For multi-tenant processes, put the tenant in the key (`CacheKey.For<T>(id).WithTenant(tenantId)`)
rather than in `TenantPrefix`, which is a process-wide static.

## 3. Key and tag limits

| Limit | Setting | Enforced |
|---|---|---|
| Key length | `Security.MaximumKeyLength` (512) | Inside the cache, per operation, for calls using the configured default entry options |
| Key characters | — | `CacheKeyBuilder` |
| Tag count | `Security.MaximumTagCount` (16) | `ICacheGuard.ValidateTags`, application-invoked |
| Tag length | `Security.MaximumTagLength` (128) | `ICacheGuard.ValidateTags`, application-invoked |

`Security.KeyLengthPolicy` and `Security.TagPolicy` accept `Throw` (default), `Warn` or `Ignore`.
`Throw` is the right default: an over-limit key is a defect, and it should surface in test rather
than silently produce an unbounded key in production.

**Two documented gaps**, both consequences of exposing the cache operation contract directly rather
than wrapping it:

1. Calls that pass explicit per-entry options bypass the engine hook the key guard runs in.
2. Tags are not visible to any interception point, so tag limits must be applied by the application.

Both are fixed by calling `ICacheGuard` at the boundary where the key or tags are built from
untrusted input:

```csharp
public sealed class ProductService(IFusionCache cache, ICacheGuard guard)
{
    public async Task<Product?> GetAsync(string sku, CancellationToken ct)
    {
        var key = CacheKey.For<Product>(sku).Build();
        guard.ValidateKey(key);
        // …
    }
}
```

Closing them properly would require a delegating wrapper over every cache method — the design this
release exists to avoid.

## 4. Payloads

Distributed payload layout:

```text
byte 0     : 0x00 raw | 0x01 Brotli
bytes 1..n : serialized value
```

- The header is always written, so toggling compression never orphans existing entries.
- An unrecognised header is **rejected**, not guessed at. A value rewritten by anything other than
  Caching.NET becomes a cache miss, and the next factory result overwrites it. This is the
  difference between a poisoned entry causing a refetch and a poisoned entry being parsed.
- With `Resilience.AllowBackgroundDistributedOperations: false`, refusing a write **fails the
  caller** rather than degrading — so a user-influenced payload size becomes a request failure. See
  [OPERATIONS.md](OPERATIONS.md#foreground-writes-surface-serialization-failures); Caching.NET warns
  about the combination at startup.
- Size is checked on write (refuse) and on read (reject as corrupt), so an oversized value planted
  directly in Redis cannot be pulled into process memory.
- Brotli output is read in bounded chunks against `Compression.MaximumDecompressedBytes`
  (16 MiB default) rather than copied wholesale.

Startup validation rejects a decompression ceiling below the payload limit, since that would reject
legitimate entries at the size boundary.

### Serializers

| Format | Safety |
|---|---|
| `SystemTextJson` (default) | No `$type` handling, no polymorphic resolution. A payload cannot name a type to construct. |
| `MessagePack` | Contractless resolver — shape-based, not type-name-based. Not the typeless resolver. |

Never used: `BinaryFormatter`, `NetDataContractSerializer`, `JsonSerializerSettings.TypeNameHandling`
or any equivalent. There is no configuration switch that enables them.

## 5. Transport

```json
"Redis": {
  "Configuration": "redis-0.cache.svc:6380",
  "UseTls": true,
  "StrictCertificateValidation": true
}
```

- `StrictCertificateValidation` defaults to `true`: any TLS policy error rejects the connection.
- `false` tolerates a certificate **name mismatch only** — never a chain error, never a missing
  certificate. Use it only for a private endpoint whose DNS name differs from the certificate
  subject.
- Setting `false` without TLS enabled **fails startup**, so the flag cannot sit in configuration
  giving a false impression of a deliberate choice.
- The first handshake is logged once at Information with subject, issuer, thumbprint and expiry —
  useful for diagnosing an expiring certificate, and none of it secret.
- Every outcome is counted at `caching.net.redis.tls.validations` with a classification: `ok`,
  `name_mismatch_accepted` (permissive validation let a name mismatch through), or one of the
  rejection reasons `name_mismatch`, `chain_error`, `certificate_missing`, `untrusted`.

Certificate policy is per cache instance, attached to that connection's configuration — there is no
process-wide mutable validator.

## 6. Redaction

| Surface | Rule |
|---|---|
| Logs | No values, no full payloads, no connection strings, no credentials. Keys are logged as a fingerprint unless `Security.AllowRawKeysInLogs` is set (development only) — see [Key redaction in engine log lines](#key-redaction-in-engine-log-lines) |
| Traces (`Caching.NET` source) | No values, no payloads, no keys. Caching.NET attaches no key attribute at all; an application that wants correlation adds `CacheTelemetryAttributes.KeyFingerprint` from `ICacheGuard.Fingerprint(key)` itself |
| Traces (engine sources) | **Raw physical cache key** on every operation span, as `fusioncache.operation.key`. Not redactable in the library — see §9 |
| Metrics | Fixed low-cardinality dimensions only. A unit test asserts nothing outside the allow-list is emitted |
| Health output | Exception **type** only — a health endpoint is often reachable, and a message can carry an endpoint or a credential fragment |
| Redis connection errors | Host only, never the full endpoint or connection string |
| Validation failures | Property names and limits, never the connection string |

### Key fingerprints

`ICacheGuard.Fingerprint(key)` returns a 16-character xxHash64 hex digest. It lets a key be
correlated across a log line, a span and a support ticket without exposing what the key contains.

It is a **non-cryptographic** hash. It protects against casual disclosure and accidental logging of
personal data in telemetry — not against a determined offline attack over a small key space. Never
fingerprint a value that is itself a secret.

### Key redaction in engine log lines

Caching.NET's own log messages never receive a raw key. The cache engine's do: it writes the
physical key into a structured `CacheKey` property on its per-operation lines, at `Information` —
the default level of an ASP.NET Core application, and a level any service turns on while
reproducing a cache problem. Because engine output is re-categorised under `Caching.NET`, an
application cannot filter those lines out by naming the engine either.

The logger adapter Caching.NET hands the engine therefore replaces that property with the
`ICacheGuard.Fingerprint` digest before the line reaches any provider, unless
`Security.AllowRawKeysInLogs` is set. Both the rendered message and the structured property carry
the fingerprint, so a structured sink sees the same redacted value as a console:

```text
FUSION [N=default I=...] (O=... K=6be8c4a02b3db9c0): GetOrDefaultAsync<T> call FEO[...]
```

The line stays useful — every entry about one key shares one token, and an operator can turn a key
from a support ticket into that token with `ICacheGuard.Fingerprint(key)` — without the key leaving
the process. `EngineKeyRedactionTests` pins both directions.

This covers logs only. Engine **trace** spans still carry the raw key and are not redactable in the
library; that is a separate decision, described in §9.

## 7. What not to cache

Caching.NET cannot detect sensitive data. Do not cache:

- Passwords, password hashes
- Access tokens, refresh tokens, session tokens
- API keys, client secrets
- Encryption keys, signing keys
- Payment-card data (PAN, CVV, expiry)
- Bank account or payment-instrument details
- Health records
- Government identifiers, or any special-category personal data

Cache an **identifier** and re-resolve the secret from its vault at point of use. If a cached value
must be user-scoped, put the user in the key and consider whether an in-memory-only named cache is
more appropriate than one that persists to shared Redis:

```csharp
services.AddCaching("user-scoped", cache => cache
    .UseInMemory()                                  // never leaves the process
    .WithApplicationPrefix("orders-api")
    .WithDefaultExpiration(TimeSpan.FromMinutes(1)));
```

## 8. Reporting

Report a suspected vulnerability in Caching.NET through the BAPS internal security process. Do not
open a public issue.

## 9. Known exposure: engine spans carry the raw cache key

The engine's activity sources attach the full physical cache key to every operation span:

```text
source: ZiggyCreatures.Caching.Fusion
tags:   fusioncache.operation.key=orders-api:prod:Order:user-4815162342
```

The engine exposes no option to turn this off, and suppressing it inside Caching.NET would mean
wrapping every cache call — the design this release exists to remove. It is therefore an exposure to
decide about, not a bug to configure away.

| Registration | Cache keys exported |
|---|---|
| `CacheTelemetry.ActivitySourceName` (recommended) | No |
| `CacheTelemetry.EngineActivitySourceNames` | Yes |
| `CacheTelemetry.ActivitySourceNames` | Yes |

Decide as follows:

1. **Keys are opaque** (surrogate ids, hashes, SKUs) — register whatever you like.
2. **Keys embed an identifier** (user id, tenant id, email, token, account number) — either register
   the branded source alone, or strip the attribute in the collector:

   ```csharp
   sealed class DropCacheKeyProcessor : BaseProcessor<Activity>
   {
       public override void OnEnd(Activity activity)
           => activity.SetTag(CacheTelemetry.EngineKeyAttributeName, null);
   }
   ```

3. **Either way, do not put a secret in a cache key.** A key is not a redacted surface anywhere:
   it also reaches Redis `MONITOR`, `SCAN` output, slow-log entries and RDB dumps.

Both the guarantee (Caching.NET spans carry no key) and the exposure (engine spans do) are asserted
by `SpanKeyExposureTests`, so neither statement can go stale against a future engine version.
