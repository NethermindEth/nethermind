param(
    [string]$ResultsDir = ".benchmark-artifacts/results",
    [string]$OutputPath = ".benchmark-results/benchmark-summary.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Convert-MeanToNs {
    param([string]$Mean)

    if ([string]::IsNullOrWhiteSpace($Mean)) {
        throw "Mean value is empty."
    }

    if ($Mean -notmatch '([0-9]+(?:\.[0-9]+)?)') {
        throw "Failed to parse mean value '$Mean'."
    }

    $value = [double]$Matches[1]
    $unit = ($Mean.Trim().Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries) | Select-Object -Last 1).ToLowerInvariant()

    if ($unit -eq "ns") { return $value }
    if ($unit -eq "ms") { return $value * 1e6 }
    if ($unit -eq "s")  { return $value * 1e9 }
    if ($unit -eq "us" -or $unit -eq "µs" -or $unit -eq "μs" -or $unit -eq "îĽs") { return $value * 1e3 }

    if ($unit.EndsWith("s")) {
        # BenchmarkDotNet microsecond symbol can be mangled by encoding in some shells.
        return $value * 1e3
    }

    throw "Unsupported mean unit '$unit' in value '$Mean'."
}

function Get-ParameterColumns {
    param([string[]]$Columns)

    $methodIndex = [Array]::IndexOf($Columns, "Method")
    $meanIndex = [Array]::IndexOf($Columns, "Mean")
    if ($methodIndex -ge 0 -and $meanIndex -gt $methodIndex) {
        if ($meanIndex -eq $methodIndex + 1) {
            return @()
        }

        return @($Columns[($methodIndex + 1)..($meanIndex - 1)])
    }

    $reserved = @("Method", "Mean", "Error", "Allocated", "Gen 0", "Gen 1", "Gen 2", "Code Size")
    return $Columns | Where-Object { $reserved -notcontains $_ }
}

if (-not (Test-Path $ResultsDir)) {
    throw "Results directory '$ResultsDir' does not exist."
}

$files = @(Get-ChildItem -Path $ResultsDir -Filter "*-report.csv" -File | Sort-Object Name)
if ($files.Count -eq 0) {
    throw "No benchmark CSV files were found in '$ResultsDir'."
}

$entries = @()
foreach ($file in $files) {
    $rows = Import-Csv -Path $file.FullName
    $className = [IO.Path]::GetFileNameWithoutExtension($file.Name).Replace("-report", "")
    if ($rows.Count -eq 0) {
        continue
    }

    $columns = @($rows[0].PSObject.Properties.Name)
    $parameterColumns = Get-ParameterColumns -Columns $columns

    foreach ($row in $rows) {
        $parameterPairs = @()
        foreach ($column in $parameterColumns) {
            $value = [string]$row.$column
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $parameterPairs += "$column=$value"
            }
        }

        $id = "$className.$($row.Method)"
        if ($parameterPairs.Count -gt 0) {
            $id = "$id|$([string]::Join(',', $parameterPairs))"
        }

        $entries += [PSCustomObject]@{
            id = $id
            benchmark = "$className.$($row.Method)"
            parameters = $parameterPairs
            mean = [string]$row.Mean
            meanNs = [double](Convert-MeanToNs -Mean ([string]$row.Mean))
            allocated = [string]$row.Allocated
            source = $file.Name
        }
    }
}

$result = [PSCustomObject]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    benchmarks = @($entries | Sort-Object id)
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
}

$result | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "Wrote benchmark summary with $($entries.Count) rows to '$OutputPath'."
