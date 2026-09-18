-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.TransactionGas

namespace Eip803x
namespace BlockReference

/-!
The standard-mainnet block relation mirrors the seventeen labels in
`standard-mainnet-processing-coverage.json`. `ProcessOne` owns the eleven
inner phases from DAO transition through processed-header validation. Phase
sixteen is deliberately owned by the outer branch relation,
after inclusion-list/prewarm hooks, because production invokes
`WorldState.CommitTree` in `BranchProcessor`, not in `BlockProcessor`; phase
two is the synchronous branch-selection/preparation boundary, phase three is
selected-block sender/authority recovery, and phase seventeen is synchronous
result classification/head finalization.

The logical state token is separate from tracing, execution-context, BAL,
receipt, root, request, and account-change observations. All of those
observations are explicit abstract contracts. This proves ordering, rollback,
and counter plumbing around the contracts; it does not prove their internal
DAO, beacon-root, blockhash, reward, withdrawal, request, trie, hash, or
persistence semantics.
-/

inductive Phase where
  | suggestedBlockValidation
  | synchronousBranchSelectionAndPreparation
  | senderAndAuthorityRecovery
  | openWorldStateScope
  | daoTransition
  | beaconRootSystemCall
  | historicalBlockhashStateChange
  | userTransactionFold
  | blobGasReceiptRootAndBloom
  | rewards
  | withdrawals
  | executionRequestsAndSystemCalls
  | storageAndStateRoots
  | blockAccessList
  | processedHeaderValidation
  | commitTree
  | synchronousResultClassificationAndHeadFinalization
  deriving DecidableEq, Repr

def Phase.all : List Phase :=
  [ .suggestedBlockValidation
  , .synchronousBranchSelectionAndPreparation
  , .senderAndAuthorityRecovery
  , .openWorldStateScope
  , .daoTransition
  , .beaconRootSystemCall
  , .historicalBlockhashStateChange
  , .userTransactionFold
  , .blobGasReceiptRootAndBloom
  , .rewards
  , .withdrawals
  , .executionRequestsAndSystemCalls
  , .storageAndStateRoots
  , .blockAccessList
  , .processedHeaderValidation
  , .commitTree
  , .synchronousResultClassificationAndHeadFinalization ]

def Phase.preprocess : List Phase :=
  [ .suggestedBlockValidation
  , .synchronousBranchSelectionAndPreparation
  , .senderAndAuthorityRecovery ]

def Phase.processOne : List Phase :=
  [ .daoTransition
  , .beaconRootSystemCall
  , .historicalBlockhashStateChange
  , .userTransactionFold
  , .blobGasReceiptRootAndBloom
  , .rewards
  , .withdrawals
  , .executionRequestsAndSystemCalls
  , .storageAndStateRoots
  , .blockAccessList
  , .processedHeaderValidation ]

theorem phaseCount : Phase.all.length = 17 := by native_decide

theorem phaseComposition : Phase.preprocess ++ [ .openWorldStateScope ] ++
    Phase.processOne ++ [ .commitTree, .synchronousResultClassificationAndHeadFinalization ] = Phase.all := by
  rfl

structure HeaderObservation where
  parentHash : Nat
  unclesHash : Nat
  author : Nat
  beneficiary : Nat
  difficulty : Nat
  number : Nat
  txRoot : Nat
  gasLimit : Nat
  gasUsed : Nat
  timestamp : Nat
  extraData : List Nat
  mixHash : Nat
  nonce : Nat
  totalDifficulty : Nat
  baseFeePerGas : Nat
  executionGasUsed : Nat
  stateGasUsed : Nat
  cumulativeReceiptGasUsed : Nat
  blobGasUsed : Nat
  excessBlobGas : Nat
  receiptsRoot : Nat
  bloom : Nat
  withdrawalsRoot : Nat
  parentBeaconBlockRoot : Nat
  requestsHash : Nat
  stateRoot : Nat
  blockHash : Nat
  isPostMerge : Bool
  slotNumber : Nat
  blockAccessListHash : Nat
  deriving DecidableEq, Repr

structure LogObservation where
  address : Nat
  topics : List Nat
  data : List Nat
  deriving DecidableEq, Repr

structure Receipt where
  id : Nat
  status : Nat
  cumulativeGasUsed : Nat
  gasUsed : Nat
  logs : List LogObservation
  logsBloom : Nat
  contractAddress : Nat
  deriving DecidableEq, Repr

structure BlockArtifacts where
  accountChanges : Nat
  executionRequests : Nat
  requestsHash : Nat
  generatedBlockAccessList : Nat
  generatedBlockAccessListHash : Nat
  encodedBlockAccessList : Nat
  deriving DecidableEq, Repr

inductive ReceiptBloomPath where
  | synchronous
  | background
  deriving DecidableEq, Repr

inductive ReceiptBloomStep where
  | started
  | computedReceiptBlooms
  | computedReceiptsRoot
  | installedReceiptsRoot
  | accumulatedBlockBloom
  | installedBloom
  | scheduledBackground
  | awaitedBackground
  deriving DecidableEq, Repr

inductive BlockHook where
  | worldStateScope
  | inclusionList
  | prewarm
  | commitState
  | commitTree
  | finalization
  | phase (phase : Phase)
  deriving DecidableEq, Repr

inductive ExceptionStage where
  | preparation
  | senderAuthorityRecovery
  | receiptBloomCalculation (path : ReceiptBloomPath)
  | receiptsRootCalculation (path : ReceiptBloomPath)
  | blockBloomAccumulation (path : ReceiptBloomPath)
  | blockHook (hook : BlockHook)
  | nonBalParallel (index : Nat)
  | preOpenedScope
  deriving DecidableEq, Repr

inductive HookResult (α : Type) where
  | completed (value : α)
  | escaped (stage : ExceptionStage)

inductive PendingReceiptBloom where
  | synchronousRoot (receiptsRoot : Nat)
  | backgroundPending (receipts : List Receipt) (receiptsRoot bloom : Nat)
      (steps : List ReceiptBloomStep)
  | backgroundReady (receipts : List Receipt) (receiptsRoot bloom : Nat)
      (steps : List ReceiptBloomStep)
  | backgroundFailure (receipts : List Receipt) (stage : ExceptionStage)
      (steps : List ReceiptBloomStep)
  deriving DecidableEq, Repr

inductive BackgroundReceiptWork where
  | failed (receipts : List Receipt) (stage : ExceptionStage)
      (steps : List ReceiptBloomStep)
  | completed (receipts : List Receipt) (receiptsRoot bloom : Nat)
      (steps : List ReceiptBloomStep)
  deriving DecidableEq, Repr

inductive BackgroundAwaitObservation where
  | nonePending
  | completed (steps : List ReceiptBloomStep)
  | failed (stage : ExceptionStage) (steps : List ReceiptBloomStep)
  deriving DecidableEq, Repr

inductive BlockControlEvent where
  | traceStarted
  | executionContextSet
  | balSetup
  | postTransactionCommit
  | receiptBloomStarted
  | receiptBloomsComputed
  | receiptsRootComputed
  | receiptsRootInstalled
  | blockBloomAccumulated
  | blockBloomInstalled
  | executionRequestsExtracted
  | executionRequestsHashed
  | traceEnded
  | balFinalized
  deriving DecidableEq, Repr

def emptyBlockArtifacts : BlockArtifacts :=
  { accountChanges := 0
    executionRequests := 0
    requestsHash := 0
    generatedBlockAccessList := 0
    generatedBlockAccessListHash := 0
    encodedBlockAccessList := 0 }

structure BlockControlObservation where
  traceStarted : Nat
  executionContext : Nat
  balSetup : Nat
  postTransactionCommit : Nat
  traceEnded : Nat
  balFinalized : Nat
  receiptBloomTrace : List ReceiptBloomStep
  taskReceiptBloomTrace : List ReceiptBloomStep
  journalTrace : List BlockHook
  controlTrace : List BlockControlEvent
  taskControlTrace : List BlockControlEvent
  deriving DecidableEq, Repr

def emptyBlockControlObservation : BlockControlObservation :=
  { traceStarted := 0
    executionContext := 0
    balSetup := 0
    postTransactionCommit := 0
    traceEnded := 0
    balFinalized := 0
    receiptBloomTrace := []
    taskReceiptBloomTrace := []
    journalTrace := []
    controlTrace := []
    taskControlTrace := [] }

structure ParallelEligibility where
  isGenesis : Bool
  balEnabled : Bool
  parallelRequested : Bool
  parentScopeAvailable : Bool
  forceSequential : Bool
  deriving DecidableEq, Repr

def ParallelEligibility.isEligible (eligibility : ParallelEligibility) : Bool :=
  !eligibility.isGenesis && eligibility.balEnabled && eligibility.parallelRequested &&
    eligibility.parentScopeAvailable && !eligibility.forceSequential

structure UserTransaction where
  id : Nat
  deriving DecidableEq, Repr

structure TransactionExecution where
  settlement : TransactionGas.Settlement
  nextState : Nat
  receipt : Receipt
  deriving DecidableEq, Repr

/-!
`phaseAccepted` represents validation and environmental checks at a phase
boundary. Branch-wide preprocessing is implemented by `BranchReference`:
the suggested-block check is evaluated only for the terminal selected block,
while sender/authority recovery is evaluated for every selected block.
-/
structure BlockInput where
  initialState : Nat
  transactions : List UserTransaction
  proposedHeader : HeaderObservation
  phaseAccepted : Phase → Bool
  phaseException : Phase → Option ExceptionStage
  parallelEligibility : ParallelEligibility
  receiptBloomPath : ReceiptBloomPath
  scopePreOpened : Bool
  baseBlock : Option Nat

structure CommitStateInput where
  stateToken : Nat
  counters : TransactionGas.BlockGas
  receipts : List Receipt
  executions : List TransactionExecution
  deriving DecidableEq, Repr

structure BlockSpec where
  executeTransaction : Nat → UserTransaction → Nat → Option TransactionExecution
  applyDao : Nat → Nat
  startBlockTrace : Nat → Nat
  setBlockExecutionContext : Nat → Nat
  setupBlockAccessList : Nat → Nat
  commitPostTransactionState : CommitStateInput → HookResult Nat
  applyBeaconRoot : Nat → Nat
  applyBlockhashState : Nat → Nat
  commitPreSystemState : Nat → Nat
  applyRewards : Nat → Nat
  applyWithdrawals : Nat → Nat
  commitWithdrawalState : Nat → Nat
  applyExecutionRequests : Nat → Nat → Nat
  endBlockTrace : Nat → Nat
  commitStorageRoots : Nat → Nat
  setBlockAccessList : Nat → Nat
  computeBlobGas : List UserTransaction → Nat
  computeReceiptBlooms : ReceiptBloomPath → List Receipt → HookResult (List Receipt)
  computeReceiptsRoot : ReceiptBloomPath → List Receipt → HookResult Nat
  accumulateBlockBloom : ReceiptBloomPath → List Receipt → HookResult Nat
  computeExecutionRequests : Nat → List Receipt → Nat
  computeRequestsHash : Nat → Nat
  computeAccountChanges : Nat → Nat
  computeGeneratedBlockAccessList : Nat → Nat
  computeGeneratedBlockAccessListHash : Nat → Nat
  computeEncodedBlockAccessList : Nat → Nat
  computeStateRoot : Nat → Nat
  computeHeaderHash : HeaderObservation → Nat
  validateProcessedHeader : BlockInput → HeaderObservation → List Receipt → BlockArtifacts → Bool

structure BlockWorld where
  stateToken : Nat
  counters : TransactionGas.BlockGas
  receipts : List Receipt
  executions : List TransactionExecution
  artifacts : BlockArtifacts
  observations : BlockControlObservation
  pendingReceiptBloom : Option PendingReceiptBloom
  header : HeaderObservation
  deriving DecidableEq, Repr

def cloneForProcessing (header : HeaderObservation) : HeaderObservation :=
  { header with
    bloom := 0
    stateRoot := 0
    gasUsed := 0 }

def initialWorld (input : BlockInput) : BlockWorld :=
  { stateToken := input.initialState
    counters := { executionGasUsed := 0, stateGasUsed := 0, cumulativeReceiptGasUsed := 0 }
    receipts := []
    executions := []
    artifacts := emptyBlockArtifacts
    observations := emptyBlockControlObservation
    pendingReceiptBloom := none
    header := cloneForProcessing input.proposedHeader }

theorem initialWorld_uses_processing_clone (input : BlockInput) :
    (initialWorld input).header = cloneForProcessing input.proposedHeader := by
  rfl

theorem cloneForProcessing_resets_only_execution_outputs (header : HeaderObservation) :
    cloneForProcessing header =
      { header with bloom := 0, stateRoot := 0, gasUsed := 0 } := by
  rfl

inductive Failure where
  | phaseRejected (phase : Phase)
  | transactionExecution (index : Nat)
  | processedHeaderValidation
  | retryableBalFailure (index : Nat)
  | parallelExecutionFailure (index : Nat)
  | escaped (stage : ExceptionStage)
  deriving DecidableEq, Repr

structure Machine where
  world : BlockWorld
  trace : List Phase
  deriving DecidableEq, Repr

inductive RunStep where
  | running (machine : Machine)
  | stopped (failure : Failure) (machine : Machine)
  deriving DecidableEq, Repr

inductive Outcome where
  | accepted
  | rejected (failure : Failure)
  | exception (stage : ExceptionStage)
  deriving DecidableEq, Repr

structure BlockRun where
  outcome : Outcome
  world : BlockWorld
  trace : List Phase
  finallyAwait : BackgroundAwaitObservation
  deriving DecidableEq, Repr

def invoke (phase : Phase) (step : BlockWorld → Except Failure BlockWorld)
    (machine : Machine) : RunStep :=
  let traced : Machine := { machine with trace := machine.trace ++ [phase] }
  match step machine.world with
  | .ok world => .running { traced with world := world }
  | .error failure => .stopped failure traced

def finish : RunStep → BlockRun
  | .running machine =>
      { outcome := .accepted
        world := machine.world
        trace := machine.trace
        finallyAwait := .nonePending }
  | .stopped failure machine =>
      { outcome := match failure with
          | .escaped stage => .exception stage
          | _ => .rejected failure
        world := machine.world
        trace := machine.trace
        finallyAwait := .nonePending }

def acceptedPhase (input : BlockInput) (phase : Phase) : Bool :=
  input.phaseAccepted phase

def checkedStateEffect (input : BlockInput) (phase : Phase) (effect : Nat → Nat)
    (world : BlockWorld) : Except Failure BlockWorld :=
  if acceptedPhase input phase then
    .ok { world with stateToken := effect world.stateToken }
  else
    .error (.phaseRejected phase)

def applyExecution (world : BlockWorld) (execution : TransactionExecution) : BlockWorld :=
  let counters := TransactionGas.applyUser world.counters execution.settlement
  let receipt := { execution.receipt with
    gasUsed := execution.settlement.paidGas
    cumulativeGasUsed := counters.cumulativeReceiptGasUsed }
  { world with
    stateToken := execution.nextState
    counters := counters
    receipts := world.receipts ++ [receipt]
    executions := world.executions ++ [execution] }

def receiptsFor (initialCumulativeGas : Nat) : List TransactionExecution → List Receipt
  | [] => []
  | execution :: rest =>
      let cumulative := initialCumulativeGas + execution.settlement.paidGas
      let receipt := { execution.receipt with
        gasUsed := execution.settlement.paidGas
        cumulativeGasUsed := cumulative }
      receipt :: receiptsFor cumulative rest

def postTransactionCommitObservation (spec : BlockSpec) (world : BlockWorld) :
    Except Failure BlockWorld :=
  match spec.commitPostTransactionState
      { stateToken := world.stateToken
        counters := world.counters
        receipts := world.receipts
        executions := world.executions } with
  | .completed observation =>
      .ok { world with observations := { world.observations with
          postTransactionCommit := observation
          journalTrace := world.observations.journalTrace ++ [ .commitState ]
          controlTrace := world.observations.controlTrace ++
            [ .postTransactionCommit ] } }
  | .escaped stage => .error (.escaped stage)

theorem applyExecution_receipt_matches_counters (world : BlockWorld)
    (execution : TransactionExecution) :
    (applyExecution world execution).receipts =
      world.receipts ++ [{ execution.receipt with
        gasUsed := execution.settlement.paidGas
        cumulativeGasUsed :=
          (applyExecution world execution).counters.cumulativeReceiptGasUsed }] := by
  simp [applyExecution, TransactionGas.applyUser]

theorem applyExecutions_receipts_match_counters (world : BlockWorld)
    (executions : List TransactionExecution) :
    (executions.foldl (fun current execution => applyExecution current execution) world).receipts =
      world.receipts ++ receiptsFor world.counters.cumulativeReceiptGasUsed executions := by
  induction executions generalizing world with
  | nil => simp [receiptsFor]
  | cons execution rest inductionHypothesis =>
      simp only [List.foldl]
      rw [inductionHypothesis (applyExecution world execution)]
      simp [applyExecution, receiptsFor, TransactionGas.applyUser, List.append_assoc]

theorem postTransactionCommitObservation_preserves_state
    (spec : BlockSpec) (world after : BlockWorld)
    (h : postTransactionCommitObservation spec world = .ok after) :
    after.stateToken = world.stateToken := by
  cases hCommit : spec.commitPostTransactionState
      { stateToken := world.stateToken
        counters := world.counters
        receipts := world.receipts
        executions := world.executions } with
  | escaped stage => simp [postTransactionCommitObservation, hCommit] at h
  | completed observation =>
      simp [postTransactionCommitObservation, hCommit] at h
      cases h
      rfl

theorem postTransactionCommitObservation_records_journal_boundary
    (spec : BlockSpec) (world after : BlockWorld)
    (h : postTransactionCommitObservation spec world = .ok after) :
    after.observations.journalTrace =
      world.observations.journalTrace ++ [ .commitState ] := by
  cases hCommit : spec.commitPostTransactionState
      { stateToken := world.stateToken
        counters := world.counters
        receipts := world.receipts
        executions := world.executions } with
  | escaped stage => simp [postTransactionCommitObservation, hCommit] at h
  | completed observation =>
      simp [postTransactionCommitObservation, hCommit] at h
      cases h
      rfl

/-! These helpers make non-state observations explicit. Their logical-state
preservation is definitional, so later ProcessOne steps cannot accidentally
charge tracing/context/BAL setup or finalization to the logical state token. -/
def startBlockObservations (spec : BlockSpec) (world : BlockWorld) : BlockWorld :=
  { world with
    observations := { world.observations with
      traceStarted := spec.startBlockTrace world.observations.traceStarted
      executionContext := spec.setBlockExecutionContext world.observations.executionContext
      balSetup := spec.setupBlockAccessList world.observations.balSetup
      controlTrace := world.observations.controlTrace ++
        [ .traceStarted, .executionContextSet, .balSetup ] } }

def endBlockTraceObservation (spec : BlockSpec) (world : BlockWorld) : BlockWorld :=
  { world with
    observations := { world.observations with
      traceEnded := spec.endBlockTrace world.observations.traceEnded
      controlTrace := world.observations.controlTrace ++ [ .traceEnded ] } }

def finalizeBlockAccessListObservation (spec : BlockSpec) (world : BlockWorld) : BlockWorld :=
  { world with
    observations := { world.observations with
      balFinalized := spec.setBlockAccessList world.observations.balFinalized
      controlTrace := world.observations.controlTrace ++ [ .balFinalized ] } }

theorem startBlockObservations_preserves_state (spec : BlockSpec) (world : BlockWorld) :
    (startBlockObservations spec world).stateToken = world.stateToken := rfl

theorem endBlockTraceObservation_preserves_state (spec : BlockSpec) (world : BlockWorld) :
    (endBlockTraceObservation spec world).stateToken = world.stateToken := rfl

theorem finalizeBlockAccessListObservation_preserves_state (spec : BlockSpec) (world : BlockWorld) :
    (finalizeBlockAccessListObservation spec world).stateToken = world.stateToken := rfl

/-! A fold is online: each successful transaction updates the working scope.
On any failure the entire current block scope is returned as the baseline,
rather than exposing a partially updated state. -/
inductive FoldOutcome where
  | success (world : BlockWorld)
  | failure (failure : Failure) (restored : BlockWorld)
  deriving DecidableEq, Repr

def foldUserTransactions (spec : BlockSpec) : Nat → List UserTransaction →
    BlockWorld → BlockWorld → FoldOutcome
  | _, [], _, world => .success world
  | index, transaction :: rest, baseline, world =>
      match spec.executeTransaction index transaction world.stateToken with
      | none => .failure (.transactionExecution index) baseline
      | some execution =>
          foldUserTransactions spec (index + 1) rest baseline (applyExecution world execution)

def userTransactionFold (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .userTransactionFold then
    match foldUserTransactions spec 0 input.transactions world world with
    | .success after => .ok after
    | .failure failure _restored => .error failure
  else
    .error (.phaseRejected .userTransactionFold)

theorem foldUserTransactions_failure_restores_scope (spec : BlockSpec)
    (index : Nat) (transactions : List UserTransaction) (baseline world restored : BlockWorld)
    (failure : Failure)
    (h : foldUserTransactions spec index transactions baseline world =
      .failure failure restored) :
    restored = baseline := by
  induction transactions generalizing index baseline world restored with
  | nil => simp [foldUserTransactions] at h
  | cons transaction rest inductionHypothesis =>
      by_cases hNone : spec.executeTransaction index transaction world.stateToken = none
      · simp [foldUserTransactions, hNone] at h
        exact h.2.symm
      · cases hExecution : spec.executeTransaction index transaction world.stateToken with
        | none => exact False.elim (hNone hExecution)
        | some execution =>
            simp [foldUserTransactions, hExecution] at h
            exact inductionHypothesis (index + 1) baseline
              (applyExecution world execution) restored h

def beaconRootAndBlockSetup (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .beaconRootSystemCall then
    let observed := startBlockObservations spec world
    .ok { observed with stateToken := spec.applyBeaconRoot observed.stateToken }
  else
    .error (.phaseRejected .beaconRootSystemCall)

def receiptControlEvents : ReceiptBloomStep → List BlockControlEvent
  | .computedReceiptBlooms => [ .receiptBloomsComputed ]
  | .computedReceiptsRoot => [ .receiptsRootComputed ]
  | .installedReceiptsRoot => [ .receiptsRootInstalled ]
  | .accumulatedBlockBloom => [ .blockBloomAccumulated ]
  | .installedBloom => [ .blockBloomInstalled ]
  | .started | .scheduledBackground | .awaitedBackground => []

def receiptControlEventsFor (steps : List ReceiptBloomStep) : List BlockControlEvent :=
  steps.flatMap receiptControlEvents

def receiptLogCount (receipts : List Receipt) : Nat :=
  (receipts.flatMap (fun receipt => receipt.logs)).length

def backgroundReceiptWorkEligible (receipts : List Receipt) : Bool :=
  decide (16 ≤ receipts.length ∨ 64 ≤ receiptLogCount receipts)

/-! The background task follows `BlockProcessor.ProcessBlock`: it computes each
receipt bloom, accumulates the block bloom, and then computes the receipts
root. The task carries its updated receipts explicitly; its local trace is
kept separate from the main-thread observation trace. -/
def backgroundReceiptWork (spec : BlockSpec) (receipts : List Receipt) :
    BackgroundReceiptWork :=
  match spec.computeReceiptBlooms .background receipts with
  | .escaped stage => .failed receipts stage []
  | .completed updatedReceipts =>
      match spec.accumulateBlockBloom .background updatedReceipts with
      | .escaped stage => .failed updatedReceipts stage [ .computedReceiptBlooms ]
      | .completed bloom =>
          match spec.computeReceiptsRoot .background updatedReceipts with
          | .escaped stage =>
              .failed updatedReceipts stage [ .computedReceiptBlooms, .accumulatedBlockBloom ]
          | .completed receiptsRoot =>
              .completed updatedReceipts receiptsRoot bloom
                [ .computedReceiptBlooms, .accumulatedBlockBloom, .computedReceiptsRoot ]

theorem backgroundReceiptWork_completed_uses_computed_receipts
    (spec : BlockSpec) (receipts updatedReceipts : List Receipt)
    (receiptsRoot bloom : Nat)
    (hCompute : spec.computeReceiptBlooms .background receipts =
      .completed updatedReceipts)
    (hBloom : spec.accumulateBlockBloom .background updatedReceipts = .completed bloom)
    (hRoot : spec.computeReceiptsRoot .background updatedReceipts = .completed receiptsRoot) :
    backgroundReceiptWork spec receipts =
      .completed updatedReceipts receiptsRoot bloom
        [ .computedReceiptBlooms, .accumulatedBlockBloom, .computedReceiptsRoot ] := by
  simp [backgroundReceiptWork, hCompute, hBloom, hRoot]

def synchronousReceiptObservations (spec : BlockSpec) (started : BlockWorld) :
    Except Failure BlockWorld :=
  match spec.computeReceiptBlooms .synchronous started.receipts with
  | .escaped stage => .error (.escaped stage)
  | .completed updatedReceipts =>
      let withBlooms := { started with receipts := updatedReceipts }
      match spec.computeReceiptsRoot .synchronous withBlooms.receipts with
      | .escaped stage => .error (.escaped stage)
      | .completed receiptsRoot =>
          .ok { withBlooms with
            pendingReceiptBloom := some (.synchronousRoot receiptsRoot)
            header := { withBlooms.header with receiptsRoot := receiptsRoot }
            observations := { withBlooms.observations with
              receiptBloomTrace := withBlooms.observations.receiptBloomTrace ++
                [ ReceiptBloomStep.computedReceiptBlooms,
                  ReceiptBloomStep.computedReceiptsRoot,
                  ReceiptBloomStep.installedReceiptsRoot ]
              controlTrace := withBlooms.observations.controlTrace ++
                [ BlockControlEvent.receiptBloomsComputed,
                  BlockControlEvent.receiptsRootComputed,
                  BlockControlEvent.receiptsRootInstalled ] } }

theorem synchronousReceiptObservations_records_computed_receipts
    (spec : BlockSpec) (started after : BlockWorld)
    (updatedReceipts : List Receipt) (receiptsRoot : Nat)
    (hCompute : spec.computeReceiptBlooms .synchronous started.receipts =
      .completed updatedReceipts)
    (hRoot : spec.computeReceiptsRoot .synchronous updatedReceipts =
      .completed receiptsRoot)
    (hAfter : synchronousReceiptObservations spec started = .ok after) :
    after.receipts = updatedReceipts ∧ after.header.receiptsRoot = receiptsRoot := by
  simp [synchronousReceiptObservations, hCompute, hRoot] at hAfter
  cases hAfter
  exact ⟨rfl, rfl⟩

def blobAndReceiptObservations (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .blobGasReceiptRootAndBloom then
    match postTransactionCommitObservation spec world with
    | .error failure => .error failure
    | .ok committed =>
        let executionGas := committed.counters.executionGasUsed
        let stateGas := committed.counters.stateGasUsed
        let receiptGas := committed.counters.cumulativeReceiptGasUsed
        let blobGas := spec.computeBlobGas input.transactions
        let headerWithGas :=
          { committed.header with
            gasUsed := TransactionGas.headerGasUsed committed.counters
            executionGasUsed := executionGas
            stateGasUsed := stateGas
            cumulativeReceiptGasUsed := receiptGas
            blobGasUsed := blobGas }
        let started :=
          { committed with
            header := headerWithGas
            observations := { committed.observations with
              receiptBloomTrace := committed.observations.receiptBloomTrace ++
                [ ReceiptBloomStep.started ]
              controlTrace := committed.observations.controlTrace ++
                [ BlockControlEvent.receiptBloomStarted ] } }
        match input.receiptBloomPath with
        | .synchronous => synchronousReceiptObservations spec started
        | .background =>
            if backgroundReceiptWorkEligible started.receipts = true then
              match backgroundReceiptWork spec started.receipts with
              | .failed updatedReceipts stage steps =>
                  .ok { started with
                    pendingReceiptBloom := some (.backgroundFailure updatedReceipts stage steps)
                    observations := { started.observations with
                      receiptBloomTrace := started.observations.receiptBloomTrace ++
                        [ ReceiptBloomStep.scheduledBackground ]
                      taskReceiptBloomTrace := started.observations.taskReceiptBloomTrace ++ steps
                      taskControlTrace := started.observations.taskControlTrace ++
                        receiptControlEventsFor steps } }
              | .completed updatedReceipts receiptsRoot bloom steps =>
                  .ok { started with
                    pendingReceiptBloom :=
                      some (.backgroundPending updatedReceipts receiptsRoot bloom steps)
                    observations := { started.observations with
                      receiptBloomTrace := started.observations.receiptBloomTrace ++
                        [ ReceiptBloomStep.scheduledBackground ]
                      taskReceiptBloomTrace := started.observations.taskReceiptBloomTrace ++ steps
                      taskControlTrace := started.observations.taskControlTrace ++
                        receiptControlEventsFor steps } }
            else
              synchronousReceiptObservations spec started
  else
    .error (.phaseRejected .blobGasReceiptRootAndBloom)

theorem backgroundReceiptObservations_keep_main_receipts_until_await
    (spec : BlockSpec) (input : BlockInput) (world committed after : BlockWorld)
    (updatedReceipts : List Receipt) (receiptsRoot bloom : Nat)
    (steps : List ReceiptBloomStep)
    (hAccepted : acceptedPhase input .blobGasReceiptRootAndBloom = true)
    (hPath : input.receiptBloomPath = .background)
    (hCommit : postTransactionCommitObservation spec world = .ok committed)
    (hEligible : backgroundReceiptWorkEligible committed.receipts = true)
    (hWork : backgroundReceiptWork spec committed.receipts =
      .completed updatedReceipts receiptsRoot bloom steps)
    (hAfter : blobAndReceiptObservations spec input world = .ok after) :
    after.receipts = committed.receipts ∧
      after.pendingReceiptBloom =
        some (.backgroundPending updatedReceipts receiptsRoot bloom steps) := by
  simp [blobAndReceiptObservations, hAccepted, hPath, hCommit, hEligible, hWork] at hAfter
  cases hAfter
  exact ⟨rfl, rfl⟩

def endBlockTraceAndBloom (spec : BlockSpec) (_input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  let traced := endBlockTraceObservation spec world
  match traced.pendingReceiptBloom with
  | some (.synchronousRoot _receiptsRoot) =>
      if traced.receipts.isEmpty then
        .ok { traced with pendingReceiptBloom := none }
      else
        match spec.accumulateBlockBloom .synchronous traced.receipts with
        | .escaped stage => .error (.escaped stage)
        | .completed bloom =>
            .ok { traced with
              pendingReceiptBloom := none
              header := { traced.header with bloom := bloom }
              observations := { traced.observations with
                receiptBloomTrace := traced.observations.receiptBloomTrace ++
                  [ .accumulatedBlockBloom, .installedBloom ]
                controlTrace := traced.observations.controlTrace ++
                  [ .blockBloomAccumulated, .blockBloomInstalled ] } }
  | some (.backgroundPending _ _ _ _) => .ok traced
  | some (.backgroundFailure _ _ _) => .ok traced
  | some (.backgroundReady _ _ _ _) => .ok traced
  | _ => .ok traced

theorem synchronousBlockBloom_uses_computed_receipts
    (spec : BlockSpec) (input : BlockInput) (world after : BlockWorld)
    (updatedReceipts : List Receipt) (receiptsRoot bloom : Nat)
    (hReceipts : world.receipts = updatedReceipts)
    (hPending : world.pendingReceiptBloom = some (.synchronousRoot receiptsRoot))
    (hNonempty : updatedReceipts ≠ [])
    (hBloom : spec.accumulateBlockBloom .synchronous updatedReceipts = .completed bloom)
    (hAfter : endBlockTraceAndBloom spec input world = .ok after) :
    after.receipts = updatedReceipts ∧ after.header.bloom = bloom := by
  cases updatedReceipts with
  | nil => exact (hNonempty rfl).elim
  | cons head tail =>
      have hTracedReceipts : (endBlockTraceObservation spec world).receipts = head :: tail := by
        simp [endBlockTraceObservation, hReceipts]
      have hTracedPending :
          (endBlockTraceObservation spec world).pendingReceiptBloom =
            some (.synchronousRoot receiptsRoot) := by
        simp [endBlockTraceObservation, hPending]
      dsimp [endBlockTraceAndBloom] at hAfter
      rw [hTracedPending, hTracedReceipts, hBloom] at hAfter
      simp only [List.isEmpty, Bool.false_eq_true, ↓reduceIte] at hAfter
      cases hAfter
      exact ⟨rfl, rfl⟩

def awaitReceiptBloom (world : BlockWorld) : Except Failure BlockWorld :=
  match world.pendingReceiptBloom with
  | none => .ok world
  | some (.synchronousRoot _) => .ok world
  | some (.backgroundPending receipts receiptsRoot bloom _) =>
      .ok { world with
        receipts := receipts
        pendingReceiptBloom := none
        header := { world.header with
          receiptsRoot := receiptsRoot
          bloom := bloom }
        observations := { world.observations with
          receiptBloomTrace := world.observations.receiptBloomTrace ++
            [ .awaitedBackground, .installedBloom, .installedReceiptsRoot ]
          controlTrace := world.observations.controlTrace ++
            [ .blockBloomInstalled, .receiptsRootInstalled ] } }
  | some (.backgroundFailure _receipts stage _) => .error (.escaped stage)
  | some (.backgroundReady receipts receiptsRoot bloom _) =>
      .ok { world with
        receipts := receipts
        pendingReceiptBloom := none
        header := { world.header with
          receiptsRoot := receiptsRoot
          bloom := bloom }
        observations := { world.observations with
          receiptBloomTrace := world.observations.receiptBloomTrace ++
            [ .awaitedBackground, .installedBloom, .installedReceiptsRoot ]
          controlTrace := world.observations.controlTrace ++
            [ .blockBloomInstalled, .receiptsRootInstalled ] } }

def withdrawals (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .withdrawals then
    let state := spec.commitWithdrawalState (spec.applyWithdrawals world.stateToken)
    .ok { world with
      stateToken := state }
  else
    .error (.phaseRejected .withdrawals)

def executionRequests (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .executionRequestsAndSystemCalls then
    let extracted := { world with observations := { world.observations with
      controlTrace := world.observations.controlTrace ++
        [ BlockControlEvent.executionRequestsExtracted ] } }
    let requests := spec.computeExecutionRequests extracted.stateToken extracted.receipts
    let state := spec.applyExecutionRequests extracted.stateToken requests
    let requestsHash := spec.computeRequestsHash requests
    .ok { extracted with
      stateToken := state
      artifacts := { extracted.artifacts with
        executionRequests := requests
        requestsHash := requestsHash }
      observations := { extracted.observations with
        controlTrace := extracted.observations.controlTrace ++
          [ BlockControlEvent.executionRequestsHashed ] }
      header := { extracted.header with requestsHash := requestsHash } }
  else
    .error (.phaseRejected .executionRequestsAndSystemCalls)

def storageAndRoots (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .storageAndStateRoots then
    match endBlockTraceAndBloom spec input world with
    | .error failure => .error failure
    | .ok observed =>
        let state := spec.commitStorageRoots observed.stateToken
        .ok { observed with
          stateToken := state
          artifacts := { observed.artifacts with accountChanges := spec.computeAccountChanges state }
          header := { observed.header with stateRoot := spec.computeStateRoot state } }
  else
    .error (.phaseRejected .storageAndStateRoots)

def blockAccessList (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .blockAccessList then
    match awaitReceiptBloom world with
    | .error failure => .error failure
    | .ok ready =>
        let generated := spec.computeGeneratedBlockAccessList ready.stateToken
        let generatedHash := spec.computeGeneratedBlockAccessListHash generated
        let encoded := spec.computeEncodedBlockAccessList generated
        let observed := finalizeBlockAccessListObservation spec ready
        .ok { observed with artifacts := { observed.artifacts with
          generatedBlockAccessList := generated
          generatedBlockAccessListHash := generatedHash
          encodedBlockAccessList := encoded } }
  else
    .error (.phaseRejected .blockAccessList)

def processedHeader (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) :
    Except Failure BlockWorld :=
  if acceptedPhase input .processedHeaderValidation then
    let header := { world.header with blockHash := spec.computeHeaderHash world.header }
    if spec.validateProcessedHeader input header world.receipts world.artifacts then
      .ok { world with header := header }
    else
      .error .processedHeaderValidation
  else
    .error (.phaseRejected .processedHeaderValidation)

def phaseAction (input : BlockInput) (phase : Phase)
    (action : BlockWorld → Except Failure BlockWorld) :
    BlockWorld → Except Failure BlockWorld :=
  fun world =>
    match input.phaseException phase with
    | some stage => .error (.escaped stage)
    | none => action world

def processOneActions (spec : BlockSpec) (input : BlockInput) :
    List (Phase × (BlockWorld → Except Failure BlockWorld)) :=
  [ ( .daoTransition
    , phaseAction input .daoTransition
        (checkedStateEffect input .daoTransition spec.applyDao) )
  , ( .beaconRootSystemCall
    , phaseAction input .beaconRootSystemCall
        (beaconRootAndBlockSetup spec input) )
  , ( .historicalBlockhashStateChange
    , phaseAction input .historicalBlockhashStateChange
        (checkedStateEffect input .historicalBlockhashStateChange
          (fun state => spec.commitPreSystemState (spec.applyBlockhashState state))) )
  , ( .userTransactionFold
    , phaseAction input .userTransactionFold
        (userTransactionFold spec input) )
  , ( .blobGasReceiptRootAndBloom
    , phaseAction input .blobGasReceiptRootAndBloom
        (blobAndReceiptObservations spec input) )
  , ( .rewards
    , phaseAction input .rewards
        (checkedStateEffect input .rewards spec.applyRewards) )
  , ( .withdrawals
    , phaseAction input .withdrawals
        (withdrawals spec input) )
  , ( .executionRequestsAndSystemCalls
    , phaseAction input .executionRequestsAndSystemCalls
        (executionRequests spec input) )
  , ( .storageAndStateRoots
    , phaseAction input .storageAndStateRoots
        (storageAndRoots spec input) )
  , ( .blockAccessList
    , phaseAction input .blockAccessList
        (blockAccessList spec input) )
  , ( .processedHeaderValidation
    , phaseAction input .processedHeaderValidation
        (processedHeader spec input) ) ]

def runPhases : List (Phase × (BlockWorld → Except Failure BlockWorld)) → RunStep → RunStep
  | [], step => step
  | _ :: _, .stopped failure machine => .stopped failure machine
  | (phase, action) :: rest, .running machine =>
      runPhases rest (invoke phase action machine)

theorem invoke_running_trace (phase : Phase)
    (action : BlockWorld → Except Failure BlockWorld) (machine next : Machine)
    (h : invoke phase action machine = .running next) :
    next.trace = machine.trace ++ [ phase ] := by
  unfold invoke at h
  split at h
  · next world =>
      cases h
      rfl
  · cases h

theorem runPhases_running_trace
    (actions : List (Phase × (BlockWorld → Except Failure BlockWorld)))
    (machine final : Machine)
    (h : runPhases actions (.running machine) = .running final) :
    final.trace = machine.trace ++ actions.map Prod.fst := by
  induction actions generalizing machine final with
  | nil =>
      simp only [runPhases] at h
      cases h
      simp
  | cons action rest inductionHypothesis =>
      rcases action with ⟨phase, step⟩
      cases hStep : invoke phase step machine with
      | stopped failure stoppedMachine =>
          have hStopped : runPhases rest (.stopped failure stoppedMachine) =
              .stopped failure stoppedMachine := by
            cases rest <;> rfl
          have hImpossible :
              (.stopped failure stoppedMachine : RunStep) = .running final := by
            rw [← hStopped]
            simpa [runPhases, hStep] using h
          cases hImpossible
      | running nextMachine =>
          have hRest : runPhases rest (.running nextMachine) = .running final := by
            simpa [runPhases, hStep] using h
          have hTrace := inductionHypothesis nextMachine final hRest
          have hInvokeTrace := invoke_running_trace phase step machine nextMachine hStep
          rw [hTrace, hInvokeTrace]
          simp [List.append_assoc]

def restoreProcessOneFailure (baseline : BlockWorld) (run : BlockRun) : BlockRun :=
  match run.outcome with
  | .accepted => run
  | .rejected _failure => { run with world := baseline }
  | .exception _stage => { run with world := baseline }

def observeBackgroundAwait (input : BlockInput) (run : BlockRun) :
    BackgroundAwaitObservation :=
  if input.receiptBloomPath = .background then
    if ReceiptBloomStep.awaitedBackground ∈ run.world.observations.receiptBloomTrace then
      .completed run.world.observations.taskReceiptBloomTrace
    else
      match run.world.pendingReceiptBloom with
      | none => .nonePending
      | some (.backgroundPending _ _ _ steps) => .completed steps
      | some (.backgroundReady _ _ _ steps) => .completed steps
      | some (.backgroundFailure _ stage steps) => .failed stage steps
      | some (.synchronousRoot _) => .nonePending
  else
    .nonePending

def runProcessOneFromWorld (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) : BlockRun :=
  let raw := finish (runPhases (processOneActions spec input)
    (.running { world := world, trace := [] }))
  let restored := restoreProcessOneFailure world raw
  { restored with finallyAwait := observeBackgroundAwait input raw }

def runProcessOne (spec : BlockSpec) (input : BlockInput) : BlockRun :=
  runProcessOneFromWorld spec input (initialWorld input)

theorem processOneActions_order (spec : BlockSpec) (input : BlockInput) :
    (processOneActions spec input).map Prod.fst = Phase.processOne := by
  rfl

theorem processOne_accepted_has_process_trace (spec : BlockSpec) (input : BlockInput)
    (world : BlockWorld)
    (h : (runProcessOneFromWorld spec input world).outcome = .accepted) :
    (runProcessOneFromWorld spec input world).trace = Phase.processOne := by
  let raw := finish (runPhases (processOneActions spec input)
    (.running { world := world, trace := [] }))
  change (restoreProcessOneFailure world raw).outcome = .accepted at h
  change (restoreProcessOneFailure world raw).trace = Phase.processOne
  cases hOutcome : raw.outcome with
  | accepted =>
      have hTrace : raw.trace = Phase.processOne := by
        unfold raw finish
        cases hStep : runPhases (processOneActions spec input)
            (.running { world := world, trace := [] }) with
        | stopped failure machine =>
            cases failure <;> simp_all [raw, finish]
        | running machine =>
            have hRunning : runPhases (processOneActions spec input)
                (.running { world := world, trace := [] }) = .running machine := hStep
            have hPhases := runPhases_running_trace
              (processOneActions spec input) { world := world, trace := [] } machine hRunning
            simpa [processOneActions_order spec input] using hPhases
      simpa [restoreProcessOneFailure, hOutcome] using hTrace
  | rejected failure =>
      simp [restoreProcessOneFailure, hOutcome] at h
  | exception stage =>
      simp [restoreProcessOneFailure, hOutcome] at h

theorem processOne_stopped_preserves_scope (spec : BlockSpec) (input : BlockInput)
    (world : BlockWorld) (run : BlockRun)
    (hRun : run = runProcessOneFromWorld spec input world)
    (hFailure : ∃ failure, run.outcome = .rejected failure) :
    run.world = world := by
  subst run
  rcases hFailure with ⟨failure, hFailure⟩
  let raw := finish (runPhases (processOneActions spec input)
    (.running { world := world, trace := [] }))
  have hRestored : (restoreProcessOneFailure world raw).outcome = .rejected failure := by
    simpa [runProcessOneFromWorld, raw] using hFailure
  have hWorld : (restoreProcessOneFailure world raw).world = world := by
    cases hRaw : raw.outcome with
    | accepted =>
        simp [restoreProcessOneFailure, hRaw] at hRestored
    | rejected rejectedFailure =>
        simp [restoreProcessOneFailure, hRaw]
    | exception stage =>
        simp [restoreProcessOneFailure, hRaw] at hRestored
  simp [runProcessOneFromWorld, raw, hWorld]

theorem foldl_add_zero (head : Nat) (rest : List TransactionExecution)
    (value : TransactionExecution → Nat) :
    rest.foldl (fun total execution => total + value execution) head =
      head + rest.foldl (fun total execution => total + value execution) 0 := by
  induction rest generalizing head with
  | nil => simp
  | cons execution rest inductionHypothesis =>
      simp only [List.foldl, Nat.zero_add]
      rw [inductionHypothesis (head + value execution)]
      rw [inductionHypothesis (value execution)]
      simp [Nat.add_assoc]

theorem applyExecutions_execution_counter (world : BlockWorld)
    (executions : List TransactionExecution) :
    (executions.foldl (fun current execution => applyExecution current execution) world).counters.executionGasUsed =
      world.counters.executionGasUsed +
        executions.foldl (fun total execution => total + execution.settlement.executionGas) 0 := by
  induction executions generalizing world with
  | nil => simp
  | cons execution rest inductionHypothesis =>
      simp only [List.foldl]
      rw [inductionHypothesis]
      simp [applyExecution, TransactionGas.applyUser]
      rw [foldl_add_zero execution.settlement.executionGas rest
        (fun item => item.settlement.executionGas)]
      simp [Nat.add_assoc]

theorem applyExecutions_state_counter (world : BlockWorld)
    (executions : List TransactionExecution) :
    (executions.foldl (fun current execution => applyExecution current execution) world).counters.stateGasUsed =
      world.counters.stateGasUsed +
        executions.foldl (fun total execution => total + execution.settlement.stateGas) 0 := by
  induction executions generalizing world with
  | nil => simp
  | cons execution rest inductionHypothesis =>
      simp only [List.foldl]
      rw [inductionHypothesis]
      simp [applyExecution, TransactionGas.applyUser]
      rw [foldl_add_zero execution.settlement.stateGas rest
        (fun item => item.settlement.stateGas)]
      simp [Nat.add_assoc]

theorem applyExecutions_receipt_counter (world : BlockWorld)
    (executions : List TransactionExecution) :
    (executions.foldl (fun current execution => applyExecution current execution) world).counters.cumulativeReceiptGasUsed =
      world.counters.cumulativeReceiptGasUsed +
        executions.foldl (fun total execution => total + execution.settlement.paidGas) 0 := by
  induction executions generalizing world with
  | nil => simp
  | cons execution rest inductionHypothesis =>
      simp only [List.foldl]
      rw [inductionHypothesis]
      simp [applyExecution, TransactionGas.applyUser]
      rw [foldl_add_zero execution.settlement.paidGas rest
        (fun item => item.settlement.paidGas)]
      simp [Nat.add_assoc]

theorem applyExecutions_header_max (world : BlockWorld)
    (executions : List TransactionExecution) :
    TransactionGas.headerGasUsed
        (executions.foldl (fun current execution => applyExecution current execution) world).counters =
      max
        (world.counters.executionGasUsed +
          executions.foldl (fun total execution => total + execution.settlement.executionGas) 0)
        (world.counters.stateGasUsed +
          executions.foldl (fun total execution => total + execution.settlement.stateGas) 0) := by
  unfold TransactionGas.headerGasUsed
  rw [applyExecutions_execution_counter, applyExecutions_state_counter]

theorem system_effect_preserves_counters (input : BlockInput) (phase : Phase)
    (effect : Nat → Nat) (world after : BlockWorld)
    (h : checkedStateEffect input phase effect world = .ok after) :
    after.counters = world.counters := by
  by_cases hAccepted : acceptedPhase input phase = true
  · simp [checkedStateEffect, hAccepted] at h
    cases h
    rfl
  · simp [checkedStateEffect, hAccepted] at h

end BlockReference
end Eip803x
