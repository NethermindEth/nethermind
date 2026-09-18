# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake", [switch]$IncludePublication)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$scratch = [IO.Path]::GetFullPath((Join-Path $package (".finalization-mutations-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Finalization mutation scratch escaped the package."
}
$tail = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/SequentialBlockPostTransactionFinalization.lean")
$supportPaths = @(
    "Specification/SequentialBlockPostTransactionFinalization.lean",
    "Refinement/SequentialBlockPostTransactionFinalization.lean",
    "Vectors/SequentialBlockPostTransactionFinalizationVectors.lean"
)
if ($IncludePublication) { $supportPaths += @(
    "Specification/ProcessOneValidatedPublication.lean",
    "Refinement/ProcessOneValidatedPublication.lean",
    "Vectors/ProcessOneValidatedPublicationVectors.lean"
) }
$support = $supportPaths | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $package $_) }
$imports = "import SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold"

function Combined([string]$Kernel, [bool]$Proofs) {
    $parts = @($Kernel)
    if ($Proofs) { $parts += $support }
    return $imports + [Environment]::NewLine +
        (($parts | ForEach-Object { [regex]::Replace($_, '(?m)^import [^\r\n]+\r?\n', '') }) -join [Environment]::NewLine)
}
$kernel = $tail
if ($IncludePublication) {
    $kernel += [Environment]::NewLine + (Get-Content -Raw -LiteralPath (Join-Path $package "Generated/ProcessOneValidatedPublication.lean"))
}
$mutations = @(
    @{ Name = "tail-omit-post-commit"; Before = "[.commitNoRoots 0] ++"; After = "[] ++" },
    @{ Name = "tail-omit-finalization-commit"; Before = ".rewardsApplied, .withdrawalsApplied, .commitNoRoots 1,"; After = ".rewardsApplied, .withdrawalsApplied," },
    @{ Name = "tail-end-trace-false"; Before = ".executionRequestsProcessed, .endBlockTrace true, .commitRoots"; After = ".executionRequestsProcessed, .endBlockTrace false, .commitRoots" },
    @{ Name = "tail-wrong-hash"; Before = "hash := some input.hooks.headerHash"; After = "hash := some input.hooks.stateRoot" },
    @{ Name = "tail-erase-receipts"; Before = "receipts := input.receipts"; After = "receipts := []" },
    @{ Name = "tail-ignore-callback-failure"; Before = "else if input.transactionsExecutedNormalReturn = false then"; After = "else if false then" },
    @{ Name = "tail-ignore-post-commit-failure"; Before = "else if input.postTransactionCommitNormalReturn = false then"; After = "else if false then" }
)
if ($IncludePublication) { $mutations += @(
    @{ Name = "publication-return-suggested"; Before = "returnedTuple := some (input.processBlock.processedBlock, input.processBlock.receipts)"; After = "returnedTuple := some (input.suggestedBlock, input.processBlock.receipts)" },
    @{ Name = "publication-omit-storage-event"; Before = "(if input.storeReceipts then [.insertDeferred] else [])"; After = "[]" },
    @{ Name = "publication-rejection-skips-disposal"; Before = "accountChangesDisposed := true"; After = "accountChangesDisposed := false" },
    @{ Name = "publication-rejection-erases-bal"; Before = "generatedBlockAccessList := input.validator.proposedGeneratedBlockAccessListAfter"; After = "generatedBlockAccessList := none" },
    @{ Name = "publication-erase-encoded-fallback"; Before = "(fun _ => input.suggestedArtifacts.encodedBlockAccessList)"; After = "(fun _ => none)" },
    @{ Name = "publication-copy-wrong-requests"; Before = "executionRequests := input.processedArtifacts.executionRequests"; After = "executionRequests := input.suggestedArtifacts.executionRequests" },
    @{ Name = "publication-ignore-no-validation"; Before = "if !input.noValidation && !input.validator.accepted then"; After = "if !input.validator.accepted then" },
    @{ Name = "publication-ignore-post-return"; Before = "else if !input.postValidationNormalReturn then escape .postValidation validated"; After = "else if false then escape .postValidation validated" }
) }
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $baseline = Join-Path $scratch "Baseline.lean"
        [IO.File]::WriteAllText($baseline, (Combined $kernel $true), [Text.UTF8Encoding]::new($false))
        & $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $baseline
        if ($LASTEXITCODE -ne 0) { throw "Finalization proof mutation baseline failed; stale or broken artifacts cannot admit mutations." }
        foreach ($mutation in $mutations) {
            $count = ([regex]::Matches($kernel, [regex]::Escape($mutation.Before))).Count
            if ($count -ne 1) { throw "Mutation $($mutation.Name) needs one exact semantic anchor; found $count." }
            $changed = $kernel.Replace($mutation.Before, $mutation.After)
            $kernelPath = Join-Path $scratch ($mutation.Name + "-kernel.lean")
            [IO.File]::WriteAllText($kernelPath, (Combined $changed $false), [Text.UTF8Encoding]::new($false))
            $kernelOutput = (& $Lake env lean -DwarningAsError=true $kernelPath 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "Finalization semantic mutation is not compile-valid: $($mutation.Name): $kernelOutput" }
            $proofPath = Join-Path $scratch ($mutation.Name + "-proof.lean")
            [IO.File]::WriteAllText($proofPath, (Combined $changed $true), [Text.UTF8Encoding]::new($false))
            $proofOutput = (& $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $proofPath 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -or $proofOutput -notmatch 'error:' -or
                $proofOutput -match 'unknown (identifier|constant|module)|unexpected token|failed to synthesize|maximum recursion depth|maxRecDepth|maximum heartbeats' -or
                $proofOutput -notmatch 'unsolved goals|[Tt]ype mismatch|[Tt]actic .*failed|[Tt]actic .*evaluated|[Tt]actic .*proved that the[\s\S]*is false|application type mismatch') {
                throw "Finalization mutation did not reach its semantic proof rejection: $($mutation.Name): $proofOutput"
            }
            Write-Host "Finalization compile-valid semantic mutation rejected: $($mutation.Name)"
        }
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
