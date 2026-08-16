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
| Unbounded keys exhausting memory or blowing up telemetry cardinality | `Security.MaximumKeyLength`, enforced on every call, inside the cache adapter, whether or not the call supplies per-call overrides |
| Oversized values exhausting Redis or process memory | `Serialization.MaximumPayloadBytes`, enforced on write **and** on read |
| Cache poisoning — a rewritten Redis value deserialized as trusted input | One-byte format header validated before any deserialization; unrecognised header → treated as a miss |
| Decompression bomb — a small compressed value expanding without bound | Bounded read loop against `Compression.MaximumDecompressedBytes` |
| Polymorphic deserialization / type confusion | System.Text.Json with no type-name handling; MessagePack contractless. No type name is ever written to or read from a payload |
| Credentials leaking through logs, traces or crash dumps | Connection strings redacted; only exception *types* in health output; validation messages never echo the connection string |
| Personal data leaking through telemetry | Caching.NET metrics, logs and spans carry no keys, values or tags by default; a non-reversible fingerprint is used on spans instead. `Security.AllowRawKeysInTelemetry` opts a cache instance into raw keys on spans — off by default — see §9 |
| A man-in-the-middle on the Redis connection | TLS with strict certificate validation by default; permissive mode rejected at startup unless TLS is actually on; optional mutual-TLS client certificate and an additional server-certificate validation callback (`Redis.ClientCertificate`, `Redis.ValidateServerCertificate`) |

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
| Key length | `Security.MaximumKeyLength` (512) | Every call, inside `FusionCacheService`, before the key reaches the engine |
| Key characters | — | `CacheKeyBuilder` |
| Tag count | `Security.MaximumTagCount` (16) | Every call that supplies tags, inside `FusionCacheService` |
| Tag length | `Security.MaximumTagLength` (128) | Every call that supplies tags, inside `FusionCacheService` |

`Security.KeyLengthPolicy` and `Security.TagPolicy` accept `Throw` (default), `Warn` or `Ignore`.
`Throw` is the right default: an over-limit key is a defect, and it should surface in test rather
than silently produce an unbounded key in production.

Because `ICacheService` is Caching.NET's own adapter over the engine rather than the engine's
operation contract, `FusionCacheService` validates the key and any supplied tags **at the start of
every call**, ahead of anything reaching the engine — not only for calls that fall back to the
engine's own configured-default entry options the way the equivalent engine-level hook does. There is
no per-call path that bypasses either guard.

`ICacheGuard` remains useful beyond that: call it directly at a boundary where a key or tags are
built from untrusted input but not yet passed to the cache — for example to reject a bad key before
doing other work in the same request:

```csharp
public sealed class ProductService(ICacheService cache, ICacheGuard guard)
{
    public async Task<Product?> GetAsync(string sku, CancellationToken ct)
    {
        var key = CacheKey.For<Product>(sku).Build();
        guard.ValidateKey(key);
        return await cache.GetOrDefaultAsync<Product?>(key, token: ct);
    }
}
```

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

### Mutual TLS and additional server-certificate checks

Two more `RedisOptions` members cover cases the strict/permissive toggle above does not:

- **`Redis.ClientCertificate`** (`X509Certificate2?`) — presented during the TLS handshake, for a
  Redis server that requires mutual TLS. Its own XML doc says "Requires `UseTls`", and startup
  validation is slightly more permissive than that: `CachingOptionsValidator` accepts either
  `Redis.UseTls: true` **or** a connection string containing `ssl=true`, because TLS can be turned on
  either way and a rule keyed only on the `UseTls` flag would reject a working `ssl=true` connection
  string. Setting `ClientCertificate` (or `ValidateServerCertificate`, below) without either fails
  startup with a message naming both acceptable fixes.
- **`Redis.ValidateServerCertificate`** (`RemoteCertificateValidationCallback?`) — an additional check
  run *after* Caching.NET's own validation. This is the security-relevant one of the two: it can only
  **tighten** the result (return `false` to reject a connection Caching.NET's own check would have
  accepted), never loosen it — `StrictCertificateValidation`/`UseTls` still run first and still decide
  whether a policy error is fatal. Use it to pin a specific certificate thumbprint or enforce a
  private CA beyond what `StrictCertificateValidation` alone checks.

Neither member has a dedicated `CachingBuilder` method; set them through `WithRedis(options => ...)`.

## 6. Redaction

| Surface | Rule |
|---|---|
| Logs | No values, no full payloads, no connection strings, no credentials. Keys are logged as a fingerprint unless `Security.AllowRawKeysInLogs` is set (development only) — see [Key redaction in engine log lines](#key-redaction-in-engine-log-lines) |
| Traces (`Caching.NET` operation spans, plus `cache.backplane.receive`) | Keys carry `cache.key.fingerprint` by default. `Security.AllowRawKeysInTelemetry` switches that cache instance's spans to `cache.key` (the literal key) instead — see §9 |
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

This covers logs only. `Security.AllowRawKeysInLogs` and `Security.AllowRawKeysInTelemetry` (§9) are
independent settings — one controls log lines, the other controls trace spans — and can be set
separately.

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

## 9. Raw keys in telemetry: `AllowRawKeysInTelemetry`

Every Caching.NET operation span that has a key (`cache.get_or_set`, `cache.set`, `cache.remove`,
`cache.expire`, `cache.try_get`, `cache.get_or_default`, and `cache.remove_by_tag`, where the tag
stands in for the key — `cache.clear` is the one operation with no key at all; the full catalogue is
in [TELEMETRY.md §3](TELEMETRY.md#3-tracing)) carries a key attribute by default:
`cache.key.fingerprint`, the non-reversible digest from `ICacheGuard.Fingerprint`, never the literal
key. `Security.AllowRawKeysInTelemetry` (default `false`) switches that cache instance's spans to
`cache.key` — the literal key, prefix included — instead:

```csharp
services.AddCaching(cache => cache
    .UseInMemory()
    .WithApplicationPrefix("orders-api")
    .WithSecurity(security => security.AllowRawKeysInTelemetry = true));
```

`CacheTelemetryContext.TagKey` decides which attribute to attach once per span, inside Caching.NET's
own code — there is no processor, collector step, or per-consumer opt-out needed either way, because
the choice is made before the span is emitted rather than stripped from it afterward.

**`cache.backplane.receive` follows the same rule.** It is not an operation span — it wraps an
invalidation another instance published — but it carries the key that message was for, so it is
covered by this setting exactly as the operation spans are. The key is recovered from the message and
decoded back to the caller-facing form, which means the raw-key opt-in exposes the same string there
as it does on the publishing instance's `cache.remove`. See
[TELEMETRY.md](TELEMETRY.md#what-a-received-message-says).

Decide as follows:

1. **Keys are opaque** (surrogate ids, hashes, SKUs) — enabling `AllowRawKeysInTelemetry` costs
   little; the fingerprint already gives correlation without exposure, so there is often no reason to
   turn it on at all.
2. **Keys embed an identifier** (user id, tenant id, email, token, account number) — leave
   `AllowRawKeysInTelemetry` off (the default). Span attributes are indexed and retained under the
   tracing backend's own policy and are readable by everyone with trace access, so this is a
   data-flow decision, not a debug toggle. An application that wants the same correlation without the
   exposure computes `ICacheGuard.Fingerprint(key)` itself and matches it against
   `cache.key.fingerprint`.

3. **Either way, do not put a secret in a cache key.** A key is not a redacted surface anywhere: it
   also reaches Redis `MONITOR`, `SCAN` output, slow-log entries and RDB dumps.

Both the guarantee (raw keys stay off spans by default) and the opt-in (`AllowRawKeysInTelemetry`
switches them on) are asserted by `SpanKeyExposureTests`, and for the backplane receive span by
`InstrumentedBackplaneReceiveTests.ReceiveSpan_CarriesTheKeyFingerprintWithoutTheApplicationPrefix`
and `ReceiveSpan_CarriesTheRawKey_WhenRawKeysAreAllowed`.
