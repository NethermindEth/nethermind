-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.BranchReference

namespace Eip803x
namespace BlockReference
namespace BranchReference
namespace Vectors

private def acceptAll : Phase → Bool := fun _ => true

private def rejectPhase (rejected : Phase) : Phase → Bool :=
  fun phase => phase != rejected

private def sequentialEligibility : ParallelEligibility :=
  { isGenesis := false
    balEnabled := false
    parallelRequested := false
    parentScopeAvailable := false
    forceSequential := true }

private def parallelEligibility : ParallelEligibility :=
  { isGenesis := false
    balEnabled := true
    parallelRequested := true
    parentScopeAvailable := true
    forceSequential := false }

private def baseHeader : HeaderObservation :=
  { parentHash := 1
    unclesHash := 2
    author := 3
    beneficiary := 4
    difficulty := 5
    number := 6
    txRoot := 15
    gasLimit := 100
    gasUsed := 0
    timestamp := 7
    extraData := [ 8 ]
    mixHash := 9
    nonce := 10
    totalDifficulty := 11
    baseFeePerGas := 12
    executionGasUsed := 0
    stateGasUsed := 0
    cumulativeReceiptGasUsed := 0
    blobGasUsed := 0
    excessBlobGas := 777
    receiptsRoot := 0
    bloom := 0
    withdrawalsRoot := 888
    parentBeaconBlockRoot := 999
    requestsHash := 0
    stateRoot := 0
    blockHash := 0
    isPostMerge := true
    slotNumber := 13
    blockAccessListHash := 14 }

private def noTransactionSpec : BlockSpec :=
  { executeTransaction := fun _ transaction state =>
      some
        { settlement :=
            { gasUsedBeforeRefund := 0
              gasRefund := 0
              gasUsedAfterRefund := 0
              paidGas := 0
              stateGas := 0
              executionGas := 0 }
          nextState := state
          receipt :=
            { id := transaction.id
              status := 1
              cumulativeGasUsed := 999
              gasUsed := 999
              logs := []
              logsBloom := 0
              contractAddress := 0 } }
    applyDao := id
    startBlockTrace := fun value => value + 1
    setBlockExecutionContext := fun value => value + 2
    setupBlockAccessList := fun value => value + 3
    commitPostTransactionState := fun input =>
      .completed
        (input.stateToken + input.receipts.length + input.counters.cumulativeReceiptGasUsed + 1)
    applyBeaconRoot := id
    applyBlockhashState := id
    commitPreSystemState := id
    applyRewards := id
    applyWithdrawals := id
    commitWithdrawalState := id
    applyExecutionRequests := fun state _ => state
    endBlockTrace := fun value => value + 4
    commitStorageRoots := id
    setBlockAccessList := fun value => value + 5
    computeBlobGas := fun transactions => transactions.length + 3
    computeReceiptBlooms := fun _ receipts =>
      .completed (receipts.map (fun receipt =>
        { receipt with logsBloom := receipt.logs.length + receipt.id }))
    computeReceiptsRoot := fun _ receipts =>
      .completed (receipts.foldl (fun total receipt => total + receipt.logsBloom) 0 + 40)
    accumulateBlockBloom := fun _ receipts =>
      .completed (receipts.foldl (fun total receipt => total + receipt.logsBloom) 0 + 50)
    computeExecutionRequests := fun _ receipts => receipts.length + 60
    computeRequestsHash := fun requests => requests + 70
    computeAccountChanges := fun state => state + 80
    computeGeneratedBlockAccessList := fun state => state + 90
    computeGeneratedBlockAccessListHash := fun accessList => accessList + 1
    computeEncodedBlockAccessList := fun accessList => accessList + 2
    computeStateRoot := fun _ => 90
    computeHeaderHash := fun header => header.gasUsed + 900
    validateProcessedHeader := fun _ header receipts _ =>
      header.gasLimit = 100 ∧
      header.excessBlobGas = 777 ∧
      header.withdrawalsRoot = 888 ∧
      header.parentBeaconBlockRoot = 999 ∧
      header.blobGasUsed = receipts.length + 3 ∧
      header.receiptsRoot =
          receipts.foldl (fun total receipt => total + receipt.logsBloom) 0 + 40 ∧
      header.bloom =
          (if receipts.isEmpty then 0
           else receipts.foldl (fun total receipt => total + receipt.logsBloom) 0 + 50) ∧
      header.requestsHash = receipts.length + 130 ∧
      header.stateRoot = 90 ∧
      header.blockHash = 900 }

private def blockInputWith (accept : Phase → Bool)
    (eligibility : ParallelEligibility) (preOpened : Bool := false) : BlockInput :=
  { initialState := 0
    transactions := []
    proposedHeader := baseHeader
    phaseAccepted := accept
    phaseException := fun _ => none
    parallelEligibility := eligibility
    receiptBloomPath := .synchronous
    scopePreOpened := preOpened
    baseBlock := none }

private def blockInput (accept : Phase → Bool) : BlockInput :=
  blockInputWith accept sequentialEligibility

private def backgroundBlockInput (accept : Phase → Bool) : BlockInput :=
  { blockInputWith accept sequentialEligibility with
    transactions := (List.range 16).map (fun id => { id := id })
    receiptBloomPath := .background }

private def parallelBlockInput (accept : Phase → Bool) : BlockInput :=
  blockInputWith accept parallelEligibility

private def branchSpec : BranchSpec :=
  { block := noTransactionSpec
    synchronousBranchSelection := fun _ => .selected
    openWorldStateScope := fun _ => .completed ()
    checkInclusionList := fun _ _ _ => .completed true
    prewarmSucceeded := fun _ _ _ => .completed ()
    notReadOnly := true
    reopenAtCheckpoint := fun _ => false
    commitTree := fun _ _ _ => .completed ()
    setTotalDifficulty := fun blocks _ => .completed blocks.length
    updateMainChain := fun _ _ states => .completed (states.length, true)
    markProcessed := fun _ => .completed true }

private def requestVisibleProjection (receipts : List Receipt) : Nat :=
  receipts.foldl (fun total receipt => total + receipt.status + receipt.logs.length) 0

private def requestReceiptBloomSum (receipts : List Receipt) : Nat :=
  receipts.foldl (fun total receipt => total + receipt.logsBloom) 0

private def requestMutationLog : LogObservation :=
  { address := 123, topics := [ 124 ], data := [ 125 ] }

private def requestMutatedReceipt (receipt : Receipt) : Receipt :=
  { receipt with
    status := receipt.status + 10
    logs := requestMutationLog :: receipt.logs
    logsBloom := receipt.logsBloom + 500 }

private def validateRequestMutationHeader (_input : BlockInput) (header : HeaderObservation)
    (receipts : List Receipt) (artifacts : BlockArtifacts) : Bool :=
  header.gasLimit = 100 ∧
    header.excessBlobGas = 777 ∧
    header.withdrawalsRoot = 888 ∧
    header.parentBeaconBlockRoot = 999 ∧
    header.blobGasUsed = receipts.length + 3 ∧
    header.receiptsRoot = requestReceiptBloomSum receipts + 40 ∧
    header.bloom = (if receipts.isEmpty then 0 else requestReceiptBloomSum receipts + 50) ∧
    header.requestsHash = artifacts.requestsHash ∧
    header.stateRoot = 90 ∧
    header.blockHash = 900

private def backgroundRequestMutationSpec : BranchSpec :=
  { branchSpec with block :=
      { noTransactionSpec with
        computeReceiptBlooms := fun _ receipts =>
          .completed (receipts.map requestMutatedReceipt)
        computeExecutionRequests := fun _ receipts => requestVisibleProjection receipts
        computeRequestsHash := fun requests => requests + 1_000
        validateProcessedHeader := validateRequestMutationHeader } }

private def inclusionFalseSpec : BranchSpec :=
  { branchSpec with checkInclusionList := fun _ _ _ => .completed false }

private def inclusionExceptionSpec : BranchSpec :=
  { branchSpec with
    checkInclusionList := fun _ _ _ => .escaped (.blockHook .inclusionList) }

private def prewarmExceptionSpec : BranchSpec :=
  { branchSpec with
    prewarmSucceeded := fun _ _ _ => .escaped (.blockHook .prewarm) }

private def commitFailureSpec : BranchSpec :=
  { branchSpec with
    commitTree := fun _ _ _ => .escaped (.blockHook .commitTree) }

private def scopeFailureSpec : BranchSpec :=
  { branchSpec with
    openWorldStateScope := fun _ => .escaped (.blockHook .worldStateScope) }

private def checkpointSpec : BranchSpec :=
  { branchSpec with reopenAtCheckpoint := fun _ => true }

private def skippedSpec : BranchSpec :=
  { branchSpec with synchronousBranchSelection := fun _ => .skipped }

private def preparationExceptionSpec : BranchSpec :=
  { branchSpec with synchronousBranchSelection := fun _ =>
      BranchSelectionDecision.escaped .preparation }

private def finalizationTotalDifficultyFailureSpec : BranchSpec :=
  { branchSpec with
    setTotalDifficulty := fun _ _ => .escaped (.blockHook .finalization) }

private def finalizationHeadFailureSpec : BranchSpec :=
  { branchSpec with
    setTotalDifficulty := fun blocks _ => .completed (blocks.length + 100)
    updateMainChain := fun _ _ _ => .escaped (.blockHook .finalization) }

private def finalizationMarkFailureSpec : BranchSpec :=
  { branchSpec with
    setTotalDifficulty := fun blocks _ => .completed (blocks.length + 100)
    updateMainChain := fun _ _ states => .completed (states.length + 900, true)
    markProcessed := fun _ => .escaped (.blockHook .finalization) }

private def failedHeadUpdateSpec : BranchSpec :=
  { branchSpec with
    updateMainChain := fun _ _ states => .completed (states.length + 900, false) }

private def statefulSpec : BranchSpec :=
  { branchSpec with block :=
      { noTransactionSpec with
        applyRewards := fun state => state + 1
        executeTransaction := fun index transaction state =>
          if transaction.id = 99 then none
          else noTransactionSpec.executeTransaction index transaction state
        validateProcessedHeader := fun _ header _ _ => header.gasLimit = 100 } }

private def recoveryExceptionInput : BlockInput :=
  { blockInput acceptAll with
    phaseException := fun phase =>
      if phase = .senderAndAuthorityRecovery then
        some .senderAuthorityRecovery
      else
        none }

private def backgroundPrimaryFailureInput : BlockInput :=
  { backgroundBlockInput acceptAll with
    phaseException := fun phase =>
      if phase = .rewards then
        some (.blockHook (.phase .rewards))
      else
        none }

private def backgroundPrimaryFailureSpec : BranchSpec :=
  { branchSpec with block :=
      { noTransactionSpec with computeReceiptBlooms := fun path receipts =>
          match path with
          | .synchronous => .completed receipts
          | .background => .escaped (.receiptBloomCalculation .background) } }

private def invalidPreOpenedInput : BlockInput :=
  blockInputWith acceptAll sequentialEligibility true

private def failingTransactionSpec : BranchSpec :=
  { branchSpec with block :=
      { noTransactionSpec with
        executeTransaction := fun index transaction state =>
          if transaction.id = 99 then none
          else noTransactionSpec.executeTransaction index transaction state } }

private def sequentialPolicy : RetryPolicy :=
  { parallelOutcome := fun _ => .successUnknown
    parallelFailedState := fun _ world => world }

private def retryPolicy : RetryPolicy :=
  { parallelOutcome := fun _ => .retryableBalFailure
    parallelFailedState := fun index world =>
      { world with stateToken := world.stateToken + 1_000 + index } }

private def parallelFailurePolicy : RetryPolicy :=
  { parallelOutcome := fun index => .invalid (.parallelExecutionFailure index)
    parallelFailedState := fun index world =>
      { world with stateToken := world.stateToken + 1_000 + index } }

private def nonBalParallelExceptionPolicy : RetryPolicy :=
  { parallelOutcome := fun index => .escaped (.nonBalParallel index)
    parallelFailedState := fun index world =>
      { world with stateToken := world.stateToken + 1_000 + index } }

private def unprovedParallelPolicy : RetryPolicy :=
  { parallelOutcome := fun _ => .successUnknown
    parallelFailedState := fun _ world => world }

def twoBlockSuccess : BranchRun :=
  runBranch branchSpec sequentialPolicy 10 [ blockInput acceptAll, blockInput acceptAll ]

def secondBlockFailure : BranchRun :=
  runBranch statefulSpec sequentialPolicy 10
    [ blockInput acceptAll,
      { blockInput acceptAll with transactions := [ { id := 99 } ] } ]

private def secondBlockExceptionInput : BlockInput :=
  { blockInput acceptAll with
    phaseException := fun phase =>
      if phase = .rewards then
        some (.blockHook (.phase .rewards))
      else
        none }

def secondBlockException : BranchRun :=
  runBranch branchSpec sequentialPolicy 10
    [ blockInput acceptAll, secondBlockExceptionInput ]

def sequentialRetrySuccess : BranchRun :=
  runBranch branchSpec retryPolicy 10 [ parallelBlockInput acceptAll ]

def parallelRetryFailure : BranchRun :=
  runBranch failingTransactionSpec retryPolicy 10
    [ { parallelBlockInput acceptAll with transactions := [ { id := 99 } ] } ]

def parallelFailureRun : BranchRun :=
  runBranch branchSpec parallelFailurePolicy 10 [ parallelBlockInput acceptAll ]

def parallelExceptionRun : BranchRun :=
  runBranch branchSpec nonBalParallelExceptionPolicy 10 [ parallelBlockInput acceptAll ]

def unprovedParallelRun : BranchRun :=
  runBranch branchSpec unprovedParallelPolicy 10 [ parallelBlockInput acceptAll ]

def commitFailureRun : BranchRun :=
  runBranch commitFailureSpec sequentialPolicy 10 [ blockInput acceptAll ]

def inclusionFalseRun : BranchRun :=
  runBranch inclusionFalseSpec sequentialPolicy 10 [ blockInput acceptAll ]

def inclusionExceptionRun : BranchRun :=
  runBranch inclusionExceptionSpec sequentialPolicy 10 [ blockInput acceptAll ]

def prewarmExceptionRun : BranchRun :=
  runBranch prewarmExceptionSpec sequentialPolicy 10 [ blockInput acceptAll ]

def scopeFailureRun : BranchRun :=
  runBranch scopeFailureSpec sequentialPolicy 10 [ blockInput acceptAll ]

def nonterminalSuggestedRejectionRun : BranchRun :=
  runBranch branchSpec sequentialPolicy 10
    [ blockInput (rejectPhase .suggestedBlockValidation), blockInput acceptAll ]

def terminalSuggestedSkipRun : BranchRun :=
  runBranch branchSpec sequentialPolicy 10
    [ blockInput (rejectPhase .suggestedBlockValidation) ]

def checkpointRun : BranchRun :=
  runBranch checkpointSpec sequentialPolicy 10
    (List.replicate 66 (blockInput acceptAll))

def genesisRun : BranchRun :=
  runBranch branchSpec sequentialPolicy 10
    [ { blockInputWith acceptAll
          { sequentialEligibility with isGenesis := true } true with baseBlock := none } ]

def skippedRun : BranchRun :=
  runBranch skippedSpec sequentialPolicy 10 [ blockInput acceptAll ]

def preparationExceptionRun : BranchRun :=
  runBranch preparationExceptionSpec sequentialPolicy 10 [ blockInput acceptAll ]

def recoveryExceptionRun : BranchRun :=
  runBranch branchSpec sequentialPolicy 10 [ recoveryExceptionInput ]

def finalizationTotalDifficultyFailureRun : BranchRun :=
  runBranch finalizationTotalDifficultyFailureSpec sequentialPolicy 10 [ blockInput acceptAll ]

def finalizationHeadFailureRun : BranchRun :=
  runBranch finalizationHeadFailureSpec sequentialPolicy 10 [ blockInput acceptAll ]

def finalizationMarkFailureRun : BranchRun :=
  runBranch finalizationMarkFailureSpec sequentialPolicy 10 [ blockInput acceptAll ]

def failedHeadUpdateRun : BranchRun :=
  runBranch failedHeadUpdateSpec sequentialPolicy 10 [ blockInput acceptAll ]

def backgroundSuccessRun : BranchRun :=
  runBranch branchSpec sequentialPolicy 10 [ backgroundBlockInput acceptAll ]

def backgroundRequestMutationRun : BranchRun :=
  runBranch backgroundRequestMutationSpec sequentialPolicy 10
    [ backgroundBlockInput acceptAll ]

def backgroundFallbackRun : BranchRun :=
  runBranch branchSpec sequentialPolicy 10
    [ { blockInput acceptAll with receiptBloomPath := .background } ]

private def backgroundFailureSpec : BranchSpec :=
  { branchSpec with block :=
      { noTransactionSpec with computeReceiptBlooms := fun path receipts =>
          match path with
          | .synchronous => .completed receipts
          | .background => .escaped (.receiptBloomCalculation .background) } }

private def backgroundBlockBloomFailureSpec : BranchSpec :=
  { branchSpec with block :=
      { noTransactionSpec with
        accumulateBlockBloom := fun path _ =>
          match path with
          | .synchronous => .completed 50
          | .background => .escaped (.blockBloomAccumulation .background) } }

private def backgroundReceiptsRootFailureSpec : BranchSpec :=
  { branchSpec with block :=
      { noTransactionSpec with
        computeReceiptsRoot := fun path _ =>
          match path with
          | .synchronous => .completed 40
          | .background => .escaped (.receiptsRootCalculation .background) } }

def backgroundFailureRun : BranchRun :=
  runBranch backgroundFailureSpec sequentialPolicy 10 [ backgroundBlockInput acceptAll ]

def backgroundBlockBloomFailureRun : BranchRun :=
  runBranch backgroundBlockBloomFailureSpec sequentialPolicy 10 [ backgroundBlockInput acceptAll ]

def backgroundReceiptsRootFailureRun : BranchRun :=
  runBranch backgroundReceiptsRootFailureSpec sequentialPolicy 10 [ backgroundBlockInput acceptAll ]

def backgroundPrimaryFailureRun : BranchRun :=
  runBranch backgroundPrimaryFailureSpec sequentialPolicy 10 [ backgroundPrimaryFailureInput ]

def invalidPreOpenedRun : BranchRun :=
  runBranch branchSpec sequentialPolicy 10 [ invalidPreOpenedInput ]

def expectedCheckpointScopeEvents : List ScopeEvent :=
  [ .opened ] ++
    (List.range 64).map (fun index => .resetAfterSuccessfulBlock index) ++
    [ .disposedAtCheckpoint 64, .reopenedAtCheckpoint 64,
      .resetAfterSuccessfulBlock 64, .resetAfterSuccessfulBlock 65, .disposed ]

private def genesisPreprocessTrace : List Phase :=
  [ .suggestedBlockValidation, .synchronousBranchSelectionAndPreparation,
    .senderAndAuthorityRecovery, .openWorldStateScope ]

theorem successful_branch_commits_each_block_once :
    twoBlockSuccess.outcome = .accepted ∧
      twoBlockSuccess.logicalState = 10 ∧
      twoBlockSuccess.committedPrefixState = 10 ∧
      twoBlockSuccess.finalization.map (fun finalization => finalization.totalDifficulty) =
        some 2 ∧
      twoBlockSuccess.finalization.map (fun finalization => finalization.head) =
        some 2 ∧
      twoBlockSuccess.finalization.map (fun finalization => finalization.markedProcessed) =
        some true ∧
      twoBlockSuccess.finalization.map (fun finalization => finalization.updateSucceeded) =
        some true ∧
      twoBlockSuccess.successfulBlocks.length = 2 ∧
      twoBlockSuccess.successfulInputs.length = 2 ∧
      twoBlockSuccess.committedStates = [ 10, 10 ] ∧
      twoBlockSuccess.inclusionSignals = [ (0, true), (1, true) ] ∧
      twoBlockSuccess.commitAttempts = 2 ∧
      twoBlockSuccess.commitCompletions = 2 ∧
      twoBlockSuccess.scopeEvents =
        [ .opened, .resetAfterSuccessfulBlock 0, .resetAfterSuccessfulBlock 1, .disposed ] ∧
      twoBlockSuccess.trace =
        [ .suggestedBlockValidation, .synchronousBranchSelectionAndPreparation,
          .senderAndAuthorityRecovery, .senderAndAuthorityRecovery, .openWorldStateScope ] ++
          (Phase.processOne ++ [ .commitTree ]) ++
          (Phase.processOne ++ [ .commitTree ]) ++
          [ .synchronousResultClassificationAndHeadFinalization ] := by
  native_decide

theorem outer_single_block_trace_is_pinned_seventeen_phase_trace :
    (runBranch branchSpec sequentialPolicy 10 [ blockInput acceptAll ]).outcome = .accepted ∧
      (runBranch branchSpec sequentialPolicy 10 [ blockInput acceptAll ]).trace = Phase.all ∧
      (runBranch branchSpec sequentialPolicy 10 [ blockInput acceptAll ]).commitAttempts = 1 ∧
      (runBranch branchSpec sequentialPolicy 10 [ blockInput acceptAll ]).commitCompletions = 1 ∧
      (runBranch branchSpec sequentialPolicy 10 [ blockInput acceptAll ]).finalization.isSome ∧
      (runBranch branchSpec sequentialPolicy 10 [ blockInput acceptAll ]).scopeEvents =
        [ .opened, .resetAfterSuccessfulBlock 0, .disposed ] := by
  native_decide

theorem failed_block_hides_successful_prefix_from_canonical_state :
    secondBlockFailure.outcome = .rejected 1 (.transactionExecution 0) ∧
    secondBlockException.outcome = .exception 1 (.blockHook (.phase .rewards)) ∧
      secondBlockFailure.logicalState = 10 ∧
      secondBlockFailure.committedPrefixState = 11 ∧
      secondBlockFailure.finalization = none ∧
      secondBlockFailure.successfulInputs.length = 1 ∧
      secondBlockFailure.successfulBlocks.length = 1 ∧
      secondBlockFailure.committedStates = [ 11 ] ∧
      secondBlockFailure.commitAttempts = 1 ∧
      secondBlockFailure.commitCompletions = 1 ∧
      secondBlockFailure.trace =
        [ .suggestedBlockValidation, .synchronousBranchSelectionAndPreparation,
          .senderAndAuthorityRecovery, .senderAndAuthorityRecovery, .openWorldStateScope ] ++
          (Phase.processOne ++ [ .commitTree ]) ++
          Phase.processOne.take 4 ++
          [ .synchronousResultClassificationAndHeadFinalization ] ∧
      secondBlockFailure.scopeEvents =
        [ .opened, .resetAfterSuccessfulBlock 0, .disposed ] ∧
      secondBlockFailure.failedBlock.map (fun block => block.world.stateToken) = some 11 := by
  native_decide

theorem exception_prefix_vector_preserves_first_k_order :
    secondBlockException.outcome = .exception 1 (.blockHook (.phase .rewards)) ∧
      secondBlockException.successfulInputs.length = 1 ∧
      secondBlockException.successfulBlocks.length = 1 ∧
      secondBlockException.committedStates = [ 10 ] ∧
      secondBlockException.logicalState = 10 ∧
      secondBlockException.committedPrefixState = 10 ∧
      secondBlockException.failedBlock.map (fun block => block.trace) =
        some (Phase.processOne.take 6) := by
  native_decide

theorem rejected_classification_follows_disposal_without_success_hooks :
    secondBlockFailure.outcome = .rejected 1 (.transactionExecution 0) ∧
      secondBlockFailure.scopeEvents =
        [ .opened, .resetAfterSuccessfulBlock 0, .disposed ] ∧
      secondBlockFailure.finalization = none ∧
      secondBlockFailure.trace.getLast? =
        some .synchronousResultClassificationAndHeadFinalization ∧
      secondBlockException.outcome = .exception 1 (.blockHook (.phase .rewards)) ∧
      secondBlockException.trace.getLast? ≠
        some .synchronousResultClassificationAndHeadFinalization := by
  native_decide

theorem sequential_retry_records_dispose_before_reopen :
    sequentialRetrySuccess.outcome = .accepted ∧
      sequentialRetrySuccess.logicalState = 10 ∧
      sequentialRetrySuccess.attemptModes =
        [ .parallelAttempt, .retryableBalFailure, .scopeRestored, .forcedSequentialAttempt ] ∧
      sequentialRetrySuccess.parallelAttempts.map (fun attempt => attempt.outcome) =
        [ .retryableBalFailure ] ∧
      sequentialRetrySuccess.parallelAttempts.map (fun attempt => attempt.failedState.stateToken) =
        [ 1_010 ] ∧
      sequentialRetrySuccess.scopeRestorations.length = 1 ∧
      sequentialRetrySuccess.scopeRestorations.head?.map
          (fun restoration => restoration.before.stateToken) = some 1_010 ∧
      sequentialRetrySuccess.scopeRestorations.head?.map
          (fun restoration => restoration.after.stateToken) = some 10 ∧
      sequentialRetrySuccess.scopeEvents =
        [ .opened, .disposedForBalRetry 0, .reopenedForBalRetry 0,
          .resetAfterSuccessfulBlock 0, .disposed ] ∧
      sequentialRetrySuccess.commitAttempts = 1 ∧
      sequentialRetrySuccess.commitCompletions = 1 := by
  native_decide

theorem retry_keeps_the_second_sequential_failure :
    parallelRetryFailure.outcome = .rejected 0 (.transactionExecution 0) ∧
      parallelRetryFailure.attemptModes =
        [ .parallelAttempt, .retryableBalFailure, .scopeRestored,
          .forcedSequentialAttempt, .sequentialFailure ] ∧
      parallelRetryFailure.parallelAttempts.map (fun attempt => attempt.failure) =
        [ some (.retryableBalFailure 0) ] ∧
      parallelRetryFailure.scopeRestorations.length = 1 ∧
      parallelRetryFailure.logicalState = 10 ∧
      parallelRetryFailure.successfulBlocks = [] ∧
      parallelRetryFailure.commitAttempts = 0 ∧
      parallelRetryFailure.failedBlock.map (fun block => block.outcome) =
        some (.rejected (.transactionExecution 0)) := by
  native_decide

theorem parallel_failure_is_not_silently_retried :
    parallelFailureRun.outcome = .exception 0 (.nonBalParallel 0) ∧
      parallelFailureRun.attemptModes = [ .parallelAttempt, .parallelException ] ∧
      parallelFailureRun.parallelAttempts.map (fun attempt => attempt.outcome) =
        [ .invalid (.parallelExecutionFailure 0) ] ∧
      parallelFailureRun.commitAttempts = 0 ∧
      parallelFailureRun.scopeRestorations = [] := by
  native_decide

theorem parallel_success_remains_an_explicit_unknown :
    unprovedParallelRun.outcome = .unmodeledParallel 0 ∧
      unprovedParallelRun.logicalState = 10 ∧
      unprovedParallelRun.successfulBlocks = [] ∧
      unprovedParallelRun.committedStates = [] ∧
      unprovedParallelRun.attemptModes = [ .parallelAttempt, .parallelSuccessUnknown ] ∧
      unprovedParallelRun.parallelAttempts.map (fun attempt => attempt.outcome) =
        [ .successUnknown ] ∧
      unprovedParallelRun.scopeEvents = [ .opened, .disposed ] ∧
      unprovedParallelRun.commitAttempts = 0 := by
  native_decide

theorem inclusion_false_is_a_committed_signal :
    inclusionFalseRun.outcome = .accepted ∧
      inclusionFalseRun.inclusionSignals = [ (0, false) ] ∧
      inclusionFalseRun.commitAttempts = 1 ∧
      inclusionFalseRun.commitCompletions = 1 ∧
      inclusionFalseRun.successfulBlocks.length = 1 ∧
      inclusionFalseRun.finalization.isSome := by
  native_decide

theorem inclusion_checker_exception_escapes_before_commit :
    inclusionExceptionRun.outcome = .exception 0 (.blockHook .inclusionList) ∧
      inclusionExceptionRun.logicalState = 10 ∧
      inclusionExceptionRun.commitAttempts = 0 ∧
      inclusionExceptionRun.commitCompletions = 0 ∧
      inclusionExceptionRun.successfulBlocks = [] ∧
      inclusionExceptionRun.failedBlock.map (fun block => block.world.stateToken) = some 10 ∧
      inclusionExceptionRun.scopeEvents = [ .opened, .disposed ] := by
  native_decide

theorem prewarm_exception_escapes_before_commit :
    prewarmExceptionRun.outcome = .exception 0 (.blockHook .prewarm) ∧
      prewarmExceptionRun.commitAttempts = 0 ∧
      prewarmExceptionRun.successfulBlocks = [] := by
  native_decide

theorem commit_failure_distinguishes_attempt_from_completion :
    commitFailureRun.outcome = .exception 0 (.blockHook .commitTree) ∧
      commitFailureRun.logicalState = 10 ∧
      commitFailureRun.commitAttempts = 1 ∧
      commitFailureRun.commitCompletions = 0 ∧
      commitFailureRun.commitFailure = some (.escaped (.blockHook .commitTree)) ∧
      commitFailureRun.committedStates = [] ∧
      commitFailureRun.failedBlock.map (fun block => block.world.stateToken) = some 10 := by
  native_decide

theorem only_terminal_input_uses_suggested_validation :
    nonterminalSuggestedRejectionRun.outcome = .accepted ∧
      nonterminalSuggestedRejectionRun.trace.count .suggestedBlockValidation = 1 ∧
      nonterminalSuggestedRejectionRun.successfulBlocks.length = 2 ∧
      nonterminalSuggestedRejectionRun.commitCompletions = 2 := by
  native_decide

theorem simple_check_false_skips_before_selection_and_recovery :
    terminalSuggestedSkipRun.outcome = .skipped ∧
      terminalSuggestedSkipRun.trace = [ .suggestedBlockValidation ] ∧
      terminalSuggestedSkipRun.scopeEvents = [] ∧
      terminalSuggestedSkipRun.commitAttempts = 0 := by
  native_decide

theorem skipped_selection_does_not_recover_senders :
    skippedRun.outcome = .skipped ∧
      skippedRun.trace =
        [ .suggestedBlockValidation, .synchronousBranchSelectionAndPreparation ] ∧
      skippedRun.scopeEvents = [] ∧
      skippedRun.commitAttempts = 0 := by
  native_decide

theorem preparation_exception_escapes_before_sender_recovery :
    preparationExceptionRun.outcome = .exception 0 .preparation ∧
      preparationExceptionRun.trace =
        [ .suggestedBlockValidation,
          .synchronousBranchSelectionAndPreparation ] ∧
      preparationExceptionRun.scopeEvents = [] ∧
      preparationExceptionRun.commitAttempts = 0 := by
  native_decide

theorem sender_recovery_exception_escapes_before_scope :
    recoveryExceptionRun.outcome = .exception 0 .senderAuthorityRecovery ∧
      recoveryExceptionRun.trace =
        [ .suggestedBlockValidation,
          .synchronousBranchSelectionAndPreparation,
          .senderAndAuthorityRecovery ] ∧
      recoveryExceptionRun.scopeEvents = [] ∧
      recoveryExceptionRun.commitAttempts = 0 := by
  native_decide

theorem non_bal_parallel_exception_is_not_invalid_rejection :
    parallelExceptionRun.outcome = .exception 0 (.nonBalParallel 0) ∧
      parallelExceptionRun.attemptModes = [ .parallelAttempt, .parallelException ] ∧
      parallelExceptionRun.parallelAttempts.map (fun attempt => attempt.outcome) =
        [ .escaped (.nonBalParallel 0) ] ∧
      parallelExceptionRun.commitAttempts = 0 ∧
      parallelExceptionRun.scopeRestorations = [] := by
  native_decide

theorem scope_exception_is_not_invalid_block_rejection :
    scopeFailureRun.outcome = .exception 0 (.blockHook .worldStateScope) ∧
      scopeFailureRun.scopeEvents = [] ∧
      scopeFailureRun.commitAttempts = 0 := by
  native_decide

theorem invalid_preopened_scope_is_an_escape_and_is_not_disposed :
    invalidPreOpenedRun.outcome = .exception 0 .preOpenedScope ∧
      invalidPreOpenedRun.scopeEvents = [] ∧
      invalidPreOpenedRun.commitAttempts = 0 ∧
      invalidPreOpenedRun.logicalState = 10 := by
  native_decide

theorem checkpoint_reopens_only_inside_a_long_branch :
    checkpointRun.outcome = .accepted ∧
      checkpointRun.scopeEvents.count (.reopenedAtCheckpoint 64) = 1 ∧
      checkpointRun.scopeEvents.count (.disposedAtCheckpoint 64) = 1 ∧
      checkpointRun.scopeEvents.head? = some .opened ∧
      checkpointRun.scopeEvents.getLast? = some .disposed ∧
      checkpointRun.commitAttempts = 66 ∧ checkpointRun.commitCompletions = 66 := by
  native_decide

theorem checkpoint_scope_event_vector_is_exact :
    checkpointRun.scopeEvents = expectedCheckpointScopeEvents := by
  native_decide

theorem genesis_uses_the_preopened_scope_boundary :
    genesisRun.outcome = .accepted ∧
      genesisRun.scopeEvents =
        [ .preOpenedForGenesis, .resetAfterSuccessfulBlock 0 ] ∧
      genesisRun.trace.take (genesisPreprocessTrace.length + 1) = genesisPreprocessTrace ++
        [ .daoTransition ] := by
  native_decide

theorem background_receipt_root_installation_is_ordered :
    backgroundSuccessRun.outcome = .accepted ∧
      backgroundSuccessRun.successfulBlocks.head?.map
          (fun block => block.world.observations.receiptBloomTrace) =
        some [ .started, .scheduledBackground, .awaitedBackground,
          .installedBloom, .installedReceiptsRoot ] ∧
      backgroundSuccessRun.successfulBlocks.head?.map
          (fun block => block.world.observations.taskReceiptBloomTrace) =
        some [ .computedReceiptBlooms, .accumulatedBlockBloom, .computedReceiptsRoot ] := by
  native_decide

theorem background_threshold_and_empty_fallback_are_explicit :
    backgroundReceiptWorkEligible [] = false ∧
      backgroundReceiptWorkEligible ((List.range 15).map (fun id =>
        { id := id, status := 1, cumulativeGasUsed := 0, gasUsed := 0,
          logs := [], logsBloom := 0, contractAddress := 0 })) = false ∧
      backgroundReceiptWorkEligible ((List.range 16).map (fun id =>
        { id := id, status := 1, cumulativeGasUsed := 0, gasUsed := 0,
          logs := [], logsBloom := 0, contractAddress := 0 })) = true ∧
      backgroundFallbackRun.outcome = .accepted ∧
      backgroundFallbackRun.successfulBlocks.head?.map
          (fun block => block.world.observations.receiptBloomTrace) =
        some [ .started, .computedReceiptBlooms, .computedReceiptsRoot,
          .installedReceiptsRoot ] ∧
      backgroundFallbackRun.successfulBlocks.head?.map
          (fun block => block.world.observations.taskReceiptBloomTrace) = some [] ∧
      backgroundFallbackRun.successfulBlocks.head?.map
          (fun block => block.world.observations.taskControlTrace) = some [] := by
  native_decide

theorem background_requests_use_original_receipts_before_await :
    backgroundRequestMutationRun.outcome = .accepted ∧
      backgroundRequestMutationRun.successfulBlocks.head?.map
          (fun block => block.world.artifacts.executionRequests) = some 16 ∧
      backgroundRequestMutationRun.successfulBlocks.head?.map
          (fun block => block.world.artifacts.requestsHash) = some 1_016 ∧
      backgroundRequestMutationRun.successfulBlocks.head?.map
          (fun block => block.world.header.requestsHash) = some 1_016 ∧
      backgroundRequestMutationRun.successfulBlocks.head?.map
          (fun block => block.world.receipts.map (fun receipt => receipt.status)) =
        some (List.replicate 16 11) ∧
      backgroundRequestMutationRun.successfulBlocks.head?.map
          (fun block => block.world.receipts.map (fun receipt => receipt.logs.length)) =
        some (List.replicate 16 1) ∧
      requestVisibleProjection ((List.range 16).map (fun id =>
        { id := id, status := 1, cumulativeGasUsed := 0, gasUsed := 0,
          logs := [], logsBloom := 0, contractAddress := 0 })) = 16 ∧
      requestVisibleProjection ((List.range 16).map (fun id =>
        requestMutatedReceipt
          { id := id, status := 1, cumulativeGasUsed := 0, gasUsed := 0,
            logs := [], logsBloom := 0, contractAddress := 0 })) = 192 := by
  native_decide

theorem background_receipt_control_order_matches_task_tuple :
    backgroundSuccessRun.successfulBlocks.head?.map
        (fun block => block.world.observations.controlTrace) =
      some [ .traceStarted, .executionContextSet, .balSetup,
        .postTransactionCommit, .receiptBloomStarted, .executionRequestsExtracted,
        .executionRequestsHashed, .traceEnded, .blockBloomInstalled,
        .receiptsRootInstalled, .balFinalized ] ∧
    backgroundSuccessRun.successfulBlocks.head?.map
        (fun block => block.world.observations.taskControlTrace) =
      some [ .receiptBloomsComputed, .blockBloomAccumulated, .receiptsRootComputed ] := by
  native_decide

theorem background_receipt_root_failure_escapes_at_await :
    backgroundFailureRun.outcome = .exception 0 (.receiptBloomCalculation .background) ∧
      backgroundFailureRun.commitAttempts = 0 ∧
      backgroundFailureRun.failedBlock.map (fun block => block.trace) =
        some (Phase.processOne.take 10) ∧
      backgroundFailureRun.failedBlock.map (fun block => block.finallyAwait) =
        some (.failed (.receiptBloomCalculation .background) []) ∧
      backgroundFailureRun.failedBlock.map
          (fun block => block.world.artifacts.generatedBlockAccessList) = some 0 := by
  native_decide

theorem background_receipt_failures_preserve_task_completion_prefixes :
    backgroundBlockBloomFailureRun.outcome =
        .exception 0 (.blockBloomAccumulation .background) ∧
      backgroundBlockBloomFailureRun.failedBlock.map (fun block => block.trace) =
        some (Phase.processOne.take 10) ∧
      backgroundBlockBloomFailureRun.failedBlock.map (fun block => block.finallyAwait) =
        some (.failed (.blockBloomAccumulation .background) [ .computedReceiptBlooms ]) ∧
      backgroundReceiptsRootFailureRun.outcome =
        .exception 0 (.receiptsRootCalculation .background) ∧
      backgroundReceiptsRootFailureRun.failedBlock.map (fun block => block.trace) =
        some (Phase.processOne.take 10) ∧
      backgroundReceiptsRootFailureRun.failedBlock.map (fun block => block.finallyAwait) =
        some (.failed (.receiptsRootCalculation .background)
          [ .computedReceiptBlooms, .accumulatedBlockBloom ]) := by
  native_decide

theorem primary_exception_precedes_background_await_failure :
    backgroundPrimaryFailureRun.outcome =
        .exception 0 (.blockHook (.phase .rewards)) ∧
      backgroundPrimaryFailureRun.failedBlock.map (fun block => block.finallyAwait) =
        some (.failed (.receiptBloomCalculation .background) []) ∧
      backgroundPrimaryFailureRun.failedBlock.map (fun block => block.trace) =
        some (Phase.processOne.take 6) ∧
      backgroundPrimaryFailureRun.failedBlock.map
          (fun block => block.world.artifacts.generatedBlockAccessList) = some 0 ∧
      backgroundPrimaryFailureRun.commitAttempts = 0 := by
  native_decide

theorem failed_head_update_is_an_accepted_observation :
    failedHeadUpdateRun.outcome = .accepted ∧
      failedHeadUpdateRun.finalization.map (fun finalization => finalization.head) =
        some 901 ∧
      failedHeadUpdateRun.finalization.map (fun finalization => finalization.updateSucceeded) =
        some false ∧
      failedHeadUpdateRun.finalization.map (fun finalization => finalization.markedProcessed) =
        some true ∧
      failedHeadUpdateRun.finalization.map (fun finalization => finalization.completedSteps) =
         some [ .totalDifficulty, .mainChainUpdate, .markProcessed ] ∧
      failedHeadUpdateRun.finalization.map (fun finalization => finalization.failedStep) =
        some none ∧
      failedHeadUpdateRun.commitAttempts = 1 ∧
      failedHeadUpdateRun.commitCompletions = 1 := by
  native_decide

theorem finalization_total_difficulty_failure_preserves_committed_prefix :
    finalizationTotalDifficultyFailureRun.outcome =
        .finalizationException 0 (.blockHook .finalization) ∧
      finalizationTotalDifficultyFailureRun.logicalState = 10 ∧
      finalizationTotalDifficultyFailureRun.committedPrefixState = 10 ∧
      finalizationTotalDifficultyFailureRun.finalization.map
           (fun finalization => finalization.completedSteps) = some [] ∧
      finalizationTotalDifficultyFailureRun.finalization.map
          (fun finalization => finalization.failedStep) = some (some .totalDifficulty) ∧
      finalizationTotalDifficultyFailureRun.commitCompletions = 1 := by
  native_decide

theorem finalization_head_failure_preserves_completed_total_difficulty :
    finalizationHeadFailureRun.outcome =
        .finalizationException 1 (.blockHook .finalization) ∧
      finalizationHeadFailureRun.logicalState = 10 ∧
      finalizationHeadFailureRun.committedPrefixState = 10 ∧
      finalizationHeadFailureRun.finalization.map
          (fun finalization => finalization.totalDifficulty) = some 101 ∧
      finalizationHeadFailureRun.finalization.map
          (fun finalization => finalization.completedSteps) =
        some [ .totalDifficulty ] ∧
      finalizationHeadFailureRun.finalization.map
          (fun finalization => finalization.failedStep) = some (some .mainChainUpdate) ∧
      finalizationHeadFailureRun.commitCompletions = 1 := by
  native_decide

theorem finalization_mark_failure_preserves_completed_head_effects :
    finalizationMarkFailureRun.outcome =
        .finalizationException 2 (.blockHook .finalization) ∧
      finalizationMarkFailureRun.logicalState = 10 ∧
      finalizationMarkFailureRun.committedPrefixState = 10 ∧
      finalizationMarkFailureRun.finalization.map
          (fun finalization => finalization.totalDifficulty) = some 101 ∧
      finalizationMarkFailureRun.finalization.map
          (fun finalization => finalization.head) = some 901 ∧
      finalizationMarkFailureRun.finalization.map
          (fun finalization => finalization.completedSteps) =
        some [ .totalDifficulty, .mainChainUpdate ] ∧
      finalizationMarkFailureRun.finalization.map
          (fun finalization => finalization.failedStep) = some (some .markProcessed) ∧
      finalizationMarkFailureRun.commitCompletions = 1 := by
  native_decide

def allBranchVectorsPass : Bool :=
  decide (twoBlockSuccess.outcome = .accepted ∧
    secondBlockFailure.outcome = .rejected 1 (.transactionExecution 0) ∧
    sequentialRetrySuccess.attemptModes =
      [ .parallelAttempt, .retryableBalFailure, .scopeRestored, .forcedSequentialAttempt ] ∧
    parallelRetryFailure.outcome = .rejected 0 (.transactionExecution 0) ∧
    parallelFailureRun.outcome = .exception 0 (.nonBalParallel 0) ∧
    parallelExceptionRun.outcome = .exception 0 (.nonBalParallel 0) ∧
    unprovedParallelRun.outcome = .unmodeledParallel 0 ∧
    inclusionFalseRun.commitCompletions = 1 ∧
    inclusionExceptionRun.commitAttempts = 0 ∧
    prewarmExceptionRun.commitAttempts = 0 ∧
    commitFailureRun.commitAttempts = 1 ∧ commitFailureRun.commitCompletions = 0 ∧
    nonterminalSuggestedRejectionRun.commitCompletions = 2 ∧
    terminalSuggestedSkipRun.outcome = .skipped ∧
    checkpointRun.scopeEvents.count (.reopenedAtCheckpoint 64) = 1 ∧
    checkpointRun.scopeEvents = expectedCheckpointScopeEvents ∧
    genesisRun.scopeEvents.head? = some .preOpenedForGenesis ∧
    genesisRun.scopeEvents.getLast? = some (.resetAfterSuccessfulBlock 0) ∧
    invalidPreOpenedRun.outcome = .exception 0 .preOpenedScope ∧
    preparationExceptionRun.outcome = .exception 0 .preparation ∧
    recoveryExceptionRun.outcome = .exception 0 .senderAuthorityRecovery ∧
    skippedRun.outcome = .skipped ∧
    scopeFailureRun.outcome = .exception 0 (.blockHook .worldStateScope) ∧
    finalizationTotalDifficultyFailureRun.outcome =
      .finalizationException 0 (.blockHook .finalization) ∧
    finalizationHeadFailureRun.outcome =
      .finalizationException 1 (.blockHook .finalization) ∧
    finalizationMarkFailureRun.outcome =
      .finalizationException 2 (.blockHook .finalization) ∧
    failedHeadUpdateRun.outcome = .accepted ∧
    backgroundSuccessRun.outcome = .accepted ∧
    backgroundRequestMutationRun.outcome = .accepted ∧
    backgroundRequestMutationRun.successfulBlocks.head?.map
      (fun block => block.world.artifacts.executionRequests) = some 16 ∧
    backgroundFailureRun.outcome = .exception 0 (.receiptBloomCalculation .background) ∧
    backgroundBlockBloomFailureRun.outcome =
      .exception 0 (.blockBloomAccumulation .background) ∧
    backgroundReceiptsRootFailureRun.outcome =
      .exception 0 (.receiptsRootCalculation .background) ∧
    backgroundPrimaryFailureRun.outcome =
      .exception 0 (.blockHook (.phase .rewards)))

theorem all_branch_vectors_pass : allBranchVectorsPass = true := by
  native_decide

/-! There is deliberately no theorem relating a parallel success to the
sequential relation. The only theorem about the selected mode records its
explicit unproved marker. -/
theorem parallel_mode_remains_unproved :
    selectAttempt
      { parallelOutcome := fun _ => .successUnknown
        parallelFailedState := fun _ world => world }
      0 parallelEligibility = .parallelSuccessUnknown := by
  native_decide

end Vectors
end BranchReference
end BlockReference
end Eip803x
