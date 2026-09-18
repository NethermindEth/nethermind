-- Generated from the exact Stage C Roslyn source/member profile.
-- IR SHA-256: fc9268af5ad1203f3156436c7edb3e0bb0aba049e9cdc7a976a652a8de3b2137
-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import PrecompileFullFrameExtractor.Specification.Types
import Eip803x.Generated.PrecompileGasPricingKernel

namespace Eip803x.PrecompileFullFrame.Generated

open Evm.FrameMachineState
open Eip803x.Generated.PrecompileGasPricingKernel

def prepare (operations : FrontOperations) (input : Input) : Machine :=
  let initial := { input.machine with returnDataBuffer := [] }
  let traced := if initial.isTracingActions then
      operations.traceActionStart (actionAddress input) initial else initial
  let logged := operations.addTransferLog traced
  let touched := operations.touchAccount input.executingAccount logged
  if !input.wasCreated && input.transferValueZero && input.executingAccount == 3 && input.ripemdDead then
    { touched with shouldRestoreRipemdTouch := true }
  else touched

def result (machine : Machine) (exit : FrameExit) (route : FailureControlRoute)
    (output : List Byte) (success : Option Bool) (error : Option String) : FrameResult :=
  { exit, controlRoute := route, frame := machine.current, output,
    createdAddress := none, precompileSuccess := success, substateError := error,
    shouldRestoreRipemdTouch := machine.shouldRestoreRipemdTouch, controlTrace := machine.controlTrace }

def hard (kind : RawKind) (machine : Machine) (exception : ExceptionKind)
    (error : Option String) (installed runs : Bool) (events : List String) : RawResult :=
  { kind, machine, result := result machine (.exception exception) .handleFailure [] (some false) error,
    installedGas := installed, invokedOracle := runs, events }

def installGas (machine : Machine) (remaining : Nat) : Machine :=
  { machine with current := { machine.current with gas := { machine.current.gas with
      gas := { machine.current.gas.gas with gasLeft := remaining } } } }

def prefixEvents : List String :=
  ["actionStartIfTracing", "transferLog", "touchExecutingAccount", "ripemdLatchIfEligible", "pricing"]

def executePrecompile (operations : FrontOperations) (oracle : LeafOracle) (input : Input) :
    Option RawResult :=
  let prepared := prepare operations input
  let pricing := tryConsume input.machine.current.gas.gas.gasLeft input.baseCost input.dataCost
  match pricing.outcome with
  | .baseDataOverflow => some (hard .pricingOverflow prepared .outOfGas none false false prefixEvents)
  | .outOfGas => some (hard .pricingOutOfGas prepared .outOfGas none false false prefixEvents)
  | .success =>
    let priced := installGas prepared pricing.remainingGas
    let events := prefixEvents ++ ["installPricedGas", "runOracle"]
    match oracle (actionAddress input) input.machine.current.input with
    | .missingNativeDependency => none
    | .returnedFailure error =>
      some (hard .returnedFailure priced .outOfGas
        (some ("Precompile " ++ input.precompileName ++ " failed with error: " ++ error.getD "")) true true events)
    | .managedException =>
      if input.machine.current.isTopLevel then
        some (hard .managedTopFailure priced .precompileFailure none true true events)
      else
        some {
          kind := .managedNestedFailure, machine := priced,
          result := result priced .revert .nestedPrecompileSoftFailure [] (some false) none,
          installedGas := true, invokedOracle := true, events }
    | .success output =>
      some {
        kind := .success, machine := priced,
        result := result priced .success .ordinary output (some true) none,
        installedGas := true, invokedOracle := true, events }

def execute (operations : FrontOperations) (oracle : LeafOracle) : Entry → Option RawResult
  | .fullFrame input => executePrecompile operations oracle input
  | .outerEvmException machine kind =>
    some (hard .outerEvmException machine kind none false false ["outerEvmException"])
  | .outerOverflow machine =>
    some (hard .outerOverflow machine .other none false false ["outerOverflow"])

def record (machine : Machine) (event : String) : Machine :=
  { machine with controlTrace := machine.controlTrace ++ [event] }

def resumed (machine : Machine) (parents : List SuspendedFrame) (parent : Frame) : Machine :=
  { machine with current := { parent with phase := .continuation }, parents }

def failedCall (machine : Machine) : Machine :=
  { machine with
    previousCallResult := ⟨none, some false⟩,
    previousCallOutputDestination := 0, previousCallOutputLength := 0, returnDataBuffer := [] }

def creditNewAccount (operations : SettlementPrimitives) (suspended : SuspendedFrame)
    (parent : Frame) : Frame :=
  if suspended.childBaseline.newAccountCharged then
    operations.creditStateGasRefund parent operations.stateGasNewAccountCost
  else parent

def restoreFailure (operations : SettlementPrimitives) (machine : Machine) : Machine :=
  let restored := record { machine with
      current := operations.restoreSnapshot machine.current.snapshot machine.current } "restoreSnapshot"
  record { restored with
    current := operations.restoreRipemdTouch
      machine.shouldRestoreRipemdTouch restored.current } "restoreRipemdTouch"

def traceFailure (operations : SettlementPrimitives) (kind : ExceptionKind)
    (machine : Machine) : Machine :=
  let traced := if machine.current.dispatchTable.tracing then
      record (operations.traceOperationFailure kind
        (record (operations.traceOperationRemainingGasZero machine) "traceOperationRemainingGasZero"))
        "traceOperationError"
    else machine
  if traced.isTracingActions then
    record (operations.traceActionFailure kind traced) "traceActionError"
  else traced

def hardSettlement (operations : SettlementPrimitives) (raw : RawResult)
    (kind : ExceptionKind) : Settlement :=
  let machine := traceFailure operations kind (restoreFailure operations raw.machine)
  match raw.machine.parents with
  | [] =>
    .complete { raw.result with
      frame := { machine.current with
        refund := 0,
        gas := { machine.current.gas with gas := { machine.current.gas.gas with refundCounter := 0 } } },
      shouldRestoreRipemdTouch := machine.shouldRestoreRipemdTouch, controlTrace := machine.controlTrace }
  | suspended :: parents =>
    let cleared := failedCall machine
    let parent := { suspended.parent with world := cleared.current.world }
    let withoutAdvanced := operations.removeAdvancedRefund parent raw.result.frame
    let restored := operations.restoreChildStateGasOnHalt withoutAdvanced.1 withoutAdvanced.2
    .resume (resumed cleared parents (creditNewAccount operations suspended restored))

def successSettlement (operations : SettlementPrimitives) (raw : RawResult)
    (suspended : SuspendedFrame) (parents : List SuspendedFrame) : Settlement :=
  let reconciled := operations.incorporateAdvancedRefund suspended.parent raw.result.frame
  let parent := { reconciled.1 with gas := { reconciled.1.gas with
    gas := operations.refundChildGas suspended.gasEntry reconciled.2.gas } }
  let machine := resumed raw.machine parents parent
  let returned := { machine with
    previousCallResult := ⟨none, some true⟩,
    previousCallOutputDestination := reconciled.2.outputDestination,
    previousCallOutputLength := min raw.result.output.length reconciled.2.outputLength,
    returnDataBuffer := raw.result.output }
  let committed := operations.commitChild returned.current reconciled.2
  .resume { returned with current := operations.repayStateGasSpill committed }

def softSettlement (operations : SettlementPrimitives) (raw : RawResult)
    (suspended : SuspendedFrame) (parents : List SuspendedFrame) : Settlement :=
  let machine := record (record raw.machine "clearExecutionGas") "classifyShouldRevert"
  let child := operations.clearExecutionGas raw.result.frame
  let returned := operations.returnChildExecutionGas suspended.parent child
  let withoutAdvanced := operations.removeAdvancedRefund returned child
  let gasRestored := operations.restoreChildStateGas withoutAdvanced.1 withoutAdvanced.2
  let credited := creditNewAccount operations suspended gasRestored
  let restored := operations.restoreRipemdTouch machine.shouldRestoreRipemdTouch
    (operations.restoreSnapshot suspended.childBaseline.snapshot credited)
  let parent := resumed machine parents restored
  .resume { parent with
    previousCallResult := ⟨none, some false⟩,
    previousCallOutputDestination := child.outputDestination,
    previousCallOutputLength := min raw.result.output.length child.outputLength,
    returnDataBuffer := raw.result.output }

def settle (front : FrontOperations) (operations : SettlementPrimitives) (raw : RawResult) : Settlement :=
  match raw.result.exit with
  | .exception kind => hardSettlement operations raw kind
  | .revert =>
    match raw.machine.parents with
    | [] => .invalidControl raw.machine
    | suspended :: parents => softSettlement operations raw suspended parents
  | .success =>
    match raw.machine.parents with
    | [] =>
      let ended := if raw.machine.isTracingActions then
          front.traceActionEnd raw.machine raw.result else raw.machine
      let prepared := front.prepareTopLevelSubstate ended raw.result
      .complete { prepared with
        shouldRestoreRipemdTouch := ended.shouldRestoreRipemdTouch,
        controlTrace := ended.controlTrace }
    | suspended :: parents => successSettlement operations raw suspended parents

def fullFrame (front : FrontOperations) (operations : SettlementPrimitives)
    (oracle : LeafOracle) (entry : Entry) : Option Settlement :=
  (execute front oracle entry).map (settle front operations)

end Eip803x.PrecompileFullFrame.Generated

namespace Eip803x.PrecompileFullFrame.Generated

def admittedBranches : List String := [
  "PricingOverflow",
  "PricingOutOfGas",
  "ReturnedFailure",
  "ManagedTopFailure",
  "ManagedNestedFailure",
  "TopSuccess",
  "NestedSuccess",
  "OuterEvmException",
  "OuterOverflow",
  "MissingNativeDependency",
]

def admittedOracleAddresses : List Nat := [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 256]

end Eip803x.PrecompileFullFrame.Generated
