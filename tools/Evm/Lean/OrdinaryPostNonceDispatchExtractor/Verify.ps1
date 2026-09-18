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
$scratchBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchBase ("ordinary-post-nonce-dispatch-" + [Guid]::NewGuid().ToString("N"))))
$scratchGenerated = Join-Path $scratch "Generated"
$project = Join-Path $package "OrdinaryPostNonceDispatchExtractor.csproj"
$testProject = Join-Path $package "Test\OrdinaryPostNonceDispatchExtractor.Test.csproj"
$compilerClosureProjects = @(
    @{ Path = Join-Path $repo "src\Nethermind\Nethermind.Init\Nethermind.Init.csproj"; Label = "production Nethermind.Init dependency" },
    @{ Path = Join-Path $repo "tools\Evm\Evm.csproj"; Label = "Evm compiler dependency bundle" },
    @{ Path = Join-Path $repo "src\Nethermind\Nethermind.Network.Enr.Test\Nethermind.Network.Enr.Test.csproj"; Label = "ENR compiler dependency bundle" }
)
$checkedIn = Join-Path $package "Generated"
$checkedLean = Join-Path $checkedIn "OrdinaryPostNonceDispatch.lean"
$env:ELAN_HOME = "D:\tmp\formal-lean-toolchain\home"
$env:Path = "$env:ELAN_HOME\bin;$env:Path"

$scratchPrefix = $scratchBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $scratch.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the operating-system temporary directory."
}

try {
    New-Item -ItemType Directory -Path $scratchGenerated -Force | Out-Null
    foreach ($dependencyProject in $compilerClosureProjects) {
        if (-not (Test-Path -LiteralPath $dependencyProject.Path)) {
            throw "Missing $($dependencyProject.Label) project: $($dependencyProject.Path)."
        }

        dotnet build $dependencyProject.Path -c $Configuration -t:Rebuild -warnaserror -p:SaveDiskSpace=true
        if ($LASTEXITCODE -ne 0) { throw "$($dependencyProject.Label) build failed." }
    }

    dotnet build $project -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor build failed." }
    dotnet build $testProject -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor test build failed." }

    $freshLean = Join-Path $scratchGenerated "OrdinaryPostNonceDispatch.lean"
    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --repo-root $repo --output $scratchGenerated --lean-output $freshLean
    if ($LASTEXITCODE -ne 0) { throw "Fresh extractor generation failed." }

    foreach ($name in @(
        "OrdinaryPostNonceDispatch.ir.json",
        "OrdinaryPostNonceDispatch.source-manifest.json",
        "OrdinaryPostNonceDispatch.lean"
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
        -p:TreatWarningsAsErrors=true --no-build -- --minimum-expected-tests 14 --no-ansi --progress off
    if ($LASTEXITCODE -ne 0) { throw "Extractor tests failed." }

    Push-Location $package
    try {
        lake --wfail build
        if ($LASTEXITCODE -ne 0) { throw "Lean package build failed." }
        foreach ($leanFile in @(
            "Generated\OrdinaryPostNonceDispatch.lean",
            "Reference\OrdinaryPostNonceDispatchReference.lean",
            "Refinement\OrdinaryPostNonceDispatch.lean"
        )) {
            lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 (Join-Path $package $leanFile)
            if ($LASTEXITCODE -ne 0) { throw "Lean target failed: $leanFile." }
        }
    }
    finally {
        Pop-Location
    }

    $placeholders = Get-ChildItem -LiteralPath $package -Recurse -File -Filter *.lean | Select-String -Pattern '\b(sorry|admit|axiom)\b'
    if ($placeholders) { throw "Lean placeholder token found: $($placeholders.Path):$($placeholders.LineNumber)." }
    Write-Host "Verified the C# suite, deterministic artifacts, and Lean targets."
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
