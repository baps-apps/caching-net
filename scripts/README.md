# scripts/

Local-only build, test, bench, and pack tooling, plus GitHub Packages publishing utilities. All scripts run on Windows, macOS, and Linux using PowerShell Core (`pwsh`).

## Prerequisites

- PowerShell Core 7.4+ (`pwsh`)
- .NET 10 SDK (with .NET 8 + 9 targeting packs installed via the SDK's multi-target support)
- Docker (only for `test:integration`)

## dev.ps1 — local CI gate

`scripts/dev.ps1` is the single entrypoint for every local gate. There is no remote CI service — every check runs on the developer's machine.

```bash
pwsh scripts/dev.ps1 help
pwsh scripts/dev.ps1 build
pwsh scripts/dev.ps1 test                  # unit tests, all TFMs
pwsh scripts/dev.ps1 test -Tfm net10.0     # single TFM iteration
pwsh scripts/dev.ps1 test:integration      # requires Docker
pwsh scripts/dev.ps1 test:chaos            # Polly fault-injection suite
pwsh scripts/dev.ps1 test:property         # FsCheck property suite
pwsh scripts/dev.ps1 aot                   # PublishAot smoke
pwsh scripts/dev.ps1 bench                 # run BenchmarkDotNet; write combined.json
pwsh scripts/dev.ps1 bench:gate            # compare combined.json vs bench-baseline.json
pwsh scripts/dev.ps1 pack                  # nupkg + snupkg into ./nupkgs/
pwsh scripts/dev.ps1 all                   # full pre-tag gate
```

### Perf gate

`bench/perf-gate.ps1` compares `BenchmarkDotNet.Artifacts/results/combined.json` against `bench/Caching.NET.Bench/bench-baseline.json`. Benchmarks with > 10% mean or allocation regression fail the gate.

### Pre-tag gate (acceptance criterion §15.2)

`scripts/dev.ps1 all` must be green on at least one Windows host AND at least one Linux/macOS host before tagging `v2.0.0`. Capture the run output and attach to the release notes.

---

## Package publishing

The publish script builds, packs, and pushes; if the version already exists, it deletes that version and republishes (requires PAT with `delete:packages`).

Two scripts are provided:

- `publish-package.ps1`: build, pack, and push a version
- `delete-package.ps1`: delete a specific version from GitHub Packages

### GitHub PAT

1. Go to `https://github.com/settings/tokens`
2. Generate new token (classic)
3. Scopes: `write:packages`, `read:packages`, and `delete:packages` (needed for overwrite)
4. Set the token as `GITHUB_PAT` when running scripts

### Configuration

- **Package ID:** `Caching.NET`
- **Project:** `src/Caching.NET/Caching.NET.csproj`
- **Output:** `nupkgs/` at repo root
- **Namespace:** GitHub org or user that hosts the package (default: `baps-apps`). Override with `GITHUB_NAMESPACE` if your repo is under a different org/user.

Ensure the project is packable. In `Caching.NET.csproj`:

```xml
<PropertyGroup>
  <PackageId>Caching.NET</PackageId>
  <Version>1.0.0</Version>
  <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
  <!-- Optional: Description, Authors, PackageReadmeFile, etc. -->
</PropertyGroup>
```

The publish script can infer version from `<Version>` in the `.csproj`, or you can pass it as an argument.

### Publish (recommended)

```powershell
# Set PAT (required)
$env:GITHUB_PAT = "your_token_here"

# Optional: use a different GitHub org/user for the feed
$env:GITHUB_NAMESPACE = "your-org"

# Publish (version from .csproj)
pwsh scripts/publish-package.ps1

# Or specify version (updates .csproj if <Version> exists, then builds/packs/pushes)
pwsh scripts/publish-package.ps1 1.0.1
```

**Windows (built-in PowerShell):**

```powershell
powershell scripts/publish-package.ps1
```

**macOS/Linux (make executable once):**

```bash
chmod +x scripts/publish-package.ps1
./scripts/publish-package.ps1
```

### Delete a package version

Removes a specific version of Caching.NET from GitHub Packages (e.g. to fix a bad publish). PAT must have `delete:packages`.

```powershell
$env:GITHUB_PAT = "your_token_here"
pwsh scripts/delete-package.ps1 1.0.0
```

---

## Manual build and pack

From repo root:

```bash
dotnet build src/Caching.NET/Caching.NET.csproj --configuration Release
dotnet pack src/Caching.NET/Caching.NET.csproj --configuration Release --no-build --output nupkgs
```

Packages are produced in `nupkgs/` (e.g. `Caching.NET.1.0.0.nupkg`).

---

## Manual push

After adding the GitHub Packages NuGet source (once):

```bash
dotnet nuget add source https://nuget.pkg.github.com/YOUR_ORG/index.json \
  --name github \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text
```

Push:

```bash
dotnet nuget push nupkgs/Caching.NET.1.0.0.nupkg --api-key YOUR_GITHUB_PAT --source github
```

---

## Troubleshooting

| Issue | What to do |
|--------|------------|
| **403 Forbidden** | Confirm PAT has `write:packages` and `delete:packages`; check namespace/org. |
| **Package already exists** | Script normally deletes and republishes. If it fails, ensure `delete:packages`; or delete the version in GitHub Packages UI and run again. |
| **Version not found in .csproj** | Add `<Version>1.0.0</Version>` to the first `<PropertyGroup>` in `Caching.NET.csproj`, or pass version: `pwsh scripts/publish-package.ps1 1.0.0`. |
| **Package file not found** | Ensure `dotnet pack` succeeds and produces `nupkgs/Caching.NET.<Version>.nupkg`; project needs `PackageId` (and optionally `Version`) for correct package name. |

---

## Reference

- [GitHub Packages – NuGet](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [Managing PATs](https://github.com/settings/tokens)

