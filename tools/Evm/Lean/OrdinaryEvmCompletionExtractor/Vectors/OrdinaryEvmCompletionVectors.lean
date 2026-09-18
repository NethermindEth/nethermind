-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion

namespace OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors

set_option maxRecDepth 10000
set_option maxHeartbeats 800000

namespace S
export OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion
  (Input TailInput VmTerminal HaltReason Boundary Representation CounterRanges ExternalPredicates
   ExternalAssumptions refundConstants prepared preparationGas refundInput refundResult consumedGas
   processorAfter fees receiptResult rollbackRequired evaluate NonnegativeGas PreparationGasRepresentable)
end S
namespace C
export OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion
  (Input mapInput InternalDomain Domain AcceptedStages accepted_stages_at_computed_inputs)
end C
namespace PG
export EvmTransactionPreparationExtractor.Generated
  (Input GasState Snapshot WorldFacts AccessObservation AccountFacts Environment EvmHandoff
   PreparationRelations gasToFixedWidth)
end PG
namespace PR
export EvmTransactionPreparationExtractor.Refinement (mapInput RepresentationObligation)
end PR
namespace P
export EvmTransactionPreparationExtractor.Reference (VmInput Result Gas run)
end P
namespace F
export OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
  (GasState evaluate FitsUInt256)
end F
namespace R
export ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold
  (Gas Receipt State Status TransactionResult AddressOracle EvmExceptionOracle effectiveBlockGas)
end R

local instance (value : F.GasState) : Decidable (S.NonnegativeGas value) := by
  unfold S.NonnegativeGas
  infer_instance

local instance (value : P.Gas) : Decidable (S.PreparationGasRepresentable value) := by
  unfold S.PreparationGasRepresentable
  infer_instance

local instance (value : Nat) : Decidable (F.FitsUInt256 value) := by
  unfold F.FitsUInt256
  infer_instance

local instance (i : S.Input) : Decidable (S.CounterRanges i) := by
  unfold S.CounterRanges
  infer_instance

inductive TerminalCase where
  | success | reverted | exceptional
  deriving DecidableEq, Repr

def gas (value : Nat) (reservoir used spill refunded : Int) : F.GasState :=
  { value, stateReservoir := reservoir, stateGasUsed := used,
    stateGasSpill := spill, stateGasSpillRefunded := refunded }

def preparationGas : PG.GasState :=
  { value := 900000, stateReservoir := 10000, stateGasUsed := 0,
    stateGasSpill := 0, stateGasSpillRefunded := 0 }

def intrinsicStandard : PG.GasState :=
  { value := 21000, stateReservoir := 10000, stateGasUsed := 0,
    stateGasSpill := 0, stateGasSpillRefunded := 0 }

def topSnapshot : PG.Snapshot :=
  { storage := { persistent := 11, transient := 12 }, state := 17, blockAccessList := 19 }

def emptySnapshot : PG.Snapshot :=
  { storage := { persistent := -1, transient := -1 }, state := -1, blockAccessList := -1 }

def world : PG.WorldFacts :=
  { physicalExists := true, logicalExists := true,
    originalPhysicalExists := true, currentPhysicalExists := true,
    originalLogicalExists := true, currentLogicalExists := true,
    originalNonce := 0, currentNonce := 0, originalCode := "", currentCode := "",
    originalDelegation := none, currentDelegation := none, originalBalance := 100,
    currentBalance := 100, recipientBalance := 0, delegationRefunds := 0,
    accountWarm := false, storageWarm := false, accountReads := [], accountWrites := [],
    storageReads := [], storageWrites := [], codeInsertRefunds := 0, traceAccess := [], authorityStates := [] }

def access : PG.AccessObservation :=
  { warmedAccounts := [], warmedStorage := [], reads := [], tracing := false }

def emptyAccount : PG.AccountFacts :=
  { physicalExists := false, logicalExists := false, nonce := 0, code := "",
    delegation := none, balance := 0, storageNonEmpty := false }

def environment : PG.Environment :=
  { code := { source := "recipient", target := none, bytes := "00", isEmpty := false, isNull := false }
    executingAccount := "recipient", caller := "sender", codeSource := some "recipient",
    callDepth := 0, value := 0, input := "" }

def relations : PG.PreparationRelations :=
  { delegatedTarget := { target := none, isPrecompile := false, account := emptyAccount, accessCharge := 0 }
    deadRecipient :=
      { sender := "sender", recipient := some "recipient", physicalExists := false,
        logicalExists := false, accountEmpty := true, stateCharge := 25000 }
    deployment :=
      { physicalExists := false, logicalExists := false, accountEmpty := true,
        storageNonEmpty := false, storageCleared := false, codeOrNonceCollision := false,
        collision := false, includeStorageCollision := true, stateCharge := 183600 }
    valueTransfer :=
      { sender := "sender", recipient := some "recipient", senderBalance := 100,
        recipientBalance := 0, amount := 0 } }

def handoff : PG.EvmHandoff :=
  { context := { sender := "sender", codeRepository := "repository", blobHashes := [], opcodeGasPrice := 2 }
    gas := preparationGas, access,
    tx :=
      { isCreate := false, isSetCode := false, sender := "sender", recipient := some "recipient",
        value := 0, input := "", accessListAccounts := [], accessListStorage := [], coinbase := none }
    spec :=
      { eip7702 := true, eip8037 := true, eip8038 := true, useHotAndColdStorage := true,
        useTxAccessLists := true, addCoinbaseToTxAccessList := true }
    options := { warmup := false, skipValidation := false }
    executionIntrinsicGasStandard := intrinsicStandard, environment, delegationRefunds := 0,
    exactBoundary := "VirtualMachine.ExecuteTransaction" }

def preparationInput : PG.Input :=
  { handoff, prePreparationGas := preparationGas, preExecutionIntrinsicGasStandard := intrinsicStandard,
    hasPreExecutionSnapshot := false, preExecutionSnapshot := emptySnapshot, topLevelSnapshot := topSnapshot,
    postIntrinsicStateReservoir := 10000, preExecutionWorld := world, world, access, authorizations := [],
    relations, environment, fixedWidth := PG.gasToFixedWidth preparationGas,
    preparationFixedWidth := PG.gasToFixedWidth preparationGas,
    baselineFixedWidth := PG.gasToFixedWidth intrinsicStandard,
    preBaselineFixedWidth := PG.gasToFixedWidth intrinsicStandard, delegationRefunds := 0 }

def prepared : P.Result := P.run (PR.mapInput preparationInput)

def frame : P.VmInput :=
  { gas := prepared.gas, executionType := "TRANSACTION", environment := prepared.environment,
    access := prepared.access, snapshot := prepared.topLevelSnapshot }

def priorReceipt : R.Receipt :=
  { logs := [], txType := 0, gasUsedTotal := 900, statusCode := 1,
    recipient := some { id := 2 }, blockHash := some { id := 7 }, blockNumber := 99,
    index := 0, gasUsed := 900, effectiveGasPrice := { id := 2 }, sender := some { id := 1 },
    contractAddress := none, txHash := some { id := 8 }, postTransactionState := none,
    blockGasUsed := 1000, executionGasUsed := 900, storageGasUsed := 500, error := none }

def receiptBefore : R.State :=
  { receipts := [priorReceipt], gasHistory := [{ executionGas := 1000, stateGas := 500 }],
    cumulativeReceiptGas := 900, headerGasUsed := 1000, parallel := false, currentIndex := 1 }

def address (value : String) : R.AddressOracle :=
  { id := if value == "sender" then 1 else if value == "recipient" then 2
      else if value == "beneficiary" then 3 else 4 }

def terminal : TerminalCase → S.VmTerminal
  | .success => .success { id := 40 } 1000 [{ id := 51 }, { id := 52 }]
  | .reverted => .reverted { id := 41 } 1000 { id := 42 }
  | .exceptional => .exceptional .outOfGas { id := 43 } (some { id := 44 })

def haltException : S.HaltReason → R.EvmExceptionOracle
  | .badInstruction => { id := 1 }
  | .stackOverflow => { id := 2 }
  | .stackUnderflow => { id := 3 }
  | .outOfGas => { id := 4 }
  | .invalidJumpDestination => { id := 5 }
  | .accessViolation => { id := 6 }
  | .staticCallViolation => { id := 7 }
  | .precompileFailure => { id := 8 }
  | .transactionCollision => { id := 9 }
  | .notEnoughBalance => { id := 10 }
  | .other => { id := 11 }
  | .invalidCode => { id := 13 }

def postGas : TerminalCase → F.GasState
  | .reverted => gas 700400 39600 0 500 500
  | _ => gas 700000 10000 30000 500 100

def tail (route : TerminalCase) : S.TailInput :=
  { vm :=
      { frameEntry := frame, postGas := postGas route, terminal := terminal route,
        destroyed := [], accessAfter := frame.access, shouldRestoreRipemdTouch := true, opCodeCount := 77 }
    intrinsicFloorGas := gas 21000 0 0 0 0
    spec :=
      { eip3529 := true, eip7778 := true, eip658 := true, eip1559 := true,
        eip4844FeeCollector := false, destroyRefund := 0, feeCollector := none }
    transaction :=
      { gasLimit := 1000000, maxFeePerGas := 7, maxPriorityFeePerGas := 1, isFree := false,
        supportsBlobs := false, txType := 2, hash := some { id := 9 } }
    block := { hash := some { id := 7 }, number := 99, baseFeePerGas := 1 }
    gasBeneficiary := "beneficiary", premiumPerGas := 1, blobBaseFee := 0, options := 1,
    processorParallel := false,
    processorBefore := { executionGas := 200, stateGas := 100, headerGasUsed := 200 }
    receiptBefore, address, diagnosticEffectivePrice := { id := 2 }, revertException := { id := 12 },
    haltException, recalculatedStateRoot := { id := 99 },
    tracingState := false, tracingFees := true, nestedReceiptTracer := true,
    currentTracerIsTracingReceipt := true }

def input (route : TerminalCase) : C.Input := { preparation := preparationInput, tail := tail route }
def specInput (route : TerminalCase) : S.Input := C.mapInput (input route)
def success : S.Input := specInput .success
def reverted : S.Input := specInput .reverted
def exceptional : S.Input := specInput .exceptional

theorem preparation_representation : PR.RepresentationObligation preparationInput := by
  constructor
  · decide
  · constructor <;> decide
  · decide

theorem all_terminal_boundaries (route : TerminalCase) : S.Boundary (specInput route) := by
  cases route <;> constructor <;> decide

theorem all_terminal_representations (route : TerminalCase) : S.Representation (specInput route) := by
  cases route <;> constructor <;> decide

theorem all_terminal_counter_ranges (route : TerminalCase) : S.CounterRanges (specInput route) := by
  cases route <;> decide

theorem internal_domain_witness (route : TerminalCase) : C.InternalDomain (input route) :=
  { preparation := preparation_representation, boundary := all_terminal_boundaries route,
    representation := all_terminal_representations route, counters := all_terminal_counter_ranges route }

theorem internal_domain_is_inhabited : ∃ i, C.InternalDomain i :=
  ⟨input .success, internal_domain_witness .success⟩

/-- Actual VM/source/hook evidence is still required, not replaced by an always-true test oracle. -/
theorem conditional_domain_witness (route : TerminalCase) (predicates : S.ExternalPredicates)
    (external : S.ExternalAssumptions predicates (specInput route)) : C.Domain predicates (input route) :=
  OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion.InternalDomain.attach_external
    (input route) (internal_domain_witness route) predicates external

theorem conditional_accepted_stages (route : TerminalCase) (predicates : S.ExternalPredicates)
    (external : S.ExternalAssumptions predicates (specInput route)) : C.AcceptedStages (input route) :=
  C.accepted_stages_at_computed_inputs predicates (input route)
    (conditional_domain_witness route predicates external)

def chargedPreparationInput : PG.Input :=
  let chargedEnvironment := { environment with value := 1 }
  { preparationInput with
    handoff :=
      { handoff with tx := { handoff.tx with value := 1 }, environment := chargedEnvironment }
    environment := chargedEnvironment
    relations := { relations with valueTransfer := { relations.valueTransfer with amount := 1 } } }

def chargedFrame : P.VmInput :=
  let result := P.run (PR.mapInput chargedPreparationInput)
  { gas := result.gas, executionType := "TRANSACTION", environment := result.environment,
    access := result.access, snapshot := result.topLevelSnapshot }

def chargedRevertInput : C.Input :=
  let currentTail := tail .reverted
  { preparation := chargedPreparationInput
    tail :=
      { currentTail with vm :=
          { currentTail.vm with
            frameEntry := chargedFrame
            accessAfter := chargedFrame.access
            postGas := gas 700000 10000 25000 20000 5000 } } }

theorem charged_preparation_representation : PR.RepresentationObligation chargedPreparationInput := by
  constructor
  · decide
  · constructor <;> decide
  · decide

theorem charged_revert_internal_domain : C.InternalDomain chargedRevertInput := by
  constructor
  · exact charged_preparation_representation
  · constructor <;> decide
  · constructor <;> decide
  · decide

theorem noncreate_false_charge_flag_does_not_mean_zero_state_gas :
    chargedFrame.gas.stateGasUsed = 25000 ∧ chargedFrame.gas.stateGasSpill = 15000 ∧
    (S.refundInput (C.mapInput chargedRevertInput)).topLevelCreateStateGasCharged = false ∧
    (S.refundResult (C.mapInput chargedRevertInput)).workingGas.stateGasUsed = 25000 := by decide

theorem negative_opcode_count_rejects_representation :
    ¬ S.Representation { success with tail := { success.tail with
      vm := { success.tail.vm with opCodeCount := -1 } } } := by
  intro representation
  have impossible : (0 : Int) ≤ -1 := representation.opCodeCount.1
  omega

theorem overflowing_opcode_count_rejects_representation :
    ¬ S.Representation { success with tail := { success.tail with
      vm := { success.tail.vm with opCodeCount := 2147483648 } } } := by
  intro representation
  have impossible : (2147483648 : Int) ≤ 2147483647 := representation.opCodeCount.2
  omega

def consumed (spent operation execution state maximum refund : Nat) : R.Gas :=
  { spentGas := spent, operationGas := operation, blockGas := execution,
    blockStateGas := state, maxUsedGas := maximum, gasRefund := refund }

theorem success_six_gas_fields :
    S.consumedGas success = consumed 289000 289000 260000 30000 290000 1000 := by decide

theorem revert_six_gas_fields_after_vm_refill :
    S.consumedGas reverted = consumed 260000 260000 260000 0 260000 0 := by decide

theorem halt_six_gas_fields :
    S.consumedGas exceptional = consumed 990000 990000 990000 0 990000 0 := by decide

theorem success_status_and_transaction_result :
    (S.evaluate success).status = .success ∧ (S.evaluate success).transactionExecuted = true ∧
    (S.receiptResult success).result = .ok := by decide

theorem revert_receipt_error_is_not_transaction_rejection :
    (S.evaluate reverted).status = .failure ∧ (S.evaluate reverted).transactionExecuted = true ∧
    (S.receiptResult reverted).trace.forwardedError = some { id := 42 } ∧
    (S.receiptResult reverted).result = .evmException { id := 12 } none := by decide

theorem halt_receipt_error_and_result_detail_are_distinct :
    (S.evaluate exceptional).transactionExecuted = true ∧
    (S.receiptResult exceptional).trace.forwardedError = some { id := 43 } ∧
    (S.receiptResult exceptional).result = .evmException { id := 4 } (some { id := 44 }) := by decide

theorem terminal_output_and_logs :
    (S.receiptResult success).trace.forwardedOutput = { id := 40 } ∧
    (S.receiptResult success).trace.forwardedLogs = [{ id := 51 }, { id := 52 }] ∧
    (S.receiptResult reverted).trace.forwardedOutput = { id := 41 } ∧
    (S.receiptResult reverted).trace.forwardedLogs = [] ∧
    (S.receiptResult exceptional).trace.forwardedOutput = { id := 0 } := by decide

theorem rollback_uses_exact_top_snapshot :
    (S.evaluate success).rollbackSnapshot = none ∧
    (S.evaluate reverted).rollbackSnapshot = some frame.snapshot ∧
    (S.evaluate exceptional).rollbackSnapshot = some frame.snapshot ∧ frame.snapshot.state = 17 := by decide

theorem ordinary_refund_gas_views :
    (S.evaluate success).gasCopies.outerCaller = gas 900000 10000 0 0 0 ∧
    (S.evaluate success).gasCopies.framePostVm = gas 700000 10000 30000 500 100 ∧
    (S.evaluate success).gasCopies.callLocal = gas 700000 10000 30000 500 100 ∧
    (S.evaluate success).gasCopies.refundWorking = gas 700000 10000 30000 500 100 := by decide

theorem halt_mutates_only_refund_working_copy :
    (S.evaluate exceptional).gasCopies.outerCaller = gas 900000 10000 0 0 0 ∧
    (S.evaluate exceptional).gasCopies.callLocal = gas 700000 10000 30000 500 100 ∧
    (S.evaluate exceptional).gasCopies.refundWorking = gas 0 10000 0 0 500 := by decide

theorem terminal_refunded_spill_survives_reset :
    (S.refundResult exceptional).workingGas.stateGasSpill = 0 ∧
    (S.refundResult exceptional).workingGas.stateGasSpillRefunded = 500 := by decide

theorem revert_refill_is_not_repeated_in_adapter :
    (S.refundResult reverted).workingGas = gas 700400 39600 0 500 500 ∧
    (S.refundResult reverted).callerGas = gas 700400 39600 0 500 500 := by decide

theorem processor_counter_store :
    S.processorAfter success = { executionGas := 260200, stateGas := 30100, headerGasUsed := 260200 } := by decide

theorem receipt_counter_store :
    ((S.receiptResult success).trace.state.gasHistory.getLast?).map
      (fun totals => (totals.executionGas, totals.stateGas)) = some (261000, 30500) ∧
    (S.receiptResult success).trace.state.cumulativeReceiptGas = 289900 ∧
    (S.receiptResult success).trace.state.headerGasUsed = 261000 := by decide

theorem header_writes_do_not_conflate_prefixes :
    (S.processorAfter success).headerGasUsed = 260200 ∧
    (S.receiptResult success).trace.state.headerGasUsed = 261000 := by decide

theorem receipt_index_and_paid_gas :
    ((S.receiptResult success).trace.state.receipts.getLast?).map
      (fun receipt => (receipt.index, receipt.gasUsed, receipt.gasUsedTotal)) =
      some (1, 289000, 289900) := by decide

theorem fees_and_sender_credit_are_computed :
    (S.fees success).beneficiaryAmount = 289000 ∧
    (S.fees success).baseFeeAmount = 289000 ∧
    (S.fees success).collectorCredit = none ∧
    (S.refundResult success).senderCredit = some 1422000 := by decide

theorem successful_effect_order :
    (S.evaluate success).events =
      [.vmReturned, .opCodeMetrics 77, .flushMetrics, .frameDisposed, .refundCall,
       .senderCredit { id := 1 } 1422000, .processorHeaderWrite 260200,
       .beneficiaryCredit { id := 3 } 289000, .reportFees 289000 289000,
       .transactionGasWrite 260000 289000, .commit false false,
       .receipt .gasMutation, .receipt .receiptAppend, .receipt .nestedTracerForward,
       .receipt .currentTracerForward, .environmentDisposed, .accessTrackerDisposed, .returned] := by decide

theorem failed_restore_precedes_frame_disposal :
    (S.evaluate reverted).events.take 7 =
      [.vmReturned, .opCodeMetrics 77, .flushMetrics, .restore frame.snapshot,
       .restoreRipemdTouch true, .frameDisposed, .refundCall] := by decide

def withFloor (value : Nat) : S.Input :=
  { success with tail := { success.tail with intrinsicFloorGas := gas value 0 0 0 0 } }

def withRefund (value : Int) : S.Input :=
  { success with tail := { success.tail with
      vm := { success.tail.vm with terminal := .success { id := 40 } value [{ id := 51 }, { id := 52 }] } } }

theorem calldata_floor_separates_paid_operation_and_block_gas :
    S.consumedGas (withFloor 350000) = consumed 350000 289000 350000 30000 350000 1000 := by decide

theorem calldata_floor_boundary :
    (S.consumedGas (withFloor 288999)).spentGas = 289000 ∧
    (S.consumedGas (withFloor 289000)).spentGas = 289000 ∧
    (S.consumedGas (withFloor 289001)).spentGas = 289001 := by decide

theorem refund_cap_boundary :
    (S.consumedGas (withRefund 57999)).gasRefund = 57999 ∧
    (S.consumedGas (withRefund 58000)).gasRefund = 58000 ∧
    (S.consumedGas (withRefund 58001)).gasRefund = 58000 ∧
    (S.consumedGas (withRefund 100000)).spentGas = 232000 := by decide

def free : S.Input :=
  { success with tail := { success.tail with transaction := { success.tail.transaction with isFree := true } } }

def zeroPrice : S.Input :=
  { success with preparation := { success.preparation with handoff := { success.preparation.handoff with
      context := { success.preparation.handoff.context with opcodeGasPrice := 0 } } } }

def collector : S.Input :=
  { success with tail := { success.tail with
      spec := { success.tail.spec with feeCollector := some "collector", eip4844FeeCollector := true }
      transaction := { success.tail.transaction with supportsBlobs := true }, blobBaseFee := 11 } }

theorem free_transaction_base_fee_branch :
    (S.fees free).baseFeeAmount = 0 ∧ (S.fees free).beneficiaryAmount = 289000 := by decide

theorem zero_price_suppresses_refund_payment :
    (S.refundResult zeroPrice).payRefundCalled = false ∧ (S.refundResult zeroPrice).senderCredit = none := by decide

theorem blob_collector_and_fee_report :
    (S.fees collector).collectorCredit = some ({ id := 4 }, 289011) ∧
    (S.fees collector).reportedBurntAmount = 289011 := by decide

/-- Raw fee-guard edge, not a claim that a validated transaction pays below its base fee. -/
theorem effective_price_caps_raw_base_fee :
    (S.fees { success with tail := { success.tail with
      block := { success.tail.block with baseFeePerGas := 7 } } }).baseFeeAmount = 578000 := by decide

theorem halt_sender_refund_is_derived_from_reservoir :
    (S.refundResult exceptional).senderCredit = some 20000 ∧
    (S.refundResult exceptional).settlement = none := by decide

def stateOnly : S.Input :=
  { success with tail :=
      { success.tail with
        intrinsicFloorGas := gas 0 0 0 0 0
        vm :=
          { success.tail.vm with
            postGas := gas 990000 0 10000 0 0
            terminal := .success { id := 40 } 0 [] } } }

theorem zero_execution_nonzero_state_does_not_fall_back :
    S.consumedGas stateOnly = consumed 10000 10000 0 10000 10000 0 ∧
    R.effectiveBlockGas (S.consumedGas stateOnly) = 0 ∧
    (S.processorAfter stateOnly).executionGas = 200 := by decide

/- Deliberately incorrect alternatives; none is an admitted adapter or a production claim. -/
namespace Mutant

def wrongStateGasCopy (i : S.Input) :=
  F.evaluate S.refundConstants { S.refundInput i with incomingGas := S.preparationGas (S.prepared i).gas }

def wrongFloor (i : S.Input) :=
  F.evaluate S.refundConstants { S.refundInput i with floorGas := S.preparationGas i.preparation.handoff.gas }

def wrongSnapshot (i : S.Input) :=
  if S.rollbackRequired i.tail.vm.terminal then some i.preparation.preExecutionSnapshot else none

def revertViaHalt (i : S.Input) :=
  F.evaluate S.refundConstants { S.refundInput i with isError := true }

def swapGasFields (i : S.Input) : R.Gas :=
  let gas := S.consumedGas i
  { gas with operationGas := gas.blockGas, blockGas := gas.operationGas }

def doubleCounters (i : S.Input) :=
  S.processorAfter { i with tail := { i.tail with processorBefore := S.processorAfter i } }

def suppliedResultShortcut (_i : S.Input)
    (supplied : OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion.Result) := supplied

def conflatedHeader (i : S.Input) := (S.processorAfter i).headerGasUsed

def paidAsEffective (i : S.Input) := (S.consumedGas i).spentGas

end Mutant

theorem detects_wrong_state_gas_copy :
    (Mutant.wrongStateGasCopy success).consumed.spentGas ≠ (S.consumedGas success).spentGas := by decide

theorem detects_wrong_intrinsic_floor :
    (Mutant.wrongFloor success).consumed.spentGas ≠ (S.consumedGas success).spentGas := by decide

theorem detects_wrong_rollback_snapshot :
    Mutant.wrongSnapshot reverted ≠ (S.evaluate reverted).rollbackSnapshot := by decide

theorem detects_revert_via_exceptional_halt :
    (Mutant.revertViaHalt reverted).consumed.spentGas ≠ (S.consumedGas reverted).spentGas := by decide

theorem detects_operation_block_swap : Mutant.swapGasFields success ≠ S.consumedGas success := by decide

theorem detects_double_counter_accumulation :
    (Mutant.doubleCounters success).headerGasUsed ≠ (S.processorAfter success).headerGasUsed := by decide

theorem detects_supplied_result_shortcut :
    (Mutant.suppliedResultShortcut success (S.evaluate exceptional)).status ≠ (S.evaluate success).status := by decide

theorem detects_prefix_conflation :
    Mutant.conflatedHeader success ≠ (S.receiptResult success).trace.state.headerGasUsed := by decide

theorem detects_paid_as_effective :
    Mutant.paidAsEffective stateOnly ≠ R.effectiveBlockGas (S.consumedGas stateOnly) := by decide

end OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors
