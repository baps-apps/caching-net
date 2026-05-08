#!/usr/bin/env pwsh
# Reads BenchmarkDotNet per-bench *-report-full.json files and emits a single
# combined.json keyed by FullName → { MeanNs, AllocatedBytes } for perf-gate.ps1.
param(
    [string]$ArtifactsDir = "benchmark/Caching.NET.Benchmark/BenchmarkDotNet.Artifacts/results"
)
$ErrorActionPreference = 'Stop'
$combined = @{}
Get-ChildItem -Path $ArtifactsDir -Filter '*-report-full.json' -ErrorAction SilentlyContinue | ForEach-Object {
    $r = Get-Content -Raw $_.FullName | ConvertFrom-Json
    foreach ($b in $r.Benchmarks) {
        $alloc = 0
        if ($b.Memory -and $b.Memory.BytesAllocatedPerOperation) { $alloc = [long]$b.Memory.BytesAllocatedPerOperation }
        $combined[$b.FullName] = @{
            MeanNs = [double]$b.Statistics.Mean
            AllocatedBytes = $alloc
        }
    }
}
$out = Join-Path $ArtifactsDir 'combined.json'
($combined | ConvertTo-Json -Depth 5) | Set-Content $out
Write-Host "wrote $out ($($combined.Count) benchmarks)"
