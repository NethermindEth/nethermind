-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.CallCreateFrame

namespace Eip803x
namespace Evm
namespace CallCreateFrameVectors

open CallCreateFrame
open MemoryStackControl
open Word
open AccountPricing
open GasMachine

def schedule : AccountGasSchedule := AccountGasSchedule.amsterdam

def gas (left reservoir : Nat) : GasState :=
  { gasLeft := left
    stateReservoir := reservoir
    stateFromGasLeft := 0
    stateUsed := 0
    refundCounter := 0 }

def stackOf (words : List UInt256) : Stack := Stack.fromWords words

def callStack : Stack :=
  stackOf [ofNat 40, ofNat 1, ofNat 7, ofNat 0, ofNat 0, ofNat 0, ofNat 0]

def delegateStack : Stack :=
  stackOf [ofNat 40, ofNat 1, ofNat 0, ofNat 0, ofNat 0, ofNat 0]

def callWorld : CallWorldOracle :=
  { callerBalance := 100
    executingAccount := .existent
    target := .dead
    targetDerived := true
    codeLoaded := true
    delegatedTargetAccess := none }

def callRequest : CallRequest :=
  { kind := .call
    stack := callStack
    environmentValue := ofNat 9
    staticContext := false
    callDepth := 1
    inputMemoryExecutionCharge := 0
    outputMemoryExecutionCharge := 0
    gas := gas 1_000_000 200_000
    access := .cold
    world := callWorld }

def delegateRequest : CallRequest :=
  { callRequest with
    kind := .delegatecall
    stack := delegateStack
    environmentValue := ofNat 9
    world := { callWorld with target := .existent } }

def staticValueRequest : CallRequest :=
  { callRequest with staticContext := true }

def staticCallCodeRequest : CallRequest :=
  { callRequest with kind := .callcode, staticContext := true }

def callOracleFailureRequest : CallRequest :=
  { callRequest with world := { callWorld with targetDerived := false } }

def callValueOutOfGasRequest : CallRequest :=
  { callRequest with gas := gas 11_299 200_000 }

def callExactAccessRequest : CallRequest :=
  { callRequest with gas := gas 11_400 200_000, access := .warm }

def callInputMemoryOutOfGasRequest : CallRequest :=
  { callRequest with
    gas := gas 11_349 200_000
    inputMemoryExecutionCharge := 50 }

def callOutputMemoryOutOfGasRequest : CallRequest :=
  { callRequest with
    gas := gas 11_349 200_000
    outputMemoryExecutionCharge := 50 }

def callAccessOutOfGasRequest : CallRequest :=
  { callRequest with gas := gas 14_299 200_000 }

def delegatedCallOutOfGasRequest : CallRequest :=
  { callRequest with
    gas := gas 14_300 200_000
    world := { callWorld with delegatedTargetAccess := some .cold } }

def callStateGasOutOfGasRequest : CallRequest :=
  { callRequest with gas := gas 20_000 100 }

def depthLimitedRequest : CallRequest :=
  { callRequest with callDepth := maxCallDepth }

def lowBalanceRequest : CallRequest :=
  { callRequest with world := { callWorld with callerBalance := 1 } }

def createStack : Stack := stackOf [ofNat 0, ofNat 0, ofNat 0]

def create2Stack : Stack := stackOf [ofNat 0, ofNat 0, ofNat 0, ofNat 0xabc]

def createDestination (status : CallCreateFrame.AccountStatus) (collision : CollisionKind)
    (derived warmed : Bool) : CreateDestinationOracle :=
  { status, collision
    physicalLeafExists := status == .existent || collision.isCollision
    logicalAccountExists := status == .existent
    destinationDerived := derived, warmed }

def createRequest : CreateRequest :=
  { kind := .create
    stack := createStack
    staticContext := false
    callDepth := 1
    balance := 100
    nonce := 0
    maxInitCodeSize := 49_152
    initCodeWordCountOverflow := false
    memoryExecutionCharge := 0
    initCodeReadable := true
    destination := createDestination .dead .none true true
    gas := gas 1_000_000 200_000
    schedule }

def create2CollisionRequest : CreateRequest :=
  { createRequest with
    kind := .create2
    stack := create2Stack
    destination := createDestination .existent .nonEmptyCode true true }

def createExistingNoCollisionRequest : CreateRequest :=
  { createRequest with
    destination := createDestination .existent .none true true }

def create2StorageOnlyRequest : CreateRequest :=
  { createRequest with
    kind := .create2
    stack := create2Stack
    destination :=
      { status := .dead
        collision := .storageOnly
        physicalLeafExists := true
        logicalAccountExists := false
        destinationDerived := true
        warmed := true } }

def create2ExistingStorageOnlyRequest : CreateRequest :=
  { createRequest with
    kind := .create2
    stack := create2Stack
    destination :=
      { status := .existent
        collision := .storageOnly
        physicalLeafExists := true
        logicalAccountExists := true
        destinationDerived := true
        warmed := true } }

def createDepthRequest : CreateRequest :=
  { createRequest with
    callDepth := maxCallDepth
    destination := createDestination .dead .none false false }

def createBalanceRequest : CreateRequest :=
  { createRequest with
    stack := stackOf [ofNat 101, ofNat 0, ofNat 0]
    balance := 100
    destination := createDestination .dead .none false false }

def createNonceRequest : CreateRequest :=
  { createRequest with
    nonce := (2 ^ 64) - 1
    destination := createDestination .dead .none false false }

def createStateGasOogRequest : CreateRequest :=
  { createRequest with
    gas := gas 20_000 100
    destination := createDestination .dead .none true true }

def createMemoryOutOfGasRequest : CreateRequest :=
  { createRequest with
    gas := gas 12_050 200_000
    memoryExecutionCharge := 100 }

def createEntryOutOfGasRequest : CreateRequest :=
  { createRequest with
    kind := .create
    gas := gas 11_999 200_000 }

def create2HashOutOfGasRequest : CreateRequest :=
  { createRequest with
    kind := .create2
    stack := stackOf [ofNat 0, ofNat 0, ofNat 33, ofNat 0xabc]
    gas := gas 12_015 200_000 }

def createUnreadableRequest : CreateRequest :=
  { createRequest with initCodeReadable := false }

def createOverLimitRequest : CreateRequest :=
  { createRequest with
    stack := stackOf [ofNat 0, ofNat 0, ofNat 1]
    maxInitCodeSize := 0 }

def createWordOverflowRequest : CreateRequest :=
  { createRequest with initCodeWordCountOverflow := true }

def absentStorageOnlyRequest : CreateRequest :=
  { createRequest with
    destination :=
      { status := .dead
        collision := .storageOnly
        physicalLeafExists := false
        logicalAccountExists := false
        destinationDerived := true
        warmed := true } }

def invalidCodeCollisionRequest : CreateRequest :=
  { createRequest with
    destination :=
      { status := .dead
        collision := .nonEmptyCode
        physicalLeafExists := true
        logicalAccountExists := false
        destinationDerived := true
        warmed := true } }

def invalidNonceCollisionRequest : CreateRequest :=
  { createRequest with
    destination :=
      { status := .existent
        collision := .nonZeroNonce
        physicalLeafExists := false
        logicalAccountExists := true
        destinationDerived := true
        warmed := true } }

def invalidExistentRequest : CreateRequest :=
  { createRequest with
    destination :=
      { status := .existent
        collision := .none
        physicalLeafExists := false
        logicalAccountExists := false
        destinationDerived := true
        warmed := true } }

def forgedChild : FrameGasState :=
  { gas := { gasLeft := 777, stateReservoir := 0, stateFromGasLeft := 0, stateUsed := 0, refundCounter := 0 },
    stateGasBaseline := 123
    stateUsedBaseline := 456
    refundCounterBaseline := -9 }

def forgedChildWorld : WorldFrame :=
  { checkpoint := 7777, current := 8888 }

def parentWorld : WorldFrame := { checkpoint := 0, current := 7 }

def parentGas : GasState := gas 1000 50

def frameEntry : FrameEntry := enterFrame 100 parentGas

def successfulChild : FrameGasState :=
  { (frameEntry.child) with
    gas := { frameEntry.child.gas with gasLeft := 40 } }

def changedChildWorld : WorldFrame :=
  { checkpoint := parentWorld.current, current := 19 }

def failedChild : FrameGasState :=
  { successfulChild with
    gas := { successfulChild.gas with
      stateReservoir := 30
      stateUsed := 20
      refundCounter := 0 } }

def creationEntry : FrameEntry :=
  enterFrame 100 { parentGas with stateUsed := 200 }

def failedCreationChild : FrameGasState :=
  { creationEntry.child with
    gas := { creationEntry.child.gas with
      stateReservoir := 30
      stateUsed := 220 } }

def depositExecutionOutOfGasChild : FrameGasState :=
  { successfulChild with
    gas := { successfulChild.gas with gasLeft := 5 } }

def depositStateOutOfGasChild : FrameGasState :=
  { successfulChild with
    gas := { successfulChild.gas with stateReservoir := 0 } }

def successfulChildSteps : List FrameGasStep :=
  [.execution 60]

def failedChildSteps : List FrameGasStep :=
  [.execution 60, .state 20]

def failedCreationChildSteps : List FrameGasStep :=
  [.state 20]

def positiveRefundChild : FrameGasState :=
  { failedChild with gas := { failedChild.gas with refundCounter := 13 } }

def positiveRefundSteps : List FrameGasStep :=
  [.execution 60, .state 20, .refund 13]

def negativeRefundChild : FrameGasState :=
  { failedChild with gas := { failedChild.gas with refundCounter := -7 } }

def negativeRefundSteps : List FrameGasStep :=
  [.execution 60, .state 20, .refund (-7)]


def expectedCallOperands : CallOperands :=
  { gasLimit := ofNat 40
    codeSource := ofNat 1
    value := ofNat 7
    dataOffset := ofNat 0
    dataLength := ofNat 0
    outputOffset := ofNat 0
    outputLength := ofNat 0 }

example :
    (popCall .call (ofNat 9) callStack).map (fun result => result.1) =
      some expectedCallOperands := by
  native_decide

example : popCall .call (ofNat 9) (stackOf [ofNat 1]) = none := by
  native_decide

example :
    (prepareCall schedule staticValueRequest).status = .staticViolation ∧
      (prepareCall schedule staticValueRequest).entry = none := by
  native_decide

example :
    (prepareCall schedule staticCallCodeRequest).status = .entered ∧
      (prepareCall schedule staticCallCodeRequest).newAccountStateCharged = false := by
  native_decide

example :
    (prepareCall schedule callOracleFailureRequest).status = .oracleFailure ∧
      (prepareCall schedule callOracleFailureRequest).targetAccessed = true ∧
      (prepareCall schedule callOracleFailureRequest).targetDerived = false ∧
      (prepareCall schedule callOracleFailureRequest).entry = none := by
  native_decide

example :
    let result := prepareCall schedule callValueOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.targetAccessed = false ∧ result.accessWarmed = false ∧
      result.executionGasCleared = true := by
  native_decide

example :
    let result := prepareCall schedule callExactAccessRequest
    let entry := result.entry.getD (enterFrame 0 (gas 0 0))
    result.status = .entered ∧ result.gas.gasLeft = 0 ∧
      result.targetAccessed = true ∧ result.accessWarmed = true ∧
      entry.child.gas.gasLeft = 2300 := by
  native_decide

example :
    let result := prepareCall schedule callInputMemoryOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.inputMemoryExpanded = false ∧ result.targetAccessed = false ∧
      result.executionGasCleared = true := by
  native_decide

example :
    let result := prepareCall schedule callOutputMemoryOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.inputMemoryExpanded = false ∧ result.outputMemoryExpanded = false ∧
      result.targetAccessed = false ∧ result.executionGasCleared = true := by
  native_decide

example :
    let result := prepareCall schedule callAccessOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.inputMemoryExpanded = false ∧ result.targetAccessed = true ∧
      result.accessWarmed = true ∧ result.executionGasCleared = true := by
  native_decide

example :
    let result := prepareCall schedule delegatedCallOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.targetDerived = true ∧ result.accessWarmed = true ∧
      result.delegatedAccessed = true ∧ result.executionGasCleared = true := by
  native_decide

example :
    let result := prepareCall schedule callStateGasOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.targetAccessed = true ∧ result.executionGasCleared = true ∧
      result.newAccountStateCharged = false := by
  native_decide

example :
    (prepareCall schedule callRequest).status = .entered ∧
      (prepareCall schedule callRequest).targetAccessed = true ∧
      (prepareCall schedule callRequest).targetDerived = true ∧
      (prepareCall schedule callRequest).newAccountStateCharged = true := by
  native_decide

example :
    let result := prepareCall schedule callRequest
    result.entry.getD (enterFrame 0 (gas 0 0)) |>.child.gas.stateReservoir = 16_400 := by
  native_decide

example :
    (prepareCall schedule depthLimitedRequest).status = .depthOrBalanceFailure ∧
      (prepareCall schedule depthLimitedRequest).entry = none ∧
      (prepareCall schedule depthLimitedRequest).targetAccessed = true ∧
      (prepareCall schedule depthLimitedRequest).gas.stateUsed = 0 := by
  native_decide

example :
    (prepareCall schedule lowBalanceRequest).status = .depthOrBalanceFailure ∧
      (prepareCall schedule lowBalanceRequest).entry = none := by
  native_decide

example :
    let result := prepareCall schedule delegateRequest
    result.status = .entered ∧
      result.newAccountStateCharged = false := by
  native_decide

example :
    priceCallCode schedule
      { codeAccess := .cold, delegatedTargetAccess := none,
        executingAccount := .existent, value := 7 } =
      { accessCharge := 3000, delegatedTargetAccessCharge := 0,
        accountWriteCharge := 9000, callStipendCharge := 2300,
        secondReadCharge := 0, executionCharge := 14_300,
        stateCharge := 0, stateRefill := 0 } := by
  native_decide

example :
    (priceDelegateCall schedule
      { codeAccess := .cold, delegatedTargetAccess := none,
        stateTarget := .existent, value := 7 }).accountWriteCharge = 0 := by
  native_decide

example :
    (priceStaticCall schedule
      { codeAccess := .cold, delegatedTargetAccess := none,
        stateTarget := .existent, value := 7 }).accountWriteCharge = 0 := by
  native_decide

example : (prepareCreate createRequest).status = .entered := by
  native_decide

example :
    let result := prepareCreate createRequest
    result.destinationAccessed = true ∧ result.stateCharged = true ∧
      result.entry.isSome = true := by
  native_decide

example :
    (prepareCreate createDepthRequest).status = .depthOrBalanceFailure ∧
      (prepareCreate createDepthRequest).destinationAccessed = false ∧
      (prepareCreate createDepthRequest).stateCharged = false := by
  native_decide

example :
    (prepareCreate createBalanceRequest).status = .depthOrBalanceFailure ∧
      (prepareCreate createBalanceRequest).destinationAccessed = false := by
  native_decide

example :
    (prepareCreate createNonceRequest).status = .nonceFailure ∧
      (prepareCreate createNonceRequest).destinationAccessed = false := by
  native_decide

example :
    (prepareCreate create2CollisionRequest).status = .collision ∧
      (prepareCreate create2CollisionRequest).collision = .nonEmptyCode ∧
      (prepareCreate create2CollisionRequest).stateCharged = false := by
  native_decide

example :
    let result := prepareCreate createExistingNoCollisionRequest
    result.status = .entered ∧ result.stateCharged = false ∧
      result.destinationLogicalAccountExists = true ∧
      result.destinationStorageCleared = false := by
  native_decide

example :
    let result := prepareCreate create2CollisionRequest
    result.gas.gasLeft = 15_437 ∧ result.gas.stateReservoir = 200_000 ∧
      result.entry = none := by
  native_decide

example :
    (prepareCreate create2StorageOnlyRequest).status = .collision ∧
      (prepareCreate create2StorageOnlyRequest).collision = .storageOnly ∧
      (prepareCreate create2StorageOnlyRequest).stateCharged = true ∧
      (prepareCreate create2StorageOnlyRequest).destinationLogicalAccountExists = false ∧
      (prepareCreate create2StorageOnlyRequest).destinationStorageCleared = false ∧
      (prepareCreate create2StorageOnlyRequest).collisionForwardedExecutionGas > 0 ∧
      (prepareCreate create2StorageOnlyRequest).collisionExecutionGasBurned = true ∧
      (prepareCreate create2StorageOnlyRequest).stateCharge = 183_600 ∧
      (prepareCreate create2StorageOnlyRequest).entry.isSome = false := by
  native_decide

example :
    let result := prepareCreate create2StorageOnlyRequest
    result.gas.gasLeft = 15_437 ∧ result.gas.stateReservoir = 200_000 ∧
      result.gas.stateUsed = 0 ∧ result.entry = none := by
  native_decide

example :
    let result := prepareCreate create2ExistingStorageOnlyRequest
    result.status = .collision ∧ result.collision = .storageOnly ∧
      result.stateCharged = false ∧ result.stateCharge = 0 ∧
      result.destinationLogicalAccountExists = true ∧ result.destinationStorageCleared = false ∧
      result.collisionForwardedExecutionGas > 0 ∧
      result.collisionExecutionGasBurned = true ∧ result.entry = none := by
  native_decide

example :
    let result := prepareCreate createMemoryOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.entryExecutionCharged = true ∧ result.memoryExpanded = false ∧
      result.destinationAccessed = false ∧ result.executionGasCleared = true := by
  native_decide

example :
    let result := prepareCreate createUnreadableRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.entryExecutionCharged = true ∧
      result.executionGasCleared = true ∧ result.destinationAccessed = false := by
  native_decide

example :
    let result := prepareCreate createOverLimitRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.entryExecutionCharged = false ∧ result.executionGasCleared = true ∧
      result.destinationAccessed = false := by
  native_decide

example :
    let result := prepareCreate createWordOverflowRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.entryExecutionCharged = false ∧ result.executionGasCleared = true ∧
      result.destinationAccessed = false := by
  native_decide

example :
    let result := prepareCreate createEntryOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.entryExecutionCharged = false ∧ result.memoryExpanded = false ∧
      result.destinationAccessed = false ∧ result.executionGasCleared = true := by
  native_decide

example :
    let result := prepareCreate create2HashOutOfGasRequest
    result.status = .outOfGas ∧ result.gas.gasLeft = 0 ∧
      result.entryExecutionCharged = false ∧ result.memoryExpanded = false ∧
      result.destinationAccessed = false ∧ result.executionGasCleared = true := by
  native_decide

example :
    let result := completeCreate schedule parentWorld frameEntry changedChildWorld
      depositExecutionOutOfGasChild 0 32 .fresh
    result.outcome.exit = .exceptional ∧ result.codeCommitted = false ∧
      result.depositExecutionCharged = 0 ∧ result.depositStateCharged = 0 := by
  native_decide

example :
    let result := completeCreate schedule parentWorld frameEntry changedChildWorld
      depositStateOutOfGasChild 0 32 .fresh
    result.outcome.exit = .exceptional ∧ result.codeCommitted = false ∧
      result.depositExecutionCharged = 6 ∧ result.depositStateCharged = 0 := by
  native_decide

example :
    let result := completeCreate schedule parentWorld creationEntry changedChildWorld
      failedCreationChild 100 0 .depositOutOfGas
    result.outcome.exit = .exceptional ∧ result.stateChargeRefilled = true ∧
      result.codeCommitted = false ∧ result.depositExecutionCharged = 0 ∧
      result.depositStateCharged = 0 := by
  native_decide

example :
    CreateDestinationOracleConsistent createRequest.destination ∧
      CreateDestinationOracleConsistent create2CollisionRequest.destination ∧
      CreateDestinationOracleConsistent create2StorageOnlyRequest.destination ∧
      CreateDestinationOracleConsistent create2ExistingStorageOnlyRequest.destination ∧
      ¬ CreateDestinationOracleConsistent absentStorageOnlyRequest.destination ∧
      ¬ CreateDestinationOracleConsistent invalidCodeCollisionRequest.destination ∧
      ¬ CreateDestinationOracleConsistent invalidNonceCollisionRequest.destination ∧
      ¬ CreateDestinationOracleConsistent invalidExistentRequest.destination := by
  native_decide

example :
    (prepareCreate absentStorageOnlyRequest).status = .oracleFailure ∧
      (prepareCreate invalidCodeCollisionRequest).status = .oracleFailure ∧
      (prepareCreate invalidNonceCollisionRequest).status = .oracleFailure ∧
      (prepareCreate invalidExistentRequest).status = .oracleFailure ∧
      (prepareCreate absentStorageOnlyRequest).destinationAccessed = false ∧
      (prepareCreate absentStorageOnlyRequest).entry = none := by
  native_decide

example :
    (prepareCreate createStateGasOogRequest).status = .outOfGas ∧
      (prepareCreate createStateGasOogRequest).destinationAccessed = true ∧
      (prepareCreate createStateGasOogRequest).entry = none := by
  native_decide

example :
    (enterFrame 17 (gas 100 50)).child.gas.gasLeft = 17 ∧
      (enterFrame 17 (gas 100 50)).child.gas.stateReservoir = 50 ∧
      (enterFrame 17 (gas 100 50)).pausedParent.gasLeft = 83 := by
  native_decide

example :
    let result := prepareCall schedule callRequest
    let entry := result.entry.getD (enterFrame 0 (gas 0 0))
    result.status = .entered ∧ result.gas = entry.pausedParent := by
  native_decide

example :
    let result := prepareCreate createRequest
    let entry := result.entry.getD (enterFrame 0 (gas 0 0))
    result.status = .entered ∧ result.gas = entry.pausedParent := by
  native_decide

example :
    let result := finishFrame parentWorld frameEntry changedChildWorld successfulChild .success
    result.exit = .success ∧ result.world.current = 19 ∧
      result.gas.gasLeft = 940 := by
  native_decide

example :
    let result := finishFrame parentWorld frameEntry changedChildWorld failedChild .revert
    result.world.current = 7 ∧ result.gas.stateUsed = failedChild.stateUsedBaseline ∧
      result.gas.refundCounter = failedChild.refundCounterBaseline := by
  native_decide

example :
    let result := finishFrame parentWorld frameEntry changedChildWorld failedChild .exceptional
    result.world.current = 7 ∧ result.gas.gasLeft = frameEntry.pausedParent.gasLeft := by
  native_decide

example : FrameGasReachable frameEntry.child successfulChildSteps successfulChild := by
  native_decide

example : FrameGasReachable frameEntry.child failedChildSteps failedChild := by
  native_decide

example :
    FrameGasReachable creationEntry.child failedCreationChildSteps failedCreationChild := by
  native_decide

example : FrameGasReachable frameEntry.child positiveRefundSteps positiveRefundChild := by
  native_decide

example : FrameGasReachable frameEntry.child negativeRefundSteps negativeRefundChild := by
  native_decide

example :
    FrameInvariant positiveRefundChild ∧ FrameInvariant negativeRefundChild := by
  constructor <;> unfold FrameInvariant <;> native_decide

example :
    let preparation := prepareCall schedule callRequest
    let entry := preparation.entry.getD (enterFrame 0 (gas 0 0))
    let child := { entry.child with
      gas := { entry.child.gas with refundCounter := entry.child.gas.refundCounter + 13 } }
    (completePreparedCall schedule callRequest parentWorld preparation changedChildWorld child [.refund 13] .revert).isSome =
      true := by
  native_decide

example :
    let preparation := prepareCreate createRequest
    let entry := preparation.entry.getD (enterFrame 0 (gas 0 0))
    let child := { entry.child with
      gas := { entry.child.gas with refundCounter := entry.child.gas.refundCounter - 7 } }
    (completePreparedCreate createRequest parentWorld preparation changedChildWorld child
      [.refund (-7)] 0 .childRevert).isSome = true := by
  native_decide

example :
    let result := completeCreate schedule parentWorld frameEntry changedChildWorld
      failedChild 100 0 .invalid
    result.stateChargeRefilled = true ∧ result.codeCommitted = false ∧
      result.outcome.world.current = changedChildWorld.checkpoint := by
  native_decide

example :
    let result := completeCreate schedule parentWorld frameEntry changedChildWorld
      successfulChild 0 0 .empty
    result.depositExecutionCharged = 0 ∧ result.depositStateCharged = 0 ∧
      result.codeCommitted = true := by
  native_decide

example :
    let result := completeCreate schedule parentWorld frameEntry changedChildWorld
      { successfulChild with gas := { successfulChild.gas with stateReservoir := 100_000 } }
        0 32 .duplicate
    result.depositExecutionCharged = 6 ∧ result.depositStateCharged = 48_960 ∧
      result.codeCommitted = true := by
  native_decide

example :
    let result := completeCreate schedule parentWorld creationEntry changedChildWorld
      failedCreationChild 100 0 .childRevert
    result.stateChargeRefilled = true ∧ result.codeCommitted = false ∧
      result.outcome.exit = .revert ∧
      result.outcome.world.current = changedChildWorld.checkpoint := by
  native_decide

example :
    let result := completeCall parentWorld creationEntry changedChildWorld
      failedCreationChild 100 .exceptional
    result.newAccountStateRefilled = true ∧ result.outcome.exit = .exceptional ∧
      result.outcome.world.current = changedChildWorld.checkpoint ∧
      result.outcome.gas.gasLeft = creationEntry.pausedParent.gasLeft := by
  native_decide

example :
    let preparation := prepareCall schedule callRequest
    let entry := preparation.entry.getD (enterFrame 0 (gas 0 0))
    let child := { entry.child with
      gas := { entry.child.gas with
        stateReservoir := entry.child.gas.stateReservoir - 1
        stateUsed := entry.child.gas.stateUsed + 1 } }
    (completePreparedCall schedule callRequest parentWorld preparation changedChildWorld child [.state 1] .revert).map
      (fun result => result.newAccountStateRefilled && result.outcome.exit == .revert) =
      some true := by
  native_decide

example :
    let preparation := prepareCreate createRequest
    let entry := preparation.entry.getD (enterFrame 0 (gas 0 0))
    let child := { entry.child with
      gas := { entry.child.gas with
        stateReservoir := entry.child.gas.stateReservoir - 1
        stateUsed := entry.child.gas.stateUsed + 1 } }
    (completePreparedCreate createRequest parentWorld preparation changedChildWorld child
        [.state 1] 0 .childRevert).map
      (fun result => result.stateChargeRefilled && result.outcome.exit == .revert &&
        result.outcome.world.current == changedChildWorld.checkpoint) = some true := by
  native_decide

example :
    let preparation := prepareCall schedule callRequest
    (completePreparedCall schedule callRequest parentWorld preparation forgedChildWorld forgedChild [] .revert).isNone = true := by
  native_decide

example :
    let preparation := prepareCreate createRequest
    (completePreparedCreate createRequest parentWorld preparation forgedChildWorld forgedChild []
      0 .childRevert).isNone = true := by
  native_decide

example :
    let result := completeCreate schedule parentWorld frameEntry changedChildWorld
      { successfulChild with gas := { successfulChild.gas with stateReservoir := 100_000 } }
        0 32 .fresh
    result.depositExecutionCharged = 6 ∧ result.depositStateCharged = 48_960 ∧
      result.codeCommitted = true := by
  native_decide

example :
    let state := gas 10 100
    let charged := { state with
      stateReservoir := 40
      stateUsed := 60
      stateFromGasLeft := 20 }
    (refillState 30 charged).gasLeft = 30 ∧
      (refillState 30 charged).stateReservoir = 50 ∧
      (refillState 30 charged).stateFromGasLeft = 0 := by
  native_decide

def mutatedForwarding (requested : Nat) (state : GasState) : Nat :=
  min requested (eip150Cap state + state.stateReservoir)

example :
    mutatedForwarding 200 (gas 100 100) ≠ forwardGas 200 (gas 100 100) := by
  native_decide

def mutatedCallCodeStateCharge (schedule : AccountGasSchedule) : Nat :=
  schedule.base.newAccountGas

example :
    mutatedCallCodeStateCharge schedule ≠
      (priceCallCode schedule
        { codeAccess := .cold, delegatedTargetAccess := none,
          executingAccount := .existent, value := 7 }).stateCharge := by
  native_decide

def mutatedExceptionalGas (entry : FrameEntry) (child : FrameGasState) : GasState :=
  mergeSuccess entry child

example : mutatedExceptionalGas frameEntry failedChild ≠
    mergeException frameEntry failedChild := by
  native_decide

def mutatedCollisionTreatsStorageAsNonCollision (collision : CollisionKind) : Bool :=
  collision = .nonEmptyCode || collision = .nonZeroNonce

example :
    mutatedCollisionTreatsStorageAsNonCollision .storageOnly ≠ CollisionKind.storageOnly.isCollision := by
  native_decide

def mutatedConsistencyRejectsExistingStorageOnly (destination : CreateDestinationOracle) : Bool :=
  destination.collision = .storageOnly && destination.physicalLeafExists

example :
    mutatedConsistencyRejectsExistingStorageOnly create2ExistingStorageOnlyRequest.destination = true ∧
      CreateDestinationOracleConsistent create2ExistingStorageOnlyRequest.destination := by
  native_decide

def mutatedStaticAllowsValue (request : CallRequest) : Bool :=
  request.staticContext && CallKind.hasTransfer request.kind 7

example : mutatedStaticAllowsValue staticValueRequest = true := by
  native_decide

def mutatedCreateNoRefill (_schedule : AccountGasSchedule) (parent : WorldFrame)
    (entry : FrameEntry) (world : WorldFrame) (child : FrameGasState) (_charge _runtime : Nat) : GasState :=
  (finishFrame parent entry world child .exceptional).gas

example :
    mutatedCreateNoRefill schedule parentWorld creationEntry changedChildWorld failedCreationChild 100 0 ≠
      (completeCreate schedule parentWorld creationEntry changedChildWorld failedCreationChild 100 0 .invalid).outcome.gas := by
  native_decide

def mutatedCallAccessFailureGas (request : CallRequest) : GasState := request.gas

example :
    mutatedCallAccessFailureGas callAccessOutOfGasRequest ≠
      (prepareCall schedule callAccessOutOfGasRequest).gas := by
  native_decide

def mutatedCreateMemoryFailureGas (request : CreateRequest) : GasState := request.gas

example :
    mutatedCreateMemoryFailureGas createMemoryOutOfGasRequest ≠
      (prepareCreate createMemoryOutOfGasRequest).gas := by
  native_decide

def physicalEmptyCreateRequest : CreateRequest :=
  { createRequest with destination :=
      { createRequest.destination with physicalLeafExists := true } }

def preparedCall : CallPreparation := prepareCall schedule callRequest

def preparedCallEntry : FrameEntry := preparedCall.entry.getD (enterFrame 0 (gas 0 0))

def preparedCreate : CreatePreparation := prepareCreate physicalEmptyCreateRequest

def preparedCreateEntry : FrameEntry := preparedCreate.entry.getD (enterFrame 0 (gas 0 0))

example :
    preparedCall.status = .entered ∧
      preparedCall.gas.gasLeft = 985_660 ∧ preparedCall.gas.stateReservoir = 0 ∧
      preparedCallEntry.child.gas.gasLeft = 2340 ∧
      preparedCallEntry.child.gas.stateReservoir = 16_400 ∧
      preparedCall.newAccountStateCharge = 183_600 := by
  native_decide

example :
    [depthLimitedRequest, lowBalanceRequest].map (fun request =>
      let result := prepareCall schedule request
      (result.status, result.gas.gasLeft, result.gas.stateReservoir, result.gas.stateUsed)) =
      [(.depthOrBalanceFailure, 988_000, 200_000, 0),
       (.depthOrBalanceFailure, 988_000, 200_000, 0)] := by
  native_decide

example :
    [ChildExit.success, .revert, .exceptional].map (fun exit =>
      (completePreparedCall schedule callRequest parentWorld preparedCall changedChildWorld
        preparedCallEntry.child [] exit).map (fun result =>
          (result.outcome.gas.gasLeft, result.outcome.gas.stateReservoir,
            result.outcome.gas.stateUsed))) =
      [some (988_000, 16_400, 183_600), some (988_000, 200_000, 0),
       some (985_660, 200_000, 0)] := by
  native_decide

def cappedValueCall : CallRequest :=
  { callRequest with
    stack := stackOf [ofNat 1_000_000, ofNat 1, ofNat 7, ofNat 0, ofNat 0, ofNat 0, ofNat 0]
    access := .warm
    gas := gas 20_000 200_000 }

example :
    let result := prepareCall schedule cappedValueCall
    let entry := result.entry.getD (enterFrame 0 (gas 0 0))
    result.status = .entered ∧ result.gas.gasLeft = 134 ∧
      entry.child.gas.gasLeft = 10_766 ∧ entry.child.gas.stateReservoir = 16_400 := by
  native_decide

example :
    let result := prepareCall schedule
      { cappedValueCall with access := .cold, gas := gas 200_000 0 }
    let entry := result.entry.getD (enterFrame 0 (gas 0 0))
    result.status = .entered ∧ result.gas.gasLeft = 32 ∧
      result.gas.stateFromGasLeft = 183_600 ∧ entry.child.gas.gasLeft = 4368 := by
  native_decide

example :
    let request := { callRequest with kind := .callcode }
    let result := prepareCall schedule request
    let entry := result.entry.getD (enterFrame 0 (gas 0 0))
    result.gas.gasLeft = 985_660 ∧ entry.child.gas.gasLeft = 2340 ∧
      entry.child.gas.stateReservoir = 200_000 ∧ result.newAccountStateCharge = 0 := by
  native_decide

example :
    [delegateRequest, { delegateRequest with kind := .staticcall }].map (fun request =>
      let result := prepareCall schedule request
      let entry := result.entry.getD (enterFrame 0 (gas 0 0))
      (result.gas.gasLeft, entry.child.gas.gasLeft, result.newAccountStateCharge)) =
      [(996_960, 40, 0), (996_960, 40, 0)] := by
  native_decide

example :
    let request := { callRequest with
      stack := stackOf [ofNat 40, ofNat 1, ofNat 0, ofNat 0, ofNat 0, ofNat 0, ofNat 0] }
    let result := prepareCall schedule request
    let entry := result.entry.getD (enterFrame 0 (gas 0 0))
    result.gas.gasLeft = 996_960 ∧ entry.child.gas.gasLeft = 40 ∧
      result.newAccountStateCharge = 0 := by
  native_decide

example :
    [10_000, 11_299, 11_300, 11_399, 11_400].map (fun left =>
      let result := prepareCall schedule { callExactAccessRequest with gas := gas left 200_000 }
      (result.status, result.targetAccessed)) =
      [(.outOfGas, false), (.outOfGas, false), (.outOfGas, true),
       (.outOfGas, true), (.entered, true)] := by
  native_decide

example :
    CreateDestinationOracleConsistent physicalEmptyCreateRequest.destination ∧
      preparedCreate.status = .entered ∧ preparedCreate.destinationPhysicalLeafExists = true ∧
      preparedCreate.destinationLogicalAccountExists = false ∧
      preparedCreate.stateCharge = 183_600 := by
  native_decide

example :
    [createRequest, physicalEmptyCreateRequest, createExistingNoCollisionRequest,
      create2StorageOnlyRequest, create2ExistingStorageOnlyRequest].map (fun request =>
        let result := prepareCreate request
        (result.destinationPhysicalLeafExists, result.destinationLogicalAccountExists,
          result.stateCharge, result.status)) =
      [(false, false, 183_600, .entered), (true, false, 183_600, .entered),
       (true, true, 0, .entered), (true, false, 183_600, .collision),
       (true, true, 0, .collision)] := by
  native_decide

example :
    [RuntimeCodeKind.childRevert, .invalid, .depositOutOfGas].map (fun code =>
      (completePreparedCreate physicalEmptyCreateRequest parentWorld preparedCreate
        changedChildWorld preparedCreateEntry.child [] 0 code).map (fun result =>
          (result.destinationPhysicalLeafExists, result.destinationLogicalAccountExists,
            result.outcome.gas.stateUsed, result.outcome.world.checkpoint,
            result.outcome.world.current))) =
      [some (some true, some false, 0, 0, 7), some (some true, some false, 0, 0, 7),
       some (some true, some false, 0, 0, 7)] := by
  native_decide

example :
    (completePreparedCreate physicalEmptyCreateRequest parentWorld preparedCreate
      changedChildWorld preparedCreateEntry.child [] 32 .fresh).map (fun result =>
        (result.destinationPhysicalLeafExists, result.destinationLogicalAccountExists,
          result.depositExecutionCharged, result.depositStateCharged, result.codeCommitted)) =
      some (some true, some false, 6, 48_960, true) := by
  native_decide

def unchangedChildChecks (entry : Option FrameEntry) (child : FrameGasState) : Bool :=
  match entry with
  | none => false
  | some admitted => decide (childWorldMatchesParent parentWorld changedChildWorld ∧
      FrameChildWellFormed admitted child ∧ FrameGasReachable admitted.child [] child)

def mutatedCallPreparations : List CallPreparation :=
  [ { preparedCall with newAccountStateCharge := 0 }
  , { preparedCall with newAccountStateCharged := false }
  , { preparedCall with status := .collision }
  , { preparedCall with gas := gas 0 0 }
  , { preparedCall with entry := some { preparedCallEntry with pausedParent := gas 0 0 } }
  , { preparedCall with operands := none }
  , { preparedCall with targetAccessed := false } ]

example :
    mutatedCallPreparations.length = 7 ∧
      mutatedCallPreparations.all (fun prepared =>
        unchangedChildChecks prepared.entry preparedCallEntry.child) = true ∧
      mutatedCallPreparations.all (fun prepared =>
        (completePreparedCall schedule callRequest parentWorld prepared changedChildWorld
          preparedCallEntry.child [] .revert).isNone) = true := by
  native_decide

def mutatedCreatePreparations : List CreatePreparation :=
  [ { preparedCreate with stateCharge := 0 }
  , { preparedCreate with stateCharged := false }
  , { preparedCreate with status := .collision }
  , { preparedCreate with gas := gas 0 0 }
  , { preparedCreate with entry := some { preparedCreateEntry with pausedParent := gas 0 0 } }
  , { preparedCreate with destinationPhysicalLeafExists := false }
  , { preparedCreate with destinationLogicalAccountExists := true }
  , { preparedCreate with destinationAccessed := false } ]

example :
    mutatedCreatePreparations.length = 8 ∧
      mutatedCreatePreparations.all (fun prepared =>
        unchangedChildChecks prepared.entry preparedCreateEntry.child) = true ∧
      mutatedCreatePreparations.all (fun prepared =>
        (completePreparedCreate physicalEmptyCreateRequest parentWorld prepared changedChildWorld
          preparedCreateEntry.child [] 0 .invalid).isNone) = true := by
  native_decide

example :
    (completePreparedCall schedule { callRequest with gas := gas 1_000_001 200_000 }
      parentWorld preparedCall changedChildWorld preparedCallEntry.child [] .revert).isNone = true ∧
    (completePreparedCreate createRequest parentWorld preparedCreate changedChildWorld
      preparedCreateEntry.child [] 0 .invalid).isNone = true := by
  native_decide

def nestedOuterRequest : CallRequest :=
  { delegateRequest with
    stack := stackOf [ofNat 100_000, ofNat 1, ofNat 0, ofNat 0, ofNat 0, ofNat 0] }

def nestedOuterPreparation : CallPreparation := prepareCall schedule nestedOuterRequest

def nestedOuterEntry : FrameEntry :=
  nestedOuterPreparation.entry.getD (enterFrame 0 (gas 0 0))

def nestedInnerRequest : CallRequest :=
  { delegateRequest with gas := nestedOuterEntry.child.gas, access := .warm }

def nestedInnerPreparation : CallPreparation := prepareCall schedule nestedInnerRequest

def nestedInnerEntry : FrameEntry :=
  nestedInnerPreparation.entry.getD (enterFrame 0 (gas 0 0))

def nestedInnerCompletion (exit : ChildExit) : Option CallCompletion :=
  completePreparedCall schedule nestedInnerRequest parentWorld nestedInnerPreparation
    changedChildWorld nestedInnerEntry.child [] exit

def nestedOuterCompletion (innerExit outerExit : ChildExit) : Option CallCompletion := do
  let inner ← nestedInnerCompletion innerExit
  let child := { nestedOuterEntry.child with gas := inner.outcome.gas }
  let spent := if innerExit = .exceptional then 140 else 100
  completePreparedCall schedule nestedOuterRequest { checkpoint := 0, current := 0 }
    nestedOuterPreparation inner.outcome.world child [.execution spent] outerExit

example :
    nestedOuterPreparation.status = .entered ∧ nestedInnerPreparation.status = .entered ∧
      [ChildExit.revert, .exceptional].all (fun exit =>
        match nestedInnerCompletion exit with
        | none => false
        | some inner => decide (FrameGasReachable nestedOuterEntry.child
            [.execution (if exit = .exceptional then 140 else 100)]
            { nestedOuterEntry.child with gas := inner.outcome.gas })) = true := by
  native_decide

example :
    [(ChildExit.revert, ChildExit.success), (.exceptional, .success),
      (.revert, .revert), (.exceptional, .revert)].map (fun (innerExit, outerExit) =>
        (nestedOuterCompletion innerExit outerExit).map (fun result =>
          (result.outcome.world.checkpoint, result.outcome.world.current,
            result.outcome.gas.gasLeft))) =
      [some (0, 7, 996_900), some (0, 7, 996_860),
       some (0, 0, 996_900), some (0, 0, 996_860)] := by
  native_decide

def mutatedValueCharge : Nat := schedule.base.accountWrite + 1

example : mutatedValueCharge = 9001 ∧
    mutatedValueCharge ≠ schedule.base.accountWrite + schedule.callStipend ∧
    (prepareCall schedule { callExactAccessRequest with gas := gas 10_000 200_000 }).status =
      .outOfGas := by
  native_decide

def mutatedPhysicalExistence (destination : CreateDestinationOracle) : Bool :=
  destination.logicalAccountExists

example : mutatedPhysicalExistence physicalEmptyCreateRequest.destination ≠
    preparedCreate.destinationPhysicalLeafExists := by
  native_decide

def mutatedRollbackWorld (child : WorldFrame) : WorldFrame :=
  { checkpoint := child.checkpoint, current := child.checkpoint }

example : mutatedRollbackWorld changedChildWorld ≠
    WorldFrame.restore parentWorld changedChildWorld := by
  native_decide

end CallCreateFrameVectors
end Evm
end Eip803x
