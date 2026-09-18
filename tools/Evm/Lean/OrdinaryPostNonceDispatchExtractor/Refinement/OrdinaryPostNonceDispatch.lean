-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Refinement and erasure obligations for the bounded generated transition.

import OrdinaryPostNonceDispatchExtractor.Generated.OrdinaryPostNonceDispatch
import OrdinaryPostNonceDispatchExtractor.Reference.OrdinaryPostNonceDispatchReference
import OrdinaryStatefulAdmissionPrefixExtractor.Generated.OrdinaryStatefulAdmissionPrefix
import TransactionProcessorExtractor.Generated.TransactionProcessorLifecycle

namespace OrdinaryPostNonceDispatchExtractor.Refinement

open OrdinaryPostNonceDispatchExtractor.Generated

def mapPreload : Preload → OrdinaryPostNonceDispatchExtractor.Reference.Preload
  | .none => .none
  | .loaded value => .loaded { codeHandle := value.codeHandle, delegation := value.delegation, codeIsEmpty := value.codeIsEmpty }

def mapGas (gas : GasPolicy) : OrdinaryPostNonceDispatchExtractor.Reference.GasPolicy :=
  { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
    stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded }

def mapOptions (options : Options) : OrdinaryPostNonceDispatchExtractor.Reference.Options := { raw := options.raw }
def mapTransaction (tx : Transaction) : OrdinaryPostNonceDispatchExtractor.Reference.Transaction :=
  { id := tx.id, to := tx.to, authorizationListPresent := tx.authorizationListPresent }
def mapHeader (header : Header) : OrdinaryPostNonceDispatchExtractor.Reference.Header := { id := header.id }
def mapSpec (spec : Spec) : OrdinaryPostNonceDispatchExtractor.Reference.Spec :=
  { id := spec.id, eip8037Enabled := spec.eip8037Enabled, eip658Enabled := spec.eip658Enabled }
def mapTracer (tracer : Tracer) : OrdinaryPostNonceDispatchExtractor.Reference.Tracer :=
  { id := tracer.id, isTracingState := tracer.isTracingState }
def mapIntrinsic (intrinsic : Intrinsic) : OrdinaryPostNonceDispatchExtractor.Reference.Intrinsic :=
  { standard := intrinsic.standard }

def mapLookupRequest (request : LookupRequest) : OrdinaryPostNonceDispatchExtractor.Reference.LookupRequest :=
  { recipient := request.recipient, followDelegation := request.followDelegation, spec := mapSpec request.spec }

def mapCommitTracer : CommitTracer → OrdinaryPostNonceDispatchExtractor.Reference.CommitTracer
  | .tracing value => .tracing (mapTracer value)
  | .null => .null

def mapCommitRequest (request : CommitRequest) : OrdinaryPostNonceDispatchExtractor.Reference.CommitRequest :=
  { spec := mapSpec request.spec, tracer := mapCommitTracer request.tracer, commitRoots := request.commitRoots }

def mapInput (input : Input) : OrdinaryPostNonceDispatchExtractor.Reference.Input :=
  { tx := mapTransaction input.tx, header := mapHeader input.header, spec := mapSpec input.spec,
    tracer := mapTracer input.tracer, options := mapOptions input.options, intrinsic := mapIntrinsic input.intrinsic,
    gasLimit := input.gasLimit, isCodeOverridable := input.isCodeOverridable,
    forceSimpleTransferDisabled := input.forceSimpleTransferDisabled,
    lookup := { escaped := input.lookup.escaped, preload := mapPreload input.lookup.preload },
    commitResponse := { escaped := input.commitResponse.escaped },
    availableGas := { accepted := input.availableGas.accepted, gas := mapGas input.availableGas.gas },
    opcodePrice := input.opcodePrice, premium := input.premium, reserved := input.reserved,
    blobBaseFee := input.blobBaseFee, deleteCallerAccount := input.deleteCallerAccount }

def mapHandoff (handoff : Handoff) : OrdinaryPostNonceDispatchExtractor.Reference.Handoff :=
  { tx := mapTransaction handoff.tx, header := mapHeader handoff.header, spec := mapSpec handoff.spec,
    tracer := mapTracer handoff.tracer, opts := mapOptions handoff.opts, restore := handoff.restore,
    commit := handoff.commit, deleteCallerAccount := handoff.deleteCallerAccount,
    intrinsic := mapIntrinsic handoff.intrinsic, gasAvailable := mapGas handoff.gasAvailable,
    opcodePrice := handoff.opcodePrice, premium := handoff.premium, reserved := handoff.reserved,
    blobBaseFee := handoff.blobBaseFee, preload := mapPreload handoff.preload, recipient := handoff.recipient }

def mapOutcome : TerminalOutcome → OrdinaryPostNonceDispatchExtractor.Reference.TerminalOutcome
  | .EscapedLookup => .EscapedLookup
  | .EscapedPrecommit => .EscapedPrecommit
  | .GasRejected => .GasRejected
  | .SimpleHandoff => .SimpleHandoff
  | .EvmHandoff => .EvmHandoff

def mapResult (result : Result) : OrdinaryPostNonceDispatchExtractor.Reference.Result :=
  { outcome := mapOutcome result.outcome, preload := mapPreload result.preload,
    gasAvailable := mapGas result.gasAvailable, handoff := result.handoff.map mapHandoff,
    lookupRequest := result.lookupRequest.map mapLookupRequest,
    commitRequest := result.commitRequest.map mapCommitRequest, failure := result.failure }

def mapPrepared (prepared : Prepared) : OrdinaryPostNonceDispatchExtractor.Reference.Prepared :=
  { recipient := prepared.recipient, preload := mapPreload prepared.preload,
    simpleRecipient := prepared.simpleRecipient, lookupEscaped := prepared.lookupEscaped,
    lookupRequest := prepared.lookupRequest.map mapLookupRequest }

private theorem map_simpleDecision (preload : Preload) :
    OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision (mapPreload preload) = simpleDecision preload := by
  cases preload with
  | none => rfl
  | loaded value =>
    cases delegation : value.delegation with
    | none =>
      cases empty : value.codeIsEmpty <;>
        simp [simpleDecision, OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision, mapPreload, delegation, empty]
    | some delegated =>
      simp [simpleDecision, OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision, mapPreload, delegation]

private theorem map_preload_none :
    mapPreload (Preload.none : Preload) = OrdinaryPostNonceDispatchExtractor.Reference.Preload.none := by
  rfl

private theorem map_simpleDecision_none :
    OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision
        (OrdinaryPostNonceDispatchExtractor.Reference.Preload.none) =
      simpleDecision (Preload.none : Preload) := by
  rfl

private theorem map_lookupRequest (request : LookupRequest) :
    mapLookupRequest request = OrdinaryPostNonceDispatchExtractor.Reference.LookupRequest.mk request.recipient request.followDelegation (mapSpec request.spec) := by
  cases request
  rfl

private theorem map_simpleDecision_loaded (value : LoadedPreload) :
    OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision
        (OrdinaryPostNonceDispatchExtractor.Reference.Preload.loaded
          { codeHandle := value.codeHandle, delegation := value.delegation, codeIsEmpty := value.codeIsEmpty }) =
      simpleDecision (Preload.loaded value) := by
  cases delegation : value.delegation with
  | none =>
    cases empty : value.codeIsEmpty <;>
      simp [simpleDecision, OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision, delegation, empty]
  | some delegated =>
    simp [simpleDecision, OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision, delegation]

private theorem map_prepare (input : Input) :
    mapPrepared (prepare input) = OrdinaryPostNonceDispatchExtractor.Reference.prepare (mapInput input) := by
  cases to : input.tx.to with
  | none =>
    simp [prepare, OrdinaryPostNonceDispatchExtractor.Reference.prepare,
      OrdinaryPostNonceDispatchExtractor.Reference.noRecipientPrepared, mapPrepared, mapInput,
      mapTransaction, map_preload_none, to]
  | some recipient =>
    cases overridable : input.isCodeOverridable <;>
      cases authorization : input.tx.authorizationListPresent <;>
      cases forced : input.forceSimpleTransferDisabled <;>
      cases escaped : input.lookup.escaped
    all_goals
      cases preload : input.lookup.preload with
      | none =>
          simp [prepare, OrdinaryPostNonceDispatchExtractor.Reference.prepare,
            OrdinaryPostNonceDispatchExtractor.Reference.candidateAllowed,
            OrdinaryPostNonceDispatchExtractor.Reference.nonCandidatePrepared,
            OrdinaryPostNonceDispatchExtractor.Reference.lookupPrepared, mapPrepared, mapInput,
            mapTransaction, mapSpec, mapPreload, mapLookupRequest,
            simpleDecision, OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision, to,
            overridable, authorization, forced, escaped, preload]
      | loaded value =>
        cases delegation : value.delegation <;>
          cases empty : value.codeIsEmpty <;>
          simp [prepare, OrdinaryPostNonceDispatchExtractor.Reference.prepare,
            OrdinaryPostNonceDispatchExtractor.Reference.candidateAllowed,
            OrdinaryPostNonceDispatchExtractor.Reference.nonCandidatePrepared,
            OrdinaryPostNonceDispatchExtractor.Reference.lookupPrepared, mapPrepared, mapInput,
            mapTransaction, mapSpec, mapPreload, mapLookupRequest, simpleDecision,
            OrdinaryPostNonceDispatchExtractor.Reference.simpleDecision, to, overridable,
            authorization, forced, escaped, preload, delegation, empty]

private theorem map_controls (input : Input) (simpleRecipient : Option Nat) :
    (OrdinaryPostNonceDispatchExtractor.Reference.controls (mapInput input) simpleRecipient).restore = restore input.options ∧
    (OrdinaryPostNonceDispatchExtractor.Reference.controls (mapInput input) simpleRecipient).commit = commit input.options input.spec.eip658Enabled ∧
    (OrdinaryPostNonceDispatchExtractor.Reference.controls (mapInput input) simpleRecipient).beforeExecution =
      commitBeforeExecution input simpleRecipient (restore input.options)
        (commit input.options input.spec.eip658Enabled) := by
  simp [OrdinaryPostNonceDispatchExtractor.Reference.controls, restore, commit,
    OrdinaryPostNonceDispatchExtractor.Reference.effectiveCommit, commitBeforeExecution,
    OrdinaryPostNonceDispatchExtractor.Reference.restore,
    OrdinaryPostNonceDispatchExtractor.Reference.hasFlag, hasFlag,
    executionOptionCommit, executionOptionRestore, executionOptionSkipValidation,
    mapInput, mapOptions, mapSpec, mapTracer]

private theorem map_controls_prepared (input : Input) :
    (OrdinaryPostNonceDispatchExtractor.Reference.controls (mapInput input)
        (mapPrepared (prepare input)).simpleRecipient).restore = restore input.options ∧
    (OrdinaryPostNonceDispatchExtractor.Reference.controls (mapInput input)
        (mapPrepared (prepare input)).simpleRecipient).commit = commit input.options input.spec.eip658Enabled ∧
    (OrdinaryPostNonceDispatchExtractor.Reference.controls (mapInput input)
        (mapPrepared (prepare input)).simpleRecipient).beforeExecution =
      commitBeforeExecution input (prepare input).simpleRecipient (restore input.options)
        (commit input.options input.spec.eip658Enabled) := by
  simpa [mapPrepared] using map_controls input (prepare input).simpleRecipient

private theorem mapPrepared_preload (prepared : Prepared) :
    (mapPrepared prepared).preload = mapPreload prepared.preload := by
  rfl

private theorem mapPrepared_simpleRecipient (prepared : Prepared) :
    (mapPrepared prepared).simpleRecipient = prepared.simpleRecipient := by
  rfl

private theorem mapPrepared_lookupEscaped (prepared : Prepared) :
    (mapPrepared prepared).lookupEscaped = prepared.lookupEscaped := by
  rfl

private theorem mapPrepared_lookupRequest (prepared : Prepared) :
    (mapPrepared prepared).lookupRequest = prepared.lookupRequest.map mapLookupRequest := by
  rfl

private theorem mapInput_commitEscaped (input : Input) :
    (mapInput input).commitResponse.escaped = input.commitResponse.escaped := by
  rfl

private theorem mapInput_availableAccepted (input : Input) :
    (mapInput input).availableGas.accepted = input.availableGas.accepted := by
  rfl

private theorem mapInput_availableGas (input : Input) :
    (mapInput input).availableGas.gas = mapGas input.availableGas.gas := by
  rfl

private theorem map_commitRequest (input : Input) :
    mapCommitRequest (commitRequest input) = OrdinaryPostNonceDispatchExtractor.Reference.commitRequest (mapInput input) := by
  cases tracing : input.tracer.isTracingState <;>
    simp [mapCommitRequest, commitRequest, mapInput, mapSpec, mapTracer, mapCommitTracer,
      OrdinaryPostNonceDispatchExtractor.Reference.commitRequest,
      OrdinaryPostNonceDispatchExtractor.Reference.selectedCommitTracer, tracing]

private theorem map_handoff (input : Input) (prepared : Prepared) (gas : GasPolicy)
    (recipient : Option Nat) (restoreValue commitValue : Bool) :
    mapHandoff (handoff input prepared gas recipient restoreValue commitValue) =
      OrdinaryPostNonceDispatchExtractor.Reference.makeHandoff (mapInput input) (mapPrepared prepared) (mapGas gas) recipient restoreValue commitValue := by
  simp [mapHandoff, handoff, mapInput, mapTransaction, mapHeader, mapSpec, mapTracer,
    mapOptions, mapIntrinsic, mapPrepared, mapGas,
    OrdinaryPostNonceDispatchExtractor.Reference.makeHandoff]

/- The admitted prefix identity is used only to name the start boundary. -/
def ordinaryStatefulPrefixBoundary : String := OrdinaryStatefulAdmissionPrefixExtractor.Generated.sourceIrSha256
def lifecycleVocabularyIdentity : List String :=
  ["prepareSimpleTransferFastPath", "commitBeforeExecution", "calculateAvailableGas",
   "dispatchSimpleTransfer", "dispatchEvm"]

theorem generated_postNonceDispatch_refines_reference (input : Input) :
    mapResult (run input) = OrdinaryPostNonceDispatchExtractor.Reference.run (mapInput input) := by
  simp only [OrdinaryPostNonceDispatchExtractor.Generated.run,
    OrdinaryPostNonceDispatchExtractor.Reference.run]
  rw [← map_prepare input]
  cases hLookup : (prepare input).lookupEscaped
  all_goals
    have hControls := map_controls input (prepare input).simpleRecipient
    cases hRestore : restore input.options <;>
      cases hCommit : commit input.options input.spec.eip658Enabled <;>
      cases hBefore : commitBeforeExecution input (prepare input).simpleRecipient
        (restore input.options) (commit input.options input.spec.eip658Enabled) <;>
      cases hEscaped : input.commitResponse.escaped <;>
      cases hAccepted : input.availableGas.accepted <;>
      cases hSimple : (prepare input).simpleRecipient
    all_goals
      have hControlsConcrete := hControls
      simp [hRestore, hCommit] at hBefore
      rw [hSimple] at hBefore
      rw [hSimple] at hControlsConcrete
      rcases hControlsConcrete with ⟨hRestoreMap, hCommitMap, hBeforeMap⟩
      simp [hLookup, hRestore, hCommit, hBefore, hEscaped, hAccepted, hSimple,
        hRestoreMap, hCommitMap, hBeforeMap, mapResult, mapPreload,
        mapGas, mapOutcome,
        mapPrepared_preload, mapPrepared_simpleRecipient, mapPrepared_lookupEscaped,
        mapPrepared_lookupRequest,
        mapInput_commitEscaped, mapInput_availableAccepted, mapInput_availableGas,
        map_handoff, map_commitRequest, zeroGas,
        classifyAvailableGas,
        OrdinaryPostNonceDispatchExtractor.Reference.zeroGas,
        OrdinaryPostNonceDispatchExtractor.Reference.lookupEscape,
        OrdinaryPostNonceDispatchExtractor.Reference.precommitEscape,
        OrdinaryPostNonceDispatchExtractor.Reference.gasRejected,
        OrdinaryPostNonceDispatchExtractor.Reference.acceptedGas,
        OrdinaryPostNonceDispatchExtractor.Reference.afterPreparation,
        ]

def lifecycleStageFor : TerminalOutcome → Eip803x.Generated.TransactionProcessorLifecycle.Stage
  | .EscapedLookup => .prepareSimpleTransferFastPath
  | .EscapedPrecommit => .commitBeforeExecution
  | .GasRejected => .calculateAvailableGas
  | .SimpleHandoff => .dispatchSimpleTransfer
  | .EvmHandoff => .dispatchEvm

def lifecycleStageForReference : OrdinaryPostNonceDispatchExtractor.Reference.TerminalOutcome →
    Eip803x.Generated.TransactionProcessorLifecycle.Stage
  | .EscapedLookup => .prepareSimpleTransferFastPath
  | .EscapedPrecommit => .commitBeforeExecution
  | .GasRejected => .calculateAvailableGas
  | .SimpleHandoff => .dispatchSimpleTransfer
  | .EvmHandoff => .dispatchEvm

def lifecycleControl (input : Input) : Eip803x.Generated.TransactionProcessorLifecycle.Stage := lifecycleStageFor (run input).outcome

theorem erases_to_lifecycleControl (input : Input) :
    lifecycleControl input = lifecycleStageForReference
      ((OrdinaryPostNonceDispatchExtractor.Reference.run (mapInput input)).outcome) := by
  have h := congrArg OrdinaryPostNonceDispatchExtractor.Reference.Result.outcome
    (generated_postNonceDispatch_refines_reference input)
  calc
    lifecycleControl input = lifecycleStageForReference (mapOutcome (run input).outcome) := by
      cases hOutcome : (run input).outcome <;>
        simp [lifecycleControl, lifecycleStageFor, lifecycleStageForReference, mapOutcome, hOutcome]
    _ = lifecycleStageForReference
        ((OrdinaryPostNonceDispatchExtractor.Reference.run (mapInput input)).outcome) := by
      exact congrArg lifecycleStageForReference (by simpa [mapResult] using h)

/- Handoff identities are boundary observations only. Downstream execution is
   intentionally not imported or composed into this refinement. -/
def futureEvmHandoff_bridge_obligation : Prop :=
  ∀ input : Input, (run input).outcome = .EvmHandoff →
    (run input).handoff.isSome

end OrdinaryPostNonceDispatchExtractor.Refinement
