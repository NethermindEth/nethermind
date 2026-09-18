-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.BlockReference

namespace Eip803x
namespace BlockReference
namespace Vectors

private def allPhasesAccepted : Phase → Bool := fun _ => true

private def rejectingAt (rejected : Phase) : Phase → Bool :=
  fun phase => phase != rejected

private def sequentialEligibility : ParallelEligibility :=
  { isGenesis := false
    balEnabled := false
    parallelRequested := false
    parentScopeAvailable := false
    forceSequential := true }

private def executionFor (index : Nat) (transaction : UserTransaction) (state : Nat) :
    Option TransactionExecution :=
  some
    { settlement :=
        { gasUsedBeforeRefund := 3 + index
          gasRefund := 0
          gasUsedAfterRefund := 3 + index
          paidGas := 2 + index
          stateGas := 1 + index
          executionGas := 2 + index }
      nextState := state + transaction.id + 1
      receipt :=
        { id := transaction.id
          status := 1
          cumulativeGasUsed := 0
          gasUsed := 999
          logs :=
            if transaction.id = 2 then
              [ { address := 77, topics := [ 88 ], data := [ 99 ] } ]
            else
              []
          logsBloom := 0
          contractAddress := 0 } }

private def updateReceiptBloom (receipt : Receipt) : Receipt :=
  { receipt with logsBloom := receipt.logs.length + receipt.id }

private def receiptBloomSum (receipts : List Receipt) : Nat :=
  receipts.foldl (fun total receipt => total + receipt.logsBloom) 0

private def commitStateSentinel (input : CommitStateInput) : Nat :=
  input.stateToken + input.receipts.length + input.counters.cumulativeReceiptGasUsed + 1

private def mutatedExecutionFor (index : Nat) (transaction : UserTransaction) (state : Nat) :
    Option TransactionExecution :=
  match executionFor index transaction state with
  | none => none
  | some execution =>
      some { execution with
        settlement := { execution.settlement with
          executionGas := execution.settlement.executionGas + if index = 1 then 1 else 0
          paidGas := execution.settlement.paidGas + if index = 1 then 1 else 0 }
        receipt := { execution.receipt with
          gasUsed := execution.receipt.gasUsed + if index = 1 then 1 else 0 } }

private def baseSpec : BlockSpec :=
  { executeTransaction := executionFor
    applyDao := fun state => state + 10
    startBlockTrace := fun state => state + 1
    setBlockExecutionContext := fun state => state + 1
    setupBlockAccessList := fun state => state + 1
    commitPostTransactionState := fun input => .completed (commitStateSentinel input)
    applyBeaconRoot := fun state => state + 20
    applyBlockhashState := fun state => state + 30
    commitPreSystemState := fun state => state + 1
    applyRewards := fun state => state + 40
    applyWithdrawals := fun state => state + 50
    commitWithdrawalState := fun state => state + 1
    applyExecutionRequests := fun state _ => state + 60
    endBlockTrace := fun state => state + 1
    commitStorageRoots := fun state => state + 70
    setBlockAccessList := fun state => state + 1
    computeBlobGas := fun transactions => transactions.length
    computeReceiptBlooms := fun _ receipts => .completed (receipts.map updateReceiptBloom)
    computeReceiptsRoot := fun _ receipts => .completed (receiptBloomSum receipts + 100)
    accumulateBlockBloom := fun _ receipts => .completed (receiptBloomSum receipts + 200)
    computeExecutionRequests := fun state receipts => state + receipts.length + 500
    computeRequestsHash := fun requests => requests + 600
    computeAccountChanges := fun state => state + 700
    computeGeneratedBlockAccessList := fun state => state + 800
    computeGeneratedBlockAccessListHash := fun accessList => accessList + 900
    computeEncodedBlockAccessList := fun accessList => accessList + 1_000
    computeStateRoot := fun state => state + 300
    computeHeaderHash := fun header =>
      header.stateRoot + header.receiptsRoot + header.bloom + 1
    validateProcessedHeader := fun _ header receipts artifacts =>
      header.gasUsed ≤ header.gasLimit ∧
        header.executionGasUsed = header.gasUsed ∧
        header.cumulativeReceiptGasUsed = header.gasUsed ∧
        header.blockHash > 0 ∧
        header.receiptsRoot = receiptBloomSum receipts + 100 ∧
        header.bloom = (if receipts.isEmpty then 0 else receiptBloomSum receipts + 200) ∧
        header.requestsHash = artifacts.requestsHash ∧
        artifacts.encodedBlockAccessList > artifacts.generatedBlockAccessList }

private def mutatedSpec : BlockSpec :=
  { baseSpec with executeTransaction := mutatedExecutionFor }

private def receiptBloomFailureSpec : BlockSpec :=
  { baseSpec with
    computeReceiptBlooms := fun _ _ =>
      .escaped (.receiptBloomCalculation .synchronous) }

private def receiptsRootFailureSpec : BlockSpec :=
  { baseSpec with
    computeReceiptsRoot := fun _ _ =>
      .escaped (.receiptsRootCalculation .synchronous) }

private def blockBloomFailureSpec : BlockSpec :=
  { baseSpec with
    accumulateBlockBloom := fun _ _ =>
      .escaped (.blockBloomAccumulation .synchronous) }

private def inputWith (state : Nat) (transactions : List UserTransaction)
    (accept : Phase → Bool) : BlockInput :=
  { initialState := state
    transactions := transactions
    proposedHeader :=
      { parentHash := 11
        unclesHash := 12
        author := 13
        beneficiary := 14
        difficulty := 15
        number := 16
        txRoot := 26
        gasLimit := 1_000
        gasUsed := 777
        timestamp := 17
        extraData := [ 18, 19 ]
        mixHash := 20
        nonce := 21
        totalDifficulty := 22
        baseFeePerGas := 23
        executionGasUsed := 777
        stateGasUsed := 777
        cumulativeReceiptGasUsed := 777
        blobGasUsed := 777
        excessBlobGas := 777
        receiptsRoot := 777
        bloom := 777
        withdrawalsRoot := 777
        parentBeaconBlockRoot := 42
        requestsHash := 777
        stateRoot := 777
        blockHash := 777
        isPostMerge := true
        slotNumber := 24
        blockAccessListHash := 25 }
    phaseAccepted := accept
    phaseException := fun _ => none
    parallelEligibility := sequentialEligibility
    receiptBloomPath := .synchronous
    scopePreOpened := false
    baseBlock := none }

def successfulMultiTransaction : BlockInput :=
  inputWith 7 [ { id := 1 }, { id := 2 }, { id := 3 } ] allPhasesAccepted

def emptySynchronousInput : BlockInput :=
  inputWith 7 [] allPhasesAccepted

def rejectedBeforeUserFold : BlockInput :=
  inputWith 7 [ { id := 1 }, { id := 2 } ] (rejectingAt .userTransactionFold)

def rejectedProcessedHeader : BlockInput :=
  inputWith 7 [ { id := 1 }, { id := 2 } ] allPhasesAccepted

private def failingTransaction (index : Nat) (transaction : UserTransaction) (state : Nat) :
    Option TransactionExecution :=
  if index = 1 then none else executionFor index transaction state

private def rejectedUserTransaction : BlockSpec :=
  { baseSpec with executeTransaction := failingTransaction }

private def commitStateFailureSpec : BlockSpec :=
  { baseSpec with
    commitPostTransactionState := fun _ =>
      .escaped (.blockHook .commitState) }

def rejectedUserInput : BlockInput :=
  inputWith 7 [ { id := 1 }, { id := 2 }, { id := 3 } ] allPhasesAccepted

def successfulRun : BlockRun := runProcessOne baseSpec successfulMultiTransaction

def emptySynchronousRun : BlockRun := runProcessOne baseSpec emptySynchronousInput

def rejectedPhaseRun : BlockRun := runProcessOne baseSpec rejectedBeforeUserFold

def rejectedHeaderRun : BlockRun :=
  runProcessOne { baseSpec with validateProcessedHeader := fun _ _ _ _ => false }
    rejectedProcessedHeader

def rejectedTransactionRun : BlockRun :=
  runProcessOne rejectedUserTransaction rejectedUserInput

def mutatedRun : BlockRun := runProcessOne mutatedSpec successfulMultiTransaction

def commitStateFailureRun : BlockRun :=
  runProcessOne commitStateFailureSpec successfulMultiTransaction

def receiptBloomFailureRun : BlockRun :=
  runProcessOne receiptBloomFailureSpec successfulMultiTransaction

def receiptsRootFailureRun : BlockRun :=
  runProcessOne receiptsRootFailureSpec successfulMultiTransaction

def blockBloomFailureRun : BlockRun :=
  runProcessOne blockBloomFailureSpec successfulMultiTransaction

def expectedSuccessfulWorld : BlockWorld :=
  { stateToken := 298
    counters :=
      { executionGasUsed := 9
        stateGasUsed := 6
        cumulativeReceiptGasUsed := 9 }
    receipts :=
      [ { id := 1
          status := 1
          cumulativeGasUsed := 2
          gasUsed := 2
          logs := []
          logsBloom := 1
          contractAddress := 0 }
      , { id := 2
          status := 1
          cumulativeGasUsed := 5
          gasUsed := 3
          logs := [ { address := 77, topics := [ 88 ], data := [ 99 ] } ]
          logsBloom := 3
          contractAddress := 0 }
      , { id := 3
          status := 1
          cumulativeGasUsed := 9
          gasUsed := 4
          logs := []
          logsBloom := 3
          contractAddress := 0 } ]
    executions :=
      [ { settlement :=
            { gasUsedBeforeRefund := 3
              gasRefund := 0
              gasUsedAfterRefund := 3
              paidGas := 2
              stateGas := 1
              executionGas := 2 }
          nextState := 70
          receipt :=
            { id := 1
              status := 1
              cumulativeGasUsed := 0
              gasUsed := 999
              logs := []
              logsBloom := 0
              contractAddress := 0 } }
      , { settlement :=
            { gasUsedBeforeRefund := 4
              gasRefund := 0
              gasUsedAfterRefund := 4
              paidGas := 3
              stateGas := 2
              executionGas := 3 }
          nextState := 73
          receipt :=
            { id := 2
              status := 1
              cumulativeGasUsed := 0
              gasUsed := 999
              logs := [ { address := 77, topics := [ 88 ], data := [ 99 ] } ]
              logsBloom := 0
              contractAddress := 0 } }
      , { settlement :=
            { gasUsedBeforeRefund := 5
              gasRefund := 0
              gasUsedAfterRefund := 5
              paidGas := 4
              stateGas := 3
              executionGas := 4 }
          nextState := 77
          receipt :=
            { id := 3
              status := 1
              cumulativeGasUsed := 0
              gasUsed := 999
              logs := []
              logsBloom := 0
              contractAddress := 0 } } ]
    artifacts :=
      { accountChanges := 998
        executionRequests := 671
        requestsHash := 1_271
        generatedBlockAccessList := 1_098
        generatedBlockAccessListHash := 1_998
        encodedBlockAccessList := 2_098 }
    observations :=
      { traceStarted := 1
        executionContext := 1
        balSetup := 1
        postTransactionCommit := 90
        traceEnded := 1
        balFinalized := 1
        receiptBloomTrace :=
          [ .started, .computedReceiptBlooms, .computedReceiptsRoot,
            .installedReceiptsRoot, .accumulatedBlockBloom, .installedBloom ]
        taskReceiptBloomTrace := []
        journalTrace := [ .commitState ]
        controlTrace :=
          [ .traceStarted, .executionContextSet, .balSetup,
            .postTransactionCommit, .receiptBloomStarted,
            .receiptBloomsComputed, .receiptsRootComputed,
            .receiptsRootInstalled, .executionRequestsExtracted,
            .executionRequestsHashed, .traceEnded,
            .blockBloomAccumulated, .blockBloomInstalled, .balFinalized ]
        taskControlTrace := [] }
    pendingReceiptBloom := none
    header :=
      { parentHash := 11
        unclesHash := 12
        author := 13
        beneficiary := 14
        difficulty := 15
        number := 16
        txRoot := 26
        gasLimit := 1_000
        gasUsed := 9
        timestamp := 17
        extraData := [ 18, 19 ]
        mixHash := 20
        nonce := 21
        totalDifficulty := 22
        baseFeePerGas := 23
        executionGasUsed := 9
        stateGasUsed := 6
        cumulativeReceiptGasUsed := 9
        blobGasUsed := 3
        excessBlobGas := 777
        receiptsRoot := 107
        bloom := 207
        withdrawalsRoot := 777
        parentBeaconBlockRoot := 42
        requestsHash := 1_271
        stateRoot := 598
        blockHash := 913
        isPostMerge := true
        slotNumber := 24
        blockAccessListHash := 25 } }

theorem successful_run_uses_process_one_phases :
    successfulRun.outcome = .accepted ∧
      successfulRun.trace = Phase.processOne ∧
      Phase.commitTree ∉ successfulRun.trace := by
  native_decide

theorem empty_synchronous_path_keeps_empty_bloom_without_accumulation :
    emptySynchronousRun.outcome = .accepted ∧
      emptySynchronousRun.world.receipts = [] ∧
      emptySynchronousRun.world.header.bloom = 0 ∧
      emptySynchronousRun.world.observations.receiptBloomTrace =
        [ .started, .computedReceiptBlooms, .computedReceiptsRoot,
          .installedReceiptsRoot ] ∧
      emptySynchronousRun.world.observations.controlTrace =
        [ .traceStarted, .executionContextSet, .balSetup,
          .postTransactionCommit, .receiptBloomStarted,
          .receiptBloomsComputed, .receiptsRootComputed,
          .receiptsRootInstalled, .executionRequestsExtracted,
          .executionRequestsHashed, .traceEnded, .balFinalized ] := by
  native_decide

theorem successful_run_matches_independent_expected_world :
    successfulRun =
      { outcome := .accepted
        world := expectedSuccessfulWorld
        trace := Phase.processOne
        finallyAwait := .nonePending } := by
  native_decide

theorem successful_run_installs_header_observations :
    successfulRun.outcome = .accepted ∧
      successfulRun.world.header.gasLimit = 1_000 ∧
      successfulRun.world.header.gasUsed = 9 ∧
      successfulRun.world.header.executionGasUsed = 9 ∧
      successfulRun.world.header.stateGasUsed = 6 ∧
      successfulRun.world.header.cumulativeReceiptGasUsed = 9 ∧
      successfulRun.world.header.blobGasUsed = 3 ∧
      successfulRun.world.header.excessBlobGas = 777 ∧
      successfulRun.world.header.receiptsRoot = 107 ∧
      successfulRun.world.header.bloom = 207 ∧
      successfulRun.world.header.withdrawalsRoot = 777 ∧
      successfulRun.world.header.parentBeaconBlockRoot = 42 ∧
      successfulRun.world.header.requestsHash = 1_271 ∧
      successfulRun.world.header.stateRoot = 598 ∧
      successfulRun.world.header.blockHash = 913 := by
  native_decide

theorem processing_clone_preserves_exact_header_roster :
    let processing := (initialWorld successfulMultiTransaction).header
    processing.parentHash = 11 ∧
      processing.unclesHash = 12 ∧
      processing.author = 13 ∧
      processing.beneficiary = 14 ∧
      processing.difficulty = 15 ∧
      processing.number = 16 ∧
      processing.txRoot = 26 ∧
      processing.gasLimit = 1_000 ∧
      processing.gasUsed = 0 ∧
      processing.timestamp = 17 ∧
      processing.extraData = [ 18, 19 ] ∧
      processing.mixHash = 20 ∧
      processing.nonce = 21 ∧
      processing.totalDifficulty = 22 ∧
      processing.baseFeePerGas = 23 ∧
      processing.executionGasUsed = 777 ∧
      processing.stateGasUsed = 777 ∧
      processing.cumulativeReceiptGasUsed = 777 ∧
      processing.blobGasUsed = 777 ∧
      processing.excessBlobGas = 777 ∧
      processing.receiptsRoot = 777 ∧
      processing.bloom = 0 ∧
      processing.withdrawalsRoot = 777 ∧
      processing.parentBeaconBlockRoot = 42 ∧
      processing.requestsHash = 777 ∧
      processing.stateRoot = 0 ∧
      processing.blockHash = 777 ∧
      processing.isPostMerge = true ∧
      processing.slotNumber = 24 ∧
      processing.blockAccessListHash = 25 := by
  native_decide

theorem successful_run_exposes_receipts_and_artifacts :
    successfulRun.world.receipts.map (fun receipt => receipt.status) = [ 1, 1, 1 ] ∧
      successfulRun.world.receipts.map (fun receipt => receipt.gasUsed) = [ 2, 3, 4 ] ∧
      successfulRun.world.receipts.map (fun receipt => receipt.cumulativeGasUsed) = [ 2, 5, 9 ] ∧
      successfulRun.world.artifacts.accountChanges = 998 ∧
      successfulRun.world.artifacts.executionRequests = 671 ∧
      successfulRun.world.artifacts.requestsHash = 1_271 ∧
      successfulRun.world.artifacts.generatedBlockAccessList = 1_098 ∧
      successfulRun.world.artifacts.generatedBlockAccessListHash = 1_998 ∧
      successfulRun.world.artifacts.encodedBlockAccessList = 2_098 := by
  native_decide

theorem execution_requests_are_receipt_dependent_and_after_commit_state :
    successfulRun.world.observations.postTransactionCommit = 90 ∧
      successfulRun.world.receipts.length = 3 ∧
      successfulRun.world.artifacts.executionRequests = 671 ∧
      successfulRun.world.header.requestsHash = successfulRun.world.artifacts.requestsHash := by
  native_decide

theorem commit_state_failure_stops_before_later_phases :
    commitStateFailureRun.outcome = .exception (.blockHook .commitState) ∧
      commitStateFailureRun.trace = Phase.processOne.take 5 ∧
      commitStateFailureRun.world = initialWorld successfulMultiTransaction ∧
      commitStateFailureRun.world.receipts = [] ∧
      commitStateFailureRun.world.observations.journalTrace = [] ∧
      commitStateFailureRun.finallyAwait = .nonePending := by
  native_decide

theorem receipt_root_and_bloom_failures_stop_at_their_distinct_boundaries :
    receiptBloomFailureRun.outcome = .exception (.receiptBloomCalculation .synchronous) ∧
      receiptBloomFailureRun.trace = Phase.processOne.take 5 ∧
      receiptBloomFailureRun.world = initialWorld successfulMultiTransaction ∧
      receiptsRootFailureRun.outcome = .exception (.receiptsRootCalculation .synchronous) ∧
      receiptsRootFailureRun.trace = Phase.processOne.take 5 ∧
      receiptsRootFailureRun.world = initialWorld successfulMultiTransaction ∧
      blockBloomFailureRun.outcome = .exception (.blockBloomAccumulation .synchronous) ∧
      blockBloomFailureRun.trace = Phase.processOne.take 9 ∧
      blockBloomFailureRun.world = initialWorld successfulMultiTransaction := by
  native_decide

theorem observation_hooks_do_not_change_logical_state :
    let initial := initialWorld successfulMultiTransaction
    let started := startBlockObservations baseSpec initial
    let ended := endBlockTraceObservation baseSpec started
    let finalized := finalizeBlockAccessListObservation baseSpec ended
    started.stateToken = initial.stateToken ∧
      ended.stateToken = initial.stateToken ∧
      finalized.stateToken = initial.stateToken ∧
      finalized.observations.traceStarted = 1 ∧
      finalized.observations.executionContext = 1 ∧
      finalized.observations.balSetup = 1 ∧
      finalized.observations.traceEnded = 1 ∧
      finalized.observations.balFinalized = 1 := by
  native_decide

theorem post_transaction_commit_is_an_ordered_journal_boundary :
    successfulRun.world.observations.journalTrace = [ .commitState ] ∧
      successfulRun.world.observations.postTransactionCommit = 90 ∧
      successfulRun.world.observations.receiptBloomTrace.head? =
        some .started := by
  native_decide

theorem block_control_trace_makes_commit_order_mutation_sensitive :
    successfulRun.world.observations.controlTrace =
      [ .traceStarted, .executionContextSet, .balSetup,
        .postTransactionCommit, .receiptBloomStarted,
        .receiptBloomsComputed, .receiptsRootComputed,
        .receiptsRootInstalled, .executionRequestsExtracted,
        .executionRequestsHashed, .traceEnded,
        .blockBloomAccumulated, .blockBloomInstalled, .balFinalized ] ∧
      successfulRun.world.observations.postTransactionCommit = 90 ∧
      mutatedRun.world.observations.postTransactionCommit = 91 ∧
      successfulRun.world.observations.postTransactionCommit ≠
        mutatedRun.world.observations.postTransactionCommit := by
  native_decide

theorem successful_receipts_follow_transaction_and_block_counters :
    successfulRun.world.receipts.map (fun receipt => receipt.gasUsed) =
        successfulRun.world.executions.map (fun execution => execution.settlement.paidGas) ∧
      successfulRun.world.receipts.map (fun receipt => receipt.cumulativeGasUsed) = [ 2, 5, 9 ] ∧
      successfulRun.world.counters.cumulativeReceiptGasUsed =
        (successfulRun.world.receipts.getLast?.map (fun receipt => receipt.cumulativeGasUsed)).getD 0 := by
  native_decide

theorem rejected_phase_has_no_later_effects :
    rejectedPhaseRun.outcome = .rejected (.phaseRejected .userTransactionFold) ∧
      rejectedPhaseRun.trace =
        [ .daoTransition
        , .beaconRootSystemCall
        , .historicalBlockhashStateChange
        , .userTransactionFold ] ∧
      rejectedPhaseRun.world = initialWorld rejectedBeforeUserFold := by
  native_decide

theorem rejected_user_transaction_restores_whole_scope :
    rejectedTransactionRun.outcome = .rejected (.transactionExecution 1) ∧
      rejectedTransactionRun.trace =
        [ .daoTransition
        , .beaconRootSystemCall
        , .historicalBlockhashStateChange
        , .userTransactionFold ] ∧
      rejectedTransactionRun.world = initialWorld rejectedUserInput ∧
      rejectedTransactionRun.world.counters.executionGasUsed = 0 ∧
      rejectedTransactionRun.world.receipts = [] := by
  native_decide

theorem rejected_header_restores_whole_scope :
    rejectedHeaderRun.outcome = .rejected .processedHeaderValidation ∧
      rejectedHeaderRun.trace = Phase.processOne ∧
      rejectedHeaderRun.world = initialWorld rejectedProcessedHeader := by
  native_decide

theorem system_phases_do_not_add_normal_gas :
    let counters := successfulRun.world.counters
    counters.executionGasUsed = 9 ∧
      counters.stateGasUsed = 6 ∧
      counters.cumulativeReceiptGasUsed = 9 ∧
      TransactionGas.headerGasUsed counters = 9 := by
  native_decide

theorem mutation_changes_the_observed_execution_dimension :
    mutatedRun.outcome = .accepted ∧
      mutatedRun.world.counters.executionGasUsed = 10 ∧
      mutatedRun.world.header.executionGasUsed = 10 ∧
      mutatedRun.world.header.gasUsed = 10 ∧
      mutatedRun.world.counters.executionGasUsed ≠ successfulRun.world.counters.executionGasUsed := by
  native_decide

def vectorPasses (run : BlockRun) (expectedOutcome : Outcome) (expectedTrace : List Phase)
    (expectedWorld : BlockWorld) : Bool :=
  decide (run.outcome = expectedOutcome ∧ run.trace = expectedTrace ∧ run.world = expectedWorld)

def allVectorsPass : Bool :=
  vectorPasses successfulRun .accepted Phase.processOne successfulRun.world &&
  vectorPasses rejectedPhaseRun (.rejected (.phaseRejected .userTransactionFold))
    [ .daoTransition
    , .beaconRootSystemCall
    , .historicalBlockhashStateChange
    , .userTransactionFold ] (initialWorld rejectedBeforeUserFold) &&
  vectorPasses rejectedTransactionRun (.rejected (.transactionExecution 1))
    [ .daoTransition
    , .beaconRootSystemCall
    , .historicalBlockhashStateChange
    , .userTransactionFold ] (initialWorld rejectedUserInput) &&
  vectorPasses rejectedHeaderRun (.rejected .processedHeaderValidation)
    Phase.processOne (initialWorld rejectedProcessedHeader) &&
  vectorPasses commitStateFailureRun (.exception (.blockHook .commitState))
    (Phase.processOne.take 5) (initialWorld successfulMultiTransaction) &&
  vectorPasses receiptBloomFailureRun (.exception (.receiptBloomCalculation .synchronous))
    (Phase.processOne.take 5) (initialWorld successfulMultiTransaction) &&
  vectorPasses receiptsRootFailureRun (.exception (.receiptsRootCalculation .synchronous))
    (Phase.processOne.take 5) (initialWorld successfulMultiTransaction) &&
  vectorPasses blockBloomFailureRun (.exception (.blockBloomAccumulation .synchronous))
    (Phase.processOne.take 9) (initialWorld successfulMultiTransaction) &&
  mutatedRun.world.counters.executionGasUsed = 10

theorem all_vectors_pass : allVectorsPass = true := by
  native_decide

/-! The BAL/parallel executor is a separate selection mode. No equivalence
claim is stated here: the branch relation records unknown parallel success
as an explicit outcome. -/
inductive ExecutionMode where
  | sequential
  | parallelBlockAccessList
  deriving DecidableEq, Repr

def selectedMode (parallelEnabled : Bool) : ExecutionMode :=
  if parallelEnabled then .parallelBlockAccessList else .sequential

theorem parallel_equivalence_not_claimed :
    selectedMode true = .parallelBlockAccessList ∧ selectedMode false = .sequential := by
  native_decide

end Vectors
end BlockReference
end Eip803x
