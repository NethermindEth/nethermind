# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release",
    [string]$Dotnet = "dotnet",
    [string]$Lake = "lake"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$previousSourceDateEpoch = $env:SOURCE_DATE_EPOCH
$env:SOURCE_DATE_EPOCH = "1789035784"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$project = Join-Path $package "SequentialBlockTransactionFoldExtractor.csproj"
$tests = Join-Path $package "Test/SequentialBlockTransactionFoldExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$scratchRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$scratch = Join-Path $scratchRoot ("sequential-block-fold-verify-" + [Guid]::NewGuid().ToString("N"))

if (-not $scratch.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the temporary root."
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null

    & $Dotnet build $project -c $Configuration -p:SaveDiskSpace=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Sequential transaction-fold extractor build failed." }

    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Checked-in source/artifact validation failed." }

    & $Dotnet build $tests -c $Configuration -p:SaveDiskSpace=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Sequential transaction-fold test build failed." }

    & $Dotnet test --project $tests -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --minimum-expected-tests 314
    if ($LASTEXITCODE -ne 0) { throw "Sequential transaction-fold mutation suite failed." }

    foreach ($name in @("first", "second")) {
        $output = Join-Path $scratch $name
        New-Item -ItemType Directory -Path $output | Out-Null
        & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
            --repo-root $repo --output $output
        if ($LASTEXITCODE -ne 0) { throw "Fresh $name extraction failed." }
    }

    foreach ($name in @(
        "SequentialBlockTransactionFold.ir.json",
        "SequentialBlockTransactionFold.source-manifest.json",
        "SequentialBlockTransactionFold.lean"
    )) {
        $first = Join-Path $scratch "first/$name"
        $second = Join-Path $scratch "second/$name"
        $pinned = Join-Path $checkedIn $name
        foreach ($path in @($first, $second, $pinned)) {
            if (-not (Test-Path -LiteralPath $path)) { throw "Missing generated artifact $path." }
        }

        $firstHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $first).Hash
        $secondHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash
        $pinnedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pinned).Hash
        Write-Host "$name SHA256 $pinnedHash"
        if ($firstHash -ne $secondHash -or $pinnedHash -ne $secondHash) {
            throw "Sequential transaction-fold generated artifact drifted or is not deterministic: $name."
        }
    }

    Push-Location $package
    try {
        & $Lake --wfail build
        if ($LASTEXITCODE -ne 0) { throw "Sequential transaction-fold Lake build failed." }

        foreach ($module in @(
            "Generated/SequentialBlockTransactionFold.lean",
            "Specification/SequentialBlockTransactionFold.lean",
            "Refinement/SequentialBlockTransactionFold.lean",
            "Vectors/SequentialBlockTransactionFoldVectors.lean"
        )) {
            & $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $module
            if ($LASTEXITCODE -ne 0) { throw "Sequential transaction-fold Lean target failed: $module." }
        }
    }
    finally {
        Pop-Location
    }

    $generated = Get-Content -Raw -LiteralPath (Join-Path $checkedIn "SequentialBlockTransactionFold.lean")
    if ([regex]::IsMatch($generated, "\b(theorem|axiom|example|sorry|admit)\b")) {
        throw "Generated sequential transaction-fold Lean contains a proof declaration or placeholder."
    }

    & (Join-Path $package "Verify-MutationGates.ps1") -Lake $Lake
    & (Join-Path $package "Verify-Axioms.ps1") -Lake $Lake
    Write-Host "Verified conditional sequential transaction fold: 314 C# cases, 16 kernel-checked scenarios, 10 Lean semantic mutations, and 49 frozen theorem exports. Source composition remains unproved."
}
finally {
    $env:SOURCE_DATE_EPOCH = $previousSourceDateEpoch
    if (Test-Path -LiteralPath $scratch) {
        $resolved = [System.IO.Path]::GetFullPath($scratch)
        if (-not $resolved.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.Path]::GetFileName($resolved).StartsWith("sequential-block-fold-verify-",
                [StringComparison]::Ordinal)) {
            throw "Refusing cleanup outside the owned sequential transaction-fold verification directory."
        }

        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
