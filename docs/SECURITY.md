# Security

## TLS posture

- v2 default: `StrictRedisCertificateValidation=true` (was `false` in v1). Any SSL policy error rejects the connection.
- Toggle to `false` only for dev/test clusters with self-signed certs that mismatch the hostname; the library still rejects chain errors and untrusted roots.
- First validation per process emits an INFO log with subject, issuer, thumbprint, expiry.
- Every validation increments `cache.tls.validation` (tag `cache.tls_result`).

## Secret redaction

Connection strings with `password=`, `user=`, and `name=` segments are redacted before any log message or exception. Used from `IValidateOptions<CacheOptions>` failure messages and any logging that touches the connection string.

## PII

- Raw cache keys never appear in metrics tags. High-cardinality placeholder names (`{key}`, `{tenant}`, `{user_id}` and `cache.*` variants) are forbidden in `ILogger` message templates and `BeginScope` formats by convention; the library follows this rule on its own logging path. Consumers must self-police — there is no compile-time analyzer.
- OpenTelemetry **metrics** are the supported production signal; subscribe to `CacheInstruments.MeterName`. The library also emits one `Activity` per cache call from `ActivitySource` `Caching.NET` (`CacheInstruments.ActivitySourceName`) — that is an automatic trace path, and consumers who export spans should weigh it accordingly. Raw cache keys never appear on a span, regardless of `IncludeRawKeyInLogs`. `CacheOptions.IncludeKeyHashInTraces` (opt-in, default `false`) is honored: when `true`, single-key operations get a `cache.key_hash` tag — `StableStringHash.Compute64(key)` as 16 hex characters — a stable per-key correlation identifier, not the raw key, but still something to weigh before enabling in production. It lets anyone with access to the tracing backend correlate spans by key across time, and — because xxHash64 is a fast non-cryptographic hash — it does not hide the key from anyone who can guess its shape: for a predictable keyspace such as `user:{id}` or `tenant:{name}`, the preimage falls to a brute-force sweep in seconds. The bound is the size of your keyspace, not the width of the digest. Treat `cache.key_hash` as a correlation ID, never as a redaction of a key that embeds an identifier you care about.
- Cache keys never appear in log messages by default (hashed fingerprint). Toggle `Options.IncludeRawKeyInLogs=true` for dev only.

## Supply chain

- All packages published from the GitHub release pipeline are signed (NuGet package signing).
- Source-link is enabled (`Microsoft.SourceLink.GitHub`) — debuggers can fetch original source from the GitHub commit referenced in the symbols.
- Each `.nupkg` ships an SPDX 2.2 SBOM at `_manifest/spdx_2.2/manifest.spdx.json`.
- Builds are deterministic (`<DeterministicSourcePaths>true</DeterministicSourcePaths>`).
- `MessagePack` is shipped as a hard dep but only loaded when the consumer wires up `WithMessagePackSerializer()` — trim eliminates unused types when AOT-publishing.

## Reporting vulnerabilities

Open a GitHub security advisory in the repository. PGP key available on request.
