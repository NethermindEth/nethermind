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
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$repo = [IO.Path]::GetFullPath($RepoRoot)
$project = Join-Path $package "SequentialBlockPostTransactionFinalizationExtractor.csproj"
$previousEpoch = $env:SOURCE_DATE_EPOCH
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$env:SOURCE_DATE_EPOCH = "1789035784"
$env:MSBUILDDISABLENODEREUSE = "1"
$scratchRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$scratch = Join-Path $scratchRoot ("process-one-publication-verify-" + [Guid]::NewGuid().ToString("N"))
try {
    & (Join-Path $package "Verify.ps1") -RepoRoot $repo -Configuration $Configuration -Dotnet $Dotnet -Lake $Lake
    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- --publication-check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Publication checked source/artifact validation failed." }
    New-Item -ItemType Directory -Path $scratch | Out-Null
    foreach ($name in @("first", "second")) {
        $output = Join-Path $scratch $name
        & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
            --publication-extract --repo-root $repo --output $output
        if ($LASTEXITCODE -ne 0) { throw "Fresh $name publication extraction failed." }
    }
    foreach ($name in @("ProcessOneValidatedPublication.ir.json", "ProcessOneValidatedPublication.lean",
            "ProcessOneValidatedPublication.source-manifest.json")) {
        $first = (Get-FileHash -LiteralPath (Join-Path $scratch "first/$name") -Algorithm SHA256).Hash
        $second = (Get-FileHash -LiteralPath (Join-Path $scratch "second/$name") -Algorithm SHA256).Hash
        $pinned = (Get-FileHash -LiteralPath (Join-Path $package "Generated/$name") -Algorithm SHA256).Hash
        if ($first -cne $second -or $first -cne $pinned) { throw "Publication artifact drifted or is nondeterministic: $name" }
        Write-Host "$name SHA256 $pinned"
    }
    Push-Location $package
    try {
        & $Lake --wfail build SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors
        if ($LASTEXITCODE -ne 0) { throw "Publication Lake target failed." }
        foreach ($module in @("Generated/ProcessOneValidatedPublication.lean", "Specification/ProcessOneValidatedPublication.lean",
                "Refinement/ProcessOneValidatedPublication.lean", "Vectors/ProcessOneValidatedPublicationVectors.lean")) {
            & $Lake env lean -DwarningAsError=true $module
            if ($LASTEXITCODE -ne 0) { throw "Publication direct Lean target failed: $module" }
        }
    }
    finally { Pop-Location }
    & (Join-Path $package "Verify-PublicationMutationGates.ps1") -Lake $Lake
    & (Join-Path $package "Verify-PublicationAxioms.ps1") -Lake $Lake
    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- --publication-check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Publication final source/artifact validation failed." }
    Write-Host "Verified conditional ProcessOne publication, deterministic artifacts, semantic mutations, and complete standard-only descendant lineage."
}
finally {
    $env:SOURCE_DATE_EPOCH = $previousEpoch
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    if (Test-Path -LiteralPath $scratch) {
        $resolved = [IO.Path]::GetFullPath($scratch)
        if (-not $resolved.StartsWith($scratchRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolved).StartsWith("process-one-publication-verify-", [StringComparison]::Ordinal)) {
            throw "Refusing publication cleanup outside the owned temporary directory."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
