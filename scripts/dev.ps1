#!/usr/bin/env pwsh
# scripts/dev.ps1 — local equivalent of CI. Cross-platform (Windows/Linux/macOS).
# Usage: pwsh scripts/dev.ps1 <command> [-Tfm net10.0] [-Configuration Release]
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'test:integration', 'test:chaos', 'test:property', 'aot', 'bench', 'bench:gate', 'pack', 'all', 'help')]
    [string]$Command = 'help',

    [string]$Tfm = '',
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

function Step($name) { Write-Host "▶ $name" -ForegroundColor Cyan }

function Invoke-Build {
    Step 'build'
    $buildArgs = @('build', '-c', $Configuration)
    if ($NoRestore) { $buildArgs += '--no-restore' }
    dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

function Invoke-Test([string]$project, [string]$tfm) {
    $testArgs = @('test', $project, '-c', $Configuration, '--no-build')
    if ($tfm) { $testArgs += @('-f', $tfm) }
    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw "test failed: $project" }
}

function Invoke-UnitMatrix {
    Step 'test (unit, all TFMs)'
    $tfms = if ($Tfm) { @($Tfm) } else { @('net8.0', 'net9.0', 'net10.0') }
    foreach ($t in $tfms) {
        Invoke-Test 'tests/Caching.NET.Tests' $t
    }
}

function Invoke-Integration {
    Step 'test:integration (Testcontainers Redis — Docker required)'
    $testArgs = @(
        'test',
        'tests/Caching.NET.Tests.Integration',
        '-c', $Configuration,
        '--no-build',
        '-f', 'net10.0'
    )
    if ($env:CACHING_ENABLE_HANG_DIAGNOSTICS -eq '1') {
        $testArgs += @('--blame-hang', '--blame-hang-timeout', '60s')
    }
    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw "test failed: tests/Caching.NET.Tests.Integration" }
}

function Invoke-Chaos {
    Step 'test:chaos (Polly fault injection)'
    Invoke-Test 'tests/Caching.NET.Tests.Chaos' 'net10.0'
}

function Invoke-Property {
    Step 'test:property (FsCheck)'
    Invoke-Test 'tests/Caching.NET.Tests.Properties' 'net10.0'
}

function Invoke-Aot {
    Step 'aot (PublishAot=true smoke)'
    $rid = if ($IsWindows) { 'win-x64' } `
           elseif ($IsMacOS) { if ((uname -m) -eq 'arm64') { 'osx-arm64' } else { 'osx-x64' } } `
           else { 'linux-x64' }
    dotnet publish aot/Caching.NET.AotSmoke -c $Configuration -r $rid --self-contained
    if ($LASTEXITCODE -ne 0) { throw "aot publish failed" }
    $publishDir = "aot/Caching.NET.AotSmoke/bin/$Configuration/net10.0/$rid/publish"
    $exe = Get-ChildItem -Path $publishDir -Filter 'Caching.NET.AotSmoke*' |
        Where-Object {
            -not $_.PSIsContainer -and
            (
                $_.Name -eq 'Caching.NET.AotSmoke' -or
                $_.Name -eq 'Caching.NET.AotSmoke.exe'
            )
        } |
        Select-Object -First 1
    if (-not $exe) { throw "aot binary not found in $publishDir" }
    & $exe.FullName
    if ($LASTEXITCODE -ne 0) { throw "aot smoke failed (exit=$LASTEXITCODE)" }
}

function Invoke-Bench {
    Step 'bench (BenchmarkDotNet)'
    dotnet run -c $Configuration --project benchmark/Caching.NET.Benchmark -- --filter '*' --exporters JSON --artifacts benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts
    if ($LASTEXITCODE -ne 0) { throw "bench run failed" }
    & "$repoRoot/scripts/combine-bench-results.ps1"
}

function Invoke-BenchGate {
    Step 'bench:gate (perf-gate.ps1 vs baseline)'
    $combinedPath = 'benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts/results/combined.json'
    if (-not (Test-Path $combinedPath)) {
        Invoke-Bench
    }
    & "$repoRoot/benchmark/perf-gate.ps1"
    if ($LASTEXITCODE -ne 0) { throw "perf-gate regression" }
}

function Invoke-Pack {
    Step 'pack'
    if (-not (Test-Path 'nupkgs')) { New-Item -ItemType Directory -Path 'nupkgs' | Out-Null }
    dotnet pack src/Caching.NET/Caching.NET.csproj -c $Configuration -o nupkgs
    if ($LASTEXITCODE -ne 0) { throw "pack failed" }
}

function Invoke-All {
    Invoke-Build
    Invoke-UnitMatrix
    Invoke-Integration
    Invoke-Chaos
    Invoke-Property
    Invoke-Aot
    Invoke-Bench
    Invoke-BenchGate
    Invoke-Pack
    Write-Host "`n✔ scripts/dev.ps1 all — green" -ForegroundColor Green
}

switch ($Command) {
    'build'            { Invoke-Build }
    'test'             { Invoke-Build; Invoke-UnitMatrix }
    'test:integration' { Invoke-Build; Invoke-Integration }
    'test:chaos'       { Invoke-Build; Invoke-Chaos }
    'test:property'    { Invoke-Build; Invoke-Property }
    'aot'              { Invoke-Aot }
    'bench'            { Invoke-Bench }
    'bench:gate'       { Invoke-BenchGate }
    'pack'             { Invoke-Pack }
    'all'              { Invoke-All }
    default {
        @"
scripts/dev.ps1 <command> [-Tfm <tfm>] [-Configuration Release|Debug] [-NoRestore]

  build              Restore + build (warnings-as-errors).
  test               Unit tests across [net8.0, net9.0, net10.0].
  test:integration   Testcontainers Redis suite (Docker required). Set CACHING_ENABLE_HANG_DIAGNOSTICS=1 to enable --blame-hang.
  test:chaos         Polly fault-injection suite.
  test:property      FsCheck property suite.
  aot                Caching.NET.AotSmoke publish + run on local RID.
  bench              BenchmarkDotNet run; writes combined.json.
  bench:gate         Compare combined.json vs bench-baseline.json (10% threshold).
  pack               dotnet pack into ./nupkgs/.
  all                Full local equivalent of pre-tag gate.
"@ | Write-Host
    }
}
