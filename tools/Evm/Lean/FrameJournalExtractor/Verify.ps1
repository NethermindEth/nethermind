# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$package = $PSScriptRoot
$generated = Join-Path $package "Generated"
$scratchRoot = [System.IO.Path]::GetFullPath("D:\tmp")
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchRoot ("frame-journal-verify-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped D:\tmp."
}
$scratchGenerated = Join-Path $scratch "Generated"

try {
    New-Item -ItemType Directory -Path $scratchGenerated | Out-Null
    dotnet run --project (Join-Path $package "FrameJournalExtractor.csproj") `
        --configuration $Configuration `
        -p:SaveDiskSpace=true `
        -p:TreatWarningsAsErrors=true `
        -- `
        --repo-root $RepoRoot `
        --output $scratchGenerated `
        --lean-output (Join-Path $scratchGenerated "FrameJournalKernel.lean")
    if ($LASTEXITCODE -ne 0) { throw "FrameJournalExtractor generation failed." }

    foreach ($name in @(
        "FrameJournalKernel.ir.json",
        "FrameJournalKernel.source-manifest.json",
        "FrameJournalKernel.lean"
    )) {
        $checkedIn = Join-Path $generated $name
        $regenerated = Join-Path $scratchGenerated $name
        if (-not (Test-Path -LiteralPath $checkedIn)) { throw "Missing checked-in artifact $checkedIn." }
        if (-not (Test-Path -LiteralPath $regenerated)) { throw "Missing regenerated artifact $regenerated." }
        $checkedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $checkedIn).Hash
        $regeneratedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $regenerated).Hash
        if ($checkedHash -ne $regeneratedHash) { throw "Generated artifact drift: $name." }
    }

    dotnet test --project (Join-Path $package "Test/FrameJournalExtractor.Test.csproj") `
        --configuration $Configuration `
        -p:SaveDiskSpace=true `
        -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw "FrameJournalExtractor tests failed." }

    Push-Location $package
    try {
        lake build
        if ($LASTEXITCODE -ne 0) { throw "FrameJournalExtractor Lean build failed." }
    }
    finally {
        Pop-Location
    }

    $placeholder = Get-ChildItem -LiteralPath $package -Recurse -Filter *.lean |
        Where-Object { $_.FullName -notlike "*\.lake\*" } |
        Select-String -Pattern '\b(sorry|admit|axiom)\b'
    if ($placeholder) { throw "Lean placeholder or axiom found: $($placeholder.Path):$($placeholder.LineNumber)." }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
