-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 2.1.0
-- Roslyn compiler version: 5.6.0.0
-- Production closure manifest SHA-256: 327b3c117d8b8af688d060e8fee99e77741e641133cdeda7bdfb1590ad78fd89
-- Canonical pure-word-opcode IR SHA-256: 958e0023ec9872f87717a1bed8debae65fad5a6163713506eac2c0983e0f0979

import Lean.Elab.Tactic.Omega
import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Eip803x.Evm.Word

namespace Eip803x.Generated.PureWordOpcodeKernel

open Eip803x
open Eip803x.Evm
open Eip803x.Evm.Word
open Eip803x.Evm.MemoryStackControl

inductive Opcode where
  | add | mul | sub | div | sdiv | mod | smod | addmod | mulmod | exp | signextend | lt | gt | slt | sgt | eq | iszero | and | or | xor | not | byte | shl | shr | sar | clz
  deriving DecidableEq, Repr

def allOpcodes : List Opcode :=
  [ .add, .mul, .sub, .div, .sdiv, .mod, .smod, .addmod, .mulmod, .exp, .signextend, .lt, .gt, .slt, .sgt, .eq, .iszero, .and, .or, .xor, .not, .byte, .shl, .shr, .sar, .clz ]

def opcodeByte : Opcode → Nat
  | .add => 1
  | .mul => 2
  | .sub => 3
  | .div => 4
  | .sdiv => 5
  | .mod => 6
  | .smod => 7
  | .addmod => 8
  | .mulmod => 9
  | .exp => 10
  | .signextend => 11
  | .lt => 16
  | .gt => 17
  | .slt => 18
  | .sgt => 19
  | .eq => 20
  | .iszero => 21
  | .and => 22
  | .or => 23
  | .xor => 24
  | .not => 25
  | .byte => 26
  | .shl => 27
  | .shr => 28
  | .sar => 29
  | .clz => 30

def stackInputs : Opcode → Nat
  | .iszero | .not | .clz => 1
  | .add | .mul | .sub | .div | .sdiv | .mod | .smod | .exp | .signextend | .lt | .gt | .slt | .sgt | .eq | .and | .or | .xor | .byte | .shl | .shr | .sar => 2
  | .addmod | .mulmod => 3

def stackGrowth (_opcode : Opcode) : Int := 0

structure Schedule where
  veryLow : Nat
  low : Nat
  mid : Nat
  expBase : Nat
  expByte : Nat
  deriving DecidableEq, Repr

def amsterdamSchedule : Schedule :=
  { veryLow := 3
    low := 5
    mid := 8
    expBase := 10
    expByte := 50 }

def staticCost (schedule : Schedule) : Opcode → Nat
  | .add | .sub | .lt | .gt | .slt | .sgt | .eq | .iszero | .and | .or | .xor | .not | .byte | .shl | .shr | .sar => schedule.veryLow
  | .mul | .div | .sdiv | .mod | .smod | .signextend | .clz => schedule.low
  | .addmod | .mulmod => schedule.mid
  | .exp => schedule.expBase

def exponentByteLength (exponent : UInt256) : Nat :=
  if exponent.val = 0 then 0 else exponent.val.log2 / 8 + 1

def expDynamicCost (schedule : Schedule) (exponent : UInt256) : Nat :=
  schedule.expByte * exponentByteLength exponent

structure State where
  stack : Stack
  gas : GasState
  pc : Nat
  deriving Repr

namespace State

def withGasLeft (state : State) (gasLeft : Nat) : State :=
  { state with gas := { state.gas with gasLeft } }

def advancePc (state : State) : State :=
  { state with pc := state.pc + 1 }

end State

inductive Error where
  | outOfGas
  | stackUnderflow
  deriving DecidableEq, Repr

inductive Outcome where
  | success (state : State)
  | error (reason : Error) (state : State)
  deriving Repr

namespace Outcome

def state : Outcome → State
  | .success state => state
  | .error _ state => state

def error? : Outcome → Option Error
  | .success _ => none
  | .error reason _ => some reason

end Outcome

inductive Debit where
  | paid (state : State)
  | outOfGas (state : State)
  deriving Repr

def debitExecution (cost : Nat) (state : State) : Debit :=
  if cost ≤ state.gas.gasLeft then
    .paid (state.withGasLeft (state.gas.gasLeft - cost))
  else
    .outOfGas (state.withGasLeft 0)

def unary (operation : UInt256 → UInt256) (stack : Stack) : Option Stack :=
  match hWords : stack.words with
  | [] => none
  | value :: rest =>
    some ⟨operation value :: rest, by
      have hBound := stack.bounded
      rw [hWords] at hBound
      simpa only [List.length_cons] using hBound⟩

def binary (operation : UInt256 → UInt256 → UInt256) (stack : Stack) : Option Stack :=
  match hWords : stack.words with
  | top :: next :: rest =>
    some ⟨operation top next :: rest, by
      have hBound := stack.bounded
      rw [hWords] at hBound
      simp only [List.length_cons] at hBound ⊢
      omega⟩
  | _ => none

def ternary (operation : UInt256 → UInt256 → UInt256 → UInt256) (stack : Stack) : Option Stack :=
  match hWords : stack.words with
  | top :: next :: third :: rest =>
    some ⟨operation top next third :: rest, by
      have hBound := stack.bounded
      rw [hWords] at hBound
      simp only [List.length_cons] at hBound ⊢
      omega⟩
  | _ => none

def stackExecute : Opcode → Stack → Option Stack
  | .add => binary Word.add
  | .mul => binary Word.mul
  | .sub => binary Word.sub
  | .div => binary Word.udiv
  | .sdiv => binary Word.sdiv
  | .mod => binary Word.umod
  | .smod => binary Word.smod
  | .addmod => ternary Word.addmod
  | .mulmod => ternary Word.mulmod
  | .exp => binary Word.exp
  | .signextend => binary Word.signextend
  | .lt => binary Word.unsignedLt
  | .gt => binary Word.unsignedGt
  | .slt => binary Word.signedLt
  | .sgt => binary Word.signedGt
  | .eq => binary Word.equal
  | .iszero => unary Word.isZero
  | .and => binary Word.bitwiseAnd
  | .or => binary Word.bitwiseOr
  | .xor => binary Word.bitwiseXor
  | .not => unary Word.bitwiseNot
  | .byte => binary Word.byte
  | .shl => binary Word.shl
  | .shr => binary Word.shr
  | .sar => binary Word.sar
  | .clz => unary Word.clz

def executeExpAfterStatic (schedule : Schedule) (state : State) : Outcome :=
  match hWords : state.stack.words with
  | base :: exponent :: tail =>
    let hBound : (base :: exponent :: tail).length ≤ stackLimit := by
      have hBound := state.stack.bounded
      rw [hWords] at hBound
      exact hBound
    let popped : State :=
      { state with
        stack := ⟨tail, by
          simp only [List.length_cons] at hBound
          omega⟩ }
    match debitExecution (expDynamicCost schedule exponent) popped with
    | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
    | .paid afterDynamic =>
      .success
        { afterDynamic with
          stack := ⟨Word.exp base exponent :: tail, by
            simp only [List.length_cons] at hBound ⊢
            omega⟩ }
  | _ => .error .stackUnderflow state

def execute (schedule : Schedule) (opcode : Opcode) (state : State) : Outcome :=
  let dispatched := state.advancePc
  match debitExecution (staticCost schedule opcode) dispatched with
  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
  | .paid afterStatic =>
    match opcode with
    | .exp => executeExpAfterStatic schedule afterStatic
    | _ =>
      match stackExecute opcode afterStatic.stack with
      | none => .error .stackUnderflow afterStatic
      | some finalStack => .success { afterStatic with stack := finalStack }

end Eip803x.Generated.PureWordOpcodeKernel
