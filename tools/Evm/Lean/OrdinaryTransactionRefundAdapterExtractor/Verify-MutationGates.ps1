# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$scratch = [IO.Path]::GetFullPath((Join-Path $package (".refund-mutations-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refund mutation scratch escaped the package."
}

$generated = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/OrdinaryTransactionRefund.lean")
$support = @("Specification/OrdinaryTransactionRefund.lean", "Refinement/OrdinaryTransactionRefund.lean",
    "Vectors/OrdinaryTransactionRefundVectors.lean") | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $package $_) }
$imports = @(
    "import Lean",
    "import Eip803x.Generated.TransactionSettlementKernel",
    "import Eip803x.Generated.StateGasTransitionKernel",
    "import Eip803x.Refinement.TransactionSettlement",
    "import Eip803x.Refinement.StateGasTransitionAdapterKernel",
    "import Eip803x.TransactionSettlement"
) -join "`n"

function Combined([string]$Kernel, [bool]$Proofs = $true) {
    $parts = @($Kernel)
    if ($Proofs) { $parts += $support }
    return $imports + "`n" + (($parts | ForEach-Object { [regex]::Replace($_, '(?m)^import [^\r\n]+\r?\n', '') }) -join "`n")
}

$firstTheorem = $support[1].IndexOf('private theorem state_normalize_of_bounded', [StringComparison]::Ordinal)
$boundaryStart = $support[1].IndexOf('def boundaryInput ', [StringComparison]::Ordinal)
$boundaryEnd = $support[1].IndexOf('/-- Every raw tagged boundary', [StringComparison]::Ordinal)
if ($firstTheorem -le 0 -or $boundaryStart -le $firstTheorem -or $boundaryEnd -le $boundaryStart) {
    throw 'Refund concrete refinement mapping or raw boundary fixture changed.'
}
$mapping = $support[1].Substring(0, $firstTheorem) + $support[1].Substring($boundaryStart, $boundaryEnd - $boundaryStart) +
    "`nend OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund`n"
$witnesses = @'
namespace OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.MutationGate

def ordinary : G.Input := boundaryInput .ordinaryRefund false false
def vmError : G.Input := { ordinary with isError := true }
def reverted : G.Input := { ordinary with shouldRevert := true }
def createReverted : G.Input :=
  { reverted with
    isContractCreation := true, topLevelCreateStateGasCharged := true,
    incomingGas := {
      value := 500000, stateReservoir := 20000, stateGasUsed := 193600,
      stateGasSpill := 500, stateGasSpillRefunded := 100 },
    postIntrinsicStateReservoir := 20000 }
def failedDeposit : G.Input := { ordinary with entry := .failedDeposit }
def paymentWrap : G.Input := { ordinary with gasPrice := 2 ^ 255 }
def legacyCollision : G.Input := { ordinary with entry := .contractCollision, isEip8037Enabled := false }

abbrev agrees (input : G.Input) : Prop :=
  mapResult (G.evaluate input) = S.evaluate (mapConstants G.constants) (mapInput input)

theorem raw_witness_inputs_are_machine_valid :
    (mapInput ordinary).Valid ∧ (mapInput vmError).Valid ∧ (mapInput reverted).Valid ∧
    (mapInput createReverted).Valid ∧ (mapInput failedDeposit).Valid ∧
    (mapInput paymentWrap).Valid ∧ (mapInput legacyCollision).Valid := by decide

theorem halt_restores_reservoir_and_burns_execution : agrees vmError := by decide
theorem create_refill_stays_untracked : agrees createReverted := by decide
theorem ordinary_halt_keeps_local_copy :
    mapGas (G.evaluate vmError).callerGas = (S.evaluate (mapConstants G.constants) (mapInput vmError)).callerGas := by decide
theorem failed_deposit_updates_ref_gas : agrees failedDeposit := by decide
theorem payment_uses_uint256_modulo : agrees paymentWrap := by decide
theorem sender_credit_uses_nonzero_amount : agrees ordinary := by decide
theorem legacy_collision_charges_full_operation_gas : agrees legacyCollision := by decide
theorem settlement_keeps_error_and_revert_roles : agrees reverted := by decide

end OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.MutationGate
'@
function ConcreteRefinement([string]$Kernel) {
    return $imports + "`n" + ((@($Kernel, $support[0], $mapping, $witnesses) | ForEach-Object {
        [regex]::Replace($_, '(?m)^import [^\r\n]+\r?\n', '')
    }) -join "`n")
}

$mutations = @(
    @{ Name = "halt-restores-stale-reservoir"; Before = "clearExecutionGas restored"; After = "clearExecutionGas { restored with stateReservoir := refunded.stateReservoir }" },
    @{ Name = "create-refill-marks-spill"; Before = "input.intrinsicStandard.stateReservoir false"; After = "input.intrinsicStandard.stateReservoir true" },
    @{ Name = "caller-copy-becomes-ref"; Before = "if modifiesCaller input then gas else input.incomingGas"; After = "gas" },
    @{ Name = "failed-deposit-does-not-mutate-ref"; Before = "(input.entry == .failedDeposit && input.isEip8037Enabled)"; After = "false" },
    @{ Name = "payment-skips-modulo"; Before = "(T.subUInt64 input.transactionGasLimit consumed.spentGas * input.gasPrice) % uint256Modulus"; After = "(T.subUInt64 input.transactionGasLimit consumed.spentGas * input.gasPrice)" },
    @{ Name = "sender-credit-only-zero"; Before = "if payRefundCalled && amount != 0 then some amount else none"; After = "if payRefundCalled && amount == 0 then some amount else none" },
    @{ Name = "full-gas-operation-zero"; Before = "spentGas := limit, operationGas := limit"; After = "spentGas := limit, operationGas := 0" },
    @{ Name = "settlement-error-revert-role-swap"; Before = "input.refundQuotient input.isError input.shouldRevert input.isEip8037Enabled input.isEip7778Enabled"; After = "input.refundQuotient input.shouldRevert input.isError input.isEip8037Enabled input.isEip7778Enabled" }
)
$witnessByMutation = @{
    'halt-restores-stale-reservoir' = 'agrees vmError'
    'create-refill-marks-spill' = 'agrees createReverted'
    'caller-copy-becomes-ref' = 'agrees vmError'
    'failed-deposit-does-not-mutate-ref' = 'agrees failedDeposit'
    'payment-skips-modulo' = 'agrees paymentWrap'
    'sender-credit-only-zero' = 'agrees ordinary'
    'full-gas-operation-zero' = 'agrees legacyCollision'
    'settlement-error-revert-role-swap' = 'agrees reverted'
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $baseline = Join-Path $scratch "Baseline.lean"
        [IO.File]::WriteAllText($baseline, (Combined $generated), [Text.UTF8Encoding]::new($false))
        & $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $baseline
        if ($LASTEXITCODE -ne 0) { throw "Refund proof-executing mutation baseline failed." }
        $concreteBaseline = Join-Path $scratch 'ConcreteBaseline.lean'
        [IO.File]::WriteAllText($concreteBaseline, (ConcreteRefinement $generated), [Text.UTF8Encoding]::new($false))
        & $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $concreteBaseline
        if ($LASTEXITCODE -ne 0) { throw 'Refund concrete generated/specification refinement baseline failed.' }
        foreach ($mutation in $mutations) {
            $count = ([regex]::Matches($generated, [regex]::Escape($mutation.Before))).Count
            if ($count -ne 1) { throw "Mutation $($mutation.Name) must match one generated semantic anchor; got $count." }
            $changed = $generated.Replace($mutation.Before, $mutation.After)
            $kernelPath = Join-Path $scratch ($mutation.Name + "-kernel.lean")
            [IO.File]::WriteAllText($kernelPath, (Combined $changed $false), [Text.UTF8Encoding]::new($false))
            $kernelOutput = (& $Lake env lean -DwarningAsError=true $kernelPath 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "Refund semantic mutation is not compile-valid: $($mutation.Name): $kernelOutput" }
            $path = Join-Path $scratch ($mutation.Name + ".lean")
            [IO.File]::WriteAllText($path, (ConcreteRefinement $changed), [Text.UTF8Encoding]::new($false))
            $output = (& $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $path 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -or $output -notmatch 'error:') { throw "Semantic mutation survived: $($mutation.Name). $output" }
            if ($output -match 'unknown (identifier|constant|module)|unexpected token|failed to synthesize|maximum recursion depth|maxRecDepth|maximum heartbeats|maximum number of steps|deterministic timeout' -or
                $output -notmatch 'Tactic `decide` proved that the proposition[\s\S]*is false' -or
                -not $output.Contains($witnessByMutation[$mutation.Name], [StringComparison]::Ordinal)) {
                throw "Mutation $($mutation.Name) failed outside the intended proof gate: $output"
            }
            Write-Host "Refund concrete refinement proof mutation rejected: $($mutation.Name)"
        }
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
