-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Elab.Tactic.Omega
import Eip803x.Evm.ExtendedStack
import Eip803x.Refinement.ExtendedStackDecoder
import ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel

/-!
Refinement of the exact source-derived Amsterdam EIP-8024 opcode projection.
The generated program is theorem-free and does not import the handwritten
transition. This proof module is the only place where the two are related.
-/

namespace ExtendedStackOpcodeExtractor.Refinement

open Eip803x
open Eip803x.Evm

namespace G
abbrev Opcode := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.Opcode
abbrev DecodedOperation :=
  ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.DecodedOperation
abbrev Error := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.Error
abbrev State := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State
abbrev Outcome := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.Outcome
abbrev descriptor := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.descriptor
abbrev allOpcodes := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.allOpcodes
abbrev specializations :=
  ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.specializations
abbrev immediateOrZero :=
  ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.immediateOrZero
abbrev decodeSingle? := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.decodeSingle?
abbrev decodePair? := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.decodePair?
abbrev decodeOperation :=
  ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.decodeOperation
abbrev applyDecoded := ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded
abbrev executeAtFork :=
  ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.executeAtFork
end G

namespace DecoderRefinement
abbrev singleOption := Eip803x.Refinement.ExtendedStackDecoder.singleOption
abbrev pairOption := Eip803x.Refinement.ExtendedStackDecoder.pairOption
end DecoderRefinement

theorem exact_opcode_count : G.allOpcodes.length = 3 := by native_decide

theorem exact_specialization_count : G.specializations.length = 12 := by native_decide

theorem exact_opcode_bytes : G.allOpcodes.map (fun opcode => (G.descriptor opcode).opcodeByte) =
    [0xe6, 0xe7, 0xe8] := by native_decide

theorem generated_readAt_matches_list_get {α : Type} (index : Nat) (values : List α) :
    ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.readAt? index values =
      values[index]? := by
  induction index generalizing values with
  | zero => cases values <;> rfl
  | succ index ih =>
    cases values with
    | nil => rfl
    | cons _ tail => exact ih tail

theorem generated_replaceAt_matches_handwritten {α : Type} (index : Nat) (value : α)
    (values : List α) :
    ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.replaceAt index value values =
      MemoryStackControl.Stack.replaceAt index value values := by
  induction index generalizing values with
  | zero => cases values <;> rfl
  | succ index ih =>
    cases values with
    | nil => rfl
    | cons _ tail =>
      simp only [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.replaceAt,
        MemoryStackControl.Stack.replaceAt]
      rw [ih]

theorem fromWords_of_length_le (words : List UInt256)
    (hBound : words.length ≤ MemoryStackControl.stackLimit) :
    MemoryStackControl.Stack.fromWords words = ⟨words, hBound⟩ := by
  simp [MemoryStackControl.Stack.fromWords, List.take_of_length_le hBound]

theorem stack_push_is_fromWords (stack : MemoryStackControl.Stack) (value : UInt256) :
    stack.push value =
      if stack.words.length < MemoryStackControl.stackLimit then
        some (MemoryStackControl.Stack.fromWords (value :: stack.words))
      else none := by
  rcases stack with ⟨words, hBound⟩
  by_cases hRoom : words.length < MemoryStackControl.stackLimit
  · have hResult : (value :: words).length ≤ MemoryStackControl.stackLimit := by
      simp only [List.length_cons]
      omega
    rw [fromWords_of_length_le (value :: words) hResult]
    simp [MemoryStackControl.Stack.push, hRoom]
  · simp [MemoryStackControl.Stack.push, hRoom]

def mapStackFault : MemoryStackControl.StackFault → G.Error
  | .underflow => .stackUnderflow
  | .overflow => .stackOverflow

def mapExtendedFault : ExtendedStack.Fault → G.Error
  | .invalidImmediate => .badInstruction
  | .underflow => .stackUnderflow
  | .overflow => .stackOverflow

def mapExtendedResult : Except ExtendedStack.Fault MemoryStackControl.Stack →
    Except G.Error MemoryStackControl.Stack
  | .ok stack => .ok stack
  | .error fault => .error (mapExtendedFault fault)

def handwrittenDecoded : G.DecodedOperation → MemoryStackControl.Stack →
    Except G.Error MemoryStackControl.Stack
  | .duplicate depth, stack =>
    match stack.duplicate depth with
    | .ok result => .ok result
    | .error fault => .error (mapStackFault fault)
  | .swap depth, stack =>
    match stack.swap depth with
    | some result => .ok result
    | none => .error .stackUnderflow
  | .exchange first second, stack =>
    mapExtendedResult (ExtendedStack.exchangeAt first second stack)

theorem generated_duplicate_stack_refines (depth : Nat) (stack : MemoryStackControl.Stack) :
    G.applyDecoded (.duplicate depth) stack = handwrittenDecoded (.duplicate depth) stack := by
  rcases stack with ⟨words, hBound⟩
  by_cases hDepth : 0 < depth ∧ depth ≤ words.length
  · cases hRead : words[depth - 1]? with
    | none =>
      simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
        ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
        ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.duplicateWords,
        handwrittenDecoded, MemoryStackControl.Stack.duplicate, hDepth,
        generated_readAt_matches_list_get, hRead, mapStackFault]
    | some value =>
      by_cases hRoom : words.length < MemoryStackControl.stackLimit
      · simp [MemoryStackControl.stackLimit] at hRoom
        simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.duplicateWords,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.stackLimit,
          handwrittenDecoded, MemoryStackControl.Stack.duplicate, hDepth,
          generated_readAt_matches_list_get, hRead, stack_push_is_fromWords, hRoom,
          MemoryStackControl.stackLimit]
      · simp [MemoryStackControl.stackLimit] at hRoom
        have hNoRoom : ¬words.length < 1024 := by omega
        simp only [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.duplicateWords,
          handwrittenDecoded, MemoryStackControl.Stack.duplicate]
        rw [if_pos hDepth, dif_pos hDepth, generated_readAt_matches_list_get, hRead]
        simp only
        rw [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.stackLimit,
          if_neg hNoRoom, stack_push_is_fromWords, MemoryStackControl.stackLimit,
          if_neg hNoRoom]
        rfl
  · simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.duplicateWords,
      handwrittenDecoded, MemoryStackControl.Stack.duplicate, hDepth, mapStackFault]

theorem generated_swap_stack_refines (depth : Nat) (stack : MemoryStackControl.Stack) :
    G.applyDecoded (.swap depth) stack = handwrittenDecoded (.swap depth) stack := by
  rcases stack with ⟨words, hBound⟩
  cases depth with
  | zero =>
    simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.swapWords,
      handwrittenDecoded, MemoryStackControl.Stack.swap]
  | succ index =>
    cases words with
    | nil =>
      simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
        ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
        ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.swapWords,
        handwrittenDecoded, MemoryStackControl.Stack.swap]
    | cons top tail =>
      cases hRead : tail[index]? with
      | none =>
        simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.swapWords,
          handwrittenDecoded, MemoryStackControl.Stack.swap,
          generated_readAt_matches_list_get, hRead]
      | some other =>
        cases hReplace : MemoryStackControl.Stack.replaceAt index top tail with
        | none =>
          simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.swapWords,
            handwrittenDecoded, MemoryStackControl.Stack.swap,
            generated_readAt_matches_list_get, generated_replaceAt_matches_handwritten,
            hRead, hReplace]
        | some changedTail =>
          simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.swapWords,
            handwrittenDecoded, MemoryStackControl.Stack.swap,
            generated_readAt_matches_list_get, generated_replaceAt_matches_handwritten,
            hRead, hReplace]

theorem generated_exchange_stack_refines (first second : Nat)
    (stack : MemoryStackControl.Stack) :
    G.applyDecoded (.exchange first second) stack =
      handwrittenDecoded (.exchange first second) stack := by
  rcases stack with ⟨words, hBound⟩
  cases hFirst : words[first]? with
  | none =>
    have hHandwritten : ExtendedStack.exchangeAt first second ⟨words, hBound⟩ =
        .error .underflow := by
      unfold ExtendedStack.exchangeAt
      rw [hFirst]
    simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.exchangeWords,
      handwrittenDecoded, mapExtendedResult, mapExtendedFault, hHandwritten,
      generated_readAt_matches_list_get, hFirst]
  | some firstValue =>
    cases hSecond : words[second]? with
    | none =>
      have hHandwritten : ExtendedStack.exchangeAt first second ⟨words, hBound⟩ =
          .error .underflow := by
        unfold ExtendedStack.exchangeAt
        rw [hFirst, hSecond]
      simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
        ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
        ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.exchangeWords,
        handwrittenDecoded, mapExtendedResult, mapExtendedFault, hHandwritten,
        generated_readAt_matches_list_get, hFirst, hSecond]
    | some secondValue =>
      cases hReplaceFirst : MemoryStackControl.Stack.replaceAt first secondValue words with
      | none =>
        have hHandwritten : ExtendedStack.exchangeAt first second ⟨words, hBound⟩ =
            .error .underflow := by
          grind [ExtendedStack.exchangeAt]
        simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
          ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.exchangeWords,
          handwrittenDecoded, mapExtendedResult, mapExtendedFault, hHandwritten,
          generated_readAt_matches_list_get, generated_replaceAt_matches_handwritten,
          hFirst, hSecond, hReplaceFirst]
      | some firstReplacement =>
        cases hReplaceSecond : MemoryStackControl.Stack.replaceAt second firstValue firstReplacement with
        | none =>
          have hHandwritten : ExtendedStack.exchangeAt first second ⟨words, hBound⟩ =
              .error .underflow := by
            grind [ExtendedStack.exchangeAt]
          simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.exchangeWords,
            handwrittenDecoded, mapExtendedResult, mapExtendedFault, hHandwritten,
            generated_readAt_matches_list_get, generated_replaceAt_matches_handwritten,
            hFirst, hSecond, hReplaceFirst, hReplaceSecond]
        | some result =>
          have hFirstLength : firstReplacement.length = words.length :=
            MemoryStackControl.Stack.replaceAt_length first secondValue words firstReplacement
              hReplaceFirst
          have hResultLength : result.length = firstReplacement.length :=
            MemoryStackControl.Stack.replaceAt_length second firstValue firstReplacement result
              hReplaceSecond
          have hResultBound : result.length ≤ MemoryStackControl.stackLimit := by
            rw [hResultLength, hFirstLength]
            exact hBound
          have hHandwritten : ExtendedStack.exchangeAt first second ⟨words, hBound⟩ =
              .ok (MemoryStackControl.Stack.fromWords result) := by
            grind [ExtendedStack.exchangeAt, fromWords_of_length_le]
          simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.applyDecoded,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.toStackResult,
            ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.exchangeWords,
            handwrittenDecoded, mapExtendedResult, hHandwritten,
            generated_readAt_matches_list_get, generated_replaceAt_matches_handwritten,
            hFirst, hSecond, hReplaceFirst, hReplaceSecond,
            fromWords_of_length_le result hResultBound]

theorem generated_decoded_stack_refines (operation : G.DecodedOperation)
    (stack : MemoryStackControl.Stack) :
    G.applyDecoded operation stack = handwrittenDecoded operation stack := by
  cases operation with
  | duplicate depth => exact generated_duplicate_stack_refines depth stack
  | swap depth => exact generated_swap_stack_refines depth stack
  | exchange first second => exact generated_exchange_stack_refines first second stack

theorem generated_single_option_refines (immediate : MemoryStackControl.Byte) :
    G.decodeSingle? immediate = ExtendedStack.decodeSingle immediate := by
  change DecoderRefinement.singleOption
      (Eip803x.Refinement.ExtendedStackDecoder.Generated.decodeSingle immediate.val) =
    ExtendedStack.decodeSingle immediate
  exact Eip803x.Refinement.ExtendedStackDecoder.generated_single_refines_handwritten immediate

theorem generated_pair_option_refines (immediate : MemoryStackControl.Byte) :
    G.decodePair? immediate = ExtendedStack.decodePair immediate := by
  change DecoderRefinement.pairOption
      (Eip803x.Refinement.ExtendedStackDecoder.Generated.decodePair immediate.val) =
    ExtendedStack.decodePair immediate
  exact Eip803x.Refinement.ExtendedStackDecoder.generated_pair_refines_handwritten immediate

def handwrittenDecode (opcode : G.Opcode) (immediate : MemoryStackControl.Byte) :
    Except G.Error G.DecodedOperation :=
  match opcode with
  | .dupN =>
    match ExtendedStack.decodeSingle immediate with
    | some depth => .ok (.duplicate depth)
    | none => .error .badInstruction
  | .swapN =>
    match ExtendedStack.decodeSingle immediate with
    | some depth => .ok (.swap depth)
    | none => .error .badInstruction
  | .exchange =>
    match ExtendedStack.decodePair immediate with
    | some pair => .ok (.exchange pair.1 pair.2)
    | none => .error .badInstruction

theorem generated_decode_refines (opcode : G.Opcode) (immediate : MemoryStackControl.Byte) :
    G.decodeOperation opcode immediate = handwrittenDecode opcode immediate := by
  cases opcode with
  | dupN =>
    change (match G.decodeSingle? immediate with
      | some depth => .ok (.duplicate depth)
      | none => .error .badInstruction) = handwrittenDecode .dupN immediate
    rw [show G.decodeSingle? immediate = ExtendedStack.decodeSingle immediate from
      generated_single_option_refines immediate]
    rfl
  | swapN =>
    change (match G.decodeSingle? immediate with
      | some depth => .ok (.swap depth)
      | none => .error .badInstruction) = handwrittenDecode .swapN immediate
    rw [show G.decodeSingle? immediate = ExtendedStack.decodeSingle immediate from
      generated_single_option_refines immediate]
    rfl
  | exchange =>
    change (match G.decodePair? immediate with
      | some pair => .ok (.exchange pair.1 pair.2)
      | none => .error .badInstruction) = handwrittenDecode .exchange immediate
    rw [show G.decodePair? immediate = ExtendedStack.decodePair immediate from
      generated_pair_option_refines immediate]
    rfl

def handwrittenExecuteAtFork (eip8024Enabled : Bool) (opcode : G.Opcode)
    (state : G.State) : G.Outcome :=
  let entered := state.advancePc
  if !eip8024Enabled then .error .badInstruction entered
  else if 3 ≤ entered.gas.gasLeft then
    let afterGas := entered.withGasLeft (entered.gas.gasLeft - 3)
    let immediate := G.immediateOrZero afterGas.code afterGas.pc
    match handwrittenDecode opcode immediate with
    | .error reason => .error reason afterGas
    | .ok operation =>
      let afterImmediate := afterGas.advancePc
      match handwrittenDecoded operation afterImmediate.stack with
      | .ok stack => .success { afterImmediate with stack }
      | .error reason => .error reason afterImmediate
  else .error .outOfGas (entered.withGasLeft 0)

theorem execute_refines_handwritten_extended_stack (eip8024Enabled : Bool)
    (opcode : G.Opcode) (state : G.State) :
    G.executeAtFork eip8024Enabled opcode state =
      handwrittenExecuteAtFork eip8024Enabled opcode state := by
  cases opcode <;> cases eip8024Enabled <;>
    by_cases hGas : 3 ≤ state.gas.gasLeft <;>
    simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.executeAtFork,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.executePlanned,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.plan,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.descriptor,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.pcOrder,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.gasOrder,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.faultOrder,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.debit,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.immediateOrZero,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State.advancePc,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State.withGasLeft,
      handwrittenExecuteAtFork, hGas, generated_decode_refines,
      generated_decoded_stack_refines] <;>
    split <;> simp_all <;>
    split <;> simp_all

theorem fork_disabled_advances_only_opcode_pc (opcode : G.Opcode) (state : G.State) :
    G.executeAtFork false opcode state = .error .badInstruction state.advancePc := by
  rw [execute_refines_handwritten_extended_stack]
  rfl

theorem out_of_gas_exhausts_after_opcode_pc (opcode : G.Opcode) (state : G.State)
    (hGas : state.gas.gasLeft < 3) :
    G.executeAtFork true opcode state = .error .outOfGas (state.advancePc |>.withGasLeft 0) := by
  rw [execute_refines_handwritten_extended_stack]
  have hNotEnoughGas : ¬3 ≤ state.gas.gasLeft := by omega
  simp [handwrittenExecuteAtFork,
    ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State.advancePc,
    hNotEnoughGas]

theorem generated_missing_immediate_is_zero (code : List MemoryStackControl.Byte) (pc : Nat)
    (hPastEnd : code.length ≤ pc) :
    G.immediateOrZero code pc = MemoryStackControl.zeroByte := by
  simp [ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.immediateOrZero,
    generated_readAt_matches_list_get, List.getElem?_eq_none hPastEnd]

theorem paid_invalid_single_does_not_consume_immediate (opcode : G.Opcode) (state : G.State)
    (hOpcode : opcode = .dupN ∨ opcode = .swapN)
    (hGas : 3 ≤ state.gas.gasLeft)
    (hInvalid : ExtendedStack.decodeSingle (G.immediateOrZero state.code (state.pc + 1)) = none) :
    (G.executeAtFork true opcode state).state.pc = state.pc + 1 ∧
      (G.executeAtFork true opcode state).state.gas.gasLeft = state.gas.gasLeft - 3 ∧
      (G.executeAtFork true opcode state).state.stack.words = state.stack.words := by
  rcases hOpcode with rfl | rfl <;>
    simp [execute_refines_handwritten_extended_stack, handwrittenExecuteAtFork,
      handwrittenDecode, hGas, hInvalid,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.Outcome.state,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State.advancePc,
      ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State.withGasLeft]

theorem paid_invalid_exchange_does_not_consume_immediate (state : G.State)
    (hGas : 3 ≤ state.gas.gasLeft)
    (hInvalid : ExtendedStack.decodePair (G.immediateOrZero state.code (state.pc + 1)) = none) :
    (G.executeAtFork true .exchange state).state.pc = state.pc + 1 ∧
      (G.executeAtFork true .exchange state).state.gas.gasLeft = state.gas.gasLeft - 3 ∧
      (G.executeAtFork true .exchange state).state.stack.words = state.stack.words := by
  simp [execute_refines_handwritten_extended_stack, handwrittenExecuteAtFork,
    handwrittenDecode, hGas, hInvalid,
    ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.Outcome.state,
    ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State.advancePc,
    ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel.State.withGasLeft]

end ExtendedStackOpcodeExtractor.Refinement
