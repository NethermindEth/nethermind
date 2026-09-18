-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.BranchReference
import Eip803x.SystemTransactionReference

namespace Eip803x
namespace BlockSystemComposition

/-!
This handwritten adapter connects the accepted system-transaction leaf relation
to the abstract beacon-root and execution-request hooks in `BlockReference`.
It is deliberately block-instance-specific: the constructor checks the exact
processor worlds, ordinary receipt projection, and request-call sequence before
allowing `BlockReference` to run.  Encoding, hashing, and the correspondence
between a full processor world and the block model's scalar state token remain
explicit adapter premises.
-/

open BlockReference

abbrev SystemWorld := SystemTransactionReference.World
abbrev SystemCallInput := SystemTransactionReference.BlockCallInput
abbrev SystemAdapterState := SystemTransactionReference.BlockAdapterState

inductive AdapterFailure where
  | beaconShape
  | beaconOutcome
  | beaconState
  | prefixNotReached
  | requestPhaseNotReachable
  | requestOrder
  | requestWorld
  | requestAdapter
  | requestInvalid (site : SystemTransactionReference.BlockCallSite)
  | requestOutcome (site : SystemTransactionReference.BlockCallSite)
  | requestState
  | requestEncoding
  | requestHash
  | branchInitialState
  | branchNotSingleton
  deriving DecidableEq, Repr

structure RequestBatchResult where
  world : SystemWorld
  adapterState : SystemAdapterState
  deriving DecidableEq, Repr

def exactRequestSites : List SystemTransactionReference.BlockCallSite :=
  [ .withdrawalRequests
  , .consolidationRequests
  , .builderDepositRequests
  , .builderExitRequests ]

theorem exact_request_site_count : exactRequestSites.length = 4 := by
  native_decide

def requestAdapterAt (world : BlockWorld) (depositPayloads : List (List Nat)) :
    SystemAdapterState :=
  { normalReceipts := world.receipts.map (fun receipt => receipt.id)
  , cumulativeReceiptGas := world.counters.cumulativeReceiptGasUsed
  , requestPayloads := depositPayloads }

def beaconTransition (call : SystemCallInput) (entryState : Nat) :
    Except AdapterFailure Nat :=
  if call.site != .beaconRoot ||
      call.processorInput.initialWorld.token != entryState ||
      call.processorInput.initialCommittedWorld != call.processorInput.initialWorld ||
      !call.adapterState.normalReceipts.isEmpty ||
      call.adapterState.cumulativeReceiptGas != 0 ||
      !call.adapterState.requestPayloads.isEmpty then
    .error .beaconShape
  else
    let result := SystemTransactionReference.runBlockCall call
    if result.adapterState != call.adapterState then
      .error .beaconOutcome
    else
      match result.disposition with
      | .skipped =>
          if result.processorResult.isNone then .ok entryState else .error .beaconOutcome
      | .applied =>
          match result.processorResult with
          | some processor =>
              if processor.normalCounterDeltas == (0, 0, 0) then
                .ok processor.state.world.token
              else
                .error .beaconOutcome
          | none => .error .beaconOutcome
      | .invalidBlock => .error .beaconOutcome
      | .unmodeled => .error .beaconShape

def runRequestCalls : List SystemTransactionReference.BlockCallSite →
    List SystemCallInput → SystemWorld → SystemAdapterState →
    Except AdapterFailure RequestBatchResult
  | [], [], world, adapterState => .ok { world := world, adapterState := adapterState }
  | expected :: remaining, call :: calls, world, adapterState =>
      if call.site != expected || !call.enabled then
        .error .requestOrder
      else if call.processorInput.initialWorld != world ||
          call.processorInput.initialCommittedWorld != world then
        .error .requestWorld
      else if call.adapterState != adapterState then
        .error .requestAdapter
      else
        let result := SystemTransactionReference.runBlockCall call
        match result.disposition with
        | .invalidBlock => .error (.requestInvalid expected)
        | .unmodeled | .skipped => .error (.requestOutcome expected)
        | .applied =>
            match result.processorResult with
            | none => .error (.requestOutcome expected)
            | some processor =>
                if processor.disposition != .completed ||
                    processor.normalCounterDeltas != (0, 0, 0) ||
                    result.adapterState.normalReceipts != adapterState.normalReceipts ||
                    result.adapterState.cumulativeReceiptGas !=
                      adapterState.cumulativeReceiptGas then
                  .error (.requestOutcome expected)
                else
                  runRequestCalls remaining calls processor.state.world result.adapterState
  | _, _, _, _ => .error .requestOrder

structure Adapter where
  baseSpec : BlockSpec
  beaconCall : SystemCallInput
  beaconStateAfter : Nat
  requestCalls : List SystemCallInput
  requestWorldBefore : SystemWorld
  requestWorldAfter : SystemWorld
  depositPayloads : List (List Nat)
  encodeRequests : List (List Nat) → Nat
  hashRequests : Nat → Nat
  encodedRequests : Nat
  requestsHash : Nat

private def composedBlockSpec (adapter : Adapter) : BlockSpec :=
  { adapter.baseSpec with
    applyBeaconRoot := fun _ => adapter.beaconStateAfter
    computeExecutionRequests := fun _ _ => adapter.encodedRequests
    applyExecutionRequests := fun _ _ => adapter.requestWorldAfter.token
    computeRequestsHash := fun _ => adapter.requestsHash }

private def prefixThroughWithdrawals (spec : BlockSpec) (input : BlockInput) :
    Option BlockWorld :=
  match runPhases ((processOneActions spec input).take 7)
      (.running { world := initialWorld input, trace := [] }) with
  | .running machine => some machine.world
  | .stopped _ _ => none

def validateAdapter (adapter : Adapter) (input : BlockInput) :
    Except AdapterFailure Unit :=
  let beaconEntry := adapter.baseSpec.applyDao input.initialState
  match beaconTransition adapter.beaconCall beaconEntry with
  | .error failure => .error failure
  | .ok beaconAfter =>
      if beaconAfter != adapter.beaconStateAfter then
        .error .beaconState
      else if acceptedPhase input .executionRequestsAndSystemCalls != true ||
          (input.phaseException .executionRequestsAndSystemCalls).isSome then
        .error .requestPhaseNotReachable
      else
        let spec := composedBlockSpec adapter
        match prefixThroughWithdrawals spec input with
        | none => .error .prefixNotReached
        | some world =>
            if world.stateToken != adapter.requestWorldBefore.token then
              .error .requestState
            else
              let initialAdapter := requestAdapterAt world adapter.depositPayloads
              match runRequestCalls exactRequestSites adapter.requestCalls
                  adapter.requestWorldBefore initialAdapter with
              | .error failure => .error failure
              | .ok batch =>
                  if batch.world != adapter.requestWorldAfter then
                    .error .requestState
                  else if adapter.encodeRequests batch.adapterState.requestPayloads !=
                      adapter.encodedRequests then
                    .error .requestEncoding
                  else if adapter.hashRequests adapter.encodedRequests != adapter.requestsHash then
                    .error .requestHash
                  else
                    .ok ()

inductive CompositionRun where
  | modeled (run : BlockRun)
  | invalidBlock (failure : AdapterFailure)
  | unmodeled (failure : AdapterFailure)
  deriving DecidableEq, Repr

def CompositionRun.blockOutcome : CompositionRun → Option Outcome
  | .modeled run => some run.outcome
  | .invalidBlock _ | .unmodeled _ => none

def CompositionRun.world : CompositionRun → Option BlockWorld
  | .modeled run => some run.world
  | .invalidBlock _ | .unmodeled _ => none

def CompositionRun.trace : CompositionRun → Option (List Phase)
  | .modeled run => some run.trace
  | .invalidBlock _ | .unmodeled _ => none

def CompositionRun.failure : CompositionRun → Option AdapterFailure
  | .modeled _ => none
  | .invalidBlock failure | .unmodeled failure => some failure

def runBlock (adapter : Adapter) (input : BlockInput) : CompositionRun :=
  match validateAdapter adapter input with
  | .ok () => .modeled (runProcessOne (composedBlockSpec adapter) input)
  | .error failure@(.requestInvalid _) => .invalidBlock failure
  | .error failure => .unmodeled failure

theorem valid_adapter_admits_a_modeled_block (adapter : Adapter) (input : BlockInput)
    (h : validateAdapter adapter input = .ok ()) :
    ∃ run, runBlock adapter input = .modeled run := by
  refine ⟨runProcessOne (composedBlockSpec adapter) input, ?_⟩
  simp [runBlock, h]

/-! This is only a structural record update. It is not an admission boundary;
callers must use `runValidatedSingletonBranch` below. -/
private def structuralBranchSpec (base : BlockReference.BranchReference.BranchSpec)
    (adapter : Adapter) : BlockReference.BranchReference.BranchSpec :=
  { base with block := composedBlockSpec adapter }

inductive BranchCompositionRun where
  | modeled (run : BlockReference.BranchReference.BranchRun)
  | invalidBlock (failure : AdapterFailure)
  | unmodeled (failure : AdapterFailure)

inductive BranchCompositionStatus where
  | modeled
  | invalidBlock
  | unmodeled
  deriving DecidableEq, Repr

def BranchCompositionRun.status : BranchCompositionRun → BranchCompositionStatus
  | .modeled _ => .modeled
  | .invalidBlock _ => .invalidBlock
  | .unmodeled _ => .unmodeled

def BranchCompositionRun.failure : BranchCompositionRun → Option AdapterFailure
  | .modeled _ => none
  | .invalidBlock failure | .unmodeled failure => some failure

def BranchCompositionRun.branchOutcome : BranchCompositionRun →
    Option BlockReference.BranchReference.BranchOutcome
  | .modeled run => some run.outcome
  | .invalidBlock _ | .unmodeled _ => none

def BranchCompositionRun.logicalState : BranchCompositionRun → Option Nat
  | .modeled run => some run.logicalState
  | .invalidBlock _ | .unmodeled _ => none

def BranchCompositionRun.trace : BranchCompositionRun → Option (List Phase)
  | .modeled run => some run.trace
  | .invalidBlock _ | .unmodeled _ => none

def BranchCompositionRun.commitCounts : BranchCompositionRun → Option (Nat × Nat)
  | .modeled run => some (run.commitAttempts, run.commitCompletions)
  | .invalidBlock _ | .unmodeled _ => none

def runValidatedSingletonBranch
    (base : BlockReference.BranchReference.BranchSpec) (adapter : Adapter)
    (policy : BlockReference.BranchReference.RetryPolicy) (initialState : Nat)
    (inputs : List BlockInput) : BranchCompositionRun :=
  match inputs with
  | [input] =>
      match validateAdapter adapter input with
      | .error failure@(.requestInvalid _) => .invalidBlock failure
      | .error failure => .unmodeled failure
      | .ok () =>
          if initialState != input.initialState then
            .unmodeled .branchInitialState
          else
            .modeled (BlockReference.BranchReference.runBranch
              (structuralBranchSpec base adapter) policy initialState [input])
  | _ => .unmodeled .branchNotSingleton

private theorem composed_block_phase_order (adapter : Adapter) (input : BlockInput) :
    (processOneActions (composedBlockSpec adapter) input).map Prod.fst =
      Phase.processOne :=
  processOneActions_order _ _

private theorem composed_accepted_has_process_trace (adapter : Adapter) (input : BlockInput)
    (world : BlockWorld)
    (h : (runProcessOneFromWorld (composedBlockSpec adapter) input world).outcome =
      .accepted) :
    (runProcessOneFromWorld (composedBlockSpec adapter) input world).trace =
      Phase.processOne :=
  processOne_accepted_has_process_trace _ _ _ h

private theorem composed_beacon_installs_system_state (adapter : Adapter) (state : Nat) :
    (composedBlockSpec adapter).applyBeaconRoot state = adapter.beaconStateAfter := by
  rfl

private theorem composed_requests_install_system_state (adapter : Adapter)
    (state requests : Nat) :
    (composedBlockSpec adapter).applyExecutionRequests state requests =
      adapter.requestWorldAfter.token := by
  rfl

private theorem composed_beacon_preserves_block_gas_counters (adapter : Adapter)
    (input : BlockInput) (world after : BlockWorld)
    (h : beaconRootAndBlockSetup (composedBlockSpec adapter) input world = .ok after) :
    after.counters = world.counters := by
  simp only [beaconRootAndBlockSetup] at h
  split at h
  · cases h
    rfl
  · contradiction

private theorem composed_requests_preserve_block_gas_counters (adapter : Adapter)
    (input : BlockInput) (world after : BlockWorld)
    (h : executionRequests (composedBlockSpec adapter) input world = .ok after) :
    after.counters = world.counters := by
  simp only [executionRequests] at h
  split at h
  · cases h
    rfl
  · contradiction

private theorem composed_requests_are_visible_before_hash_installation (adapter : Adapter)
    (input : BlockInput) (world after : BlockWorld)
    (h : executionRequests (composedBlockSpec adapter) input world = .ok after) :
    after.artifacts.executionRequests = adapter.encodedRequests ∧
      after.artifacts.requestsHash = adapter.requestsHash ∧
      after.header.requestsHash = adapter.requestsHash ∧
      after.stateToken = adapter.requestWorldAfter.token ∧
      after.observations.controlTrace = world.observations.controlTrace ++
        [ .executionRequestsExtracted, .executionRequestsHashed ] := by
  simp only [executionRequests] at h
  split at h
  · cases h
    simp [composedBlockSpec]
  · contradiction

private theorem composed_stopped_block_restores_entry_world (adapter : Adapter)
    (input : BlockInput) (world : BlockWorld) (run : BlockRun)
    (hRun : run = runProcessOneFromWorld (composedBlockSpec adapter) input world)
    (hFailure : ∃ failure, run.outcome = .rejected failure) :
    run.world = world :=
  processOne_stopped_preserves_scope _ _ _ _ hRun hFailure

theorem invalid_adapter_never_runs_block (adapter : Adapter) (input : BlockInput)
    (failure : AdapterFailure) (h : validateAdapter adapter input = .error failure) :
    runBlock adapter input =
      match failure with
      | .requestInvalid _ => .invalidBlock failure
      | _ => .unmodeled failure := by
  cases failure <;> simp [runBlock, h]

theorem invalid_adapter_has_no_modeled_block (adapter : Adapter) (input : BlockInput)
    (failure : AdapterFailure) (h : validateAdapter adapter input = .error failure) :
    (runBlock adapter input).blockOutcome = none := by
  cases failure <;> simp [runBlock, h, CompositionRun.blockOutcome]

theorem admitted_accepted_block_has_process_trace (adapter : Adapter) (input : BlockInput)
    (hValid : validateAdapter adapter input = .ok ())
    (hAccepted : (runBlock adapter input).blockOutcome = some .accepted) :
    (runBlock adapter input).trace = some Phase.processOne := by
  have hRaw : (runProcessOne (composedBlockSpec adapter) input).outcome = .accepted := by
    simpa [runBlock, hValid, CompositionRun.blockOutcome] using hAccepted
  have hTrace := composed_accepted_has_process_trace adapter input
    (initialWorld input) hRaw
  simpa [runBlock, hValid, CompositionRun.trace, runProcessOne] using congrArg some hTrace

theorem admitted_rejected_block_restores_entry_world (adapter : Adapter)
    (input : BlockInput) (failure : Failure)
    (hValid : validateAdapter adapter input = .ok ())
    (hRejected : (runBlock adapter input).blockOutcome = some (.rejected failure)) :
    (runBlock adapter input).world = some (initialWorld input) := by
  have hRaw : (runProcessOne (composedBlockSpec adapter) input).outcome =
      .rejected failure := by
    simpa [runBlock, hValid, CompositionRun.blockOutcome] using hRejected
  have hWorld : (runProcessOne (composedBlockSpec adapter) input).world =
      initialWorld input := by
    apply processOne_stopped_preserves_scope
      (composedBlockSpec adapter) input (initialWorld input)
      (runProcessOne (composedBlockSpec adapter) input)
    · rfl
    · exact ⟨failure, hRaw⟩
  simpa [runBlock, hValid, CompositionRun.world] using congrArg some hWorld

theorem positive_value_creation_is_not_a_block_system_call
    (call : SystemCallInput)
    (hCreate : call.processorInput.tx.isContractCreation = true) :
    SystemTransactionReference.blockCallShapeValid call = false := by
  cases hSite : call.site <;>
    simp_all [SystemTransactionReference.blockCallShapeValid,
      SystemTransactionReference.Transaction.isContractCreation,
      SystemTransactionReference.BlockCallSite.destination]

theorem positive_value_is_not_a_block_system_call
    (call : SystemCallInput) (hValue : call.processorInput.tx.value != 0) :
    SystemTransactionReference.blockCallShapeValid call = false := by
  simp at hValue
  simp [SystemTransactionReference.blockCallShapeValid, hValue]

theorem valid_adapter_admits_singleton_branch
    (base : BlockReference.BranchReference.BranchSpec) (adapter : Adapter)
    (policy : BlockReference.BranchReference.RetryPolicy) (initialState : Nat)
    (input : BlockInput) (hValid : validateAdapter adapter input = .ok ())
    (hState : initialState = input.initialState) :
    ∃ run, runValidatedSingletonBranch base adapter policy initialState [input] =
      .modeled run := by
  refine ⟨BlockReference.BranchReference.runBranch
    (structuralBranchSpec base adapter) policy initialState [input], ?_⟩
  simp [runValidatedSingletonBranch, hValid, hState]

theorem invalid_singleton_adapter_never_runs_branch
    (base : BlockReference.BranchReference.BranchSpec) (adapter : Adapter)
    (policy : BlockReference.BranchReference.RetryPolicy) (initialState : Nat)
    (input : BlockInput) (failure : AdapterFailure)
    (hInvalid : validateAdapter adapter input = .error failure) :
    runValidatedSingletonBranch base adapter policy initialState [input] =
      match failure with
      | .requestInvalid _ => .invalidBlock failure
      | _ => .unmodeled failure := by
  cases failure <;> simp [runValidatedSingletonBranch, hInvalid]

theorem multiple_block_inputs_fail_closed
    (base : BlockReference.BranchReference.BranchSpec) (adapter : Adapter)
    (policy : BlockReference.BranchReference.RetryPolicy) (initialState : Nat)
    (first second : BlockInput) :
    runValidatedSingletonBranch base adapter policy initialState [first, second] =
      .unmodeled .branchNotSingleton := by
  rfl

theorem system_counter_exclusion_rule_is_installed
    (options : SystemTransactionReference.Options) (executionGas stateGas : Nat) :
    SystemTransactionReference.normalBlockCounterUpdate options.forSystemCore
      executionGas stateGas = (0, 0, 0) :=
  SystemTransactionReference.system_processor_never_charges_normal_block_counters
    options executionGas stateGas

end BlockSystemComposition
end Eip803x
