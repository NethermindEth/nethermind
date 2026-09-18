# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [ValidateSet("Release")][string]$Configuration = "Release",
    [string]$Dotnet = "dotnet",
    [string]$Lake = "lake"
)

$ErrorActionPreference = "Stop"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$project = Join-Path $package "ReceiptTerminalFoldExtractor.csproj"
$tests = Join-Path $package "Test/ReceiptTerminalFoldExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$elanHome = "D:\tmp\formal-lean-toolchain\home"
$previousMsbuildNodeReuse = [Environment]::GetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "Process")
$previousSourceDateEpoch = [Environment]::GetEnvironmentVariable("SOURCE_DATE_EPOCH", "Process")
$env:MSBUILDDISABLENODEREUSE = "1"
$env:SOURCE_DATE_EPOCH = "1789035784"
$scratchRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$scratch = Join-Path $scratchRoot ("receipt-terminal-fold-verify-" + [Guid]::NewGuid().ToString("N"))

if (-not $scratch.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the temporary root."
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null

    foreach ($compilerProject in @(
        "tools/Evm/Evm.csproj",
        "src/Nethermind/Nethermind.Network.Enr.Test/Nethermind.Network.Enr.Test.csproj"
    )) {
        & $Dotnet build (Join-Path $repo $compilerProject) -c $Configuration -p:SaveDiskSpace=true -warnaserror
        if ($LASTEXITCODE -ne 0) { throw "Compiler reference build failed: $compilerProject." }
    }

    & $Dotnet build $project -c $Configuration -p:SaveDiskSpace=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Receipt-terminal extractor build failed." }

    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Checked-in compiler-reference inventory/artifact check failed." }

    & $Dotnet build $tests -c $Configuration -p:SaveDiskSpace=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Receipt-terminal extractor test build failed." }

    foreach ($name in @("first", "second")) {
        $output = Join-Path $scratch $name
        New-Item -ItemType Directory -Path $output | Out-Null
        & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
            --repo-root $repo --output $output --lean-output (Join-Path $output "ReceiptTerminalFoldKernel.lean")
        if ($LASTEXITCODE -ne 0) { throw "Fresh $name receipt-terminal extraction failed." }
    }

    foreach ($name in @(
        "ReceiptTerminalFoldKernel.ir.json",
        "ReceiptTerminalFoldKernel.source-manifest.json",
        "ReceiptTerminalFoldKernel.lean"
    )) {
        $first = Join-Path $scratch "first/$name"
        $second = Join-Path $scratch "second/$name"
        $pinned = Join-Path $checkedIn $name
        foreach ($path in @($first, $second, $pinned)) {
            if (-not (Test-Path -LiteralPath $path)) { throw "Missing receipt-terminal artifact $path." }
        }

        $firstHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $first).Hash
        $secondHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash
        $pinnedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pinned).Hash
        Write-Host "$name SHA256 $pinnedHash"
        if ($firstHash -ne $secondHash -or $pinnedHash -ne $secondHash) {
            throw "Receipt-terminal generated artifact drift: $name."
        }
    }

    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Checked-in receipt-terminal artifact validation failed." }

    if (Test-Path -LiteralPath (Join-Path $elanHome "bin")) {
        $env:ELAN_HOME = $elanHome
        $env:Path = "$elanHome\bin;$env:Path"
    }

    Push-Location $package
    try {
        & $Lake --wfail build
        if ($LASTEXITCODE -ne 0) { throw "Receipt-terminal Lake build failed." }

        foreach ($module in @(
            "Generated/ReceiptTerminalFoldKernel.lean",
            "Specification/ReceiptTerminalFold.lean",
            "Refinement/ReceiptTerminalFold.lean",
            "Vectors/ReceiptTerminalFoldVectors.lean"
        )) {
            & $Lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 $module
            if ($LASTEXITCODE -ne 0) { throw "Receipt-terminal Lean target failed: $module." }
        }
        & (Join-Path $package "Verify-Axioms.ps1") -Lake $Lake
    }

    finally {
        Pop-Location
    }

    & $Dotnet test --project $tests -c $Configuration -p:SaveDiskSpace=true -p:TreatWarningsAsErrors=true --no-build -- `
        --minimum-expected-tests 180 --no-ansi --progress off
    if ($LASTEXITCODE -ne 0) { throw "Receipt-terminal mutation/determinism suite failed." }

    $generated = Get-Content -Raw -LiteralPath (Join-Path $checkedIn "ReceiptTerminalFoldKernel.lean")
    if ([regex]::IsMatch($generated, "\b(theorem|axiom|example|sorry|admit)\b")) {
        throw "Generated receipt-terminal Lean contains a proof declaration or placeholder."
    }

    & $Dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- --check --repo-root $repo
    if ($LASTEXITCODE -ne 0) { throw "Final receipt-terminal artifact check failed." }
    Write-Host "Verified receipt-terminal fold: focused C# tests, 4 direct Lean targets, 234 public theorem axiom audits (12 declared claims), and Lake build."
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolved = [System.IO.Path]::GetFullPath($scratch)
        if (-not $resolved.StartsWith($scratchRoot + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.Path]::GetFileName($resolved).StartsWith("receipt-terminal-fold-verify-",
                [StringComparison]::Ordinal)) {
            throw "Refusing cleanup outside the owned receipt-terminal verification directory."
        }

        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    if ($null -eq $previousMsbuildNodeReuse) {
        Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue
    }
    else {
        $env:MSBUILDDISABLENODEREUSE = $previousMsbuildNodeReuse
    }
    if ($null -eq $previousSourceDateEpoch) {
        Remove-Item Env:SOURCE_DATE_EPOCH -ErrorAction SilentlyContinue
    }
    else {
        $env:SOURCE_DATE_EPOCH = $previousSourceDateEpoch
    }
}
