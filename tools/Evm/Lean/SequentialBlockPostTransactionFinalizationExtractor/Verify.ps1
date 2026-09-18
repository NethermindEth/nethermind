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
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$env:SOURCE_DATE_EPOCH = "1789035784"
$env:MSBUILDDISABLENODEREUSE = "1"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$project = Join-Path $package "SequentialBlockPostTransactionFinalizationExtractor.csproj"
$tests = Join-Path $package "Test/SequentialBlockPostTransactionFinalizationExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$scratchRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$scratch = Join-Path $scratchRoot ("sequential-post-finalization-verify-" + [Guid]::NewGuid().ToString("N"))

if (-not $scratch.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the temporary root."
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null

    & $Dotnet build $project -c $Configuration -p:SaveDiskSpace=true -warnaserror -nr:false -m:1
    if ($LASTEXITCODE -ne 0) { throw "Post-transaction finalization extractor build failed." }

    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Checked-in finalization source/artifact validation failed." }

    & $Dotnet build $tests -c $Configuration -p:SaveDiskSpace=true -warnaserror -nr:false -m:1
    if ($LASTEXITCODE -ne 0) { throw "Post-transaction finalization test build failed." }

    & $Dotnet test --project $tests -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --minimum-expected-tests 604
    if ($LASTEXITCODE -ne 0) { throw "Finalization mutation suite failed." }

    foreach ($name in @("first", "second")) {
        $output = Join-Path $scratch $name
        New-Item -ItemType Directory -Path $output | Out-Null
        & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
            --repo-root $repo --output $output
        if ($LASTEXITCODE -ne 0) { throw "Fresh $name extraction failed." }
    }

    foreach ($name in @(
        "SequentialBlockPostTransactionFinalization.ir.json",
        "SequentialBlockPostTransactionFinalization.source-manifest.json",
        "SequentialBlockPostTransactionFinalization.lean"
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
            throw "Finalization generated artifact drifted or is not deterministic: $name."
        }
    }

    Push-Location $package
    try {
        & $Lake --wfail build
        if ($LASTEXITCODE -ne 0) { throw "Finalization Lake build failed." }

        foreach ($module in @(
            "Generated/SequentialBlockPostTransactionFinalization.lean",
            "Specification/SequentialBlockPostTransactionFinalization.lean",
            "Refinement/SequentialBlockPostTransactionFinalization.lean",
            "Vectors/SequentialBlockPostTransactionFinalizationVectors.lean"
        )) {
            & $Lake env lean -DwarningAsError=true $module
            if ($LASTEXITCODE -ne 0) { throw "Finalization Lean target failed: $module." }
        }
    }
    finally {
        Pop-Location
    }

    & (Join-Path $package "Verify-MutationGates.ps1") -Lake $Lake
    & (Join-Path $package "Verify-Axioms.ps1") -Lake $Lake
    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- --check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Final post-build finalization artifact check failed." }
    Write-Host "Verified conditional post-transaction finalization, semantic mutations and the complete descendant theorem axiom gate. ProcessOne publication has a separate Verify-Publication.ps1 gate."
}
finally {
    $env:SOURCE_DATE_EPOCH = $previousSourceDateEpoch
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    if (Test-Path -LiteralPath $scratch) {
        $resolved = [System.IO.Path]::GetFullPath($scratch)
        if (-not $resolved.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.Path]::GetFileName($resolved).StartsWith("sequential-post-finalization-verify-",
                [StringComparison]::Ordinal)) {
            throw "Refusing cleanup outside the owned finalization verification directory."
        }

        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
