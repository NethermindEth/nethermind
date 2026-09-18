# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake", [ValidateSet("finite", "publication", "all")][string]$Slice = "all")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$scratch = [IO.Path]::GetFullPath((Join-Path $package (".outer-mutations-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Outer mutation scratch escaped the package."
}

$finite = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/NormalFiniteBranchCompletion.lean")
$publication = if ($Slice -eq "finite") { "" } else {
    Get-Content -Raw -LiteralPath (Join-Path $package "Generated/BlockchainPublication.lean")
}
$supportFiles = @("Specification/NormalFiniteBranchCompletion.lean", "Refinement/NormalFiniteBranchCompletion.lean",
    "Vectors/FiniteBranchVectors.lean")
if ($Slice -ne "finite") {
    $supportFiles += @("Specification/BlockchainPublication.lean", "Refinement/BlockchainPublication.lean", "Vectors/OuterBlockVectors.lean")
}
$support = $supportFiles | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $package $_) }
$imports = @(
    "import BlockProcessorExtractor.Generated.BranchAcceptedIteration",
    "import BlockProcessorExtractor.Specification.BranchAcceptedIteration",
    "import BlockProcessorExtractor.Refinement.BranchAcceptedIteration",
    "import BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors"
) -join "`n"

function Combined([string]$FiniteKernel, [string]$PublicationKernel, [bool]$Proofs) {
    $parts = @($FiniteKernel, $PublicationKernel)
    if ($Proofs) { $parts += $support }
    return $imports + "`n" + (($parts | ForEach-Object { [regex]::Replace($_, '(?m)^import [^\r\n]+\r?\n', '') }) -join "`n")
}

$mutations = @(
    @{ Name = "finite-no-increment"; Slice = "finite"; Before = "index + loopIncrement"; After = "index" },
    @{ Name = "finite-reset-slots"; Slice = "finite"; Before = "processedSlots := a.slots"; After = "processedSlots := (initial i).slots" },
    @{ Name = "finite-reset-scope"; Slice = "finite"; Before = "scope := a.scope, reopenedScope"; After = "scope := i.scope, reopenedScope" },
    @{ Name = "finite-wrong-base-observation"; Slice = "finite"; Before = "baseHeader := a.baseHeader,"; After = "baseHeader := if i.baseHeader.isSome then i.baseHeader else a.baseHeader," },
    @{ Name = "publication-wrong-input-resource"; Slice = "publication"; Before = "(i.finite.originalList == i.inputsResource)"; After = "true" },
    @{ Name = "publication-forced-preload-admitted"; Slice = "publication"; Before = "i.preparedBlocks.isEmpty && (i.blocksToProcess == [i.suggestedBlock])"; After = "true && (i.blocksToProcess == [i.suggestedBlock])" },
    @{ Name = "publication-forced-nonsingleton-admitted"; Slice = "publication"; Before = "(i.blocksToProcess == [i.suggestedBlock])"; After = "true" },
    @{ Name = "publication-ordinary-list-divergence"; Slice = "publication"; Before = "(i.preparedBlocks == i.blocksToProcess)"; After = "true" },
    @{ Name = "publication-ordinary-empty-admitted"; Slice = "publication"; Before = "!i.blocksToProcess.isEmpty)"; After = "true)" },
    @{ Name = "publication-wrong-difficulty-target"; Slice = "publication"; Before = "let target := i.finite.header last"; After = "let target := suggestedHeader i" },
    @{ Name = "publication-wrong-difficulty-source"; Slice = "publication"; Before = "let value := i.difficulty (suggestedHeader i)"; After = "let value := i.difficulty (i.finite.header last)" },
    @{ Name = "publication-wrong-head-payload"; Slice = "publication"; Before = ".headInvoked (suggestedHeader i) true false i.preparedBlocks"; After = ".headInvoked (suggestedHeader i) true false blocks" },
    @{ Name = "publication-forced-head"; Slice = "publication"; Before = ".headInvoked (suggestedHeader i) true false i.preparedBlocks"; After = ".headInvoked (suggestedHeader i) true true i.preparedBlocks" },
    @{ Name = "publication-mark-depends-on-head"; Slice = "publication"; Before = "| .mark => if !markProcessed i then r else"; After = "| .mark => if !markProcessed i || !i.headResult then r else" },
    @{ Name = "publication-wrong-mark-payload"; Slice = "publication"; Before = "[.markInvoked i.preparedBlocks, .markReturned]"; After = "[.markInvoked blocks, .markReturned]" },
    @{ Name = "publication-reversed-disposal"; Slice = "publication"; Before = ".prepareReturn, .disposeBlocks, .disposeInputs, .«return»"; After = ".prepareReturn, .disposeInputs, .disposeBlocks, .«return»" },
    @{ Name = "publication-invented-escape-state"; Slice = "publication"; Before = "outcome := .escaped site, state := none,"; After = "outcome := .escaped site, state := r.state," }
)
$mutations = @($mutations | Where-Object { $Slice -eq "all" -or $_.Slice -eq $Slice })
$expectedCount = if ($Slice -eq "finite") { 4 } elseif ($Slice -eq "publication") { 13 } else { 17 }
if ($mutations.Count -ne $expectedCount) { throw "Outer semantic mutation discovery changed." }

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $baseline = Join-Path $scratch "Baseline.lean"
        [IO.File]::WriteAllText($baseline, (Combined $finite $publication $true), [Text.UTF8Encoding]::new($false))
        & $Lake env lean -DwarningAsError=true $baseline
        if ($LASTEXITCODE -ne 0) { throw "Outer proof-executing mutation baseline failed; no mutation can pass a stale or broken baseline." }
        foreach ($mutation in $mutations) {
            $original = if ($mutation.Slice -eq "finite") { $finite } else { $publication }
            $count = ([regex]::Matches($original, [regex]::Escape($mutation.Before))).Count
            if ($count -ne 1) { throw "Mutation $($mutation.Name) must match one semantic anchor; got $count." }
            $changed = $original.Replace($mutation.Before, $mutation.After)
            $finiteCase = if ($mutation.Slice -eq "finite") { $changed } else { $finite }
            $publicationCase = if ($mutation.Slice -eq "publication") { $changed } else { $publication }
            $kernelPath = Join-Path $scratch ($mutation.Name + "-kernel.lean")
            [IO.File]::WriteAllText($kernelPath, (Combined $finiteCase $publicationCase $false), [Text.UTF8Encoding]::new($false))
            $kernelOutput = (& $Lake env lean -DwarningAsError=true $kernelPath 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "Semantic mutation $($mutation.Name) is not independently compile-valid: $kernelOutput" }

            $proofPath = Join-Path $scratch ($mutation.Name + "-proof.lean")
            [IO.File]::WriteAllText($proofPath, (Combined $finiteCase $publicationCase $true), [Text.UTF8Encoding]::new($false))
            $proofOutput = (& $Lake env lean -DwarningAsError=true $proofPath 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -or $proofOutput -notmatch 'error:') { throw "Semantic mutation survived: $($mutation.Name). $proofOutput" }
            if ($proofOutput -match 'unknown (identifier|constant|module)|unexpected token|failed to synthesize|maximum recursion depth|maxRecDepth|maximum heartbeats|maximum number of steps|deterministic timeout|declaration uses .sorry.' -or
                $proofOutput -notmatch 'unsolved goals|[Tt]ype mismatch|[Tt]actic .*failed|[Tt]actic .*evaluated|application type mismatch') {
                throw "Mutation $($mutation.Name) did not reach the intended semantic proof gate: $proofOutput"
            }
            Write-Host "Outer compile-valid semantic proof mutation rejected: $($mutation.Name)"
        }
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
