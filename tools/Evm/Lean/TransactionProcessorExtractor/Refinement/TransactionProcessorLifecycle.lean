-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import TransactionProcessorExtractor.Generated.TransactionProcessorLifecycle
import Eip803x.TransactionReference

namespace Eip803x.Refinement.TransactionProcessorLifecycle

namespace Generated

abbrev Stage := Eip803x.Generated.TransactionProcessorLifecycle.Stage
abbrev RejectStage := Eip803x.Generated.TransactionProcessorLifecycle.RejectStage
abbrev Route := Eip803x.Generated.TransactionProcessorLifecycle.Route
abbrev Options := Eip803x.Generated.TransactionProcessorLifecycle.Options
abbrev PrefixInput := Eip803x.Generated.TransactionProcessorLifecycle.PrefixInput
abbrev AdmissionHooks := Eip803x.Generated.TransactionProcessorLifecycle.AdmissionHooks
abbrev PrefixOutcome := Eip803x.Generated.TransactionProcessorLifecycle.PrefixOutcome
abbrev PrefixResult := Eip803x.Generated.TransactionProcessorLifecycle.PrefixResult
abbrev Status := Eip803x.Generated.TransactionProcessorLifecycle.Status
abbrev StateAction := Eip803x.Generated.TransactionProcessorLifecycle.StateAction
abbrev ErrorSource := Eip803x.Generated.TransactionProcessorLifecycle.ErrorSource
abbrev ResultProjection := Eip803x.Generated.TransactionProcessorLifecycle.ResultProjection
abbrev SpecFlags := Eip803x.Generated.TransactionProcessorLifecycle.SpecFlags
abbrev SubstateProjection := Eip803x.Generated.TransactionProcessorLifecycle.SubstateProjection
abbrev FinalizeInput := Eip803x.Generated.TransactionProcessorLifecycle.FinalizeInput
abbrev ReceiptProjection := Eip803x.Generated.TransactionProcessorLifecycle.ReceiptProjection
abbrev FinalizeResult := Eip803x.Generated.TransactionProcessorLifecycle.FinalizeResult
abbrev runPrefix := Eip803x.Generated.TransactionProcessorLifecycle.runPrefix
abbrev finalize := Eip803x.Generated.TransactionProcessorLifecycle.finalize

end Generated

namespace Spec

structure Options where
  commit : Bool
  restore : Bool
  skipValidation : Bool
  warmup : Bool
  buildUp : Bool
  deriving DecidableEq, Repr

structure PrefixInput where
  options : Options
  eip658 : Bool
  simpleTransfer : Bool
  tracingState : Bool
  deriving DecidableEq, Repr

structure AdmissionHooks where
  validateStatic : Bool
  validateSender : Bool
  buyGas : Bool
  incrementNonce : Bool
  calculateAvailableGas : Bool
  deriving DecidableEq, Repr

inductive RejectStage where
  | validateStatic | validateSender | buyGas | incrementNonce | calculateAvailableGas
  deriving DecidableEq, Repr

inductive Route where | simpleTransfer | evm deriving DecidableEq, Repr

inductive PrefixOutcome where
  | rejected (stage : RejectStage) | routed (route : Route)
  deriving DecidableEq, Repr

structure PrefixObservation where
  outcome : PrefixOutcome
  restoreOnReject : Bool
  committedBeforeExecution : Bool
  trace : List TransactionReference.Event
  deriving DecidableEq, Repr

def effectiveCommit (input : PrefixInput) : Bool :=
  input.options.commit || (!input.options.skipValidation && !input.eip658)

def shouldCommitBeforeExecution (input : PrefixInput) : Bool :=
  effectiveCommit input && (!input.simpleTransfer || input.options.restore || input.tracingState)

def reject (input : PrefixInput) (stage : RejectStage)
    (restoreEligible : Bool) (trace : List TransactionReference.Event) : PrefixObservation :=
  { outcome := .rejected stage
    restoreOnReject := restoreEligible && input.options.restore
    committedBeforeExecution := false
    trace }

def runPrefix (input : PrefixInput) (hooks : AdmissionHooks) : PrefixObservation :=
  let staticTrace := [.recoverSenderBeforeIntrinsicGas, .calculateIntrinsicGas, .validateStatic]
  if !hooks.validateStatic then reject input .validateStatic false staticTrace else
  let senderTrace := staticTrace ++
    [.calculateEffectiveGasPrice, .recoverSenderIfNeeded, .validateSender]
  if !hooks.validateSender then reject input .validateSender true senderTrace else
  let boughtTrace := senderTrace ++ [.buyGas]
  if !hooks.buyGas then reject input .buyGas true boughtTrace else
  let nonceTrace := boughtTrace ++ [.incrementNonce]
  if !hooks.incrementNonce then reject input .incrementNonce true nonceTrace else
  let preparedTrace := nonceTrace ++ [.prepareSimpleTransferFastPath]
  let commitBefore := shouldCommitBeforeExecution input
  let committedTrace := if commitBefore then preparedTrace ++ [.commitBeforeExecution] else preparedTrace
  let availableTrace := committedTrace ++ [.calculateAvailableGas]
  if !hooks.calculateAvailableGas then
    { outcome := .rejected .calculateAvailableGas
      restoreOnReject := false
      committedBeforeExecution := commitBefore
      trace := availableTrace }
  else if input.simpleTransfer then
    { outcome := .routed .simpleTransfer
      restoreOnReject := false
      committedBeforeExecution := commitBefore
      trace := availableTrace }
  else
    { outcome := .routed .evm
      restoreOnReject := false
      committedBeforeExecution := commitBefore
      trace := availableTrace }

inductive Status where | success | failure deriving DecidableEq, Repr

inductive StateAction where
  | reset | deleteCaller | returnReservedGas | decrementNonce
  | commitWithoutRoots | commitWithRoots (roots : Bool)
  | resetTransient | reapEmptyAccounts
  deriving DecidableEq, Repr

inductive ErrorSource where | substate | evmException | none deriving DecidableEq, Repr
inductive ResultProjection where | ok | evmException deriving DecidableEq, Repr

structure SpecFlags where
  eip658 : Bool
  eip8037 : Bool
  deriving DecidableEq, Repr

structure SubstateProjection where
  shouldRevert : Bool
  hasError : Bool
  hasEvmException : Bool
  deriving DecidableEq, Repr

structure FinalizeInput where
  options : Options
  spec : SpecFlags
  tracingReceipt : Bool
  deleteCallerAccount : Bool
  hasReservedGasPayment : Bool
  status : Status
  substate : SubstateProjection
  deriving DecidableEq, Repr

structure ReceiptObservation where
  status : Status
  includesStateRoot : Bool
  includesOutput : Bool
  includesLogs : Bool
  errorSource : ErrorSource
  deriving DecidableEq, Repr

structure FinalizeObservation where
  writesBlockGas : Bool
  writesSpentGas : Bool
  stateActions : List StateAction
  receipt : Option ReceiptObservation
  result : ResultProjection
  trace : List TransactionReference.Event
  deriving DecidableEq, Repr

def isExactBuildUp (options : Options) : Bool :=
  options.buildUp && !options.commit && !options.restore && !options.skipValidation && !options.warmup

def stateActions (input : FinalizeInput) : List StateAction :=
  if input.options.restore then
    [.reset] ++ if input.deleteCallerAccount then [.deleteCaller] else
      (if input.hasReservedGasPayment then [.returnReservedGas] else []) ++
        [.decrementNonce, .commitWithoutRoots]
  else if effectiveCommit
      { options := input.options, eip658 := input.spec.eip658,
        simpleTransfer := false, tracingState := false } then
    [.commitWithRoots (!input.spec.eip658)]
  else [.resetTransient] ++
    if isExactBuildUp input.options && input.spec.eip8037 then [.reapEmptyAccounts] else []

def receipt (input : FinalizeInput) : Option ReceiptObservation :=
  if !input.tracingReceipt then none else
  let failed := input.status = .failure
  let errorSource := if !failed then .none else if input.substate.hasError then .substate
    else if input.substate.hasEvmException then .evmException else .none
  some
    { status := input.status
      includesStateRoot := !input.spec.eip658
      includesOutput := !failed || input.substate.shouldRevert
      includesLogs := !failed
      errorSource }

def finalizationTrace (input : FinalizeInput) : List TransactionReference.Event :=
  let stateEvent := if input.options.restore then .restore else
    if effectiveCommit
        { options := input.options, eip658 := input.spec.eip658,
          simpleTransfer := false, tracingState := false } then .commit else .resetTransient
  [stateEvent] ++ if input.tracingReceipt then [.receiptStart, .receiptObserve] else []

def finalize (input : FinalizeInput) : FinalizeObservation :=
  { writesBlockGas := !input.options.warmup
    writesSpentGas := !input.options.warmup
    stateActions := stateActions input
    receipt := receipt input
    result := if input.substate.hasEvmException then .evmException else .ok
    trace := finalizationTrace input }

end Spec

def toSpecOptions (options : Generated.Options) : Spec.Options :=
  { commit := options.commit
    restore := options.restore
    skipValidation := options.skipValidation
    warmup := options.warmup
    buildUp := options.buildUp }

def toSpecPrefixInput (input : Generated.PrefixInput) : Spec.PrefixInput :=
  { options := toSpecOptions input.options
    eip658 := input.eip658
    simpleTransfer := input.simpleTransfer
    tracingState := input.tracingState }

def toSpecHooks (hooks : Generated.AdmissionHooks) : Spec.AdmissionHooks :=
  { validateStatic := hooks.validateStatic
    validateSender := hooks.validateSender
    buyGas := hooks.buyGas
    incrementNonce := hooks.incrementNonce
    calculateAvailableGas := hooks.calculateAvailableGas }

def toSpecRejectStage : Generated.RejectStage → Spec.RejectStage
  | .validateStatic => .validateStatic
  | .validateSender => .validateSender
  | .buyGas => .buyGas
  | .incrementNonce => .incrementNonce
  | .calculateAvailableGas => .calculateAvailableGas

def toSpecRoute : Generated.Route → Spec.Route
  | .simpleTransfer => .simpleTransfer
  | .evm => .evm

def toSpecPrefixOutcome : Generated.PrefixOutcome → Spec.PrefixOutcome
  | .rejected stage => .rejected (toSpecRejectStage stage)
  | .routed route => .routed (toSpecRoute route)

def stageReferenceEvents : Generated.Stage → List TransactionReference.Event
  | .recoverSenderBeforeIntrinsicGas => [.recoverSenderBeforeIntrinsicGas]
  | .calculateIntrinsicGas => [.calculateIntrinsicGas]
  | .validateStatic => [.validateStatic]
  | .calculateEffectiveGasPrice => [.calculateEffectiveGasPrice]
  | .updateMetrics => []
  | .recoverSenderIfNeeded => [.recoverSenderIfNeeded]
  | .validateSender => [.validateSender]
  | .buyGas => [.buyGas]
  | .incrementNonce => [.incrementNonce]
  | .prepareSimpleTransferFastPath => [.prepareSimpleTransferFastPath]
  | .commitBeforeExecution => [.commitBeforeExecution]
  | .calculateAvailableGas => [.calculateAvailableGas]
  | .dispatchSimpleTransfer | .dispatchEvm => []
  | .recipientStateCharge => [.recipientStateCharge]
  | .payValue => [.payValue]
  | .refund => [.refund]
  | .headerGasAndPayFees => [.headerGas, .payFees]
  | .processDelegations => [.processDelegations]
  | .buildExecutionEnvironment => [.buildExecutionEnvironment]
  | .executeEvmCall => []
  | .deferredDestroyList => [.destroyListFinalize]
  | .finalizeTransaction => []
  | .topExecutionSnapshot => [.topExecutionSnapshot]
  | .vmExecution => [.vmExecution]
  | .executionRollback => [.executionRollback]
  | .deployment => [.deployment]
  | .restore => [.restore]
  | .commit => [.commit]
  | .resetTransient => [.resetTransient]
  | .receiptStart => [.receiptStart]
  | .receiptObserve => [.receiptObserve]

def toReferenceTrace (trace : List Generated.Stage) : List TransactionReference.Event :=
  trace.flatMap stageReferenceEvents

@[simp] theorem toReferenceTrace_nil : toReferenceTrace [] = [] := rfl

@[simp] theorem toReferenceTrace_cons (stage : Generated.Stage) (trace : List Generated.Stage) :
    toReferenceTrace (stage :: trace) = stageReferenceEvents stage ++ toReferenceTrace trace := by
  rfl

@[simp] theorem toReferenceTrace_append (left right : List Generated.Stage) :
    toReferenceTrace (left ++ right) = toReferenceTrace left ++ toReferenceTrace right := by
  simp [toReferenceTrace, List.flatMap_append]

@[simp] theorem toReferenceTrace_if (condition : Prop) [Decidable condition]
    (whenTrue whenFalse : List Generated.Stage) :
    toReferenceTrace (if condition then whenTrue else whenFalse) =
      if condition then toReferenceTrace whenTrue else toReferenceTrace whenFalse := by
  split <;> rfl

def observeGeneratedPrefix (result : Generated.PrefixResult) : Spec.PrefixObservation :=
  { outcome := toSpecPrefixOutcome result.outcome
    restoreOnReject := result.restoreOnReject
    committedBeforeExecution := result.committedBeforeExecution
    trace := toReferenceTrace result.trace }

theorem effective_commit_refines_handwritten_spec (input : Generated.PrefixInput) :
    Eip803x.Generated.TransactionProcessorLifecycle.effectiveCommit input =
      Spec.effectiveCommit (toSpecPrefixInput input) := by
  rfl

theorem commit_before_refines_handwritten_spec (input : Generated.PrefixInput) :
    Eip803x.Generated.TransactionProcessorLifecycle.shouldCommitBeforeExecution input =
      Spec.shouldCommitBeforeExecution (toSpecPrefixInput input) := by
  rfl

theorem admission_prefix_refines_handwritten_transaction_reference
    (input : Generated.PrefixInput) (hooks : Generated.AdmissionHooks) :
    observeGeneratedPrefix (Generated.runPrefix input hooks) =
      Spec.runPrefix (toSpecPrefixInput input) (toSpecHooks hooks) := by
  rcases hooks with ⟨validateStatic, validateSender, buyGas, incrementNonce, availableGas⟩
  cases validateStatic <;> cases validateSender <;> cases buyGas <;>
    cases incrementNonce <;> cases availableGas <;>
    by_cases hCommit :
      Eip803x.Generated.TransactionProcessorLifecycle.shouldCommitBeforeExecution input = true <;>
    cases hSimple : input.simpleTransfer <;>
    simp [Generated.runPrefix, Eip803x.Generated.TransactionProcessorLifecycle.runPrefix,
      Eip803x.Generated.TransactionProcessorLifecycle.reject, Spec.runPrefix, Spec.reject,
      observeGeneratedPrefix, stageReferenceEvents,
      toSpecPrefixOutcome, toSpecRejectStage, toSpecRoute,
      commit_before_refines_handwritten_spec, toSpecPrefixInput, toSpecOptions,
      toSpecHooks, hSimple] at *

theorem admission_prefix_reference_trace_is_ordered
    (input : Generated.PrefixInput) (hooks : Generated.AdmissionHooks) :
    TransactionReference.traceOrdered
      (toReferenceTrace (Generated.runPrefix input hooks).trace) = true := by
  have refinement := admission_prefix_refines_handwritten_transaction_reference input hooks
  have traceRefinement := congrArg Spec.PrefixObservation.trace refinement
  simp [observeGeneratedPrefix] at traceRefinement
  rw [traceRefinement]
  rcases hooks with ⟨validateStatic, validateSender, buyGas, incrementNonce, availableGas⟩
  cases validateStatic <;> cases validateSender <;> cases buyGas <;>
    cases incrementNonce <;> cases availableGas <;>
    simp [Spec.runPrefix, Spec.reject, TransactionReference.traceOrdered,
      Spec.shouldCommitBeforeExecution, Spec.effectiveCommit, toSpecHooks]
  all_goals repeat' first | split | simp_all [TransactionReference.traceOrdered,
    TransactionReference.eventRank]

theorem extracted_simple_tail_matches_reference_order :
    toReferenceTrace
        Eip803x.Generated.TransactionProcessorLifecycle.simpleTransferTailOrder =
      [.recipientStateCharge, .payValue, .refund, .headerGas, .payFees] := by
  rfl

theorem extracted_evm_tail_matches_reference_order :
    toReferenceTrace Eip803x.Generated.TransactionProcessorLifecycle.evmOuterTailOrder =
      [.processDelegations, .buildExecutionEnvironment, .recipientStateCharge,
       .headerGas, .payFees, .destroyListFinalize] := by
  rfl

theorem extracted_tail_reference_events_are_ordered :
    TransactionReference.traceOrdered
        (toReferenceTrace Eip803x.Generated.TransactionProcessorLifecycle.simpleTransferTailOrder) = true ∧
      TransactionReference.traceOrdered
        (toReferenceTrace Eip803x.Generated.TransactionProcessorLifecycle.evmOuterTailOrder) = true := by
  native_decide

def extractedEdgeReferenceTrace (edge : Generated.Stage × Generated.Stage) :
    List TransactionReference.Event :=
  stageReferenceEvents edge.1 ++ stageReferenceEvents edge.2

theorem extracted_evm_call_edges_match_handwritten_reference :
    Eip803x.Generated.TransactionProcessorLifecycle.evmCallRequiredOrder =
      [(.topExecutionSnapshot, .payValue), (.payValue, .vmExecution),
       (.vmExecution, .executionRollback), (.vmExecution, .deployment),
       (.executionRollback, .refund), (.deployment, .refund)] := by
  rfl

theorem every_extracted_evm_call_edge_refines_transaction_reference_order :
    Eip803x.Generated.TransactionProcessorLifecycle.evmCallRequiredOrder.all
      (fun edge => TransactionReference.traceOrdered (extractedEdgeReferenceTrace edge)) = true := by
  rw [extracted_evm_call_edges_match_handwritten_reference]
  native_decide

def toSpecStateAction : Generated.StateAction → Spec.StateAction
  | .reset => .reset
  | .deleteCaller => .deleteCaller
  | .returnReservedGas => .returnReservedGas
  | .decrementNonce => .decrementNonce
  | .commitWithoutRoots => .commitWithoutRoots
  | .commitWithRoots roots => .commitWithRoots roots
  | .resetTransient => .resetTransient
  | .reapEmptyAccounts => .reapEmptyAccounts

def toSpecStatus : Generated.Status → Spec.Status
  | .success => .success
  | .failure => .failure

def toSpecErrorSource : Generated.ErrorSource → Spec.ErrorSource
  | .substate => .substate
  | .evmException => .evmException
  | .none => .none

def toSpecResult : Generated.ResultProjection → Spec.ResultProjection
  | .ok => .ok
  | .evmException => .evmException

def toSpecFinalizeInput (input : Generated.FinalizeInput) : Spec.FinalizeInput :=
  { options := toSpecOptions input.options
    spec := { eip658 := input.spec.eip658, eip8037 := input.spec.eip8037 }
    tracingReceipt := input.tracingReceipt
    deleteCallerAccount := input.deleteCallerAccount
    hasReservedGasPayment := input.hasReservedGasPayment
    status := toSpecStatus input.status
    substate :=
      { shouldRevert := input.substate.shouldRevert
        hasError := input.substate.hasError
        hasEvmException := input.substate.hasEvmException } }

def toSpecReceipt (receipt : Generated.ReceiptProjection) : Spec.ReceiptObservation :=
  { status := toSpecStatus receipt.status
    includesStateRoot := receipt.includesStateRoot
    includesOutput := receipt.includesOutput
    includesLogs := receipt.includesLogs
    errorSource := toSpecErrorSource receipt.errorSource }

def observeGeneratedFinalize (result : Generated.FinalizeResult) : Spec.FinalizeObservation :=
  { writesBlockGas := result.writesBlockGas
    writesSpentGas := result.writesSpentGas
    stateActions := result.stateActions.map toSpecStateAction
    receipt := result.receipt.map toSpecReceipt
    result := toSpecResult result.result
    trace := toReferenceTrace result.trace }

theorem finalization_refines_handwritten_transaction_reference (input : Generated.FinalizeInput) :
    observeGeneratedFinalize (Generated.finalize input) =
      Spec.finalize (toSpecFinalizeInput input) := by
  rcases input with ⟨options, spec, tracingReceipt, deleteCaller, reserved, status, substate⟩
  rcases status with (_ | _) <;> rcases substate with ⟨shouldRevert, hasError, hasEvmException⟩ <;>
    simp [observeGeneratedFinalize, Generated.finalize,
      Eip803x.Generated.TransactionProcessorLifecycle.finalize,
      Eip803x.Generated.TransactionProcessorLifecycle.finalizationActions,
      Eip803x.Generated.TransactionProcessorLifecycle.receiptProjection,
      Eip803x.Generated.TransactionProcessorLifecycle.finalizationTrace,
      Eip803x.Generated.TransactionProcessorLifecycle.isExactBuildUp,
      Spec.finalize, Spec.stateActions, Spec.receipt, Spec.finalizationTrace,
      Spec.effectiveCommit, Spec.isExactBuildUp, toSpecFinalizeInput, toSpecOptions,
      toSpecStatus, toSpecResult,
      toReferenceTrace, stageReferenceEvents]
  all_goals repeat' first | split | simp_all [toSpecStateAction, toSpecReceipt, toSpecStatus,
    toSpecErrorSource, stageReferenceEvents]

theorem finalization_reference_trace_is_ordered (input : Generated.FinalizeInput) :
    TransactionReference.traceOrdered
      (toReferenceTrace (Generated.finalize input).trace) = true := by
  rcases input with ⟨options, spec, tracingReceipt, deleteCaller, reserved, status, substate⟩
  rcases options with ⟨commit, restore, skipValidation, warmup, buildUp⟩
  rcases spec with ⟨eip658, eip8037⟩
  cases commit <;> cases restore <;> cases skipValidation <;> cases eip658 <;>
    cases tracingReceipt <;>
    simp [Generated.finalize, Eip803x.Generated.TransactionProcessorLifecycle.finalize,
      Eip803x.Generated.TransactionProcessorLifecycle.finalizationTrace,
      toReferenceTrace, stageReferenceEvents, TransactionReference.traceOrdered,
      TransactionReference.eventRank]

theorem warmup_suppresses_both_transaction_gas_writes (input : Generated.FinalizeInput)
    (h : input.options.warmup = true) :
    (Generated.finalize input).writesBlockGas = false ∧
      (Generated.finalize input).writesSpentGas = false := by
  simp [Generated.finalize, Eip803x.Generated.TransactionProcessorLifecycle.finalize, h]

theorem exception_result_is_independent_of_receipt_status (input : Generated.FinalizeInput)
    (h : input.substate.hasEvmException = true) :
    (Generated.finalize input).result = .evmException := by
  simp [Generated.finalize, Eip803x.Generated.TransactionProcessorLifecycle.finalize, h]

theorem failed_nonrevert_receipt_hides_output (input : Generated.FinalizeInput)
    (hTrace : input.tracingReceipt = true) (hStatus : input.status = .failure)
    (hRevert : input.substate.shouldRevert = false) :
    (Generated.finalize input).receipt.map (fun receipt => receipt.includesOutput) = some false := by
  simp [Generated.finalize, Eip803x.Generated.TransactionProcessorLifecycle.finalize,
    Eip803x.Generated.TransactionProcessorLifecycle.receiptProjection,
    hTrace, hStatus, hRevert]

end Eip803x.Refinement.TransactionProcessorLifecycle
