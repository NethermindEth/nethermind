-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from the exact Stage D source/member admission profile.
-- This theorem-free kernel is a handwritten operational transcription of one admitted
-- ExecuteTransaction loop iteration; source-body semantics are not extracted here.
-- Opcode bodies, STATICCALL direct-inline details, full-precompile leaves, journals,
-- settlement arithmetic, tracing, and lifecycle effects remain named adapter boundaries.
-- Canonical IR SHA-256: 4839548ffa67c70acb685964a9805e915e47c55638a547e2f3fc390ce00f3f7a

import EvmFrameMachineExtractor.Generated.EvmFrameMachineKernel
import EvmFrameMachineExtractor.Refinement.StageARouting
import FrameJournalExtractor.Generated.FrameJournalKernel
import FrameJournalExtractor.Refinement.FrameJournal
import WorldJournalExtractor.Generated.WorldJournalKernel
import WorldJournalExtractor.Refinement.WorldJournal
import CallCreateOpcodeExtractor.Refinement.CallCreateOpcode
import PrecompileFrameExtractor.Refinement.PrecompileFrameStageB
import PrecompileFullFrameExtractor.Refinement.PrecompileFullFrame
import Eip803x.Generated.StateGasTransitionKernel
import Eip803x.Refinement.StateGasTransition
import Eip803x.Generated.PrecompileGasPricingKernel
import Eip803x.Refinement.PrecompileGasPricing


import EvmFrameControlSettlementExtractor.Specification.Types

#check @EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec
#check @EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec
#check @FrameJournalExtractor.Refinement.FrameJournal.transition_refines
#check @FrameJournalExtractor.Refinement.FrameJournal.finite_trace_refines
#check @FrameJournalExtractor.Refinement.FrameJournal.transition_refines
#check @FrameJournalExtractor.Refinement.FrameJournal.finite_trace_refines
#check @WorldJournalExtractor.Refinement.WorldJournal.transition_refines
#check @WorldJournalExtractor.Refinement.WorldJournal.finite_trace_refines
#check @WorldJournalExtractor.Refinement.WorldJournal.transition_refines
#check @WorldJournalExtractor.Refinement.WorldJournal.finite_trace_refines
#check @Eip803x.Generated.CallCreateOpcodeRefinement.closed_amsterdam_refines
#check @Eip803x.Generated.CallCreateOpcodeRefinement.generated_create_deposit_failure_gas_is_exception_merge_and_refill
#check @Eip803x.PrecompileFrame.StageB.Refinement.source_execute_precompile_refines_reference
#check @Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference
#check @Eip803x.PrecompileFullFrame.Refinement.generated_execute_is_raw_admitted
#check @Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec
#check @Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec
#check @Eip803x.Refinement.PrecompileGasPricing.tryConsume_refines_wrapper
#check @Eip803x.Refinement.PrecompileGasPricing.tryConsume_refines_wrapper


namespace EvmFrameControlSettlementExtractor.Generated

open Eip803x.Evm.FrameMachineState
open EvmFrameControlSettlementExtractor

def irSha256 : String := "4839548ffa67c70acb685964a9805e915e47c55638a547e2f3fc390ce00f3f7a"

def admittedBranchNames : List String := [
  "FreshFramePreparation",
  "ContinuationFramePreparation",
  "BytecodeFrameDispatch",
  "FullPrecompileFrameDispatch",
  "DirectInlineStaticPrecompileOpcodeOutcome",
  "ReturnedContinue",
  "ReturnedSuspend",
  "ReturnedRegularSuccessNested",
  "ReturnedTopLevelSuccess",
  "ReturnedCreateSuccessNested",
  "NestedCreateCodeDepositInvalid",
  "NestedCreateCodeDepositOutOfGas",
  "ReturnedRevertNested",
  "ReturnedRevertTop",
  "ThrownEvm",
  "ThrownOverflow",
  "EscapedInvocation",
  "CancelledBeforeFirstOpcode",
  "CancelledAfterCompletedBatch",
  "FullPrecompileOutOfGasNested",
  "FullPrecompileOutOfGasTop",
  "FullPrecompileReturnedFailureNested",
  "FullPrecompileReturnedFailureTop",
  "FullPrecompileManagedExceptionNested",
  "FullPrecompileManagedExceptionTop",
  "FrameCleanupScope",
]

def admittedBranchContracts : List String := [
  "FreshFramePreparation|fresh|clear-return-data-before-fresh-dispatch -> initialize-current-frame|preparation",
  "ContinuationFramePreparation|continuation|retain-return-data -> retain-child-copy-metadata|preparation",
  "BytecodeFrameDispatch|bytecodeFrame|trace-entry-when-fresh -> execute-call-or-invalid-code|bytecode-frame-result",
  "FullPrecompileFrameDispatch|fullPrecompileFrame|execute-current-precompile-frame -> complete-current-non-create-frame-or-named-failure|full-precompile-result",
  "DirectInlineStaticPrecompileOpcodeOutcome|bytecodeOpcode|STATICCALL-direct-leaf-before-child-rent -> remain-in-bytecode-frame|bytecode-frame-result",
  "ReturnedContinue|returned|retain-current-frame -> advance-loop|continued",
  "ReturnedSuspend|returned|prepare-child-frame -> retain-parent|suspend",
  "ReturnedRegularSuccessNested|returned|pop-parent -> merge-child -> repay-state-gas-spill|regularSuccessNested",
  "ReturnedTopLevelSuccess|returned|trace-action-end -> prepare-top-level-substate|topLevelSuccess",
  "ReturnedCreateSuccessNested|returned|refund-initcode-gas -> enter-nested-code-deposit-settlement|createSuccessNested",
  "NestedCreateCodeDepositInvalid|nestedCreateDeposit|apply-invalid-code-create-failure -> do-not-dispatch-as-opcode|codeDepositInvalidNested",
  "NestedCreateCodeDepositOutOfGas|nestedCreateDeposit|apply-code-deposit-out-of-gas -> do-not-dispatch-as-opcode|codeDepositOutOfGasNested",
  "ReturnedRevertNested|returned|restore-snapshot -> restore-child-state-gas -> prepare-revert-output|revertNested",
  "ReturnedRevertTop|returned|prepare-top-level-substate -> top-level-result|revertTop",
  "ThrownEvm|thrownEvm|route-to-failure-handler -> settle-top-or-nested|exceptionTop-or-exceptionNested",
  "ThrownOverflow|thrownOverflow|route-to-failure-handler -> settle-top-or-nested|exceptionTop-or-exceptionNested",
  "EscapedInvocation|escapedInvocation|abrupt-unwind -> do-not-synthesize-success|escaped",
  "CancelledBeforeFirstOpcode|cancelled|cancelable-bytecode-only -> abrupt-unwind|cancelled",
  "CancelledAfterCompletedBatch|cancelled|normal-1024-opcode-batch -> successor-required -> abrupt-unwind|cancelled",
  "FullPrecompileOutOfGasNested|fullPrecompileOutOfGas|price-before-leaf -> nested-managed-failure|fullPrecompileOutOfGasNested",
  "FullPrecompileOutOfGasTop|fullPrecompileOutOfGas|price-before-leaf -> top-managed-failure|fullPrecompileOutOfGasTop",
  "FullPrecompileReturnedFailureNested|fullPrecompileReturnedFailure|returned-false -> nested-managed-failure|fullPrecompileReturnedFailureNested",
  "FullPrecompileReturnedFailureTop|fullPrecompileReturnedFailure|returned-false -> top-managed-failure|fullPrecompileReturnedFailureTop",
  "FullPrecompileManagedExceptionNested|fullPrecompileManagedException|managed-exception -> nested-failure-handler|fullPrecompileManagedExceptionNested",
  "FullPrecompileManagedExceptionTop|fullPrecompileManagedException|managed-exception -> top-failure-handler|fullPrecompileManagedExceptionTop",
  "FrameCleanupScope|abrupt-unwind|dispose-active-child-frames -> drain-state-stack -> leave-normal-exit-to-settlement-and-caller -> leave-top-state-to-caller|cleanup",
]

def admittedDispatchOrder : List String := [
  "clearReturnDataOnFreshOnly",
  "freshOrContinuationPreparation",
  "selectCurrentFrameBytecodeOrFullPrecompile",
  "executeSelectedFrame",
  "bytecodeDispatchCancellationAtEntryOrCompletedBatchWithSuccessor",
  "recordBytecodeDirectInlineStaticPrecompileOutcome",
  "classifyReturnedThrownOrFullPrecompileOutcome",
  "settleNestedCreateDepositAfterInitcodeSuccess",
  "applyVmFrameExitSettlement",
  "cleanupFrameUnwindWhenIterationLeavesVm",
]

def prepare (adapters : CanonicalLeaves) (machine : Machine) : Machine :=
  match machine.current.phase with
  | .fresh => adapters.preparation.prepareFresh (adapters.preparation.clearReturnData machine)
  | .continuation => adapters.preparation.prepareContinuation machine
  | .running => machine

def dispatch (adapters : CanonicalLeaves) (machine : Machine) : DriverInvocation :=
  match subjectOf machine with
  | .bytecode =>
      let bytecode := adapters.dispatch.executeBytecodeFrame machine
      { invocation := bytecode.invocation
        directInlineStaticPrecompile := bytecode.directInlineStaticPrecompile }
  | .fullPrecompile =>
      { invocation := adapters.dispatch.executeFullPrecompileFrame machine
        directInlineStaticPrecompile := none }

def classifyHalt (machine : Machine) (result : FrameResult) : SettlementRoute :=
  match result.exit with
  | .success =>
      if topLevel machine then .topLevelSuccess
      else if machine.current.executionType.isCreate then .createSuccessNested
      else .regularSuccessNested
  | .revert => if topLevel machine then .revertTop else .revertNested
  | .exception _ => if topLevel machine then .exceptionTop else .exceptionNested

def classifyReturned (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ => .continued
  | .suspend _ _ => .suspend
  | .halt result => classifyHalt machine result

def classify (machine : Machine) : InvocationResult → SettlementRoute
  | .returned step => classifyReturned machine step
  | .fullPrecompileSuccess step => classifyReturned machine step
  | .fullPrecompileOutOfGas =>
      if topLevel machine then .fullPrecompileOutOfGasTop else .fullPrecompileOutOfGasNested
  | .fullPrecompileReturnedFailure _ =>
      if topLevel machine then .fullPrecompileReturnedFailureTop else .fullPrecompileReturnedFailureNested
  | .fullPrecompileManagedException _ =>
      if topLevel machine then .fullPrecompileManagedExceptionTop else .fullPrecompileManagedExceptionNested
  | .thrownEvm _ | .thrownOverflow =>
      if topLevel machine then .exceptionTop else .exceptionNested
  | .escapedInvocation _ => .escaped
  | .cancelled _ _ => .cancelled

def tagged (route : SettlementRoute) (outcome : ShellResult) : ShellResult :=
  { outcome with route := route }

def settleNestedCreate (adapters : CanonicalLeaves) (invocation : InvocationResult)
    (machine : Machine) : ShellResult :=
  match adapters.settlement.classifyNestedCreateCodeDeposit invocation machine with
  | .deposited => tagged .createSuccessNested
      (adapters.settlement.createSuccessNested invocation machine)
  | .invalidCode => tagged .codeDepositInvalidNested
      (adapters.settlement.codeDepositInvalidNested invocation machine)
  | .outOfGas => tagged .codeDepositOutOfGasNested
      (adapters.settlement.codeDepositOutOfGasNested invocation machine)

def settleRoute (adapters : CanonicalLeaves) (route : SettlementRoute)
    (invocation : InvocationResult) (machine : Machine) : ShellResult :=
  match route with
  | .continued => tagged .continued (adapters.settlement.continueStep invocation machine)
  | .suspend => tagged .suspend (adapters.settlement.suspendStep invocation machine)
  | .regularSuccessNested => tagged .regularSuccessNested
      (adapters.settlement.regularSuccessNested invocation machine)
  | .topLevelSuccess => tagged .topLevelSuccess
      (adapters.settlement.topLevelSuccess invocation machine)
  | .createSuccessNested => settleNestedCreate adapters invocation machine
  | .revertNested => tagged .revertNested (adapters.settlement.revertNested invocation machine)
  | .revertTop => tagged .revertTop (adapters.settlement.revertTop invocation machine)
  | .exceptionTop => tagged .exceptionTop (adapters.settlement.exceptionTop invocation machine)
  | .exceptionNested => tagged .exceptionNested (adapters.settlement.exceptionNested invocation machine)
  | .codeDepositInvalidNested => tagged .codeDepositInvalidNested
      (adapters.settlement.codeDepositInvalidNested invocation machine)
  | .codeDepositOutOfGasNested => tagged .codeDepositOutOfGasNested
      (adapters.settlement.codeDepositOutOfGasNested invocation machine)
  | .fullPrecompileOutOfGasNested => tagged .fullPrecompileOutOfGasNested
      (adapters.settlement.fullPrecompileOutOfGasNested invocation machine)
  | .fullPrecompileOutOfGasTop => tagged .fullPrecompileOutOfGasTop
      (adapters.settlement.fullPrecompileOutOfGasTop invocation machine)
  | .fullPrecompileReturnedFailureNested => tagged .fullPrecompileReturnedFailureNested
      (adapters.settlement.fullPrecompileReturnedFailureNested invocation machine)
  | .fullPrecompileReturnedFailureTop => tagged .fullPrecompileReturnedFailureTop
      (adapters.settlement.fullPrecompileReturnedFailureTop invocation machine)
  | .fullPrecompileManagedExceptionNested => tagged .fullPrecompileManagedExceptionNested
      (adapters.settlement.fullPrecompileManagedExceptionNested invocation machine)
  | .fullPrecompileManagedExceptionTop => tagged .fullPrecompileManagedExceptionTop
      (adapters.settlement.fullPrecompileManagedExceptionTop invocation machine)
  | .cancelled =>
      { termination := .cancelled
        route := .cancelled
        machine := machine
        result := none
        reason := some (cancellationReasonOf invocation)
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }
  | .escaped =>
      { termination := .escaped
        route := .escaped
        machine := machine
        result := none
        reason := some (escapedReasonOf invocation)
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }
  | .noSettlement =>
      { termination := .incomplete
        route := .noSettlement
        machine := machine
        result := none
        reason := some "unsettledRoute"
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }

def cleanupTerminal (adapters : CanonicalLeaves) (outcome : ShellResult) : ShellResult :=
  match outcome.termination with
  | .completed | .suspended => outcome
  | .cancelled | .escaped | .incomplete =>
      let cleanup := adapters.cleanup outcome
      { outcome with machine := cleanup.2, disposal := cleanup.1 }

def driveIteration (adapters : CanonicalLeaves) (machine : Machine) : ShellResult :=
  let prepared := prepare adapters machine
  let dispatched := dispatch adapters prepared
  let settled := settleRoute adapters (classify prepared dispatched.invocation)
    dispatched.invocation prepared
  let observed := { settled with directInlineStaticPrecompile := dispatched.directInlineStaticPrecompile }
  cleanupTerminal adapters observed

end EvmFrameControlSettlementExtractor.Generated