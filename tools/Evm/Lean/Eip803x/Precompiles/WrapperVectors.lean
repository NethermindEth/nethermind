-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Wrapper

namespace Eip803x
namespace Precompiles
namespace WrapperVectors

open Wrapper

def gas (left : Nat) (reservoir : Int := 0) : ProductionGasState :=
  { gasLeft := left
    stateReservoir := reservoir
    stateGasUsed := 0
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

def ordinaryAccount : AccountFacts :=
  { priorRestorePending := false
    wasCreated := true
    transferValueIsZero := true
    eip158Active := true
    isRipemd160 := false
    deadAfterCredit := false
    existsAfterSnapshotRestore := false }

def ripemdAccount : AccountFacts :=
  { priorRestorePending := false
    wasCreated := false
    transferValueIsZero := true
    eip158Active := true
    isRipemd160 := true
    deadAfterCredit := true
    existsAfterSnapshotRestore := true }

def full (available : Nat) (cost : Cost) (leaf : LeafResult)
    (account : AccountFacts := ordinaryAccount) : FullInput :=
  { cost, gas := gas available 40, account, leaf }

def direct (available forwarded : Nat) (cost : Cost) (leaf : LeafResult) : DirectInput :=
  { traceInstructions := false
    traceActions := false
    isRipemdCodeSource := false
    inputMemoryValid := true
    postReservationParent := gas available 40
    forwardedGas := forwarded
    cost
    leaf
    outputLength := 2
    priorReturnData := [0xff] }

example : totalCost? { base := uint64Max, data := 1 } = none := by native_decide

example : totalCost? { base := uint64Max, data := 0 } = some uint64Max := by native_decide

example : tryConsumePrecompileGas { base := 15, data := 3 } (gas 17) =
    .failure .outOfGas (gas 0) := by native_decide

example : tryConsumePrecompileGas { base := 15, data := 3 } (gas 18) =
    .success (gas 0) 18 := by native_decide

example : shouldRestoreRipemdTouch ripemdAccount = true := by native_decide

example : shouldRestoreRipemdTouch { ripemdAccount with wasCreated := true } = false := by
  native_decide

example : (runFullRaw (full 17 { base := 15, data := 3 } (.success [0xaa])
    ripemdAccount)).events =
  [.transferLogOracle, .accountTouchedOrCreated, .ripemdRestoreMarked,
    .pricingChecked 15 3, .localExecutionGasCleared,
    .pricingRejected .outOfGas] := by native_decide

example :
    let result := runFullRaw (full 17 { base := 15, data := 3 } (.success [0xaa]))
    result.gas = gas 17 40 /\
      result.localPricingGas = gas 0 40 := by native_decide

example : (executeTop (full 17 { base := 15, data := 3 } (.success [0xaa])
    ripemdAccount)).status = .exceptional .outOfGas := by native_decide

example : (executeTop (full 100 { base := 15, data := 3 } (.failure "bad input")
    ripemdAccount)).world =
  { accountTouchOrCreditDurable := false, ripemdDirtyTouchDurable := true } := by native_decide

example : (executeTop (full 100 { base := 15, data := 3 }
    (.failure "bad input"))).status = .exceptional .outOfGas := by native_decide

example : (executeTop (full 100 { base := 15, data := 3 } .managedException)).status =
    .exceptional .precompileFailure := by native_decide

example :
    let laterFailureAccount : AccountFacts :=
      { ordinaryAccount with
        priorRestorePending := true
        existsAfterSnapshotRestore := true }
    let result := executeTop
      (full 17 { base := 15, data := 3 } (.success []) laterFailureAccount)
    result.restorePending = true /\
      result.world.ripemdDirtyTouchDurable = true /\
      result.events.contains .ripemdRestoreMarked = false /\
      result.events.contains .ripemdTouchRestored = true := by native_decide

example :
    let input := full 17 { base := 15, data := 3 } (.success [0xaa])
    let result := executeNested (gas 100) 8 input
    result.exit = .exceptionalHalt /\
      result.vmException = .outOfGas /\
      result.parentGas = gas 100 40 /\
      result.stackResult = false := by native_decide

example :
    let input := full 50 { base := 15, data := 3 } (.failure "invalid")
    let result := executeNested (gas 100) 8 input
    result.exit = .exceptionalHalt /\
      result.vmException = .outOfGas /\
      result.childGasAtExit.gasLeft = 32 /\
      result.parentGas = gas 100 40 /\
      result.rawCall.shouldRevert = true /\
      result.rawCall.exception = .precompileFailure /\
      result.stackResult = false := by native_decide

example :
    let input := full 50 { base := 15, data := 3 } .managedException
    let result := executeNested (gas 100) 8 input
    result.exit = .reverted /\
      result.childGasAtExit.gasLeft = 0 /\
      result.rawCall.exception = .none /\
      result.stackResult = false := by native_decide

example :
    let input := full 50 { base := 15, data := 3 } (.success [0xaa, 0xbb, 0xcc])
    let result := executeNested (gas 100) 2 input
    result.exit = .success /\
      result.parentGas = gas 132 40 /\
      result.returnData = [0xaa, 0xbb, 0xcc] /\
      result.copiedOutput = [0xaa, 0xbb] /\
      result.stackResult = true := by native_decide

example :
    let parent : ProductionGasState :=
      { gasLeft := 100, stateReservoir := 5, stateGasUsed := 20,
        stateGasSpill := 7, stateGasSpillRefunded := 2 }
    let child : ProductionGasState :=
      { gasLeft := 50, stateReservoir := 30, stateGasUsed := 12,
        stateGasSpill := 9, stateGasSpillRefunded := 4 }
    let input : FullInput :=
      { cost := { base := 6, data := 4 }, gas := child,
        account := ordinaryAccount, leaf := .success [] }
    let result := executeNested parent 0 input
    result.parentGas =
      { gasLeft := 150, stateReservoir := 25, stateGasUsed := 32,
        stateGasSpill := 16, stateGasSpillRefunded := 16 } := by native_decide

example :
    let parent : ProductionGasState :=
      { gasLeft := 100, stateReservoir := 5, stateGasUsed := 20,
        stateGasSpill := 7, stateGasSpillRefunded := 2 }
    let child : ProductionGasState :=
      { gasLeft := 50, stateReservoir := 30, stateGasUsed := 12,
        stateGasSpill := 9, stateGasSpillRefunded := 4 }
    let input : FullInput :=
      { cost := { base := 6, data := 4 }, gas := child,
        account := ordinaryAccount, leaf := .managedException }
    let result := executeNested parent 0 input
    result.exit = .reverted /\
      result.childGasAtExit.gasLeft = 0 /\
      result.parentGas =
        { gasLeft := 105, stateReservoir := 42, stateGasUsed := 20,
          stateGasSpill := 7, stateGasSpillRefunded := 2 } := by native_decide

example :
    let parent : ProductionGasState :=
      { gasLeft := 100, stateReservoir := 5, stateGasUsed := 20,
        stateGasSpill := 7, stateGasSpillRefunded := 2 }
    let child : ProductionGasState :=
      { gasLeft := 50, stateReservoir := 30, stateGasUsed := 12,
        stateGasSpill := 9, stateGasSpillRefunded := 4 }
    let input : FullInput :=
      { cost := { base := 6, data := 4 }, gas := child,
        account := ordinaryAccount, leaf := .failure "bad" }
    let result := executeNested parent 0 input
    result.exit = .exceptionalHalt /\
      result.childGasAtExit.gasLeft = 40 /\
      result.parentGas =
        { gasLeft := 100, stateReservoir := 42, stateGasUsed := 20,
          stateGasSpill := 7, stateGasSpillRefunded := 2 } := by native_decide

example : directEligible { (direct 100 50 { base := 15, data := 3 }
    (.success [])) with isRipemdCodeSource := true } = false := by native_decide

example : directEligible { (direct 100 50 { base := 15, data := 3 }
    (.success [])) with traceInstructions := true } = false := by native_decide

example :
    let input := { (direct 100 50 { base := 15, data := 3 } (.success [])) with
      inputMemoryValid := false }
    let result := executeDirect input
    result.status = .inputMemoryOutOfGas /\
      result.parentGas = gas 100 40 /\
      result.childGasAtExit = none /\
      result.returnData = [0xff] := by native_decide

example :
    let result := executeDirect (direct 100 17 { base := 15, data := 3 } (.success [0xaa]))
    result.status = .handledFailure /\
      result.parentGas = gas 100 40 /\
      result.childGasAtExit = some (gas 0 40) /\
      result.stackResult = some false /\
      result.returnData = [] /\
      result.accountTouchDurable = false := by native_decide

example :
    let result := executeDirect
      (direct 100 100 { base := uint64Max, data := 1 } (.success [0xaa]))
    result.status = .handledFailure /\
      result.parentGas = gas 100 40 /\
      result.childGasAtExit = some (gas 100 40) /\
      result.stackResult = some false := by native_decide

example :
    let result := executeDirect (direct 100 50 { base := 15, data := 3 }
      (.failure "invalid point"))
    result.status = .handledFailure /\
      result.childGasAtExit = some (gas 0 40) /\
      result.parentGas = gas 100 40 /\
      result.stackResult = some false /\
      result.accountTouchDurable = false := by native_decide

example :
    let result := executeDirect (direct 100 50 { base := 15, data := 3 } .managedException)
    result.status = .handledFailure /\
      result.childGasAtExit = some (gas 0 40) /\
      result.parentGas = gas 100 40 := by native_decide

example : executeDirect (direct 100 50 { base := 15, data := 3 }
    (.failure "invalid point")) =
    executeDirect (direct 100 50 { base := 15, data := 3 } .managedException) := by
  native_decide

example :
    let result := executeDirect (direct 100 50 { base := 15, data := 3 }
      (.success [0xaa, 0xbb, 0xcc]))
    result.status = .handledSuccess /\
      result.childGasAtExit = some (gas 32 40) /\
      result.parentGas = gas 132 40 /\
      result.returnData = [0xaa, 0xbb, 0xcc] /\
      result.copiedOutput = [0xaa, 0xbb] /\
      result.stackResult = some true /\
      result.accountTouchDurable = true := by native_decide

example :
    let result := executeNested (gas 100) 0
      (full 50 { base := 15, data := 3 } (.failure "bad"))
    result.events =
      [.transferLogOracle, .accountTouchedOrCreated, .pricingChecked 15 3,
        .executionGasDebited 18, .precompileLeafInvoked, .snapshotRestored,
        .returnDataSet [], .childStateRestoredOnHalt, .childExecutionDiscarded,
        .stackResultPushed false, .outputCopied []] := by native_decide

example :
    let result := executeDirect
      (direct 100 17 { base := 15, data := 3 } (.success [0xaa]))
    result.events =
      [.pricingChecked 15 3, .localExecutionGasCleared, .pricingRejected .outOfGas,
        .childStateRestoredOnHalt, .returnDataSet [], .stackResultPushed false,
        .childExecutionDiscarded] := by native_decide

example :
    let result := executeDirect
      (direct 100 100 { base := uint64Max, data := 1 } (.success [0xaa]))
    result.events =
      [.pricingChecked uint64Max 1, .pricingRejected .baseDataOverflow,
        .childStateRestoredOnHalt, .returnDataSet [], .stackResultPushed false,
        .childExecutionDiscarded] := by native_decide

/-- Mutation sentinel: wrapping addition would turn `MAX + 1` into an affordable zero cost. -/
def mutatedWrappingCost (cost : Cost) : Nat := (cost.base + cost.data) % (2 ^ 64)

example : mutatedWrappingCost { base := uint64Max, data := 1 } = 0 /\
    totalCost? { base := uint64Max, data := 1 } = none := by native_decide

/-- Mutation sentinel: pricing before the full-frame account action changes the audit order. -/
def mutatedPricingFirstPrelude (input : FullInput) : List Event :=
  [.transferLogOracle, .pricingChecked input.cost.base input.cost.data,
    .accountTouchedOrCreated]

example : mutatedPricingFirstPrelude (full 100 { base := 15, data := 3 } (.success [])) ≠
    fullPrelude (full 100 { base := 15, data := 3 } (.success [])) := by native_decide

/-- Mutation sentinel: refunding a failed inline child's execution gas violates halt semantics. -/
def mutatedFailedDirectRefund : ProductionGasState :=
  refundChildGas (gas 100) (gas 32 40)

example : mutatedFailedDirectRefund ≠
    restoreChildStateGasOnHalt (gas 100) (gas 32 40) := by native_decide

/-- Mutation sentinel: direct failure must not touch or create the target account. -/
example : (executeDirect (direct 100 50 { base := 15, data := 3 }
    (.failure "bad"))).accountTouchDurable ≠ true := by native_decide

/-- Mutation sentinel: abandoning a hard-failed child before reading its state gas is reordered. -/
def mutatedDiscardBeforeRestore : List Event :=
  [.childExecutionDiscarded, .childStateRestoredOnHalt]

example : mutatedDiscardBeforeRestore ≠
    [.childStateRestoredOnHalt, .childExecutionDiscarded] := by native_decide

/-- Mutation sentinel: clearing gas on addition overflow would conflate it with ordinary OOG. -/
example :
    let ordinary := executeDirect
      (direct 100 17 { base := 15, data := 3 } (.success []))
    let overflow := executeDirect
      (direct 100 17 { base := uint64Max, data := 1 } (.success []))
    ordinary.childGasAtExit ≠ overflow.childGasAtExit := by native_decide

end WrapperVectors
end Precompiles
end Eip803x
