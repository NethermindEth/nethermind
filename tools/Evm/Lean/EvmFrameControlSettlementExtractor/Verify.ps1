# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release",
    [string]$Dotnet = "dotnet",
    [string]$Lake = "lake"
)

$ErrorActionPreference = "Stop"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$project = Join-Path $package "EvmFrameControlSettlementExtractor.csproj"
$tests = Join-Path $package "Test/EvmFrameControlSettlementExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$scratchRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$scratch = Join-Path $scratchRoot ("evm-frame-control-settlement-verify-" + [Guid]::NewGuid().ToString("N"))

if (-not $scratch.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the temporary root."
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    & $Dotnet build $tests -c $Configuration -p:SaveDiskSpace=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Stage D warning-as-error extractor build failed." }

    & $Dotnet test --project $tests -c $Configuration --no-build -- --filter FullyQualifiedName~EvmFrameControlSettlementExtractorTests --minimum-expected-tests 28
    if ($LASTEXITCODE -ne 0) { throw "Stage D focused mutation/determinism suite failed." }

    foreach ($name in @("first", "second")) {
        $output = Join-Path $scratch $name
        New-Item -ItemType Directory -Path $output | Out-Null
        & $Dotnet run --project $project -c $Configuration --no-build -- --repo-root $RepoRoot --output $output --lean-output (Join-Path $output "EvmFrameControlSettlementKernel.lean")
        if ($LASTEXITCODE -ne 0) { throw "Stage D $name extraction failed." }
    }

    foreach ($name in @(
        "EvmFrameControlSettlementKernel.ir.json",
        "EvmFrameControlSettlementKernel.source-manifest.json",
        "EvmFrameControlSettlementKernel.lean"
    )) {
        $first = Join-Path $scratch "first/$name"
        $second = Join-Path $scratch "second/$name"
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $first).Hash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash) {
            throw "Stage D nondeterministic artifact: $name."
        }
        $pinned = Join-Path $checkedIn $name
        if (-not (Test-Path -LiteralPath $pinned)) { throw "Missing checked-in Stage D artifact: $pinned." }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $pinned).Hash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash) {
            throw "Stage D checked-in artifact drift: $name."
        }
    }

    Push-Location $package
    try {
        & $Lake build -KwarningAsError=true
        if ($LASTEXITCODE -ne 0) { throw "Stage D Lake warning-as-error gate failed." }
        foreach ($module in @(
            "Generated/EvmFrameControlSettlementKernel.lean",
            "Specification/Types.lean",
            "Specification/Reference.lean",
            "Specification/AdmissionWitnesses.lean",
            "Refinement/FrameControlSettlement.lean",
            "Refinement/StageCFullPrecompileBridge.lean",
            "Refinement/StageCFullPrecompileBridgeVectors.lean"
        )) {
            & $Lake env lean -DwarningAsError=true $module
            if ($LASTEXITCODE -ne 0) { throw "Stage D direct Lean gate failed for $module." }
        }
        & (Join-Path $package 'Verify-BridgeMutationGates.ps1') -Lake $Lake
    }
    finally { Pop-Location }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolved = [System.IO.Path]::GetFullPath($scratch)
        if (-not $resolved.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.Path]::GetFileName($resolved).StartsWith("evm-frame-control-settlement-verify-", [StringComparison]::Ordinal)) {
            throw "Refusing cleanup outside the owned Stage D verification directory."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
