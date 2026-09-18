# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$env:MSBUILDDISABLENODEREUSE = "1"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$scratchBase = [System.IO.Path]::GetFullPath("D:\tmp")
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchBase ("ordinary-stateful-admission-" + [Guid]::NewGuid().ToString("N"))))
$scratchGenerated = Join-Path $scratch "Generated"
$project = Join-Path $package "OrdinaryStatefulAdmissionPrefixExtractor.csproj"
$testProject = Join-Path $package "Test\OrdinaryStatefulAdmissionPrefixExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$checkedLean = Join-Path $checkedIn "OrdinaryStatefulAdmissionPrefix.lean"

if (-not $scratch.StartsWith(([System.IO.Path]::GetFullPath($scratchBase) + [System.IO.Path]::DirectorySeparatorChar),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped D:\tmp."
}

try {
    New-Item -ItemType Directory -Path $scratchGenerated -Force | Out-Null

    dotnet build $project -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor build failed." }
    dotnet build $testProject -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor test build failed." }

    $freshLean = Join-Path $scratchGenerated "OrdinaryStatefulAdmissionPrefix.lean"
    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --repo-root $repo --output $scratchGenerated --lean-output $freshLean
    if ($LASTEXITCODE -ne 0) { throw "Fresh extractor generation failed." }

    foreach ($name in @(
        "OrdinaryStatefulAdmissionPrefix.ir.json",
        "OrdinaryStatefulAdmissionPrefix.source-manifest.json",
        "OrdinaryStatefulAdmissionPrefix.lean"
    )) {
        $checkedPath = Join-Path $checkedIn $name
        $freshPath = Join-Path $scratchGenerated $name
        if (-not (Test-Path -LiteralPath $checkedPath)) { throw "Missing checked-in artifact $checkedPath." }
        if (-not (Test-Path -LiteralPath $freshPath)) { throw "Missing fresh artifact $freshPath." }
        $checkedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $checkedPath).Hash
        $freshHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $freshPath).Hash
        Write-Host "$name SHA256 $checkedHash"
        if ($checkedHash -ne $freshHash) { throw "Generated artifact drift: $name." }
    }

    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --check --repo-root $repo --output $checkedIn --lean-output $checkedLean
    if ($LASTEXITCODE -ne 0) { throw "Checked-artifact validation failed." }

    dotnet test --project $testProject -c $Configuration -p:SaveDiskSpace=true `
        -p:TreatWarningsAsErrors=true --no-build -- `
        --minimum-expected-tests 39 --no-ansi --progress off
    if ($LASTEXITCODE -ne 0) { throw "Extractor tests failed." }

    $env:ELAN_HOME = "D:\tmp\formal-lean-toolchain\home"
    $env:Path = "$env:ELAN_HOME\bin;$env:Path"
    Push-Location $package
    try {
        lake --wfail build
        if ($LASTEXITCODE -ne 0) { throw "Lean package build failed." }
        $leanFiles = @(
            "Generated\OrdinaryStatefulAdmissionPrefix.lean",
            "Reference\OrdinaryStatefulAdmissionPrefixReference.lean",
            "Refinement\OrdinaryStatefulAdmissionPrefix.lean",
            "Reference\OrdinaryStatefulAdmissionPrefixVectors.lean"
        )
        foreach ($leanFile in $leanFiles) {
            lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 (Join-Path $package $leanFile)
            if ($LASTEXITCODE -ne 0) { throw "Lean target failed: $leanFile." }
        }
    }
    finally {
        Pop-Location
    }

    $placeholders = Get-ChildItem -LiteralPath $package -Recurse -File -Filter *.lean |
        Select-String -Pattern '\b(sorry|admit|axiom)\b'
    if ($placeholders) { throw "Lean placeholder token found: $($placeholders.Path):$($placeholders.LineNumber)." }

    Write-Host "Verified the extractor C# suite, 4 explicit Lean jobs, and the default package build."
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
