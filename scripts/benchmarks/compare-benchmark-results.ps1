param(
    [string]$CurrentPath = ".benchmark-results/benchmark-summary.json",
    [string]$BaselinePath = ".benchmark-baseline/benchmark-summary.json",
    [string]$OutputJsonPath = ".benchmark-results/benchmark-comparison.json",
    [string]$OutputMarkdownPath = ".benchmark-results/benchmark-comparison.md",
    [double]$RegressionThresholdPercent = 5.0,
    [int]$TopCount = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Format-Ns {
    param([double]$Nanoseconds)

    if ($Nanoseconds -ge 1e9) { return "{0:N3} s" -f ($Nanoseconds / 1e9) }
    if ($Nanoseconds -ge 1e6) { return "{0:N3} ms" -f ($Nanoseconds / 1e6) }
    if ($Nanoseconds -ge 1e3) { return "{0:N3} us" -f ($Nanoseconds / 1e3) }
    return "{0:N3} ns" -f $Nanoseconds
}

function Format-BenchmarkDisplayName {
    param([string]$BenchmarkId)

    if ([string]::IsNullOrWhiteSpace($BenchmarkId)) {
        return $BenchmarkId
    }

    return $BenchmarkId.Replace("Nethermind.Evm.Benchmark.", "").Replace("Nethermind.Benchmarks.Blockchain.", "")
}

if (-not (Test-Path $CurrentPath)) {
    throw "Current benchmark summary '$CurrentPath' does not exist."
}

$current = Get-Content -Path $CurrentPath -Raw | ConvertFrom-Json
$currentRows = @($current.benchmarks)

$baselineRows = @()
$hasBaseline = Test-Path $BaselinePath
if ($hasBaseline) {
    $baseline = Get-Content -Path $BaselinePath -Raw | ConvertFrom-Json
    $baselineRows = @($baseline.benchmarks)
}

$baselineById = @{}
foreach ($row in $baselineRows) {
    $baselineById[$row.id] = $row
}

$currentById = @{}
foreach ($row in $currentRows) {
    $currentById[$row.id] = $row
}

$compared = @()
$newBenchmarks = @()
foreach ($row in $currentRows) {
    if (-not $baselineById.ContainsKey($row.id)) {
        $newBenchmarks += $row
        continue
    }

    $baselineRow = $baselineById[$row.id]
    $baselineNs = [double]$baselineRow.meanNs
    $currentNs = [double]$row.meanNs
    $deltaNs = $currentNs - $baselineNs
    $deltaPercent = if ($baselineNs -eq 0) { 0.0 } else { ($deltaNs / $baselineNs) * 100.0 }

    $status = "neutral"
    if ($deltaPercent -gt $RegressionThresholdPercent) {
        $status = "regression"
    } elseif ($deltaPercent -lt -$RegressionThresholdPercent) {
        $status = "improvement"
    }

    $compared += [PSCustomObject]@{
        id = $row.id
        benchmark = $row.benchmark
        baselineMeanNs = $baselineNs
        currentMeanNs = $currentNs
        baselineMean = $baselineRow.mean
        currentMean = $row.mean
        deltaNs = $deltaNs
        deltaPercent = $deltaPercent
        status = $status
    }
}

$missingBenchmarks = @()
foreach ($row in $baselineRows) {
    if (-not $currentById.ContainsKey($row.id)) {
        $missingBenchmarks += $row
    }
}

$regressions = @($compared | Where-Object status -eq "regression" | Sort-Object deltaPercent -Descending)
$improvements = @($compared | Where-Object status -eq "improvement" | Sort-Object deltaPercent)
$neutrals = @($compared | Where-Object status -eq "neutral")

$summary = [PSCustomObject]@{
    hasBaseline = $hasBaseline
    comparedCount = $compared.Count
    regressionCount = $regressions.Count
    improvementCount = $improvements.Count
    neutralCount = $neutrals.Count
    newCount = $newBenchmarks.Count
    missingCount = $missingBenchmarks.Count
    regressionThresholdPercent = $RegressionThresholdPercent
}

$output = [PSCustomObject]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    summary = $summary
    compared = $compared
    regressions = $regressions
    improvements = $improvements
    newBenchmarks = $newBenchmarks
    missingBenchmarks = $missingBenchmarks
}

$outputDirectory = Split-Path -Parent $OutputJsonPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
}

$output | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputJsonPath -Encoding UTF8

$lines = @()
$lines += "<!-- benchmark-comparison -->"
$lines += "## Benchmark Comparison"
$lines += ""

if (-not $hasBaseline) {
    $lines += "No cached master baseline was found. Current run has $($currentRows.Count) benchmark rows."
} else {
    $lines += "Threshold for regression/improvement: **$RegressionThresholdPercent%**."
    $lines += ""
    $lines += "- Compared: **$($summary.comparedCount)**"
    $lines += "- Regressions: **$($summary.regressionCount)**"
    $lines += "- Improvements: **$($summary.improvementCount)**"
    $lines += "- Neutral: **$($summary.neutralCount)**"
    $lines += "- New benchmarks in PR: **$($summary.newCount)**"
    $lines += "- Missing vs baseline: **$($summary.missingCount)**"
    $lines += ""

    if ($regressions.Count -gt 0) {
        $lines += "### Top Regressions"
        $lines += "| Benchmark | Baseline | Current | Delta |"
        $lines += "|---|---:|---:|---:|"
        foreach ($row in $regressions | Select-Object -First $TopCount) {
            $displayName = Format-BenchmarkDisplayName -BenchmarkId ([string]$row.id)
            $lines += "| $displayName | $(Format-Ns -Nanoseconds $row.baselineMeanNs) | $(Format-Ns -Nanoseconds $row.currentMeanNs) | +$([Math]::Round($row.deltaPercent, 2))% |"
        }
        $lines += ""
    }

    if ($improvements.Count -gt 0) {
        $lines += "### Top Improvements"
        $lines += "| Benchmark | Baseline | Current | Delta |"
        $lines += "|---|---:|---:|---:|"
        foreach ($row in $improvements | Select-Object -First $TopCount) {
            $displayName = Format-BenchmarkDisplayName -BenchmarkId ([string]$row.id)
            $lines += "| $displayName | $(Format-Ns -Nanoseconds $row.baselineMeanNs) | $(Format-Ns -Nanoseconds $row.currentMeanNs) | $([Math]::Round($row.deltaPercent, 2))% |"
        }
        $lines += ""
    }
}

$mdDirectory = Split-Path -Parent $OutputMarkdownPath
if (-not [string]::IsNullOrWhiteSpace($mdDirectory)) {
    New-Item -Path $mdDirectory -ItemType Directory -Force | Out-Null
}

$lines -join "`n" | Set-Content -Path $OutputMarkdownPath -Encoding UTF8
Write-Host "Wrote benchmark comparison to '$OutputJsonPath' and '$OutputMarkdownPath'."
