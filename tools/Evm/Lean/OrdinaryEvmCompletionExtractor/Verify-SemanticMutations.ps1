# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$generated = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Generated/OrdinaryEvmCompletion.lean')
$imports = "import OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors`n"
$aliases = @'
namespace OrdinaryEvmCompletionExtractor.MutationWitness
namespace G
export OrdinaryEvmCompletionExtractor.Generated.OrdinaryEvmCompletion
  (evaluate refundResult consumedGas processorAfter fees receiptResult)
end G
namespace V
export OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors
  (success reverted exceptional frame)
end V
'@
$mutations = @(
    @{ Name = 'rollback-and'; Before = '(shouldRevert terminal) || (isError terminal)'; After = '(shouldRevert terminal) && (isError terminal)';
       Witness = '(G.evaluate V.reverted).rollbackSnapshot = some V.frame.snapshot' },
    @{ Name = 'prepared-gas-instead-of-frame'; Before = 'incomingGas := i.tail.vm.postGas'; After = 'incomingGas := preparationGas (prepared i).gas';
       Witness = '(G.refundResult V.success).consumed.spentGas = 289000' },
    @{ Name = 'available-gas-instead-of-floor'; Before = 'floorGas := i.tail.intrinsicFloorGas'; After = 'floorGas := preparationGas i.preparation.handoff.gas';
       Witness = '(G.refundResult V.success).consumed.spentGas = 289000' },
    @{ Name = 'revert-enters-halt'; Before = 'isError := isError i.tail.vm.terminal'; After = 'isError := rollbackRequired i.tail.vm.terminal';
       Witness = '(G.refundResult V.reverted).consumed.spentGas = 260000' },
    @{ Name = 'operation-block-field-swap'; Before = 'operationGas := gas.operationGas, blockGas := gas.blockGas'; After = 'operationGas := gas.blockGas, blockGas := gas.operationGas';
       Witness = '(G.consumedGas V.success).operationGas = 289000' },
    @{ Name = 'double-processor-prefix'; Before = 'before.executionGas + R.effectiveBlockGas gas'; After = 'before.executionGas + before.executionGas + R.effectiveBlockGas gas';
       Witness = '(G.processorAfter V.success).headerGasUsed = 260200' },
    @{ Name = 'maximum-base-fee'; Before = 'min (i.tail.block.baseFeePerGas) (i.preparation.handoff.context.opcodeGasPrice)'; After = 'max (i.tail.block.baseFeePerGas) (i.preparation.handoff.context.opcodeGasPrice)';
       Witness = '(G.fees V.success).baseFeeAmount = 289000' },
    @{ Name = 'premium-uses-effective-gas'; Before = '((i.tail.premiumPerGas) * (spent))'; After = '((i.tail.premiumPerGas) * (R.effectiveBlockGas (consumedGas i)))';
       Witness = '(G.fees V.success).beneficiaryAmount = 289000' },
    @{ Name = 'refund-working-copy-leaks'; Before = 'callLocal := i.tail.vm.postGas'; After = 'callLocal := (refundResult i).workingGas';
       Witness = '(G.evaluate V.exceptional).gasCopies.callLocal.value = 700000' },
    @{ Name = 'receipt-prefix-conflation'; Before = '{ i.tail.receiptBefore with headerGasUsed := (processorAfter i).headerGasUsed }'; After = '{ i.tail.receiptBefore with cumulativeReceiptGas := (processorAfter i).headerGasUsed, headerGasUsed := (processorAfter i).headerGasUsed }';
       Witness = '(G.receiptResult V.success).trace.state.cumulativeReceiptGas = 289900' },
    @{ Name = 'revert-output-erased'; Before = 'status := .failure, shouldRevert := true, output, substateError'; After = 'status := .failure, shouldRevert := true, output := (fun _ => R.emptyBytes) output, substateError';
       Witness = '(G.receiptResult V.reverted).trace.forwardedOutput.id = 41' },
    @{ Name = 'cleanup-order-swapped'; Before = '[.environmentDisposed, .accessTrackerDisposed, .returned]'; After = '[.accessTrackerDisposed, .environmentDisposed, .returned]';
       Witness = '(G.evaluate V.success).events.reverse.take 3 = [.returned, .accessTrackerDisposed, .environmentDisposed]' },
    @{ Name = 'max-used-field-lost'; Before = 'maxUsedGas := gas.maxUsedGas'; After = 'maxUsedGas := gas.operationGas';
       Witness = '(G.consumedGas V.success).maxUsedGas = 290000' }
)
function Kernel([string]$Text) {
    return $imports + [regex]::Replace($Text, '(?m)^import [^\r\n]+\r?\n', '')
}
function Witness([string]$Text, [hashtable]$Mutation) {
    return (Kernel $Text) + "`n" + $aliases + "`ntheorem witness : $($Mutation.Witness) := by decide`nend OrdinaryEvmCompletionExtractor.MutationWitness`n"
}
function Invoke-Lean([string]$Source) {
    $output = @($Source | & lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 --stdin 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output -join "`n" }
}
Push-Location $PSScriptRoot
try {
    foreach ($mutation in $mutations) {
        if ([regex]::Matches($generated, [regex]::Escape($mutation.Before)).Count -ne 1) {
            throw "Semantic anchor must occur exactly once: $($mutation.Name)"
        }
        $baseline = Invoke-Lean (Witness $generated $mutation)
        if ($baseline.Exit -ne 0) { throw "Semantic witness baseline failed: $($mutation.Name)`n$($baseline.Output)" }
        $changed = $generated.Replace($mutation.Before, $mutation.After)
        $compile = Invoke-Lean (Kernel $changed)
        if ($compile.Exit -ne 0) { throw "Semantic mutant must compile before proof rejection: $($mutation.Name)`n$($compile.Output)" }
        $rejected = Invoke-Lean (Witness $changed $mutation)
        if ($rejected.Exit -eq 0 -or
            $rejected.Output -notmatch 'Tactic `decide` proved that the proposition[\s\S]*is false' -or
            $rejected.Output -match 'unknown (identifier|constant|module)|unexpected token|failed to synthesize|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout') {
            throw "Semantic mutant was not rejected by its concrete equality: $($mutation.Name)`n$($rejected.Output)"
        }
        Write-Output "Compile-valid generated completion semantic mutation rejected: $($mutation.Name)"
    }
    Write-Output "All $($mutations.Count) generated completion semantic mutations rejected by concrete witnesses."
} finally { Pop-Location }
