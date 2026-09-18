-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameDriverExtractor.Specification.OperationalTypes

/-!
Independent source-shaped reference for the Stage F finite-fuel driver.

This module does not import the generated kernel or any generated route table.
Its dispatch, preparation, classification, settlement, and cleanup functions
are separate definitions.  The production relation in `Refinement` supplies
the source-bound route-table and source-derived frame/admitted-state
obligations that connect this reference to the generated executable.  Per-op
`adapterSuppliedRouteEvidence` is an adapter premise for route/PC trace shape;
this reference does not infer CFG successors or PUSH widths from it.
-/

namespace Eip803x.Evm.FrameDriver.Operational.Reference

open Eip803x.Evm.FrameMachineState
open Eip803x.Evm.FrameDriver.Operational

def prepareReference (semantics : OperationalSemantics) (machine : Machine) : Option Machine :=
  match machine.current.phase with
  | .fresh =>
      match semantics.clearReturnData machine with
      | none => none
      | some cleared => semantics.prepareFresh cleared
  | .continuation => semantics.prepareContinuation machine
  | .running => none

def dispatchReference (semantics : OperationalSemantics) (machine : Machine) :
    Option BytecodeExecution :=
  match machine.current.dispatchTable with
  | .noTrace => semantics.runNoTrace machine
  | .noTraceCancelable => semantics.runNoTraceCancelable machine
  | .traced => semantics.runTraced machine
  | .tracedCancelable => semantics.runTracedCancelable machine

def invokeReference (semantics : OperationalSemantics) (machine : Machine) :
    Option Invocation :=
  match machine.current.kind with
  | .bytecode =>
      match dispatchReference semantics machine with
      | some execution => some (.bytecode execution)
      | none => none
  | .precompile _ =>
      match semantics.runFullPrecompile machine with
      | some execution => some (.fullPrecompile execution)
      | none => none

def classifyReference (step : MachineStep) : DriverStep :=
  match step.result with
  | .continue frame => .continue { step.machine with current := frame }
  | .suspend parent child => .suspend step.machine parent child
  | .halt result => .halt step.machine result

def failureReference (semantics : OperationalSemantics) (machine : Machine)
    (kind : ExceptionKind) : DriverStep :=
  match semantics.failureResult kind machine with
  | some result =>
      if FrameResultControlValid result then .halt machine result
      else cleanupFailure semantics machine
  | none => cleanupFailure semantics machine

def precompileFailureReference (semantics : OperationalSemantics) (machine : Machine)
    (outcome : PrecompileRunOutcome) : DriverStep :=
  match semantics.precompileFailureResult outcome machine with
  | some result =>
      if FrameResultControlValid result then .halt machine result
      else cleanupFailure semantics machine
  | none => cleanupFailure semantics machine

def bytecodeReference (semantics : OperationalSemantics) (machine : Machine)
    (execution : BytecodeExecution) : DriverStep :=
  if bytecodeExecutionValid semantics machine execution then
    match execution.outcome with
    | .returned step => classifyReference step
    | .thrownEvm failureMachine kind => failureReference semantics failureMachine kind
    | .thrownOverflow failureMachine => failureReference semantics failureMachine .other
    | .escaped escapedMachine reason => .escaped escapedMachine reason
    | .cancelled cancelledMachine _ reason => .cancelled cancelledMachine reason
  else
    .incomplete machine (.unresolvedAdapter "RunDispatchLoop/batch-or-cancellation-boundary")

def precompileReference (semantics : OperationalSemantics) (machine : Machine)
    (execution : PrecompileExecution) : DriverStep :=
  if fullPrecompileExecutionValid semantics machine execution then
    match execution.outcome with
    | .returned step =>
        match step.result with
        | .halt result => .halt step.machine result
        | .continue _ | .suspend _ _ => .incomplete machine .invalidControlRoute
    | .outOfGas failureMachine | .returnedFailure failureMachine _ |
      .managedException failureMachine _ =>
        precompileFailureReference semantics failureMachine execution.outcome
    | .escaped escapedMachine reason => .escaped escapedMachine reason
  else
    .incomplete machine (.unresolvedAdapter "ExecutePrecompile/full-frame-domain")

def stepReference (semantics : OperationalSemantics) (machine : Machine) : DriverStep :=
  match machine.current.phase with
  | .running => .incomplete machine (.unresolvedAdapter
      "ExecuteTransaction/fresh-or-continuation-preparation")
  | .fresh | .continuation =>
      match prepareReference semantics machine with
      | none =>
          .incomplete machine (.unresolvedAdapter
            "ExecuteTransaction/fresh-or-continuation-preparation")
      | some prepared =>
          match prepared.current.kind with
          | .bytecode =>
              match dispatchReference semantics prepared with
              | some execution => bytecodeReference semantics prepared execution
              | none =>
                  .incomplete prepared (.unresolvedAdapter "RunDispatchLoop/dispatch-table-mode")
          | .precompile _ =>
              match semantics.runFullPrecompile prepared with
              | some execution => precompileReference semantics prepared execution
              | none => .incomplete prepared (.unresolvedAdapter "ExecutePrecompile")

def settleReferenceSettlement (semantics : OperationalSemantics) (machine : Machine) :
    Settlement → DriverStep
  | .complete result =>
      if FrameResultControlValid result then
        match machine.parents with
        | [] =>
            if result.frame.isTopLevel then
              match semantics.prepareTopLevelSubstate machine result with
              | some substate =>
                  if transactionSubstateFieldsValid result substate then
                    match semantics.settleTopLevel machine result substate with
                    | some settled => .topLevelHalt machine settled substate
                    | none => cleanupFailure semantics machine
                  else cleanupFailure semantics machine
              | none => cleanupFailure semantics machine
            else .incomplete machine .invalidControlRoute
        | _ => .incomplete machine .invalidControlRoute
      else
        cleanupFailure semantics machine
  | .resume resumed => settleResume machine resumed
  | .invalidControl _ => .incomplete machine .invalidControlRoute

def settleReferenceHalt (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) : DriverStep :=
  if FrameResultControlValid result then
    match machine.parents with
    | [] =>
        if result.frame.isTopLevel then
          match semantics.prepareTopLevelSubstate machine result with
          | some substate =>
              if transactionSubstateFieldsValid result substate then
                match semantics.settleTopLevel machine result substate with
                | some settled => .topLevelHalt machine settled substate
                | none => cleanupFailure semantics machine
              else cleanupFailure semantics machine
          | none => cleanupFailure semantics machine
        else .incomplete machine .invalidControlRoute
    | _ =>
        if machine.current.executionType.isCreate then
          if result.exit = .success then
            match semantics.createDeposit machine result with
            | some execution =>
                if createDepositOutcomeValid execution then
                  settleReferenceSettlement semantics machine execution.settlement
                else
                  cleanupFailure semantics machine
            | none => cleanupFailure semantics machine
          else
            match semantics.settleChild machine result with
            | some settlement => settleReferenceSettlement semantics machine settlement
            | none => cleanupFailure semantics machine
        else
          match semantics.settleChild machine result with
          | some settlement => settleReferenceSettlement semantics machine settlement
          | none => cleanupFailure semantics machine
  else
    cleanupFailure semantics machine

def settleStep (semantics : OperationalSemantics) : DriverStep → DriverStep
  | .halt machine result => settleReferenceHalt semantics machine result
  | other => other

def cleanupCompletedReference (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) : OperationalRunResult :=
  match semantics.cleanup machine with
  | some _ => .completed result
  | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") machine

def cleanupCompletedTopLevelReference (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) (substate : TransactionSubstate) : OperationalRunResult :=
  match semantics.cleanup machine with
  | some _ => .completedTopLevel result substate
  | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") machine

def runFuel : Nat → OperationalSemantics → Machine → OperationalRunResult
  | 0, _, machine => .incomplete .fuelExhausted machine
  | fuel + 1, semantics, machine =>
      match settleStep semantics (stepReference semantics machine) with
      | .continue next => runFuel fuel semantics next
      | .suspend beforePush parent child =>
          if childEntryValid child then
            runFuel fuel semantics (pushChild beforePush parent child)
          else .incomplete .invalidControlRoute beforePush
      | .halt current result => cleanupCompletedReference semantics current result
      | .topLevelHalt current result substate =>
          cleanupCompletedTopLevelReference semantics current result substate
      | .cancelled current _ =>
          match semantics.cleanup current with
          | some cleaned => .incomplete .invalidControlRoute cleaned
          | none => .incomplete (.unresolvedAdapter
              "FrameCleanupScope.Dispose/DisposeActiveFrames") current
      | .escaped current _ =>
          match semantics.cleanup current with
          | some cleaned => .incomplete .invalidControlRoute cleaned
          | none => .incomplete (.unresolvedAdapter
              "FrameCleanupScope.Dispose/DisposeActiveFrames") current
      | .incomplete current reason => .incomplete reason current

end Eip803x.Evm.FrameDriver.Operational.Reference
