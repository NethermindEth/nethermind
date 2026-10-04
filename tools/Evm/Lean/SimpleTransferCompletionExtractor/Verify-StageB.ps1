# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
if ($Configuration -cne "Release") { throw "Stage-B control extraction and execution require Configuration=Release." }
$env:MSBUILDDISABLENODEREUSE = "1"
$env:ELAN_HOME = "D:\tmp\formal-lean-toolchain\home"
$env:Path = "$env:ELAN_HOME\bin;$env:Path"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$leanRoot = [System.IO.Path]::GetDirectoryName($package)
$project = Join-Path $package "SimpleTransferCompletionExtractor.csproj"
$testProject = Join-Path $package "Test\SimpleTransferCompletionExtractor.Test.csproj"
$generated = Join-Path $package "StageB\Generated"
$dispatchGenerated = Join-Path $package "StageB\Dispatch\Generated"
$dependencyGenerated = Join-Path $leanRoot "Extractor\Generated"
$refundVerification = Join-Path $leanRoot "OrdinaryTransactionRefundAdapterExtractor\Verify.ps1"
$axiomVerification = Join-Path $package "Verify-StageBAxioms.ps1"

function Get-Sha256([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Required Stage-B dependency artifact is missing: $Path"
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-DependencyManifest(
    [string]$ManifestName,
    [string]$ExpectedManifestSha256,
    [string]$ExpectedRoot,
    [string]$ExpectedSourcePath,
    [string]$ExpectedIrPath,
    [string]$ExpectedLeanPath)
{
    [string]$manifestPath = Join-Path $dependencyGenerated $ManifestName
    [string]$manifestSha256 = Get-Sha256 $manifestPath
    if ($manifestSha256 -cne $ExpectedManifestSha256)
    {
        throw "Stage-B dependency manifest drifted: $ManifestName (expected $ExpectedManifestSha256, got $manifestSha256)."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 3 -or
        $manifest.root -cne $ExpectedRoot -or
        $manifest.source.path -cne $ExpectedSourcePath -or
        $manifest.artifact.path -cne $ExpectedIrPath -or
        $manifest.leanArtifact.path -cne $ExpectedLeanPath)
    {
        throw "Stage-B dependency manifest shape drifted: $ManifestName"
    }

    [string]$sourcePath = Join-Path $repo $manifest.source.path
    [string]$irPath = Join-Path $dependencyGenerated $manifest.artifact.path
    [string]$leanPath = Join-Path $repo $manifest.leanArtifact.path
    foreach ($artifact in @(
        @{ Name = "source"; Path = $sourcePath; Expected = $manifest.source.sha256 },
        @{ Name = "IR"; Path = $irPath; Expected = $manifest.artifact.sha256 },
        @{ Name = "Lean"; Path = $leanPath; Expected = $manifest.leanArtifact.sha256 }
    ))
    {
        [string]$actual = Get-Sha256 $artifact.Path
        if ($actual -cne $artifact.Expected)
        {
            throw "Stage-B dependency $($artifact.Name) drifted for $ManifestName (expected $($artifact.Expected), got $actual)."
        }
    }
    if ($manifest.PSObject.Properties.Name -contains "supportingSources")
    {
        foreach ($source in $manifest.supportingSources)
        {
            if ((Get-Sha256 (Join-Path $repo $source.path)) -cne $source.sha256)
            {
                throw "Stage-B supporting source drifted for ${ManifestName}: $($source.path)"
            }
        }
    }
}

function Assert-OrdinaryRefundDependency([string]$RepoRoot)
{
    [string]$manifestRelativePath = "tools/Evm/Lean/OrdinaryTransactionRefundAdapterExtractor/Generated/OrdinaryTransactionRefund.source-manifest.json"
    [string]$manifestPath = Join-Path $RepoRoot $manifestRelativePath
    [string]$expectedManifestSha256 = "a0dd398a94994b65c3ea6bc59b6d62cd77958025208c423237e14a44d1ce30a1"
    [string]$manifestSha256 = Get-Sha256 $manifestPath
    if ($manifestSha256 -cne $expectedManifestSha256)
    {
        throw "Stage-B ordinary-refund manifest drifted (expected $expectedManifestSha256, got $manifestSha256)."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.extractorVersion -cne "1.0.0" -or
        $manifest.acceptanceState -cne "source-admitted" -or
        $manifest.sourceClosureSha256 -cne "8e753ccf7f150729d60d0fa31c7b3720865057cd556084d405c3dba1cb283734" -or
        $manifest.compilerInventorySha256 -cne "b36eeb0db516d68842159d34b9b9b730e83460e385de87dc35110db9a756b84c" -or
        $manifest.ir.path -cne "OrdinaryTransactionRefund.ir.json" -or
        $manifest.lean.path -cne "OrdinaryTransactionRefund.lean" -or
        @($manifest.sources).Count -ne 157 -or
        @($manifest.dependencies).Count -ne 52)
    {
        throw "Stage-B ordinary-refund manifest shape drifted: $manifestRelativePath"
    }

    [string]$refundGenerated = [System.IO.Path]::GetDirectoryName($manifestPath)
    foreach ($artifact in @($manifest.ir, $manifest.lean))
    {
        [string]$actual = Get-Sha256 (Join-Path $refundGenerated $artifact.path)
        if ($actual -cne $artifact.sha256)
        {
            throw "Stage-B ordinary-refund generated artifact drifted: $($artifact.path) (expected $($artifact.sha256), got $actual)."
        }
    }

    foreach ($kind in @("sources", "dependencies"))
    {
        foreach ($artifact in $manifest.$kind)
        {
            [string]$actual = Get-Sha256 (Join-Path $RepoRoot $artifact.path)
            if ($actual -cne $artifact.sha256)
            {
                throw "Stage-B ordinary-refund $kind artifact drifted: $($artifact.path) (expected $($artifact.sha256), got $actual)."
            }
        }
    }
}

Assert-DependencyManifest `
    "StateGasChargeKernel.source-manifest.json" `
    "bc62e03880343c8cd27dedd238fa386125a412e112548bec70d4a3bba4a0e575" `
    "Nethermind.Evm.GasPolicy.StateGasChargeKernel.TryCharge" `
    "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs" `
    "StateGasChargeKernel.ir.json" `
    "tools/Evm/Lean/Eip803x/Generated/StateGasChargeKernel.lean"

Assert-DependencyManifest `
    "TransactionGasInitializationKernel.source-manifest.json" `
    "c4081417da301771539d0509bf281a42f40a1a719cc4611c051e60fc8d6c7202" `
    "Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel" `
    "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs" `
    "TransactionGasInitializationKernel.ir.json" `
    "tools/Evm/Lean/Eip803x/Generated/TransactionGasInitializationKernel.lean"

Assert-DependencyManifest `
    "BlockReceiptGasAccountingKernel.source-manifest.json" `
    "1bc07122e230b88e69f5444f526d6c3654f4788c4479ea114185982471bdc2aa" `
    "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel" `
    "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs" `
    "BlockReceiptGasAccountingKernel.ir.json" `
    "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean"

$routingDirectory = Join-Path $leanRoot "SystemTransactionRoutingExtractor/Generated"
$routingManifest = Join-Path $routingDirectory "SystemTransactionRoutingKernel.source-manifest.json"
if ((Get-Sha256 $routingManifest) -cne "e321f7c52ccb5fbcf5b84f86027c092d56b75e36392eb47d0bc41de186ba46ee")
{
    throw "Stage-B system-routing dependency manifest drifted."
}
$routing = Get-Content -LiteralPath $routingManifest -Raw | ConvertFrom-Json
foreach ($source in $routing.sources)
{
    if ((Get-Sha256 (Join-Path $repo $source.path)) -cne $source.sha256)
    {
        throw "Stage-B system-routing source drifted: $($source.path)"
    }
}
if ((Get-Sha256 (Join-Path $routingDirectory $routing.ir.path)) -cne $routing.ir.sha256 -or
    (Get-Sha256 (Join-Path $repo $routing.lean.path)) -cne $routing.lean.sha256)
{
    throw "Stage-B system-routing generated artifacts drifted."
}

& $refundVerification -RepoRoot $repo -Configuration $Configuration -SkipLean
if ($LASTEXITCODE -ne 0) { throw "Stage-B ordinary-refund source-admission gate failed." }
Assert-OrdinaryRefundDependency $repo

dotnet build $project -c $Configuration -warnaserror -p:SaveDiskSpace=true
if ($LASTEXITCODE -ne 0) { throw "Stage-B extractor build failed." }
dotnet build $testProject -c $Configuration -warnaserror -p:SaveDiskSpace=true
if ($LASTEXITCODE -ne 0) { throw "Stage-B test build failed." }

dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
    --stage-b --check --repo-root $repo --output $generated
if ($LASTEXITCODE -ne 0) { throw "Stage-B artifact freshness check failed." }

dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
    --stage-b-control --check --repo-root $repo --output (Join-Path $package "StageB\Control\Generated")
if ($LASTEXITCODE -ne 0) { throw "Stage-B control source extraction/freshness check failed." }

dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
    --stage-b-refund-dispatch --check --repo-root $repo --output $dispatchGenerated
if ($LASTEXITCODE -ne 0) { throw "Stage-B standard-mainnet refund-dispatch extraction/freshness check failed." }

dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
    --stage-b-effective-block-gas --check --repo-root $repo --output (Join-Path $package "StageB\Leaf\Generated")
if ($LASTEXITCODE -ne 0) { throw "Stage-B EffectiveBlockGas extraction/freshness check failed." }

dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
    --stage-b-pay-fees --check --repo-root $repo --output (Join-Path $package "StageB\PayFees\Generated")
if ($LASTEXITCODE -ne 0) { throw "Stage-B PayFees source-admission/freshness check failed." }

dotnet run --project $project -c $Configuration -p:SaveDiskSpace=true --no-build -- `
    --stage-b-finalize-entry --check --repo-root $repo --output (Join-Path $package "StageB\Finalize\Generated")
if ($LASTEXITCODE -ne 0) { throw "Stage-B FinalizeTransaction entry extraction/freshness check failed." }

Push-Location $package
try {
    lake --wfail build `
        Eip803x.Refinement.StateGasCharge `
        Eip803x.Refinement.TransactionGasInitialization `
        SimpleTransferCompletionExtractor.StageB.Control.Refinement `
        SimpleTransferCompletionExtractor.StageB.Dispatch.Refinement `
        SimpleTransferCompletionExtractor.StageB.RefundBridge `
        SimpleTransferCompletionExtractor.StageB.FeeHelperVectors `
        SimpleTransferCompletionExtractor.StageB.PayFees.Vectors `
        SimpleTransferCompletionExtractor.StageB.Finalize.Vectors `
        SimpleTransferCompletionExtractor.StageB.Admission `
        stage-b-replay `
        SimpleTransferCompletionExtractor.StageB.Vectors
    if ($LASTEXITCODE -ne 0) { throw "Stage-B Lean build failed." }
}
finally {
    Pop-Location
}

dotnet test --project $testProject -c $Configuration -p:SaveDiskSpace=true `
    -p:TreatWarningsAsErrors=true --no-build -- --filter FullyQualifiedName~Stage_b `
    --minimum-expected-tests 584 --no-ansi --progress off
if ($LASTEXITCODE -ne 0) { throw "Stage-B tests failed." }

Push-Location $package
try {
    foreach ($leanFile in @(
        "StageB\RuntimeSyntax.lean",
        "StageB\Dispatch\Specification.lean",
        "StageB\Dispatch\Refinement.lean",
        "StageB\RefundBridge.lean",
        "StageB\Semantics.lean",
        "StageB\Leaf\Generated\EffectiveBlockGas.lean",
        "StageB\FeeHelperSource.lean",
        "StageB\FeeHelper.lean",
        "StageB\FeeHelperVectors.lean",
        "StageB\PayFees\Generated\PayFees.lean",
        "StageB\PayFees\Residual.lean",
        "StageB\PayFees\Return.lean",
        "StageB\PayFees\Vectors.lean",
        "StageB\Finalize\Generated\FinalizeEntry.lean",
        "StageB\Finalize\Binding.lean",
        "StageB\Finalize\Entry.lean",
        "StageB\Finalize\Vectors.lean",
        "StageB\Admission.lean",
        "StageB\Replay.lean",
        "StageB\Vectors.lean",
        "StageB\Control\Refinement.lean"
    )) {
        lake env lean -DwarningAsError=true (Join-Path $package $leanFile)
        if ($LASTEXITCODE -ne 0) { throw "Stage-B Lean target failed: $leanFile." }
    }
}
finally {
    Pop-Location
}

& $axiomVerification

$placeholders = Get-ChildItem -LiteralPath (Join-Path $package "StageB") -Recurse -File -Filter *.lean |
    Select-String -Pattern '\b(sorry|admit|axiom)\b'
if ($placeholders) { throw "Stage-B Lean placeholder token found: $($placeholders.Path):$($placeholders.LineNumber)." }

Write-Host "Verified fresh Stage-B artifacts, focused C# tests, and executable Lean vectors."
