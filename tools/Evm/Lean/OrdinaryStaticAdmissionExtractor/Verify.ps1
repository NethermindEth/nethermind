# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$scratchBase = "D:\tmp\formal-verify"
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchBase ("ordinary-static-admission-" + [Guid]::NewGuid().ToString("N"))))
$scratchGenerated = Join-Path $scratch "Generated"
$project = Join-Path $package "OrdinaryStaticAdmissionExtractor.csproj"
$testProject = Join-Path $package "Test\OrdinaryStaticAdmissionExtractor.Test.csproj"

if (-not $scratch.StartsWith(([System.IO.Path]::GetFullPath($scratchBase) + [System.IO.Path]::DirectorySeparatorChar),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped D:\tmp\formal-verify."
}

try {
    New-Item -ItemType Directory -Path $scratchGenerated -Force | Out-Null

    dotnet build $project -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor build failed." }
    dotnet build $testProject -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor test build failed." }

    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --repo-root $repo `
        --output $scratchGenerated `
        --lean-output (Join-Path $scratchGenerated "OrdinaryStaticAdmissionKernel.lean")
    if ($LASTEXITCODE -ne 0) { throw "Fresh extractor generation failed." }

    $checkedIn = Join-Path $package "Generated"
    foreach ($name in @(
        "OrdinaryStaticAdmissionKernel.ir.json",
        "OrdinaryStaticAdmissionKernel.source-manifest.json",
        "OrdinaryStaticAdmissionKernel.lean"
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
        --check --repo-root $repo --output $checkedIn `
        --lean-output (Join-Path $checkedIn "OrdinaryStaticAdmissionKernel.lean")
    if ($LASTEXITCODE -ne 0) { throw "Checked-artifact validation failed." }

    dotnet test --project $testProject -c $Configuration -p:SaveDiskSpace=true -p:TreatWarningsAsErrors=true --no-build -- `
        --minimum-expected-tests 96 --no-ansi --progress off
    if ($LASTEXITCODE -ne 0) { throw "Extractor tests failed." }

    $env:ELAN_HOME = "D:\tmp\formal-lean-toolchain\home"
    $env:Path = "$env:ELAN_HOME\bin;$env:Path"
    Push-Location $package
    try {
        lake build
        if ($LASTEXITCODE -ne 0) { throw "Lean package build failed." }
        $leanJobs = @(
            @{ Name = "vectors"; File = "Reference\OrdinaryStaticAdmissionVectors.lean" },
            @{ Name = "generated"; File = "Generated\OrdinaryStaticAdmissionKernel.lean" },
            @{ Name = "reference"; File = "Reference\OrdinaryStaticAdmissionReference.lean" },
            @{ Name = "refinement"; File = "Refinement\OrdinaryStaticAdmission.lean" }
        )
        foreach ($job in $leanJobs) {
            lake env lean -DwarningAsError=true (Join-Path $package $job.File)
            if ($LASTEXITCODE -ne 0) { throw "Lean target failed: $($job.Name)." }
        }
    }
    finally {
        Pop-Location
    }

    $placeholders = Get-ChildItem -LiteralPath $package -Recurse -File -Filter *.lean |
        Select-String -Pattern '\b(sorry|admit|axiom)\b'
    if ($placeholders) { throw "Lean placeholder or axiom found: $($placeholders.Path):$($placeholders.LineNumber)." }

    Write-Host "Verified 96 C# tests, 4 explicit Lean jobs plus the default package build."
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
