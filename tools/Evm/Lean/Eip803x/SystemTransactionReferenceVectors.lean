-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.SystemTransactionReference

namespace Eip803x.SystemTransactionReferenceVectors

open Eip803x.SystemTransactionReference

private def initialWorld : World :=
  { token := 1, senderBalance := 100, recipientBalance := 0, senderNonce := 7 }

private def changedWorld : World :=
  { token := 2, senderBalance := 100, recipientBalance := 0, senderNonce := 7 }

private def paidWorld : World :=
  { token := 1, senderBalance := 93, recipientBalance := 0, senderNonce := 7 }

private def transferredWorld : World :=
  { token := 3, senderBalance := 93, recipientBalance := 7, senderNonce := 7 }

private def discardedWorld : World :=
  { token := 999, senderBalance := 1, recipientBalance := 99, senderNonce := 88 }

private def settlement : SettlementObservation :=
  { spentGas := 21
  , operationGas := 20
  , effectiveBlockGas := 21
  , blockStateGas := 4
  , maxUsedGas := 21
  , gasRefund := 2
  , remainingGas := 30_000_000 - 21
  , remainingStateReservoir := 1_566_716
  , refundCounter := 2 }

private def systemCallTx : Transaction :=
  { kind := .systemCall
  , sender := .systemUser
  , destination := some withdrawalRequestsAddress
  , gasLimit := 31_566_720 }

private def successBody (worldAfter : World := changedWorld)
    (returndata : List Nat := []) : BodyObservation :=
  .evm (.success
    { expectedInputWorld := initialWorld
    , worldAfter := worldAfter
    , returndata := returndata
    , logs := [44]
    , settlement := settlement })

private def systemCallInput : Input :=
  { tx := systemCallTx
  , initialWorld := initialWorld
  , initialCommittedWorld := initialWorld
  , oracle :=
      { ordinaryIntrinsic := { execution := 77, state := 88, floor := 99 }
      , body := successBody } }

example : systemCallInput.pinnedDomain = true ∧
    systemCallInput.spec.eip7708 = true ∧ systemCallInput.spec.eip8246 = true := by
  native_decide

example : (run systemCallInput).disposition = .completed := by native_decide

example : (run systemCallInput).effectiveOptions =
    { commit := true, skipValidation := true } := by
  native_decide

example : (run systemCallInput).payOriginalValue = true := by native_decide

example : (run systemCallInput).initialAvailableGas =
    some (createSystemCallAvailable Schedule.pinnedAmsterdam 31_566_720) := by
  native_decide

example : (run systemCallInput).initialAvailableGas = some
    { executionLeft := 30_000_000
    , stateReservoir := 1_566_720
    , stateUsed := 0
    , stateFromGasLeft := 0
    , stateFromGasLeftRefunded := 0 } := by
  native_decide

example : (run systemCallInput).state.world = changedWorld := by native_decide

example : (run systemCallInput).state.commitCount = 2 := by native_decide

example : (run systemCallInput).state.events =
    [ .routeToSystemProcessor
    , .acquireSystemProcessor
    , .beginSystemAccountReadSuppression
    , .beforeSystemTransactionHook
    , .deriveSystemOptions
    , .getHeader
    , .selectSystemSpec
    , .recoverSenderBeforeIntrinsic
    , .calculateIntrinsicGas
    , .validateStatic
    , .calculateEffectiveGasPrice
    , .updateMetrics
    , .recoverSenderIfNeeded
    , .validateSenderSkipped
    , .buyGas
    , .incrementNonce
    , .selectExecutionPath
    , .commitBeforeExecution
    , .calculateAvailableGas
    , .setTransactionExecutionContext
    , .buildExecutionEnvironment
    , .takeExecutionSnapshot
    , .executeEvm
    , .calculateRefundAndSettlement
    , .skipNormalBlockGasCounters
    , .payFeesBypassed
    , .finalizeDestroyList
    , .writeTransactionGasFields
    , .finalCommit
    , .disposeSystemAccountReadSuppression
    , .returnResult ] := by
  native_decide

private def plainSystemTx : Transaction :=
  { kind := .plain
  , sender := .systemUser
  , destination := some beaconRootsAddress
  , gasLimit := 30_000_000
  , value := 7 }

private def simpleInput : Input :=
  let tx : Transaction :=
    { kind := .systemTransaction
    , sender := .other "0x1111111111111111111111111111111111111111"
    , destination := some beaconRootsAddress
    , gasLimit := 30_000_000
    , value := 7 }
  { tx := tx
  , initialWorld := initialWorld
  , initialCommittedWorld := initialWorld
  , oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic tx
      , body := .simpleTransfer
          { expectedInputWorld := paidWorld
          , worldAfter := transferredWorld
          , settlement := settlement } } }

example : (run simpleInput).state.world = transferredWorld := by native_decide

example : (run simpleInput).state.events.contains .payOriginalValue = true := by
  native_decide

example : (run simpleInput).state.commitCount = 1 := by native_decide

example : (run simpleInput).transferLogs =
    [{ emitter := "0xfffffffffffffffffffffffffffffffffffffffe"
     , signature := "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"
     , fromAddress := "0x1111111111111111111111111111111111111111"
     , toAddress := "0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02"
     , value := 7 }] := by
  native_decide

private def simpleOutOfGasInput : Input :=
  { simpleInput with
    oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic simpleInput.tx
      , body := .simpleTransfer
          { expectedInputWorld := initialWorld
          , worldAfter := discardedWorld
          , stateGasOutOfGas := true
          , settlement := settlement } } }

example : (run simpleOutOfGasInput).receiptStatus = some .failure := by native_decide

example : (run simpleOutOfGasInput).state.world = initialWorld := by native_decide

example : (run simpleOutOfGasInput).state.events.contains .payOriginalValue = false := by
  native_decide

example : (run simpleOutOfGasInput).transferLogs = [] := by native_decide

private def zeroValueSimpleInput : Input :=
  let tx := { simpleInput.tx with value := 0 }
  { simpleInput with
    tx := tx
    oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic tx
      , body := .simpleTransfer
          { expectedInputWorld := initialWorld
          , worldAfter := changedWorld
          , settlement := settlement } } }

example : (run zeroValueSimpleInput).disposition = .completed ∧
    (run zeroValueSimpleInput).transferLogs = [] := by native_decide

private def selfSendSimpleInput : Input :=
  let tx := { simpleInput.tx with
    destination := some "0x1111111111111111111111111111111111111111" }
  { simpleInput with
    tx := tx
    oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic tx
      , body := .simpleTransfer
          { expectedInputWorld := initialWorld
          , worldAfter := changedWorld
          , settlement := settlement } } }

example : (run selfSendSimpleInput).disposition = .completed ∧
    (run selfSendSimpleInput).transferLogs = [] := by native_decide

private def revertedTransferInput : Input :=
  { simpleInput with
    oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic simpleInput.tx
      , body := .evm (.revert
          { expectedInputWorld := paidWorld
          , discardedWorld := discardedWorld
          , settlement := settlement }) } }

example : (run revertedTransferInput).receiptStatus = some .failure ∧
    (run revertedTransferInput).transferLogs = [] := by native_decide

private def failedTransferInput : Input :=
  { simpleInput with
    oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic simpleInput.tx
      , body := .evm (.exceptionalHalt
          { expectedInputWorld := paidWorld
          , discardedWorld := discardedWorld
          , exceptionKind := some 17
          , settlement := settlement }) } }

example : (run failedTransferInput).receiptStatus = some .failure ∧
    (run failedTransferInput).transferLogs = [] := by native_decide

private def creationTransaction (size : Nat) : Transaction :=
  { kind := .plain
  , sender := .systemUser
  , destination := none
  , data := List.replicate size 1
  , gasLimit := 30_000_000 }

example : calculateAmsterdamIntrinsic (creationTransaction 0) =
    { execution := 24_000, state := 0, floor := 24_000 } := by native_decide

example : calculateAmsterdamIntrinsic (creationTransaction 1) =
    { execution := 24_018, state := 0, floor := 24_064 } := by native_decide

example : calculateAmsterdamIntrinsic (creationTransaction 32) =
    { execution := 24_514, state := 0, floor := 26_048 } := by native_decide

example : calculateAmsterdamIntrinsic (creationTransaction 33) =
    { execution := 24_532, state := 0, floor := 26_112 } := by native_decide

private def positiveValueCreationTx : Transaction :=
  { simpleInput.tx with destination := none, data := [1] }

private def positiveValueCreationInput : Input :=
  { simpleInput with
    tx := positiveValueCreationTx
    oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic positiveValueCreationTx
      , body := .evm (.success
          { expectedInputWorld := paidWorld
          , worldAfter := transferredWorld
          , executingAddress := some "0x2222222222222222222222222222222222222222"
          , settlement := settlement }) } }

example : (run positiveValueCreationInput).disposition = .completed ∧
    (run positiveValueCreationInput).transferLogs =
      [{ emitter := "0xfffffffffffffffffffffffffffffffffffffffe"
       , signature := "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"
       , fromAddress := "0x1111111111111111111111111111111111111111"
       , toAddress := "0x2222222222222222222222222222222222222222"
       , value := 7 }] := by
  native_decide

private def creationWithoutExecutingAddress : Input :=
  { positiveValueCreationInput with
    oracle :=
      { positiveValueCreationInput.oracle with
        body := .evm (.success
          { expectedInputWorld := paidWorld
          , worldAfter := transferredWorld
          , executingAddress := none
          , settlement := settlement }) } }

example : (run creationWithoutExecutingAddress).disposition = .unmodeled ∧
    (run creationWithoutExecutingAddress).transferLogs = [] := by native_decide

private def creationAtDifferentExecutingAddress : Input :=
  { positiveValueCreationInput with
    oracle :=
      { positiveValueCreationInput.oracle with
        body := .evm (.success
          { expectedInputWorld := paidWorld
          , worldAfter := transferredWorld
          , executingAddress := some "0x3333333333333333333333333333333333333333"
          , settlement := settlement }) } }

private def creationWithEmptyExecutingAddress : Input :=
  { positiveValueCreationInput with
    oracle :=
      { positiveValueCreationInput.oracle with
        body := .evm (.success
          { expectedInputWorld := paidWorld
          , worldAfter := transferredWorld
          , executingAddress := some ""
          , settlement := settlement }) } }

example : (run creationAtDifferentExecutingAddress).transferLogs =
    [{ emitter := "0xfffffffffffffffffffffffffffffffffffffffe"
     , signature := "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"
     , fromAddress := "0x1111111111111111111111111111111111111111"
     , toAddress := "0x3333333333333333333333333333333333333333"
     , value := 7 }] := by
  native_decide

private def revertedCreationInput : Input :=
  { positiveValueCreationInput with
    oracle :=
      { positiveValueCreationInput.oracle with
        body := .evm (.revert
          { expectedInputWorld := paidWorld
          , discardedWorld := discardedWorld
          , settlement := settlement }) } }

private def failedCreationInput : Input :=
  { positiveValueCreationInput with
    oracle :=
      { positiveValueCreationInput.oracle with
        body := .evm (.exceptionalHalt
          { expectedInputWorld := paidWorld
          , discardedWorld := discardedWorld
          , exceptionKind := some 17
          , settlement := settlement }) } }

private def creationOutOfGasInput : Input :=
  { positiveValueCreationInput with
    oracle :=
      { positiveValueCreationInput.oracle with
        body := .evm (.topFrameOutOfGas settlement) } }

private def creationStateOutOfGasInput : Input :=
  { positiveValueCreationInput with
    oracle :=
      { positiveValueCreationInput.oracle with
        body := .evm (.createStateOutOfGas
          { expectedInputWorld := paidWorld
          , discardedWorld := discardedWorld
          , settlement := settlement }) } }

example : (run revertedCreationInput).transferLogs = [] ∧
    (run failedCreationInput).transferLogs = [] ∧
    (run creationOutOfGasInput).transferLogs = [] ∧
    (run creationStateOutOfGasInput).transferLogs = [] := by native_decide

private def revertInput : Input :=
  { systemCallInput with
    tx := { systemCallTx with value := 7 }
    oracle :=
      { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
      , body := .evm (.revert
          { expectedInputWorld := paidWorld
          , discardedWorld := discardedWorld
          , returndata := [9, 8]
          , settlement := settlement }) } }

example : (run revertInput).receiptStatus = some .failure := by native_decide

example : (run revertInput).txResult = .ok := by native_decide

example : (run revertInput).returndata = [9, 8] := by native_decide

example : (run revertInput).state.world = initialWorld := by native_decide

example : (run revertInput).state.events.contains .restoreExecutionSnapshot = true := by
  native_decide

private def exceptionInput : Input :=
  { revertInput with
    oracle :=
      { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
      , body := .evm (.exceptionalHalt
          { expectedInputWorld := paidWorld
          , discardedWorld := discardedWorld
          , exceptionKind := some 17
          , settlement := settlement }) } }

example : (run exceptionInput).txResult = .evmException 17 := by native_decide

example : (run exceptionInput).returndata = [] := by native_decide

private def topFrameOutOfGasInput : Input :=
  { revertInput with
    oracle :=
      { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
      , body := .evm (.topFrameOutOfGas settlement) } }

example : (run topFrameOutOfGasInput).state.events.contains .payOriginalValue = false := by
  native_decide

private def missingSenderInput : Input :=
  { systemCallInput with
    tx := { systemCallTx with sender := .missing }
    options := .onlySkipValidation }

example : (run missingSenderInput).txResult = .rejected .senderNotSpecified := by
  native_decide

private def maxNonceInput : Input :=
  { systemCallInput with tx := { systemCallTx with nonceIsMax := true } }

example : (run maxNonceInput).disposition = .completed := by native_decide

private def nonSystemCallWithStateIntrinsic : Input :=
  { systemCallInput with
    tx := { plainSystemTx with value := 0 }
    oracle :=
      { ordinaryIntrinsic := { execution := 0, state := 1, floor := 0 }
      , body := successBody } }

example : (run nonSystemCallWithStateIntrinsic).disposition = .unmodeled := by
  native_decide

private def otherSenderPlainTx : Transaction :=
  { kind := .plain
  , sender := .other "0x03"
  , destination := some "0x04"
  , gasLimit := 30_000_000 }

private def otherSenderPlain : Input :=
  { systemCallInput with
    tx := otherSenderPlainTx
    oracle :=
      { ordinaryIntrinsic := calculateAmsterdamIntrinsic otherSenderPlainTx
      , body := successBody } }

private def otherSenderSystemCall : Input :=
  { systemCallInput with tx := { systemCallTx with sender := .other "0x03" } }

example : (run otherSenderPlain).disposition = .notRouted := by native_decide

private def exactSkipRoute : Input :=
  { otherSenderPlain with options := .onlySkipValidation }

example : (run exactSkipRoute).disposition = .completed := by native_decide

private def skipAndCommitDoesNotForceRoute : Input :=
  { otherSenderPlain with options := .trace }

example : (run skipAndCommitDoesNotForceRoute).disposition = .notRouted := by
  native_decide

private def unsupportedEngine : Input :=
  { systemCallInput with engine := .aura }

example : (run unsupportedEngine).disposition = .unmodeled := by native_decide

example : (run unsupportedEngine).state.events = [.returnResult] := by native_decide

example : (run unsupportedEngine).gasPurchaseOutput = .notRun ∧
    (run unsupportedEngine).nonceOutput = .notRun := by
  native_decide

private def warmInput : Input :=
  { systemCallInput with options := .warm }

example : (run warmInput).effectiveOptions.warmup = true := by native_decide

example : (run warmInput).state.transientResetCount = 1 ∧
    (run warmInput).state.txSpentGas = none ∧
    (run warmInput).state.txBlockGasUsed = none := by
  native_decide

private def callAndRestoreInput : Input :=
  { systemCallInput with options := .callAndRestore }

example : (run callAndRestoreInput).state.world = initialWorld ∧
    (run callAndRestoreInput).state.resetCount = 1 ∧
    (run callAndRestoreInput).state.commitCount = 2 := by
  native_decide

private def buildUpInput : Input :=
  { systemCallInput with options := .build }

example : (run buildUpInput).state.outerBuildUpSnapshotTaken = true ∧
    (run buildUpInput).state.events.head? = some .outerBuildUpSnapshot := by
  native_decide

private def receiptTracingInput : Input :=
  { systemCallInput with tracingReceipt := true, tracerKind := .callOutput }

example : (run receiptTracingInput).callOutputTrace = some
    { status := .success
    , returnValue := []
    , gasSpent := settlement.spentGas
    , operationGas := settlement.operationGas } := by
  native_decide

private def callOutputWithoutReceipt : Input :=
  { systemCallInput with tracerKind := .callOutput, tracingReceipt := false }

private def nullTracerWithReceipt : Input :=
  { systemCallInput with tracerKind := .null, tracingReceipt := true }

private def nullTracerWithStateTrace : Input :=
  { systemCallInput with tracerKind := .null, tracingState := true }

private def callOutputWithStateTrace : Input :=
  { receiptTracingInput with tracingState := true }

example : callOutputWithoutReceipt.tracingFactsConsistent = false ∧
    (run callOutputWithoutReceipt).disposition = .unmodeled ∧
    (run callOutputWithoutReceipt).state.events = [.returnResult] := by
  native_decide

example : nullTracerWithReceipt.tracingFactsConsistent = false ∧
    (run nullTracerWithReceipt).disposition = .unmodeled ∧
    (run nullTracerWithReceipt).state.events = [.returnResult] := by
  native_decide

example : nullTracerWithStateTrace.tracingFactsConsistent = false ∧
    (run nullTracerWithStateTrace).disposition = .unmodeled ∧
    callOutputWithStateTrace.tracingFactsConsistent = false ∧
    (run callOutputWithStateTrace).disposition = .unmodeled := by
  native_decide

private def destroyFinalizationInput : Input :=
  { systemCallInput with
    oracle :=
      { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
      , body := .evm (.success
          { expectedInputWorld := initialWorld
          , worldAfter := changedWorld
          , logs := [44]
          , destroyList := [{ address := 10, balance := 20 }, { address := 11, balance := 0 }]
          , settlement := settlement }) } }

example : (run destroyFinalizationInput).state.events.contains .finalizeDestroyList = true ∧
    (run destroyFinalizationInput).state.destroyedAccounts = [10, 11] ∧
    (run destroyFinalizationInput).state.preservedDestroyedBalances = [(10, 20)] ∧
    (run destroyFinalizationInput).state.destroyBurnLogs = [] := by
  native_decide

private def destroyFailureInput : Input := revertInput

example : (run destroyFailureInput).state.events.contains .finalizeDestroyList = false := by
  native_decide

private def authorizationPartialOutOfGasInput : Input :=
  { systemCallInput with
    tx := { systemCallTx with supportsAuthorizationList := true }
    oracle :=
      { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
      , body := .evm (.exceptionalHalt
          { expectedInputWorld := initialWorld
          , discardedWorld := discardedWorld
          , exceptionKind := some 1
          , settlement := settlement }) } }

example : (run authorizationPartialOutOfGasInput).disposition = .unmodeled ∧
    (run authorizationPartialOutOfGasInput).state.world = initialWorld ∧
    (run authorizationPartialOutOfGasInput).state.events.contains .executeEvm = false := by
  native_decide

example : (run systemCallInput).premiumPerGas = 0 ∧
    (run systemCallInput).senderReservedGasPayment = 0 ∧
    (run systemCallInput).blobBaseFee = 0 ∧
    (run systemCallInput).nonceIncrementDelta = 0 ∧
    (run systemCallInput).state.world.senderNonce = initialWorld.senderNonce := by
  native_decide

example : (run systemCallInput).gasPurchaseOutput = .bypassed ∧
    (run systemCallInput).nonceOutput = .bypassed initialWorld.senderNonce := by
  native_decide

private def mutatedOverrides : OverrideKernel where
  buyGas := fun world =>
    { output := .charged 1 2 3
    , world := { world with senderBalance := world.senderBalance - 1 }
    , continues := true }
  incrementNonce := fun world =>
    { output := .incremented world.senderNonce (world.senderNonce + 1)
    , world := { world with senderNonce := world.senderNonce + 1 }
    , continues := true }

private def rejectingBuyGasOverrides : OverrideKernel where
  buyGas := fun world =>
    { output := .charged 1 1 0
    , world := { world with senderBalance := world.senderBalance - 1 }
    , continues := false }
  incrementNonce := incrementNonceOverride

example : (runWithOverrides mutatedOverrides systemCallInput).gasPurchaseOutput = .charged 1 2 3 ∧
    (runWithOverrides mutatedOverrides systemCallInput).nonceOutput =
      .incremented initialWorld.senderNonce (initialWorld.senderNonce + 1) ∧
    (runWithOverrides mutatedOverrides systemCallInput).disposition = .unmodeled ∧
    (runWithOverrides mutatedOverrides systemCallInput).state.world.senderBalance = 99 ∧
    (runWithOverrides mutatedOverrides systemCallInput).state.world.senderNonce = 8 := by
  native_decide

example : (runWithOverrides rejectingBuyGasOverrides systemCallInput).disposition = .unmodeled ∧
    (runWithOverrides rejectingBuyGasOverrides systemCallInput).nonceOutput = .notRun ∧
    (runWithOverrides rejectingBuyGasOverrides systemCallInput).state.events.contains .incrementNonce = false := by
  native_decide

example : (runWithOverrides mutatedOverrides unsupportedEngine).gasPurchaseOutput = .notRun ∧
    (runWithOverrides mutatedOverrides unsupportedEngine).nonceOutput = .notRun := by
  native_decide

private def adapterState : BlockAdapterState :=
  { normalReceipts := [4, 5]
  , cumulativeReceiptGas := 91
  , requestPayloads := [] }

private def beaconCall : BlockCallInput :=
  let root := List.range 32
  let tx : Transaction :=
    { plainSystemTx with
      kind := .systemCall
      value := 0
      destination := some beaconRootsAddress
      data := root
      gasLimit := 31_566_720
      accessList := some { addresses := [beaconRootsAddress], storageKeys := [] } }
  { site := .beaconRoot
  , enabled := true
  , targetHasCode := true
  , beaconRootCalldata := root
  , processorInput :=
      { simpleInput with
        tx := tx
        oracle :=
          { ordinaryIntrinsic := calculateAmsterdamIntrinsic tx
          , body := successBody changedWorld } }
  , adapterState := adapterState }

example : (runBlockCall beaconCall).disposition = .applied := by native_decide

example : beaconCall.processorInput.tx.isContractCreation = false ∧
    beaconCall.processorInput.tx.senderIsRecipient = false := by
  native_decide

example : (run beaconCall.processorInput).initialAvailableGas = some
    { executionLeft := 30_000_000
    , stateReservoir := 1_566_720
    , stateUsed := 0
    , stateFromGasLeft := 0
    , stateFromGasLeftRefunded := 0 } := by
  native_decide

private def requestCall : BlockCallInput :=
  { site := .withdrawalRequests
  , enabled := true
  , targetHasCode := true
  , requestTypeByte := 1
  , processorInput :=
      { systemCallInput with
        tracingReceipt := true
        tracerKind := .callOutput
        oracle :=
          { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
          , body := successBody changedWorld [10, 11] } }
  , adapterState := adapterState }

example : (runBlockCall requestCall).adapterState.requestPayloads = [[1, 10, 11]] := by
  native_decide

private def beaconNoCodeCall : BlockCallInput :=
  { beaconCall with
    targetHasCode := false
    processorInput :=
      { beaconCall.processorInput with
        oracle :=
          { beaconCall.processorInput.oracle with
            body := .simpleTransfer
              { expectedInputWorld := initialWorld
              , worldAfter := changedWorld
              , settlement := settlement } } } }

example : (runBlockCall beaconNoCodeCall).disposition = .applied := by native_decide

example : (runBlockCall { requestCall with
    processorInput := { systemCallInput with
      tracingReceipt := true, tracerKind := .callOutput } }).adapterState.requestPayloads = [] := by
  native_decide

private def requestCallAt (site : BlockCallSite) : BlockCallInput :=
  { requestCall with
    site := site
    requestTypeByte := site.requestType.getD 0
    processorInput :=
      { requestCall.processorInput with
        tx :=
          { requestCall.processorInput.tx with
            destination := site.destination } } }

example :
    [ .withdrawalRequests, .consolidationRequests,
      .builderDepositRequests, .builderExitRequests ].all
        (fun site => (runBlockCall (requestCallAt site)).disposition == .applied) = true := by
  native_decide

private def requestNoCodeCall : BlockCallInput :=
  { requestCall with
    targetHasCode := false
    processorInput :=
      { requestCall.processorInput with
        oracle :=
          { requestCall.processorInput.oracle with
            body := .simpleTransfer
              { expectedInputWorld := initialWorld
              , worldAfter := changedWorld
              , settlement := settlement } } } }

example : (runBlockCall requestNoCodeCall).disposition = .invalidBlock := by
  native_decide

private def requestFailureInput : Input :=
  { systemCallInput with
    tracingReceipt := true
    tracerKind := .callOutput
    oracle :=
      { ordinaryIntrinsic := { execution := 0, state := 0, floor := 0 }
      , body := .evm (.revert
          { expectedInputWorld := initialWorld
          , discardedWorld := discardedWorld
          , returndata := [9, 8]
          , settlement := settlement }) } }

example : (runBlockCall { requestCall with processorInput := requestFailureInput }).disposition =
    .invalidBlock := by
  native_decide

example : (runBlockCall { beaconCall with
    processorInput := { beaconCall.processorInput with engine := .aura } }).disposition = .unmodeled := by
  native_decide

private def beaconUnmodeledProcessor : BlockCallInput :=
  { beaconCall with
    processorInput :=
      { beaconCall.processorInput with
        tx := { beaconCall.processorInput.tx with supportsAuthorizationList := true } } }

example : blockCallShapeValid beaconUnmodeledProcessor = true ∧
    (run beaconUnmodeledProcessor.processorInput).disposition = .unmodeled ∧
    (runBlockCall beaconUnmodeledProcessor).disposition = .unmodeled := by
  native_decide

example : (runBlockCall { requestCall with enabled := false }).processorResult = none := by
  native_decide

example : (runBlockCall { beaconCall with site := .historicalBlockhash }).disposition =
    .unmodeled := by
  native_decide

example : (runBlockCall requestCall).adapterState.normalReceipts = [4, 5] ∧
    (runBlockCall requestCall).adapterState.cumulativeReceiptGas = 91 := by
  native_decide

structure MutationVector where
  name : String
  killed : Bool
  deriving DecidableEq, Repr

private def beaconStorageAccessList : AccessList :=
  { addresses := [beaconRootsAddress]
  , storageKeys := [(beaconRootsAddress, 0)] }

def shapeMutationVectors : List MutationVector :=
  [ { name := "beacon gas limit"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with gasLimit := 31_566_721 } } } }
  , { name := "beacon value"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with value := 1 } } } }
  , { name := "beacon gas price"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with gasPrice := 1 } } } }
  , { name := "beacon destination"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with destination := some withdrawalRequestsAddress } } } }
  , { name := "beacon calldata"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with data := [] } } } }
  , { name := "beacon calldata byte bound"
      killed := !blockCallShapeValid
        { beaconCall with
          beaconRootCalldata := 256 :: List.range 31
          processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with data := 256 :: List.range 31 } } } }
  , { name := "beacon address-only access list"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with accessList := none } } } }
  , { name := "beacon access-list storage must remain empty"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with
              accessList := some beaconStorageAccessList } } } }
  , { name := "beacon sender"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with sender := .other "0x01" } } } }
  , { name := "beacon kind"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with
            tx := { beaconCall.processorInput.tx with kind := .plain } } } }
  , { name := "beacon options"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with options := .trace } } }
  , { name := "beacon tracer"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with tracerKind := .other } } }
  , { name := "beacon state tracing"
      killed := !blockCallShapeValid
        { beaconCall with processorInput := { beaconCall.processorInput with tracingState := true } } }
  , { name := "code-bearing beacon must use the EVM body"
      killed := !blockCallShapeValid { beaconNoCodeCall with targetHasCode := true } }
  , { name := "code-empty beacon must use the simple-transfer body"
      killed := !blockCallShapeValid { beaconCall with targetHasCode := false } }
  , { name := "request gas limit"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with gasLimit := 31_566_719 } } } }
  , { name := "request value"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with value := 1 } } } }
  , { name := "request gas price"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with gasPrice := 1 } } } }
  , { name := "request destination"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with destination := some beaconRootsAddress } } } }
  , { name := "request calldata"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with data := [0] } } } }
  , { name := "request access list"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with accessList := some {} } } } }
  , { name := "request sender"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with sender := .other "0x01" } } } }
  , { name := "request kind"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with
            tx := { requestCall.processorInput.tx with kind := .plain } } } }
  , { name := "request options"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with options := .trace } } }
  , { name := "request tracer"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with tracerKind := .other } } }
  , { name := "request state tracing"
      killed := !blockCallShapeValid
        { requestCall with processorInput := { requestCall.processorInput with tracingState := true } } }
  , { name := "request type"
      killed := !blockCallShapeValid
        { requestCall with requestTypeByte := 2 } } ]

theorem every_exact_shape_mutation_is_killed :
    shapeMutationVectors.all (fun vector => vector.killed) = true := by
  native_decide

/-!
Each mutation below negates one production-specific guard or state transition.
The vector is killed when its observable differs from `run`/`runBlockCall`.
-/

private def routeOnSystemCallKindMutant (tx : Transaction) (options : Options) : Bool :=
  tx.routesToSystemProcessor options || tx.kind == .systemCall

private def routeOnAnySkipFlagMutant (tx : Transaction) (options : Options) : Bool :=
  tx.isSystem || options.skipValidation

private def dropWarmupBeforeBaseMutant (options : Options) : Options := options.core

private def ordinaryIntrinsicForSystemCallMutant (input : Input) : IntrinsicGas :=
  input.oracle.ordinaryIntrinsic

private def retainFailedWorldMutant (input : Input) : World :=
  match input.oracle.body with
  | .evm (.revert observation) => observation.discardedWorld
  | _ => input.initialWorld

private def appendReceiptMutant (state : BlockAdapterState) : BlockAdapterState :=
  { state with
    normalReceipts := state.normalReceipts ++ [99]
    cumulativeReceiptGas := state.cumulativeReceiptGas + 1 }

def mutationVectors : List MutationVector :=
  [ { name := "nonstandard engine fails before block shape interpretation"
      killed := (runBlockCall { beaconCall with processorInput :=
        { beaconCall.processorInput with engine := .aura } }).disposition != .applied }
  , { name := "non-pinned EIP-7708 fork flag fails closed"
      killed := (runBlockCall { beaconCall with processorInput :=
        { beaconCall.processorInput with spec :=
          { Spec.pinnedAmsterdam with eip7708 := false } } }).disposition != .applied }
  , { name := "non-pinned EIP-8246 fork flag fails closed"
      killed := (runBlockCall { beaconCall with processorInput :=
        { beaconCall.processorInput with spec :=
          { Spec.pinnedAmsterdam with eip8246 := false } } }).disposition != .applied }
  , { name := "non-pinned schedule fails closed"
      killed := (runBlockCall { beaconCall with processorInput :=
        { beaconCall.processorInput with schedule :=
          { Schedule.pinnedAmsterdam with systemCallStateReservoir := 1_566_719 } } }).disposition != .applied }
  , { name := "SystemCall kind alone must not route"
      killed := routeOnSystemCallKindMutant otherSenderSystemCall.tx otherSenderSystemCall.options !=
        otherSenderSystemCall.tx.routesToSystemProcessor otherSenderSystemCall.options }
  , { name := "SkipValidation must be the exact standalone option for fallback routing"
      killed := routeOnAnySkipFlagMutant skipAndCommitDoesNotForceRoute.tx
          skipAndCommitDoesNotForceRoute.options !=
        skipAndCommitDoesNotForceRoute.tx.routesToSystemProcessor
          skipAndCommitDoesNotForceRoute.options }
  , { name := "Warmup masking for the payment decision must not alter base Execute options"
      killed := dropWarmupBeforeBaseMutant warmInput.options != warmInput.options.forSystemCore }
  , { name := "SystemCall must use the fixed intrinsic reservoir"
      killed := ordinaryIntrinsicForSystemCallMutant systemCallInput !=
        systemCallIntrinsic systemCallInput.schedule }
  , { name := "REVERT must restore the top-level snapshot"
      killed := retainFailedWorldMutant revertInput != (run revertInput).state.world }
  , { name := "reached system gas purchase must use the bypass override"
      killed := (runWithOverrides mutatedOverrides systemCallInput).gasPurchaseOutput !=
        (run systemCallInput).gasPurchaseOutput }
  , { name := "reached system nonce update must use the no-op override"
      killed := (runWithOverrides mutatedOverrides systemCallInput).nonceOutput !=
        (run systemCallInput).nonceOutput }
  , { name := "charging and incrementing overrides alter reached execution state and control"
      killed := (runWithOverrides mutatedOverrides systemCallInput).disposition !=
          (run systemCallInput).disposition &&
        (runWithOverrides mutatedOverrides systemCallInput).state.world !=
          (run systemCallInput).state.world }
  , { name := "failed gas purchase prevents nonce and body execution"
      killed := (runWithOverrides rejectingBuyGasOverrides systemCallInput).state.events.contains
          .selectExecutionPath != true }
  , { name := "EIP-7708 non-self positive-value simple transfer emits its log"
      killed := (run simpleInput).transferLogs != [] }
  , { name := "EIP-7708 transfer-log emitter and from cannot be swapped"
      killed := (run simpleInput).transferLogs !=
        [{ emitter := "0x1111111111111111111111111111111111111111"
         , signature := "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"
         , fromAddress := "0xfffffffffffffffffffffffffffffffffffffffe"
         , toAddress := "0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02"
         , value := 7 }] }
  , { name := "EIP-7708 transfer-log signature is pinned"
      killed := (run simpleInput).transferLogs !=
        [{ emitter := "0xfffffffffffffffffffffffffffffffffffffffe"
         , signature := "0x00"
         , fromAddress := "0x1111111111111111111111111111111111111111"
         , toAddress := "0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02"
         , value := 7 }] }
  , { name := "EIP-7708 transfer-log from and to cannot be swapped"
      killed := (run simpleInput).transferLogs !=
        [{ emitter := "0xfffffffffffffffffffffffffffffffffffffffe"
         , signature := "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"
         , fromAddress := "0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02"
         , toAddress := "0x1111111111111111111111111111111111111111"
         , value := 7 }] }
  , { name := "EIP-7708 transfer-log amount is pinned"
      killed := (run simpleInput).transferLogs !=
        [{ emitter := "0xfffffffffffffffffffffffffffffffffffffffe"
         , signature := "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"
         , fromAddress := "0x1111111111111111111111111111111111111111"
         , toAddress := "0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02"
         , value := 8 }] }
  , { name := "EIP-7708 zero-value transfer must not emit a log"
      killed := (run zeroValueSimpleInput).transferLogs !=
        [{ emitter := "x", signature := "x", fromAddress := "x", toAddress := "x", value := 0 }] }
  , { name := "EIP-7708 self-send must not emit a log"
      killed := (run selfSendSimpleInput).transferLogs !=
        [{ emitter := "x", signature := "x", fromAddress := "x", toAddress := "x", value := 7 }] }
  , { name := "EIP-7708 reverted transfer must not retain a log"
      killed := (run revertedTransferInput).transferLogs !=
        [{ emitter := "x", signature := "x", fromAddress := "x", toAddress := "x", value := 7 }] }
  , { name := "EIP-7708 failed transfer must not retain a log"
      killed := (run failedTransferInput).transferLogs !=
        [{ emitter := "x", signature := "x", fromAddress := "x", toAddress := "x", value := 7 }] }
  , { name := "EIP-7708 state-OOG transfer must not emit a log"
      killed := (run simpleOutOfGasInput).transferLogs !=
        [{ emitter := "x", signature := "x", fromAddress := "x", toAddress := "x", value := 7 }] }
  , { name := "EIP-7708 positive-value creation requires an executing address"
      killed := (run creationWithoutExecutingAddress).disposition != .completed }
  , { name := "EIP-7708 positive-value creation rejects an empty executing address"
      killed := (run creationWithEmptyExecutingAddress).disposition != .completed }
  , { name := "EIP-7708 creation log recipient follows the executing address observation"
      killed := (run creationAtDifferentExecutingAddress).transferLogs !=
        (run positiveValueCreationInput).transferLogs }
  , { name := "EIP-7708 reverted positive-value creation must not retain a log"
      killed := (run revertedCreationInput).transferLogs !=
        (run positiveValueCreationInput).transferLogs }
  , { name := "EIP-7708 failed positive-value creation must not retain a log"
      killed := (run failedCreationInput).transferLogs !=
        (run positiveValueCreationInput).transferLogs }
  , { name := "EIP-7708 OOG positive-value creation must not retain a log"
      killed := (run creationOutOfGasInput).transferLogs !=
        (run positiveValueCreationInput).transferLogs }
  , { name := "EIP-7708 state-OOG positive-value creation must not retain a log"
      killed := (run creationStateOutOfGasInput).transferLogs !=
        (run positiveValueCreationInput).transferLogs }
  , { name := "authorization-list system calls fail before partial authorization OOG"
      killed := (run authorizationPartialOutOfGasInput).disposition !=
        (run { authorizationPartialOutOfGasInput with
          tx := { authorizationPartialOutOfGasInput.tx with
            supportsAuthorizationList := false } }).disposition }
  , { name := "CallAndRestore restores the committed snapshot"
      killed := (run callAndRestoreInput).state.world != changedWorld }
  , { name := "BuildUp snapshot gate uses exact option equality"
      killed := !(run { buildUpInput with
        options := { Options.build with commit := true } }).state.outerBuildUpSnapshotTaken }
  , { name := "Warmup skips transaction gas-field writes"
      killed := (run warmInput).state.txSpentGas != some settlement.spentGas }
  , { name := "CallOutputTracer records receipt-derived status and return"
      killed := (run receiptTracingInput).callOutputTrace != none }
  , { name := "CallOutput tracer cannot contradict receipt tracing"
      killed := (run callOutputWithoutReceipt).disposition != .completed }
  , { name := "Null tracer cannot claim receipt tracing"
      killed := (run nullTracerWithReceipt).disposition != .completed }
  , { name := "Null pinned system tracer cannot trace state"
      killed := (run nullTracerWithStateTrace).disposition != .completed }
  , { name := "CallOutput pinned system tracer cannot trace state"
      killed := (run callOutputWithStateTrace).disposition != .completed }
  , { name := "failed EIP-7708 execution must not finalize destroy list"
      killed := (run destroyFailureInput).state.events.contains .finalizeDestroyList != true }
  , { name := "EIP-8246 destroy finalization preserves nonzero balance"
      killed := (run destroyFinalizationInput).state.preservedDestroyedBalances != [] }
  , { name := "EIP-8246 destroy finalization suppresses burn log"
      killed := (run destroyFinalizationInput).state.destroyBurnLogs != [(10, 20)] }
  , { name := "system processor must not increment normal block counters"
      killed := (run systemCallInput).normalHeaderGasUsedDelta != 1 }
  , { name := "block adapters must not append ordinary receipts"
      killed := appendReceiptMutant adapterState != (runBlockCall requestCall).adapterState }
  , { name := "request target without code must invalidate the block"
      killed := (runBlockCall requestNoCodeCall).disposition != .applied }
  , { name := "failed request SystemCall must invalidate the block"
      killed := (runBlockCall { requestCall with
          processorInput := requestFailureInput }).disposition != .applied }
  , { name := "current historical-blockhash path must fail closed"
      killed := (runBlockCall { beaconCall with site := .historicalBlockhash }).disposition != .applied } ]

theorem every_adversarial_mutation_is_killed :
    mutationVectors.all (fun vector => vector.killed) = true := by
  native_decide

end Eip803x.SystemTransactionReferenceVectors
