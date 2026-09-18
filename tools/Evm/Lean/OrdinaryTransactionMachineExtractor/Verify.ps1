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
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchBase ("ordinary-machine-" + [Guid]::NewGuid().ToString("N"))))
$scratchGenerated = Join-Path $scratch "Generated"
$scratchGeneratedSecond = Join-Path $scratch "Generated-second"
$project = Join-Path $package "OrdinaryTransactionMachineExtractor.csproj"
$testProject = Join-Path $package "Test\OrdinaryTransactionMachineExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$checkedLean = Join-Path $checkedIn "OrdinaryTransactionMachine.lean"

function Assert-ByteIdentical([string]$Left, [string]$Right, [string]$Message) {
    [byte[]]$leftBytes = [System.IO.File]::ReadAllBytes($Left)
    [byte[]]$rightBytes = [System.IO.File]::ReadAllBytes($Right)
    if ($leftBytes.Length -ne $rightBytes.Length) { throw $Message }
    for ($index = 0; $index -lt $leftBytes.Length; $index++) {
        if ($leftBytes[$index] -ne $rightBytes[$index]) { throw $Message }
    }
}

$scratchPrefix = $scratchBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $scratch.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the operating-system temporary directory."
}

try {
    New-Item -ItemType Directory -Path $scratchGenerated -Force | Out-Null
    New-Item -ItemType Directory -Path $scratchGeneratedSecond -Force | Out-Null
    dotnet build $project -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor build failed." }
    dotnet build $testProject -c $Configuration -warnaserror -p:SaveDiskSpace=true
    if ($LASTEXITCODE -ne 0) { throw "Extractor test build failed." }

    $freshLean = Join-Path $scratchGenerated "OrdinaryTransactionMachine.lean"
    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --extract --repo-root $repo --output $scratchGenerated --lean-output $freshLean
    if ($LASTEXITCODE -ne 0) { throw "Fresh ordinary-machine generation failed." }

    $freshLeanSecond = Join-Path $scratchGeneratedSecond "OrdinaryTransactionMachine.lean"
    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --extract --repo-root $repo --output $scratchGeneratedSecond --lean-output $freshLeanSecond
    if ($LASTEXITCODE -ne 0) { throw "Second ordinary-machine generation failed." }

    foreach ($name in @(
        "OrdinaryTransactionMachine.ir.json",
        "OrdinaryTransactionMachine.source-manifest.json",
        "OrdinaryTransactionMachine.lean"
    )) {
        $checkedPath = Join-Path $checkedIn $name
        $freshPath = Join-Path $scratchGenerated $name
        $freshSecondPath = Join-Path $scratchGeneratedSecond $name
        if (-not (Test-Path -LiteralPath $checkedPath)) { throw "Missing checked-in artifact $checkedPath." }
        if (-not (Test-Path -LiteralPath $freshPath)) { throw "Missing fresh artifact $freshPath." }
        if (-not (Test-Path -LiteralPath $freshSecondPath)) { throw "Missing second fresh artifact $freshSecondPath." }
        $checkedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $checkedPath).Hash
        $freshHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $freshPath).Hash
        $freshSecondHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $freshSecondPath).Hash
        Write-Host "$name SHA256 $checkedHash"
        Assert-ByteIdentical $freshPath $freshSecondPath "Two-run generated artifact mismatch: $name."
        Assert-ByteIdentical $checkedPath $freshPath "Generated artifact drift: $name."
        if ($freshHash -ne $freshSecondHash -or $checkedHash -ne $freshHash) {
            throw "Generated artifact digest mismatch after byte comparison: $name."
        }
    }

    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --check --repo-root $repo --output $checkedIn --lean-output $checkedLean
    if ($LASTEXITCODE -ne 0) { throw "Checked ordinary-machine artifacts failed validation." }

    dotnet test --project $testProject -c $Configuration -p:SaveDiskSpace=true `
        -p:TreatWarningsAsErrors=true --no-build -- --minimum-expected-tests 115 --no-ansi --progress off
    if ($LASTEXITCODE -ne 0) { throw "Ordinary-machine tests failed." }

    Push-Location $package
    try {
        lake --wfail build
        if ($LASTEXITCODE -ne 0) { throw "Lean package build failed." }
        foreach ($leanFile in @(
            "Generated\OrdinaryTransactionMachine.lean",
            "Specification\OrdinaryTransactionMachine.lean",
            "Specification\TransactionState.lean",
            "Specification\Economics.lean",
            "Specification\ExecutionBoundary.lean",
            "Refinement\OrdinaryTransactionMachine.lean",
            "Vectors\OrdinaryTransactionMachineVectors.lean"
        )) {
            lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 (Join-Path $package $leanFile)
            if ($LASTEXITCODE -ne 0) { throw "Lean target failed: $leanFile." }
        }
    }
    finally {
        Pop-Location
    }

    $generatedDeclarations = Select-String -LiteralPath $checkedLean `
        -Pattern '^\s*(theorem|lemma|example|axiom)\b|\b(sorry|admit)\b'
    if ($generatedDeclarations) {
        throw "Generated Lean contains a forbidden declaration or placeholder: $($generatedDeclarations.LineNumber)."
    }
    $placeholders = Get-ChildItem -LiteralPath $package -Recurse -File -Filter *.lean |
        Select-String -Pattern '\b(sorry|admit|axiom)\b'
    if ($placeholders) { throw "Lean placeholder token found: $($placeholders.Path):$($placeholders.LineNumber)." }
    Write-Host "Verified ordinary-machine source closure, deterministic artifacts, tests, and Lean targets."
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
