#!/usr/bin/env pwsh
# combine-bench-results.ps1 — Flatten BenchmarkDotNet JSON results into a single combined.json
# Usage: pwsh scripts/combine-bench-results.ps1 [<results-dir>] [<output-file>]

param(
    [string]$ResultsDir = "bench/Caching.NET.Bench/BenchmarkDotNet.Artifacts/results",
    [string]$OutputFile = "bench/combined.json"
)

$combined = @{}

if (-not (Test-Path $ResultsDir)) {
    Write-Warning "Results directory not found: $ResultsDir"
    exit 0
}

$jsonFiles = Get-ChildItem -Path $ResultsDir -Filter "*-report-full.json" -Recurse

foreach ($file in $jsonFiles) {
    $data = Get-Content $file.FullName | ConvertFrom-Json
    foreach ($bench in $data.Benchmarks) {
        $combined[$bench.FullName] = @{
            MeanNs         = [long]$bench.Statistics.Mean
            AllocatedBytes = [long]$bench.Memory.BytesAllocatedPerOperation
        }
    }
}

$combined | ConvertTo-Json -Depth 5 | Set-Content -Path $OutputFile
Write-Host "Combined results written to $OutputFile"
