# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$scratch = [IO.Path]::GetFullPath((Join-Path $package (".fold-mutations-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Fold mutation scratch escaped the package."
}
$kernel = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/SequentialBlockTransactionFold.lean")
$support = @("Specification/SequentialBlockTransactionFold.lean", "Refinement/SequentialBlockTransactionFold.lean",
    "Vectors/SequentialBlockTransactionFoldVectors.lean") | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $package $_) }
$imports = @(
    "import ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel",
    "import ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold",
    "import ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors"
) -join [Environment]::NewLine

function Combined([string]$Kernel, [bool]$Proofs) {
    $parts = @($Kernel)
    if ($Proofs) { $parts += $support }
    return $imports + [Environment]::NewLine +
        (($parts | ForEach-Object { [regex]::Replace($_, '(?m)^import [^\r\n]+\r?\n', '') }) -join [Environment]::NewLine)
}
$mutations = @(
    @{ Name = "no-end-index-increment"; Before = "currentIndex := state.currentIndex + 1"; After = "currentIndex := state.currentIndex" },
    @{ Name = "adopt-start-not-terminal"; Before = "let nextState := afterTxTrace transaction.observation.trace.state"; After = "let nextState := afterTxTrace transaction.startState" },
    @{ Name = "ignore-no-validation"; Before = "if input.shouldValidate && nextState.headerGasUsed > input.gasLimit then"; After = "if nextState.headerGasUsed > input.gasLimit then" },
    @{ Name = "reject-equal-gas-limit"; Before = "nextState.headerGasUsed > input.gasLimit"; After = "nextState.headerGasUsed >= input.gasLimit" },
    @{ Name = "omit-pre-commit"; Before = "foldEntries input initial [] [] [.preTransactionCommit] input.entries"; After = "foldEntries input initial [] [] [] input.entries" },
    @{ Name = "commit-before-signal"; Before = "[.transactionFoldCompleted, .transactionsExecuted, .postTransactionCommit]"; After = "[.transactionFoldCompleted, .postTransactionCommit, .transactionsExecuted]" },
    @{ Name = "normal-return-not-committed"; Before = "committed := true"; After = "committed := false" },
    @{ Name = "non-ok-admitted"; Before = "transaction.observation.result == .ok"; After = "transaction.observation.result != .ok" },
    @{ Name = "skip-terminal-admission"; Before = "else if !terminalObservationValid state transaction then"; After = "else if false then" },
    @{ Name = "invalid-prefix-committed"; Before = "outcome := .invalidPrefix"; After = "outcome := .completed" }
)
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $baseline = Join-Path $scratch "Baseline.lean"
        [IO.File]::WriteAllText($baseline, (Combined $kernel $true), [Text.UTF8Encoding]::new($false))
        & $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $baseline
        if ($LASTEXITCODE -ne 0) { throw "Fold proof mutation baseline failed; stale or broken artifacts cannot admit mutations." }
        foreach ($mutation in $mutations) {
            $count = ([regex]::Matches($kernel, [regex]::Escape($mutation.Before))).Count
            if ($count -ne 1) { throw "Mutation $($mutation.Name) needs one exact semantic anchor; found $count." }
            $changed = $kernel.Replace($mutation.Before, $mutation.After)
            $kernelPath = Join-Path $scratch ($mutation.Name + "-kernel.lean")
            [IO.File]::WriteAllText($kernelPath, (Combined $changed $false), [Text.UTF8Encoding]::new($false))
            $kernelOutput = (& $Lake env lean -DwarningAsError=true $kernelPath 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "Fold semantic mutation is not compile-valid: $($mutation.Name): $kernelOutput" }
            $proofPath = Join-Path $scratch ($mutation.Name + "-proof.lean")
            [IO.File]::WriteAllText($proofPath, (Combined $changed $true), [Text.UTF8Encoding]::new($false))
            $proofOutput = (& $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $proofPath 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -or $proofOutput -notmatch 'error:' -or
                $proofOutput -match 'unknown (identifier|constant|module)|unexpected token|failed to synthesize|maximum recursion depth|maxRecDepth|maximum heartbeats' -or
                $proofOutput -notmatch 'unsolved goals|[Tt]ype mismatch|[Tt]actic .*failed|[Tt]actic .*evaluated|application type mismatch') {
                throw "Fold mutation did not reach its semantic proof rejection: $($mutation.Name): $proofOutput"
            }
            Write-Host "Fold compile-valid semantic mutation rejected: $($mutation.Name)"
        }
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
