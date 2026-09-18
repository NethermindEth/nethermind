-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Specification.FrameMachineState

namespace Eip803x.PrecompileFullFrame

open Evm.FrameMachineState

inductive LeafResult where
  | success (output : List Byte)
  | returnedFailure (error : Option String)
  | managedException
  | missingNativeDependency
  deriving DecidableEq, Repr

abbrev LeafOracle := Nat → List Byte → LeafResult

structure Input where
  machine : Machine
  codeSource : Option Nat
  executingAccount : Nat
  precompileName : String
  baseCost : Nat
  dataCost : Nat
  wasCreated : Bool
  transferValueZero : Bool
  ripemdDead : Bool

def actionAddress (input : Input) : Nat := input.codeSource.getD input.executingAccount

structure FrontOperations where
  traceActionStart : Nat → Machine → Machine
  addTransferLog : Machine → Machine
  touchAccount : Nat → Machine → Machine
  traceActionEnd : Machine → FrameResult → Machine
  prepareTopLevelSubstate : Machine → FrameResult → FrameResult

inductive RawKind where
  | pricingOverflow
  | pricingOutOfGas
  | returnedFailure
  | managedTopFailure
  | managedNestedFailure
  | success
  | outerEvmException
  | outerOverflow
  deriving DecidableEq, Repr

structure RawResult where
  kind : RawKind
  machine : Machine
  result : FrameResult
  installedGas : Bool
  invokedOracle : Bool
  events : List String

inductive Entry where
  | fullFrame (input : Input)
  | outerEvmException (atThrow : Machine) (kind : ExceptionKind)
  | outerOverflow (atThrow : Machine)

def Input.Admitted (input : Input) : Prop :=
  input.machine.current.kind = .precompile (actionAddress input) ∧
  input.machine.current.phase = .fresh ∧
  input.machine.current.isTopLevel = input.machine.parents.isEmpty ∧
  input.machine.current.executionType.isCreate = false ∧
  (match input.machine.parents with
    | [] => True
    | suspended :: _ => suspended.childBaseline.executionType.isCreate = false) ∧
  input.machine.current.gas.gas.gasLeft < 2 ^ 64 ∧
  input.baseCost < 2 ^ 64 ∧ input.dataCost < 2 ^ 64

def AdmittedPrecompileBoundary (machine : Machine) : Prop :=
  (∃ address, machine.current.kind = .precompile address) ∧
  machine.current.isTopLevel = machine.parents.isEmpty ∧
  machine.current.executionType.isCreate = false ∧
  (match machine.parents with
    | [] => True
    | suspended :: _ => suspended.childBaseline.executionType.isCreate = false)

def Entry.Admitted : Entry → Prop
  | .fullFrame input => input.Admitted
  | .outerEvmException machine _ => AdmittedPrecompileBoundary machine
  | .outerOverflow machine => AdmittedPrecompileBoundary machine

def OracleAgrees (implementation specification : LeafOracle) : Prop :=
  ∀ address input, implementation address input = specification address input

/- Callback contracts are premises about the concrete adapters. No whole-frame
   execution equality is accepted as a premise. The frame driver remains open. -/
def FrontAgrees (left right : FrontOperations) : Prop :=
  left.traceActionStart = right.traceActionStart ∧
  left.addTransferLog = right.addTransferLog ∧
  left.touchAccount = right.touchAccount ∧
  left.traceActionEnd = right.traceActionEnd ∧
  left.prepareTopLevelSubstate = right.prepareTopLevelSubstate

end Eip803x.PrecompileFullFrame
