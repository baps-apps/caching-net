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
- OpenTelemetry **metrics** are the supported production signal; subscribe to `CacheInstruments.MeterName`. The library exposes an `ActivitySource` for future use but **does not emit spans** for cache operations in v2, so there is no automatic trace path that could carry key material. `IncludeKeyHashInTraces` is currently unused.
- Cache keys never appear in log messages by default (hashed fingerprint). Toggle `Options.IncludeRawKeyInLogs=true` for dev only.

## Supply chain

- All packages published from the GitHub release pipeline are signed (NuGet package signing).
- Source-link is enabled (`Microsoft.SourceLink.GitHub`) — debuggers can fetch original source from the GitHub commit referenced in the symbols.
- Each `.nupkg` ships an SPDX 2.2 SBOM at `_manifest/spdx_2.2/manifest.spdx.json`.
- Builds are deterministic (`<DeterministicSourcePaths>true</DeterministicSourcePaths>`).
- `MessagePack` is shipped as a hard dep but only loaded when the consumer wires up `WithMessagePackSerializer()` — trim eliminates unused types when AOT-publishing.

## Reporting vulnerabilities

Open a GitHub security advisory in the repository. PGP key available on request.
