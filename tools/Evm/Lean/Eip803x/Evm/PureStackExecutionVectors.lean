-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.PureStackExecution

/-!
  Executable vectors for the accepted handwritten pure-word execution reference.

  These make the remaining production-refinement boundaries concrete:
  PC-first dispatch, fixed-charge-before-underflow, `EXP`'s post-pop dynamic
  charge, and OOG execution-gas clearing.
-/

namespace Eip803x
namespace Evm
namespace PureStackExecutionVectors

open Word
open PureStack
open PureStackExecution

def w (value : Nat) : UInt256 := Word.ofNat value

def s (value : Int) : UInt256 := Word.fromInt value

def vectorGas (gasLeft : Nat) : GasState :=
  { gasLeft
    stateReservoir := 17
    stateFromGasLeft := 19
    stateUsed := 23
    refundCounter := -29 }

def vectorState (words : List UInt256) (gasLeft : Nat) (pc : Nat := 41) : PureStackExecution.State :=
  { stack := MemoryStackControl.Stack.fromWords words
    gas := vectorGas gasLeft
    pc }

def run (opcode : PureStack.Opcode) (words : List UInt256) (gasLeft : Nat) : Outcome :=
  execute Schedule.amsterdam opcode (vectorState words gasLeft)

def outputWords (outcome : Outcome) : List UInt256 := outcome.state.stack.words

def outputGas (outcome : Outcome) : Nat := outcome.state.gas.gasLeft

def outputPc (outcome : Outcome) : Nat := outcome.state.pc

def preservesStateGas (outcome : Outcome) : Bool :=
  decide (outcome.state.gas.stateReservoir = 17 ∧
    outcome.state.gas.stateFromGasLeft = 19 ∧
    outcome.state.gas.stateUsed = 23 ∧
    outcome.state.gas.refundCounter = -29)

structure SuccessVector where
  name : String
  opcode : PureStack.Opcode
  input : List UInt256
  expected : List UInt256
  expectedGas : Nat
  deriving Repr

def successVectors : List SuccessVector :=
  [ { name := "add"
      opcode := .add
      input := [w 2, w 3]
      expected := [w 5]
      expectedGas := 997 }
  , { name := "mul"
      opcode := .mul
      input := [w 7, w 6]
      expected := [w 42]
      expectedGas := 995 }
  , { name := "sub-wrap"
      opcode := .sub
      input := [w 2, w 7]
      expected := [Word.sub (w 2) (w 7)]
      expectedGas := 997 }
  , { name := "div"
      opcode := .div
      input := [w 7, w 3]
      expected := [w 2]
      expectedGas := 995 }
  , { name := "sdiv"
      opcode := .sdiv
      input := [s (-7), w 3]
      expected := [s (-2)]
      expectedGas := 995 }
  , { name := "mod"
      opcode := .mod
      input := [w 7, w 3]
      expected := [w 1]
      expectedGas := 995 }
  , { name := "smod"
      opcode := .smod
      input := [s (-7), w 3]
      expected := [s (-1)]
      expectedGas := 995 }
  , { name := "addmod"
      opcode := .addmod
      input := [w 2, w 3, w 4]
      expected := [w 1]
      expectedGas := 992 }
  , { name := "mulmod"
      opcode := .mulmod
      input := [w 3, w 4, w 5]
      expected := [w 2]
      expectedGas := 992 }
  , { name := "exp"
      opcode := .exp
      input := [w 2, w 10]
      expected := [w 1024]
      expectedGas := 940 }
  , { name := "signextend"
      opcode := .signextend
      input := [w 0, w 128]
      expected := [s (-128)]
      expectedGas := 995 }
  , { name := "lt"
      opcode := .lt
      input := [w 2, w 7]
      expected := [w 1]
      expectedGas := 997 }
  , { name := "gt"
      opcode := .gt
      input := [w 7, w 2]
      expected := [w 1]
      expectedGas := 997 }
  , { name := "slt"
      opcode := .slt
      input := [s (-1), w 1]
      expected := [w 1]
      expectedGas := 997 }
  , { name := "sgt"
      opcode := .sgt
      input := [w 1, s (-1)]
      expected := [w 1]
      expectedGas := 997 }
  , { name := "eq"
      opcode := .eq
      input := [w 3, w 3]
      expected := [w 1]
      expectedGas := 997 }
  , { name := "iszero"
      opcode := .iszero
      input := [Word.zero]
      expected := [Word.one]
      expectedGas := 997 }
  , { name := "and"
      opcode := .and
      input := [w 240, w 15]
      expected := [Word.zero]
      expectedGas := 997 }
  , { name := "or"
      opcode := .or
      input := [w 240, w 15]
      expected := [w 255]
      expectedGas := 997 }
  , { name := "xor"
      opcode := .xor
      input := [w 240, w 15]
      expected := [w 255]
      expectedGas := 997 }
  , { name := "not"
      opcode := .not
      input := [Word.zero]
      expected := [Word.allOnes]
      expectedGas := 997 }
  , { name := "byte"
      opcode := .byte
      input := [w 31, w 171]
      expected := [w 171]
      expectedGas := 997 }
  , { name := "shl"
      opcode := .shl
      input := [w 1, w 1]
      expected := [w 2]
      expectedGas := 997 }
  , { name := "shr"
      opcode := .shr
      input := [w 1, w 4]
      expected := [w 2]
      expectedGas := 997 }
  , { name := "sar"
      opcode := .sar
      input := [w 1, s (-4)]
      expected := [s (-2)]
      expectedGas := 997 }
  , { name := "clz"
      opcode := .clz
      input := [Word.one]
      expected := [w 255]
      expectedGas := 995 } ]

def successVectorPasses (vector : SuccessVector) : Bool :=
  let outcome := run vector.opcode vector.input 1000
  decide (outcome.error? = none ∧
    outputWords outcome = vector.expected ∧
    outputGas outcome = vector.expectedGas ∧
    outputPc outcome = 42 ∧
    preservesStateGas outcome = true)

theorem success_vector_count : successVectors.length = 26 := by native_decide

theorem all_success_vectors_pass : successVectors.all successVectorPasses = true := by native_decide

theorem static_oog_clears_execution_gas :
    let outcome := run .add [w 2, w 3] 2
    outcome.error? = some .outOfGas ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [w 2, w 3] ∧
    outputPc outcome = 42 ∧
    preservesStateGas outcome = true := by native_decide

theorem static_exact_boundary_succeeds :
    let outcome := run .add [w 2, w 3] 3
    outcome.error? = none ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [w 5] ∧
    outputPc outcome = 42 := by native_decide

theorem binary_underflow_follows_fixed_charge :
    let outcome := run .mul [w 7] 5
    outcome.error? = some .stackUnderflow ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [w 7] ∧
    outputPc outcome = 42 ∧
    preservesStateGas outcome = true := by native_decide

theorem ternary_underflow_follows_fixed_charge :
    let outcome := run .addmod [w 2, w 3] 8
    outcome.error? = some .stackUnderflow ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [w 2, w 3] ∧
    outputPc outcome = 42 := by native_decide

theorem exp_underflow_follows_base_charge :
    let outcome := run .exp [w 2] 10
    outcome.error? = some .stackUnderflow ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [w 2] ∧
    outputPc outcome = 42 := by native_decide

theorem exp_zero_exponent_has_no_byte_charge :
    let outcome := run .exp [w 2, Word.zero] 10
    outcome.error? = none ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [Word.one] ∧
    outputPc outcome = 42 := by native_decide

theorem exp_dynamic_one_short_pops_before_oog :
    let outcome := run .exp [w 2, Word.one, w 7] 59
    outcome.error? = some .outOfGas ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [w 7] ∧
    outputPc outcome = 42 ∧
    preservesStateGas outcome = true := by native_decide

theorem exp_dynamic_exact_boundary_succeeds :
    let outcome := run .exp [w 2, Word.one, w 7] 60
    outcome.error? = none ∧
    outputGas outcome = 0 ∧
    outputWords outcome = [w 2, w 7] ∧
    outputPc outcome = 42 := by native_decide

theorem exp_byte_length_boundaries :
    exponentByteLength Word.zero = 0 ∧
    exponentByteLength (w 1) = 1 ∧
    exponentByteLength (w 255) = 1 ∧
    exponentByteLength (w 256) = 2 ∧
    exponentByteLength (w 65535) = 2 ∧
    exponentByteLength (w 65536) = 3 ∧
    exponentByteLength Word.minSignedWord = 32 ∧
    expDynamicCost Schedule.amsterdam (w 256) = 100 ∧
    expDynamicCost Schedule.amsterdam Word.minSignedWord = 1600 := by native_decide

theorem signed_and_zero_divisor_edges :
    outputWords (run .div [w 7, Word.zero] 5) = [Word.zero] ∧
    outputWords (run .mod [w 7, Word.zero] 5) = [Word.zero] ∧
    outputWords (run .sdiv [Word.minSignedWord, s (-1)] 5) = [Word.minSignedWord] ∧
    outputWords (run .sdiv [s (-7), Word.zero] 5) = [Word.zero] ∧
    outputWords (run .smod [s (-7), Word.zero] 5) = [Word.zero] ∧
    outputWords (run .smod [s (-7), w 3] 5) = [s (-1)] := by native_decide

theorem shift_edges :
    outputWords (run .shl [w 256, Word.one] 3) = [Word.zero] ∧
    outputWords (run .shr [w 256, Word.one] 3) = [Word.zero] ∧
    outputWords (run .sar [w 256, s (-1)] 3) = [Word.allOnes] ∧
    outputWords (run .sar [w 256, Word.one] 3) = [Word.zero] := by native_decide

def fullStackState (opcodeGas : Nat) : PureStackExecution.State :=
  { stack := MemoryStackControl.Stack.full
    gas := vectorGas opcodeGas
    pc := 41 }

theorem full_stack_unary_stays_bounded :
    let outcome := execute Schedule.amsterdam .not (fullStackState 3)
    outcome.error? = none ∧
    outcome.state.stack.words.length = MemoryStackControl.stackLimit ∧
    outputGas outcome = 0 ∧
    outputPc outcome = 42 := by native_decide

theorem full_stack_binary_and_ternary_reduce_depth :
    let binaryOutcome := execute Schedule.amsterdam .add (fullStackState 3)
    let ternaryOutcome := execute Schedule.amsterdam .addmod (fullStackState 8)
    binaryOutcome.error? = none ∧
    ternaryOutcome.error? = none ∧
    binaryOutcome.state.stack.words.length = MemoryStackControl.stackLimit - 1 ∧
    ternaryOutcome.state.stack.words.length = MemoryStackControl.stackLimit - 2 := by native_decide

def mutatedClzSchedule : Schedule :=
  { Schedule.amsterdam with low := 6 }

def mutatedExpByteSchedule : Schedule :=
  { Schedule.amsterdam with expByte := 10 }

theorem mutation_clz_low_cost_is_killed :
    (execute Schedule.amsterdam .clz (vectorState [Word.zero] 5)).error? = none ∧
    (execute mutatedClzSchedule .clz (vectorState [Word.zero] 5)).error? = some .outOfGas := by native_decide

theorem mutation_exp_byte_price_is_killed :
    let actual := execute Schedule.amsterdam .exp (vectorState [w 2, Word.one] 20)
    let mutated := execute mutatedExpByteSchedule .exp (vectorState [w 2, Word.one] 20)
    actual.error? = some .outOfGas ∧
    outputWords actual = [] ∧
    mutated.error? = none ∧
    outputWords mutated = [w 2] ∧
    outputGas mutated = 0 := by native_decide

theorem mutation_oog_gas_preservation_is_killed :
    let state := vectorState [w 2, w 3] 2
    (debitExecution 3 state).state.gas.gasLeft = 0 ∧
    (mutatedPreserveGasOnOutOfGas 3 state).state.gas.gasLeft = 2 := by native_decide

theorem mutation_exp_charge_before_pop_is_killed :
    let state := vectorState [w 2, Word.one, w 7] 49
    let actual := executeExpAfterStatic Schedule.amsterdam state
    let mutated := mutatedExpChargeBeforePop Schedule.amsterdam state
    actual.error? = some .outOfGas ∧
    outputWords actual = [w 7] ∧
    mutated.error? = some .outOfGas ∧
    outputWords mutated = [w 2, Word.one, w 7] := by native_decide

end PureStackExecutionVectors
end Evm
end Eip803x
