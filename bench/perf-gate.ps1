#!/usr/bin/env pwsh
# perf-gate.ps1 — Compare BenchmarkDotNet output against bench-baseline.json
# Usage: pwsh bench/perf-gate.ps1 [<results-dir>]
# Returns exit code 0 if no regression > 10%, 1 otherwise.

param(
    [string]$ResultsDir = "bench/Caching.NET.Bench/BenchmarkDotNet.Artifacts/results"
)

$BaselineFile = "bench/Caching.NET.Bench/bench-baseline.json"
$threshold = 0.10  # 10% regression threshold

if (-not (Test-Path $BaselineFile)) {
    Write-Error "Baseline file not found: $BaselineFile"
    exit 1
}

$baseline = Get-Content $BaselineFile | ConvertFrom-Json

if (-not (Test-Path $ResultsDir)) {
    Write-Warning "Results directory not found: $ResultsDir — skipping gate."
    exit 0
}

$regressions = @()
$jsonFiles = Get-ChildItem -Path $ResultsDir -Filter "*-report-full.json" -Recurse

foreach ($file in $jsonFiles) {
    $data = Get-Content $file.FullName | ConvertFrom-Json
    foreach ($bench in $data.Benchmarks) {
        $name = $bench.FullName
        $meanNs = $bench.Statistics.Mean
        $allocBytes = $bench.Memory.BytesAllocatedPerOperation

        $baseEntry = $baseline.$name
        if ($null -eq $baseEntry) { continue }

        if ($baseEntry.MeanNs -gt 0) {
            $meanDelta = ($meanNs - $baseEntry.MeanNs) / $baseEntry.MeanNs
            if ($meanDelta -gt $threshold) {
                $regressions += "$name Mean regressed by $([Math]::Round($meanDelta * 100, 1))%"
            }
        }

        if ($baseEntry.AllocatedBytes -gt 0) {
            $allocDelta = ($allocBytes - $baseEntry.AllocatedBytes) / $baseEntry.AllocatedBytes
            if ($allocDelta -gt $threshold) {
                $regressions += "$name Allocated regressed by $([Math]::Round($allocDelta * 100, 1))%"
            }
        }
    }
}

if ($regressions.Count -gt 0) {
    Write-Error "PERF GATE FAILED — regressions detected:"
    $regressions | ForEach-Object { Write-Host "  x $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Perf gate passed — no regressions > $($threshold * 100)%" -ForegroundColor Green
exit 0
