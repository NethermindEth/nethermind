# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$package = $PSScriptRoot
$generated = Join-Path $package "Generated"
$scratchRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchRoot ("world-journal-verify-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($scratchRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the system temporary directory."
}
$scratchGenerated = Join-Path $scratch "Generated"

try {
    New-Item -ItemType Directory -Path $scratchGenerated | Out-Null
    dotnet run --project (Join-Path $package "WorldJournalExtractor.csproj") `
        --configuration $Configuration `
        -p:SaveDiskSpace=true `
        -- `
        --repo-root $RepoRoot `
        --output $scratchGenerated `
        --lean-output (Join-Path $scratchGenerated "WorldJournalKernel.lean")
    if ($LASTEXITCODE -ne 0) { throw "WorldJournalExtractor generation failed." }

    foreach ($name in @(
        "WorldJournalKernel.ir.json",
        "WorldJournalKernel.source-manifest.json",
        "WorldJournalKernel.lean"
    )) {
        $checkedIn = Join-Path $generated $name
        $regenerated = Join-Path $scratchGenerated $name
        if (-not (Test-Path -LiteralPath $checkedIn)) { throw "Missing checked-in artifact $checkedIn." }
        if (-not (Test-Path -LiteralPath $regenerated)) { throw "Missing regenerated artifact $regenerated." }
        $checkedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $checkedIn).Hash
        $regeneratedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $regenerated).Hash
        if ($checkedHash -ne $regeneratedHash) { throw "Generated artifact drift: $name." }
    }

    dotnet test --project (Join-Path $package "Test/WorldJournalExtractor.Test.csproj") `
        --configuration $Configuration `
        -p:SaveDiskSpace=true `
        -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw "WorldJournalExtractor tests failed." }

    Push-Location $package
    try {
        lake build
        if ($LASTEXITCODE -ne 0) { throw "WorldJournalExtractor Lean build failed." }
    }
    finally {
        Pop-Location
    }

    $placeholder = Get-ChildItem -LiteralPath $package -Recurse -Filter *.lean |
        Select-String -Pattern '\b(sorry|admit|axiom)\b'
    if ($placeholder) { throw "Lean placeholder or axiom found: $($placeholder.Path):$($placeholder.LineNumber)." }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
