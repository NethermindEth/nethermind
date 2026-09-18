-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameDriverExtractor.Specification.Types

/-!
An independently organized reference for the Stage E shell.  It uses an entry
plan, a dispatch decision, and a settlement plan instead of reusing the
generated function names.  The only shared values are the explicit leaf
interfaces and the neutral frame-machine carrier.
-/

namespace Eip803x.Evm.FrameDriver.Reference

open Eip803x.Evm.FrameMachineState
open Eip803x.Evm.FrameDriver

inductive EntryPlan where
  | fresh
  | continuation
  | alreadyRunning
  deriving DecidableEq, Repr

def planEntry (machine : Machine) : EntryPlan :=
  match machine.current.phase with
  | .fresh => .fresh
  | .continuation => .continuation
  | .running => .alreadyRunning

def enterFrame (leaves : DriverLeaves) (machine : Machine) : Machine :=
  match planEntry machine with
  | .fresh => leaves.preparation.prepareFresh (leaves.preparation.clearReturnData machine)
  | .continuation => leaves.preparation.prepareContinuation machine
  | .alreadyRunning => machine

inductive DispatchPlan where
  | bytecode
  | fullPrecompile
  deriving DecidableEq, Repr

def planDispatch (machine : Machine) : DispatchPlan :=
  match machine.current.kind with
  | .bytecode => .bytecode
  | .precompile _ => .fullPrecompile

structure Decision where
  frame : Machine
  plan : DispatchPlan
  invocation : Invocation
  deriving Repr

def chooseInvocation (leaves : DriverLeaves) (machine : Machine) : Decision :=
  match planDispatch machine with
  | .bytecode =>
      let outcome := leaves.dispatch.runBytecode machine
      { frame := machine
        plan := .bytecode
        invocation := .bytecode outcome.outcome outcome.directInlineStaticPrecompile }
  | .fullPrecompile =>
      { frame := machine
        plan := .fullPrecompile
        invocation := .fullPrecompile (leaves.dispatch.runFullPrecompile machine) }

def classifyReturnedHalt (machine : Machine) (result : FrameResult) : SettlementRoute :=
  match result.exit with
  | .success =>
      if machine.current.isTopLevel then .topLevelSuccess
      else if machine.current.executionType.isCreate then .childCreateSuccess
      else .childSuccess
  | .revert =>
      if machine.current.isTopLevel then .topLevelRevert else .childRevert
  | .exception _ =>
      if machine.current.isTopLevel then .topLevelException else .childException

def classifyBytecode (machine : Machine) (outcome : BytecodeInvocation) : SettlementRoute :=
  match outcome with
  | .returned step =>
      match step.result with
      | .continue _ => .continued
      | .suspend _ _ => .suspended
      | .halt result => classifyReturnedHalt machine result
  | .thrownEvm _ | .thrownOverflow =>
      if machine.current.isTopLevel then .topLevelException else .childException
  | .escaped _ => .escaped
  | .cancelled _ _ => .cancelled

def classifyPrecompile (machine : Machine) (outcome : PrecompileInvocation) : SettlementRoute :=
  match outcome with
  | .returned step =>
      match step.result with
      | .continue _ | .suspend _ _ => .invalidControl
      | .halt result =>
          match result.precompileSuccess with
          | some false =>
              if machine.current.isTopLevel then .fullPrecompileReturnedFailureTop
              else .fullPrecompileReturnedFailureNested
          | _ => classifyReturnedHalt machine result
  | .outOfGas =>
      if machine.current.isTopLevel then .fullPrecompileOutOfGasTop
      else .fullPrecompileOutOfGasNested
  | .returnedFailure _ =>
      if machine.current.isTopLevel then .fullPrecompileReturnedFailureTop
      else .fullPrecompileReturnedFailureNested
  | .managedException _ =>
      if machine.current.isTopLevel then .fullPrecompileManagedExceptionTop
      else .fullPrecompileManagedExceptionNested
  | .escaped _ => .escaped

def routeForDecision (decision : Decision) : SettlementRoute :=
  match decision.plan, decision.invocation with
  | .bytecode, .bytecode outcome _ => classifyBytecode decision.frame outcome
  | .fullPrecompile, .fullPrecompile outcome => classifyPrecompile decision.frame outcome
  | .bytecode, .fullPrecompile _ | .fullPrecompile, .bytecode _ _ => .invalidControl

inductive SettlementPlan where
  | continueStep
  | suspendStep
  | childReturn (route : SettlementRoute)
  | topLevelReturn (route : SettlementRoute)
  | fullPrecompileFailure (route : SettlementRoute)
  | cancelled
  | escaped
  | invalid
  deriving DecidableEq, Repr

def planSettlement (_machine : Machine) (route : SettlementRoute) : SettlementPlan :=
  match route with
  | .continued => .continueStep
  | .suspended => .suspendStep
  | .childSuccess | .childRevert | .childException
  | .childCreateSuccess | .childCreateInvalidCode | .childCreateOutOfGas =>
      .childReturn route
  | .topLevelSuccess | .topLevelRevert | .topLevelException =>
      .topLevelReturn route
  | .fullPrecompileOutOfGasNested | .fullPrecompileOutOfGasTop
  | .fullPrecompileReturnedFailureNested | .fullPrecompileReturnedFailureTop
  | .fullPrecompileManagedExceptionNested | .fullPrecompileManagedExceptionTop =>
      .fullPrecompileFailure route
  | .cancelled => .cancelled
  | .escaped => .escaped
  | .invalidControl => .invalid

def settleCreate (leaves : DriverLeaves) (invocation : Invocation) (machine : Machine) : DriverResult :=
  match leaves.classifyNestedCreateDeposit invocation machine with
  | .deposited =>
      tag .childCreateSuccess
        (leaves.settlement.childReturn .childCreateSuccess invocation machine)
  | .invalidCode =>
      tag .childCreateInvalidCode
        (leaves.settlement.childReturn .childCreateInvalidCode invocation machine)
  | .outOfGas =>
      tag .childCreateOutOfGas
        (leaves.settlement.childReturn .childCreateOutOfGas invocation machine)

def enactSettlement (leaves : DriverLeaves) (plan : SettlementPlan)
    (invocation : Invocation) (machine : Machine) : DriverResult :=
  match plan with
  | .continueStep => tag .continued (leaves.settlement.continueStep invocation machine)
  | .suspendStep => tag .suspended (leaves.settlement.suspendStep invocation machine)
  | .childReturn route =>
      if route = .childCreateSuccess then settleCreate leaves invocation machine
      else tag route (leaves.settlement.childReturn route invocation machine)
  | .topLevelReturn route =>
      tag route (leaves.settlement.topLevelReturn route invocation machine)
  | .fullPrecompileFailure route =>
      tag route (leaves.settlement.fullPrecompileFailure route invocation machine)
  | .cancelled => tag .cancelled (leaves.settlement.cancelled invocation machine)
  | .escaped => tag .escaped (leaves.settlement.escaped invocation machine)
  | .invalid => DriverResult.incomplete machine

def closeTerminal (leaves : DriverLeaves) (result : DriverResult) : DriverResult :=
  match result.termination with
  | .completed | .suspended => result
  | .cancelled | .escaped | .incomplete => leaves.settlement.cleanup result

def evaluateStepBody (leaves : DriverLeaves) (machine : Machine) : DriverResult :=
  let entered := enterFrame leaves machine
  let decision := chooseInvocation leaves entered
  let route := routeForDecision decision
  let settled := enactSettlement leaves (planSettlement decision.frame route)
    decision.invocation decision.frame
  let observed := { settled with
    directInlineStaticPrecompile := decision.invocation.directInline }
  closeTerminal leaves observed

def evaluateStep (control : SourceControl) (leaves : DriverLeaves) (machine : Machine) : DriverResult :=
  if sourceControlReady control && phaseReady machine then evaluateStepBody leaves machine
  else DriverResult.incomplete machine

def runOne (fuel : Nat) (control : SourceControl) (leaves : DriverLeaves) (machine : Machine) : FuelResult :=
  match fuel with
  | 0 => .exhausted machine
  | _ + 1 => .stepped (evaluateStep control leaves machine)

end Eip803x.Evm.FrameDriver.Reference
