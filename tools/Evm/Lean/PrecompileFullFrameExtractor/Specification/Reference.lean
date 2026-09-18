-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import PrecompileFullFrameExtractor.Specification.Types
import Eip803x.Generated.PrecompileGasPricingKernel
import EvmFrameMachineExtractor.Specification.FrameMachineExecution

namespace Eip803x.PrecompileFullFrame.Reference

open Evm.FrameMachineState
open Evm.FrameMachineExecution
open Eip803x.Generated.PrecompileGasPricingKernel

/- The reference separates branch selection from state projection. It never
   imports the emitted transition or calls its helpers. -/
structure Decision where
  kind : RawKind
  remaining : Option Nat
  output : List Byte
  error : Option String
  runs : Bool

def select (oracle : LeafOracle) (input : Input) : Option Decision :=
  let priced := tryConsume input.machine.current.gas.gas.gasLeft input.baseCost input.dataCost
  match priced.outcome with
  | .baseDataOverflow => some ⟨.pricingOverflow, none, [], none, false⟩
  | .outOfGas => some ⟨.pricingOutOfGas, none, [], none, false⟩
  | .success =>
    match oracle (actionAddress input) input.machine.current.input with
    | .success output => some ⟨.success, some priced.remainingGas, output, none, true⟩
    | .returnedFailure error => some ⟨.returnedFailure, some priced.remainingGas, [],
        some ("Precompile " ++ input.precompileName ++ " failed with error: " ++ error.getD ""), true⟩
    | .managedException => some ⟨if input.machine.current.isTopLevel then .managedTopFailure
        else .managedNestedFailure, some priced.remainingGas, [], none, true⟩
    | .missingNativeDependency => none

def enter (operations : FrontOperations) (input : Input) : Machine :=
  let cleared := { input.machine with returnDataBuffer := [] }
  let action := if cleared.isTracingActions then operations.traceActionStart
      (input.codeSource.getD input.executingAccount) cleared else cleared
  let touched := operations.touchAccount input.executingAccount (operations.addTransferLog action)
  { touched with shouldRestoreRipemdTouch :=
    if !input.wasCreated && input.transferValueZero && input.executingAccount == 3 && input.ripemdDead
    then true else touched.shouldRestoreRipemdTouch }

def exitFor : RawKind → FrameExit
  | .success => .success
  | .managedNestedFailure => .revert
  | .managedTopFailure => .exception .precompileFailure
  | .outerOverflow => .exception .other
  | _ => .exception .outOfGas

def routeFor : RawKind → FailureControlRoute
  | .success => .ordinary
  | .managedNestedFailure => .nestedPrecompileSoftFailure
  | _ => .handleFailure

def project (operations : FrontOperations) (input : Input) (decision : Decision) : RawResult :=
  let entered := enter operations input
  let machine := match decision.remaining with
    | none => entered
    | some gas => { entered with current := { entered.current with gas := { entered.current.gas with
        gas := { entered.current.gas.gas with gasLeft := gas } } } }
  { kind := decision.kind, machine,
    result := {
      exit := exitFor decision.kind, controlRoute := routeFor decision.kind,
      frame := machine.current, output := decision.output, createdAddress := none,
      precompileSuccess := some (decision.kind == .success), substateError := decision.error,
      shouldRestoreRipemdTouch := machine.shouldRestoreRipemdTouch, controlTrace := machine.controlTrace },
    installedGas := decision.remaining.isSome, invokedOracle := decision.runs,
    events := ["actionStartIfTracing", "transferLog", "touchExecutingAccount", "ripemdLatchIfEligible", "pricing"] ++
      if decision.runs then ["installPricedGas", "runOracle"] else [] }

def executePrecompile (operations : FrontOperations) (oracle : LeafOracle) (input : Input) : Option RawResult :=
  (select oracle input).map (project operations input)

def exceptional (machine : Machine) (kind : RawKind) (exception : ExceptionKind)
    (event : String) : RawResult :=
  { kind, machine, installedGas := false, invokedOracle := false, events := [event],
    result := {
      exit := .exception exception, controlRoute := .handleFailure,
      frame := machine.current, output := [], createdAddress := none,
      precompileSuccess := some false, substateError := none,
      shouldRestoreRipemdTouch := machine.shouldRestoreRipemdTouch, controlTrace := machine.controlTrace } }

def execute (front : FrontOperations) (oracle : LeafOracle) : Entry → Option RawResult
  | .fullFrame input => executePrecompile front oracle input
  | .outerEvmException machine kind => some (exceptional machine .outerEvmException kind "outerEvmException")
  | .outerOverflow machine => some (exceptional machine .outerOverflow .other "outerOverflow")

def settle (front : FrontOperations) (semantics : Semantics) (raw : RawResult) : Settlement :=
  match raw.result.exit, raw.machine.parents with
  | .success, [] =>
    let machine := if raw.machine.isTracingActions then
        front.traceActionEnd raw.machine raw.result else raw.machine
    settleChild semantics machine (front.prepareTopLevelSubstate machine raw.result)
  | _, _ => settleChild semantics raw.machine raw.result

def fullFrame (front : FrontOperations) (semantics : Semantics)
    (oracle : LeafOracle) (entry : Entry) : Option Settlement :=
  (execute front oracle entry).map (settle front semantics)

end Eip803x.PrecompileFullFrame.Reference
