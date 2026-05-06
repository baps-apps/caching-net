# Security

## TLS posture

- v2 default: `StrictRedisCertificateValidation=true` (was `false` in v1). Any SSL policy error rejects the connection.
- Toggle to `false` only for dev/test clusters with self-signed certs that mismatch the hostname; the library still rejects chain errors and untrusted roots.
- First validation per process emits an INFO log with subject, issuer, thumbprint, expiry.
- Every validation increments `cache.tls.validation` (tag `cache.tls_result`).

## Secret redaction

Connection strings with `password=`, `user=`, and `name=` segments are redacted before any log message or exception. Used from `IValidateOptions<CacheOptions>` failure messages and any logging that touches the connection string.

## PII

- Raw cache keys never appear in metrics tags (the `CN0001` analyzer enforces this).
- Cache keys never appear in trace activities by default. Toggle `Options.IncludeKeyHashInTraces=true` to emit a `cache.key_hash` (xxHash64 hex) attribute when needed.
- Cache keys never appear in log messages by default. Toggle `Options.IncludeRawKeyInLogs=true` for dev only.

## Supply chain

- All packages published from the GitHub release pipeline are signed (NuGet package signing).
- Source-link is enabled (`Microsoft.SourceLink.GitHub`) — debuggers can fetch original source from the GitHub commit referenced in the symbols.
- Each `.nupkg` ships an SPDX 2.2 SBOM at `_manifest/spdx_2.2/manifest.spdx.json`.
- Builds are deterministic (`<DeterministicSourcePaths>true</DeterministicSourcePaths>`).
- `MessagePack` is shipped as a hard dep but only loaded when the consumer wires up `WithMessagePackSerializer()` — trim eliminates unused types when AOT-publishing.

## Reporting vulnerabilities

Open a GitHub security advisory in the repository. PGP key available on request.
