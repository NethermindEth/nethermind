-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.BlockSystemComposition

namespace Eip803x
namespace BlockSystemComposition
namespace Vectors

open BlockReference
open SystemTransactionReference

private instance {ε α : Type} [DecidableEq ε] [DecidableEq α] :
    DecidableEq (Except ε α)
  | .error left, .error right =>
      if h : left = right then isTrue (by cases h; rfl)
      else isFalse (by intro equality; cases equality; exact h rfl)
  | .ok left, .ok right =>
      if h : left = right then isTrue (by cases h; rfl)
      else isFalse (by intro equality; cases equality; exact h rfl)
  | .error _, .ok _ => isFalse (by intro equality; contradiction)
  | .ok _, .error _ => isFalse (by intro equality; contradiction)

private def exceptIsError : Except ε α → Bool
  | .error _ => true
  | .ok _ => false

private def systemWorld (token : Nat) : SystemWorld :=
  { token := token, senderBalance := 100, recipientBalance := 0, senderNonce := 7 }

private def discardedWorld : SystemWorld :=
  { token := 999, senderBalance := 1, recipientBalance := 99, senderNonce := 88 }

private def settlement : SettlementObservation :=
  { spentGas := 21
  , operationGas := 20
  , effectiveBlockGas := 21
  , blockStateGas := 4
  , maxUsedGas := 21
  , gasRefund := 2
  , remainingGas := 29_999_979
  , remainingStateReservoir := 1_566_716
  , refundCounter := 2 }

private def stateGasOutOfGasSettlement : SettlementObservation :=
  { settlement with
    blockStateGas := 1_566_721
    remainingGas := 0
    remainingStateReservoir := 0 }

private def successfulBody (before after : SystemWorld) (output : List Nat := []) :
    BodyObservation :=
  .evm (.success
    { expectedInputWorld := before
    , worldAfter := after
    , returndata := output
    , settlement := settlement })

private def beaconAdapter : SystemAdapterState :=
  { normalReceipts := [], cumulativeReceiptGas := 0, requestPayloads := [] }

private def beaconCall : SystemCallInput :=
  let root := List.range 32
  let tx : Transaction :=
    { kind := .systemCall
    , sender := .systemUser
    , destination := some beaconRootsAddress
    , data := root
    , gasLimit := 31_566_720
    , accessList := some { addresses := [beaconRootsAddress], storageKeys := [] } }
  { site := .beaconRoot
  , enabled := true
  , targetHasCode := true
  , beaconRootCalldata := root
  , processorInput :=
      { tx := tx
      , initialWorld := systemWorld 11
      , initialCommittedWorld := systemWorld 11
      , oracle :=
          { ordinaryIntrinsic := calculateAmsterdamIntrinsic tx
          , body := successfulBody (systemWorld 11) (systemWorld 20) } }
  , adapterState := beaconAdapter }

private def adapterState (payloads : List (List Nat)) : SystemAdapterState :=
  { normalReceipts := [5]
  , cumulativeReceiptGas := 7
  , requestPayloads := payloads }

private def requestCall (site : BlockCallSite) (before after : Nat)
    (payloads output : List (List Nat)) : SystemCallInput :=
  let tx : Transaction :=
    { kind := .systemCall
    , sender := .systemUser
    , destination := site.destination
    , gasLimit := 31_566_720 }
  { site := site
  , enabled := true
  , targetHasCode := true
  , requestTypeByte := site.requestType.getD 0
  , processorInput :=
      { tx := tx
      , initialWorld := systemWorld before
      , initialCommittedWorld := systemWorld before
      , tracingReceipt := true
      , tracerKind := .callOutput
      , oracle :=
          { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
          , body := successfulBody (systemWorld before) (systemWorld after)
              output.flatten } }
  , adapterState := adapterState payloads }

private def deposits : List (List Nat) := [[0, 9]]

private def withdrawalCall : SystemCallInput :=
  requestCall .withdrawalRequests 33 34 deposits [[11]]

private def consolidationCall : SystemCallInput :=
  requestCall .consolidationRequests 34 35 (deposits ++ [[1, 11]]) [[22]]

private def builderDepositCall : SystemCallInput :=
  requestCall .builderDepositRequests 35 36
    (deposits ++ [[1, 11], [2, 22]]) [[33]]

private def builderExitCall : SystemCallInput :=
  requestCall .builderExitRequests 36 37
    (deposits ++ [[1, 11], [2, 22], [3, 33]]) [[44]]

private def requestCalls : List SystemCallInput :=
  [withdrawalCall, consolidationCall, builderDepositCall, builderExitCall]

private def encodePayloads (payloads : List (List Nat)) : Nat :=
  payloads.length * 100 +
    payloads.foldl (fun total payload => total + payload.foldl (· + ·) 0) 0

private def executionFor (_ : Nat) (_ : UserTransaction) (state : Nat) :
    Option TransactionExecution :=
  some
    { settlement :=
        { gasUsedBeforeRefund := 8
          gasRefund := 1
          gasUsedAfterRefund := 7
          paidGas := 7
          stateGas := 4
          executionGas := 7 }
      nextState := state + 5
      receipt :=
        { id := 5
          status := 1
          cumulativeGasUsed := 0
          gasUsed := 999
          logs := []
          logsBloom := 0
          contractAddress := 0 } }

private def baseSpec : BlockSpec :=
  { executeTransaction := executionFor
    applyDao := fun state => state + 10
    startBlockTrace := fun observation => observation + 1
    setBlockExecutionContext := fun observation => observation + 1
    setupBlockAccessList := fun observation => observation + 1
    commitPostTransactionState := fun input => .completed input.stateToken
    applyBeaconRoot := fun state => state
    applyBlockhashState := fun state => state + 1
    commitPreSystemState := fun state => state + 1
    applyRewards := fun state => state + 2
    applyWithdrawals := fun state => state + 3
    commitWithdrawalState := fun state => state + 1
    applyExecutionRequests := fun state _ => state
    endBlockTrace := fun observation => observation + 1
    commitStorageRoots := fun state => state + 4
    setBlockAccessList := fun observation => observation + 1
    computeBlobGas := fun transactions => transactions.length
    computeReceiptBlooms := fun _ receipts => .completed receipts
    computeReceiptsRoot := fun _ receipts => .completed (receipts.length + 100)
    accumulateBlockBloom := fun _ receipts => .completed (receipts.length + 200)
    computeExecutionRequests := fun _ _ => 0
    computeRequestsHash := fun requests => requests
    computeAccountChanges := fun state => state + 300
    computeGeneratedBlockAccessList := fun state => state + 400
    computeGeneratedBlockAccessListHash := fun accessList => accessList + 500
    computeEncodedBlockAccessList := fun accessList => accessList + 600
    computeStateRoot := fun state => state + 700
    computeHeaderHash := fun header => header.stateRoot + header.requestsHash + 1
    validateProcessedHeader := fun _ _ _ _ => true }

private def header : HeaderObservation :=
  { parentHash := 1
  , unclesHash := 2
  , author := 3
  , beneficiary := 4
  , difficulty := 5
  , number := 6
  , txRoot := 7
  , gasLimit := 1_000
  , gasUsed := 999
  , timestamp := 8
  , extraData := []
  , mixHash := 9
  , nonce := 10
  , totalDifficulty := 11
  , baseFeePerGas := 12
  , executionGasUsed := 999
  , stateGasUsed := 999
  , cumulativeReceiptGasUsed := 999
  , blobGasUsed := 999
  , excessBlobGas := 999
  , receiptsRoot := 999
  , bloom := 999
  , withdrawalsRoot := 999
  , parentBeaconBlockRoot := 13
  , requestsHash := 999
  , stateRoot := 999
  , blockHash := 999
  , isPostMerge := true
  , slotNumber := 14
  , blockAccessListHash := 15 }

private def sequentialEligibility : ParallelEligibility :=
  { isGenesis := false
  , balEnabled := false
  , parallelRequested := false
  , parentScopeAvailable := false
  , forceSequential := true }

private def blockInput : BlockInput :=
  { initialState := 1
  , transactions := [{ id := 5 }]
  , proposedHeader := header
  , phaseAccepted := fun _ => true
  , phaseException := fun _ => none
  , parallelEligibility := sequentialEligibility
  , receiptBloomPath := .synchronous
  , scopePreOpened := false
  , baseBlock := none }

private def adapter : Adapter :=
  { baseSpec := baseSpec
  , beaconCall := beaconCall
  , beaconStateAfter := 20
  , requestCalls := requestCalls
  , requestWorldBefore := systemWorld 33
  , requestWorldAfter := systemWorld 37
  , depositPayloads := deposits
  , encodeRequests := encodePayloads
  , hashRequests := fun requests => requests + 1_000
  , encodedRequests := 629
  , requestsHash := 1_629 }

def successfulRun : CompositionRun := runBlock adapter blockInput

theorem adapter_is_admitted : validateAdapter adapter blockInput = .ok () := by
  native_decide

theorem successful_block_composes_both_system_surfaces :
    successfulRun.blockOutcome = some .accepted ∧
      successfulRun.trace = some Phase.processOne ∧
      successfulRun.world.map (fun world => world.stateToken) = some 41 := by
  native_decide

theorem successful_block_preserves_user_gas_and_receipt_accounting :
    successfulRun.world.map (fun world => world.counters) = some
        { executionGasUsed := 7, stateGasUsed := 4, cumulativeReceiptGasUsed := 7 } ∧
      successfulRun.world.map (fun world => world.receipts.map
        (fun receipt => (receipt.id, receipt.gasUsed, receipt.cumulativeGasUsed))) =
          some [(5, 7, 7)] := by
  native_decide

theorem successful_request_payload_is_visible_when_hash_is_installed :
    successfulRun.world.map (fun world => world.artifacts.executionRequests) = some 629 ∧
      successfulRun.world.map (fun world => world.artifacts.requestsHash) = some 1_629 ∧
      successfulRun.world.map (fun world => world.header.requestsHash) = some 1_629 ∧
      successfulRun.world.map (fun world => world.observations.controlTrace.contains
        BlockControlEvent.executionRequestsExtracted) = some true ∧
      successfulRun.world.map (fun world => world.observations.controlTrace.contains
        BlockControlEvent.executionRequestsHashed) = some true := by
  native_decide

theorem request_batch_threads_world_and_adapter_state :
    runRequestCalls exactRequestSites requestCalls (systemWorld 33)
        (adapterState deposits) =
      .ok
        { world := systemWorld 37
        , adapterState := adapterState
            (deposits ++ [[1, 11], [2, 22], [3, 33], [4, 44]]) } := by
  native_decide

private def beaconRevert : SystemCallInput :=
  { beaconCall with
    processorInput :=
      { beaconCall.processorInput with
        oracle :=
          { ordinaryIntrinsic := beaconCall.processorInput.oracle.ordinaryIntrinsic
          , body := .evm (.revert
              { expectedInputWorld := systemWorld 11
              , discardedWorld := discardedWorld
              , returndata := [9]
              , settlement := settlement }) } } }

private def beaconException : SystemCallInput :=
  { beaconCall with
    processorInput :=
      { beaconCall.processorInput with
        oracle :=
          { ordinaryIntrinsic := beaconCall.processorInput.oracle.ordinaryIntrinsic
          , body := .evm (.exceptionalHalt
              { expectedInputWorld := systemWorld 11
              , discardedWorld := discardedWorld
              , exceptionKind := some 17
              , settlement := settlement }) } } }

theorem beacon_handler_ignores_revert_and_exception_results_but_state_is_rolled_back :
    beaconTransition beaconRevert 11 = .ok 11 ∧
      beaconTransition beaconException 11 = .ok 11 := by
  native_decide

private def failingRequest (body : BodyObservation) : SystemCallInput :=
  { withdrawalCall with
    processorInput :=
      { withdrawalCall.processorInput with
        oracle :=
          { withdrawalCall.processorInput.oracle with body := body } } }

private def revertedRequest : SystemCallInput :=
  failingRequest (.evm (.revert
    { expectedInputWorld := systemWorld 33
    , discardedWorld := discardedWorld
    , returndata := [9]
    , settlement := settlement }))

private def exceptionalRequest : SystemCallInput :=
  failingRequest (.evm (.exceptionalHalt
    { expectedInputWorld := systemWorld 33
    , discardedWorld := discardedWorld
    , exceptionKind := some 17
    , settlement := settlement }))

private def stateGasOutOfGasRequest : SystemCallInput :=
  failingRequest (.evm (.topFrameOutOfGas stateGasOutOfGasSettlement))

theorem request_revert_exception_and_state_gas_oog_are_block_invalid :
    (runRequestCalls exactRequestSites (revertedRequest :: requestCalls.drop 1)
      (systemWorld 33) (adapterState deposits) =
        .error (.requestInvalid .withdrawalRequests)) ∧
    (runRequestCalls exactRequestSites (exceptionalRequest :: requestCalls.drop 1)
      (systemWorld 33) (adapterState deposits) =
        .error (.requestInvalid .withdrawalRequests)) ∧
    (runRequestCalls exactRequestSites (stateGasOutOfGasRequest :: requestCalls.drop 1)
      (systemWorld 33) (adapterState deposits) =
        .error (.requestInvalid .withdrawalRequests)) := by
  native_decide

private def invalidTracerRequest : SystemCallInput :=
  { withdrawalCall with
    processorInput :=
      { withdrawalCall.processorInput with tracerKind := .null } }

private def invalidForkRequest : SystemCallInput :=
  { withdrawalCall with
    processorInput :=
      { withdrawalCall.processorInput with
        spec := { Spec.pinnedAmsterdam with eip8038 := false } } }

private def invalidBodyRequest : SystemCallInput :=
  { withdrawalCall with
    processorInput :=
      { withdrawalCall.processorInput with
        oracle :=
          { withdrawalCall.processorInput.oracle with
            body := .simpleTransfer
              { expectedInputWorld := systemWorld 33
              , worldAfter := systemWorld 34
              , settlement := settlement } } } }

theorem invalid_tracer_fork_and_body_shapes_fail_closed :
    (runRequestCalls exactRequestSites (invalidTracerRequest :: requestCalls.drop 1)
      (systemWorld 33) (adapterState deposits) =
        .error (.requestOutcome .withdrawalRequests)) ∧
    (runRequestCalls exactRequestSites (invalidForkRequest :: requestCalls.drop 1)
      (systemWorld 33) (adapterState deposits) =
        .error (.requestOutcome .withdrawalRequests)) ∧
    (runRequestCalls exactRequestSites (invalidBodyRequest :: requestCalls.drop 1)
      (systemWorld 33) (adapterState deposits) =
        .error (.requestOutcome .withdrawalRequests)) := by
  native_decide

private def positiveValueCreateRequest : SystemCallInput :=
  { withdrawalCall with
    processorInput :=
      { withdrawalCall.processorInput with
        tx := { withdrawalCall.processorInput.tx with destination := none, value := 1 } } }

theorem positive_value_create_is_unreachable_at_the_pinned_call_sites :
    positiveValueCreateRequest.processorInput.tx.isContractCreation = true ∧
      positiveValueCreateRequest.processorInput.tx.value = 1 ∧
      blockCallShapeValid positiveValueCreateRequest = false ∧
      (runBlockCall positiveValueCreateRequest).disposition = .unmodeled := by
  native_decide

private def rejectedAfterRequestsInput : BlockInput :=
  { blockInput with
    phaseAccepted := fun phase => phase != .storageAndStateRoots }

theorem downstream_rejection_restores_the_complete_entry_world :
    validateAdapter adapter rejectedAfterRequestsInput = .ok () ∧
      (runBlock adapter rejectedAfterRequestsInput).blockOutcome =
        some (.rejected (.phaseRejected .storageAndStateRoots)) ∧
      (runBlock adapter rejectedAfterRequestsInput).world =
        some (initialWorld rejectedAfterRequestsInput) := by
  native_decide

private def branchSpec : BlockReference.BranchReference.BranchSpec :=
  { block := baseSpec
  , synchronousBranchSelection := fun _ => .selected
  , openWorldStateScope := fun _ => .completed ()
  , checkInclusionList := fun _ _ _ => .completed true
  , prewarmSucceeded := fun _ _ _ => .completed ()
  , notReadOnly := true
  , reopenAtCheckpoint := fun _ => false
  , commitTree := fun _ _ _ => .completed ()
  , setTotalDifficulty := fun _ states => .completed (states.foldl (· + ·) 0)
  , updateMainChain := fun total _ _ => .completed (total + 1, true)
  , markProcessed := fun _ => .completed true }

private def retryPolicy : BlockReference.BranchReference.RetryPolicy :=
  { parallelOutcome := fun _ => .successUnknown
  , parallelFailedState := fun _ world => world }

def successfulBranch : BranchCompositionRun :=
  runValidatedSingletonBranch branchSpec adapter retryPolicy 1 [blockInput]

theorem synchronous_branch_composes_the_system_aware_block :
    successfulBranch.branchOutcome = some .accepted ∧
      successfulBranch.logicalState = some 41 ∧
      successfulBranch.trace = some Phase.all ∧
      successfulBranch.commitCounts = some (1, 1) := by
  native_decide

structure ScenarioVector where
  name : String
  passes : Bool
  deriving DecidableEq, Repr

def scenarioVectors : List ScenarioVector :=
  [ { name := "admitted composition"
    , passes := decide (validateAdapter adapter blockInput = .ok ()) }
  , { name := "successful composed block"
    , passes := decide
        (successfulRun.blockOutcome = some .accepted) }
  , { name := "threaded request batch"
    , passes := !exceptIsError
        (runRequestCalls exactRequestSites requestCalls (systemWorld 33)
          (adapterState deposits)) }
  , { name := "beacon revert is ignored after rollback"
    , passes := decide (beaconTransition beaconRevert 11 = .ok 11) }
  , { name := "beacon exception is ignored after rollback"
    , passes := decide (beaconTransition beaconException 11 = .ok 11) }
  , { name := "request revert invalidates block"
    , passes := decide
        (runRequestCalls exactRequestSites (revertedRequest :: requestCalls.drop 1)
          (systemWorld 33) (adapterState deposits) =
            .error (.requestInvalid .withdrawalRequests)) }
  , { name := "request exception invalidates block"
    , passes := decide
        (runRequestCalls exactRequestSites (exceptionalRequest :: requestCalls.drop 1)
          (systemWorld 33) (adapterState deposits) =
            .error (.requestInvalid .withdrawalRequests)) }
  , { name := "request state gas OOG invalidates block"
    , passes := decide
        (runRequestCalls exactRequestSites (stateGasOutOfGasRequest :: requestCalls.drop 1)
          (systemWorld 33) (adapterState deposits) =
            .error (.requestInvalid .withdrawalRequests)) }
  , { name := "invalid request tracer fails closed"
    , passes := decide
        (runRequestCalls exactRequestSites (invalidTracerRequest :: requestCalls.drop 1)
          (systemWorld 33) (adapterState deposits) =
            .error (.requestOutcome .withdrawalRequests)) }
  , { name := "invalid fork fails closed"
    , passes := decide
        (runRequestCalls exactRequestSites (invalidForkRequest :: requestCalls.drop 1)
          (systemWorld 33) (adapterState deposits) =
            .error (.requestOutcome .withdrawalRequests)) }
  , { name := "target and body mismatch fails closed"
    , passes := decide
        (runRequestCalls exactRequestSites (invalidBodyRequest :: requestCalls.drop 1)
          (systemWorld 33) (adapterState deposits) =
            .error (.requestOutcome .withdrawalRequests)) }
  , { name := "positive value CREATE is unreachable"
    , passes := decide
        ((runBlockCall positiveValueCreateRequest).disposition = .unmodeled) }
  , { name := "downstream rejection restores entry world"
    , passes := decide
        ((runBlock adapter rejectedAfterRequestsInput).world =
          some (initialWorld rejectedAfterRequestsInput)) }
  , { name := "synchronous branch accepts composed block"
    , passes := decide (successfulBranch.branchOutcome = some .accepted) } ]

theorem all_scenario_vectors_pass :
    scenarioVectors.length = 14 ∧ scenarioVectors.all (fun vector => vector.passes) = true := by
  native_decide

structure MutationVector where
  name : String
  killed : Bool
  deriving DecidableEq, Repr

private def swappedOrderState : Nat :=
  baseSpec.applyDao adapter.beaconStateAfter

private def systemAsUserExecution : TransactionExecution :=
  { settlement :=
      { gasUsedBeforeRefund := 21
      , gasRefund := 0
      , gasUsedAfterRefund := 21
      , paidGas := 21
      , stateGas := 4
      , executionGas := 21 }
  , nextState := 20
  , receipt :=
      { id := 999, status := 1, cumulativeGasUsed := 0, gasUsed := 0,
        logs := [], logsBloom := 0, contractAddress := 0 } }

def mutationVectors : List MutationVector :=
  [ { name := "beacon hook moved before DAO"
    , killed := swappedOrderState != 20 }
  , { name := "system call charged as a user transaction"
    , killed :=
        (applyExecution (initialWorld blockInput) systemAsUserExecution).counters !=
          (initialWorld blockInput).counters }
  , { name := "request system calls reversed"
    , killed :=
        exceptIsError (runRequestCalls exactRequestSites requestCalls.reverse (systemWorld 33)
          (adapterState deposits)) }
  , { name := "request output encoded differently"
    , killed :=
        let run := runValidatedSingletonBranch branchSpec
          { adapter with encodedRequests := 630 } retryPolicy 1 [blockInput]
        run.status == .unmodeled && run.failure == some .requestEncoding }
  , { name := "request hash installed before final request value"
    , killed :=
        let run := runValidatedSingletonBranch branchSpec
          { adapter with requestsHash := 1_630 } retryPolicy 1 [blockInput]
        run.status == .unmodeled && run.failure == some .requestHash }
  , { name := "request receipt projection changed"
    , killed :=
        exceptIsError (validateAdapter { adapter with depositPayloads := [[0, 8]] } blockInput) }
  , { name := "beacon body disagrees with target code"
    , killed :=
        exceptIsError (validateAdapter
          { adapter with
            beaconCall := { beaconCall with
              processorInput := { beaconCall.processorInput with
                oracle := { beaconCall.processorInput.oracle with
                  body := .simpleTransfer
                    { expectedInputWorld := systemWorld 11
                    , worldAfter := systemWorld 20
                    , settlement := settlement } } } } }
          blockInput) }
  , { name := "request world is not the post-withdrawal world"
    , killed :=
        exceptIsError (validateAdapter { adapter with requestWorldBefore := systemWorld 32 }
          blockInput) }
  , { name := "second block cannot reuse singleton adapter"
    , killed :=
        let run := runValidatedSingletonBranch branchSpec adapter retryPolicy 1
          [blockInput, blockInput]
        run.status == .unmodeled && run.failure == some .branchNotSingleton }
  , { name := "branch initial state cannot disagree with validated block"
    , killed :=
        let run := runValidatedSingletonBranch branchSpec adapter retryPolicy 2
          [blockInput]
        run.status == .unmodeled && run.failure == some .branchInitialState } ]

theorem every_composition_mutation_is_killed :
    mutationVectors.length = 10 ∧ mutationVectors.all (fun vector => vector.killed) = true := by
  native_decide

def allVectorsPass : Bool :=
  scenarioVectors.all (fun vector => vector.passes) &&
    mutationVectors.all (fun vector => vector.killed)

theorem all_vectors_pass : allVectorsPass = true := by
  native_decide

end Vectors
end BlockSystemComposition
end Eip803x
