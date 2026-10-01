# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$previousMsbuildNodeReuse = [Environment]::GetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "Process")
$previousSourceDateEpoch = [Environment]::GetEnvironmentVariable("SOURCE_DATE_EPOCH", "Process")
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$verificationManifest = Get-Content -Raw -LiteralPath (Join-Path $repo "tools/Evm/Lean/verification-manifest.json") | ConvertFrom-Json
$sourceRevisionId = [string] $verificationManifest.pins.nethermindCommit
if ($verificationManifest.schemaVersion -ne 1 -or $sourceRevisionId -notmatch '^[0-9a-f]{40}$') { throw "Invalid verification manifest Nethermind pin." }
$pathMapRoot = $repo.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
if ($pathMapRoot.IndexOfAny([char[]] @(',', ';', '=')) -ge 0) { throw "Repository path cannot be represented by PathMap: $pathMapRoot" }
$deterministicBuildProperties = @(
    "-p:SourceRevisionId=$sourceRevisionId",
    "-p:SourceDateEpoch=1789035784",
    "-p:EnableSourceLink=false",
    "-p:EmbedUntrackedSources=false",
    "-p:ContinuousIntegrationBuild=false",
    "-p:BuildingInsideVisualStudio=false",
    "-p:Deterministic=true",
    "-p:DeterministicSourcePaths=true",
    "-p:PathMap=$pathMapRoot=/_/"
)
$scratchBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchBase ("evm-transaction-preparation-" + [Guid]::NewGuid().ToString("N"))))
$scratchGenerated = Join-Path $scratch "Generated"
$project = Join-Path $package "EvmTransactionPreparationExtractor.csproj"
$testProject = Join-Path $package "Test\EvmTransactionPreparationExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$checkedLean = Join-Path $checkedIn "EvmTransactionPreparation.lean"
$requiredArtifacts = @(
    (Join-Path $checkedIn "EvmTransactionPreparation.ir.json"),
    (Join-Path $checkedIn "EvmTransactionPreparation.source-manifest.json"),
    (Join-Path $checkedIn "EvmTransactionPreparation.lean")
)
$env:ELAN_HOME = "D:\tmp\formal-lean-toolchain\home"
$env:Path = "$env:ELAN_HOME\bin;$env:Path"

$scratchPrefix = $scratchBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $scratch.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the operating-system temporary directory."
}

try {
    $env:MSBUILDDISABLENODEREUSE = "1"
    $env:SOURCE_DATE_EPOCH = "1789035784"
    foreach ($artifact in $requiredArtifacts) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
            throw "Missing checked-in generated artifact $artifact. The serialized artifact lane must generate all three exact artifacts."
        }
    }
    New-Item -ItemType Directory -Path $scratchGenerated -Force | Out-Null
    foreach ($compilerProject in @(
        "src/Nethermind/Nethermind.Init/Nethermind.Init.csproj",
        "tools/Evm/Evm.csproj",
        "src/Nethermind/Nethermind.Network.Enr.Test/Nethermind.Network.Enr.Test.csproj"
    )) {
        dotnet build (Join-Path $repo $compilerProject) -c $Configuration -warnaserror -p:SaveDiskSpace=true -nr:false -m:1 @deterministicBuildProperties
        if ($LASTEXITCODE -ne 0) { throw "Compiler reference build failed: $compilerProject." }
    }
    dotnet build $project -c $Configuration -warnaserror -p:SaveDiskSpace=true -nr:false -m:1 @deterministicBuildProperties
    if ($LASTEXITCODE -ne 0) { throw "Extractor build failed." }
    dotnet build $testProject -c $Configuration -warnaserror -p:SaveDiskSpace=true -nr:false -m:1 @deterministicBuildProperties
    if ($LASTEXITCODE -ne 0) { throw "Extractor test build failed." }

    $freshLean = Join-Path $scratchGenerated "EvmTransactionPreparation.lean"
    dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
        --repo-root $repo --output $scratchGenerated --lean-output $freshLean
    if ($LASTEXITCODE -ne 0) { throw "Fresh extractor generation failed." }

    foreach ($name in @(
        "EvmTransactionPreparation.ir.json",
        "EvmTransactionPreparation.source-manifest.json",
        "EvmTransactionPreparation.lean"
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
        -p:TreatWarningsAsErrors=true --no-build -- --minimum-expected-tests 43 --no-ansi --progress off
    if ($LASTEXITCODE -ne 0) { throw "Extractor tests failed." }

    Push-Location $package
    try {
        lake --wfail build
        if ($LASTEXITCODE -ne 0) { throw "Lean package build failed." }
        foreach ($leanFile in @(
            "Generated\EvmTransactionPreparation.lean",
            "Reference\EvmTransactionPreparationReference.lean",
            "Refinement\Admission.lean",
            "Refinement\Maps.lean",
            "Refinement\EvmTransactionPreparation.lean"
        )) {
            lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 (Join-Path $package $leanFile)
            if ($LASTEXITCODE -ne 0) { throw "Lean target failed: $leanFile." }
        }
        $zeroGas = "{ value := 0, stateReservoir := 0, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }"
        foreach ($siblingImport in @(
            "Generated/OrdinaryPostNonceDispatch.lean",
            "Reference/OrdinaryPostNonceDispatchReference.lean"
        )) {
            $source = [System.IO.File]::ReadAllText((Join-Path $package "../OrdinaryPostNonceDispatchExtractor/$siblingImport"))
            if (($source.Split($zeroGas, [StringSplitOptions]::None)).Length -ne 2) {
                throw "The semantic-import mutation must change exactly one zeroGas definition: $siblingImport."
            }
            $mutated = Join-Path $scratch ([System.IO.Path]::GetFileName($siblingImport))
            [System.IO.File]::WriteAllText($mutated, $source.Replace($zeroGas, $zeroGas.Replace("value := 0", "value := 1")))
            lake env lean -DwarningAsError=true $mutated
            if ($LASTEXITCODE -ne 0) { throw "Semantic-import drift fixture did not compile: $siblingImport." }
            Write-Host "Compiled semantic-import drift fixture: $siblingImport"
        }
        & (Join-Path $package "Verify-Axioms.ps1")
    }
    finally {
        Pop-Location
    }

    foreach ($artifact in $requiredArtifacts) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { throw "Required artifact disappeared during verification: $artifact." }
    }
    $placeholders = Get-ChildItem -LiteralPath $package -Recurse -File -Filter *.lean |
        Select-String -Pattern '\b(sorry|admit|axiom)\b'
    if ($placeholders) { throw "Lean placeholder token found: $($placeholders.Path):$($placeholders.LineNumber)." }
    Write-Host "Verified the source closure, deterministic artifacts, mutation tests, and Lean targets."
}
finally {
    try {
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    }
    finally {
        [Environment]::SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", $previousMsbuildNodeReuse, "Process")
        [Environment]::SetEnvironmentVariable("SOURCE_DATE_EPOCH", $previousSourceDateEpoch, "Process")
    }
}
