-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.ExtendedStack
import Eip803x.Generated.ExtendedStackDecoderKernel

namespace Eip803x.Refinement.ExtendedStackDecoder

open Eip803x
open Eip803x.Evm

namespace Generated

abbrev SingleDecode := Eip803x.Generated.ExtendedStackDecoderKernel.SingleDecode
abbrev PairDecode := Eip803x.Generated.ExtendedStackDecoderKernel.PairDecode
abbrev decodeSingle := Eip803x.Generated.ExtendedStackDecoderKernel.decodeSingle
abbrev decodePair := Eip803x.Generated.ExtendedStackDecoderKernel.decodePair

end Generated

abbrev Byte := MemoryStackControl.Byte

/-- Erases the production decoder's explicit validity bit to EIP-8024's option result. -/
def singleOption (result : Generated.SingleDecode) : Option Nat :=
  if result.isValid then some result.depth else none

/--
Erases the production `EvmStack.Exchange` one-based position convention to the handwritten
EIP-8024 pair convention. The `- 1` conversion is part of the refinement boundary.
-/
def pairOption (result : Generated.PairDecode) : Option (Nat × Nat) :=
  if result.isValid then some (result.firstPosition - 1, result.secondPosition - 1) else none

theorem generated_single_refines_handwritten :
    ∀ immediate : Byte,
      singleOption (Generated.decodeSingle immediate.val) = ExtendedStack.decodeSingle immediate := by
  native_decide

theorem generated_pair_refines_handwritten :
    ∀ immediate : Byte,
      pairOption (Generated.decodePair immediate.val) = ExtendedStack.decodePair immediate := by
  native_decide

/-- The production adapter represents `codeLength` by the actual code length at this boundary. -/
def readImmediateOrZero (code : List Byte) (programCounter : Nat) : Byte :=
  MemoryStackControl.readByte code programCounter

theorem readImmediateOrZero_past_end (code : List Byte) (programCounter : Nat)
    (hPastEnd : code.length ≤ programCounter) :
    readImmediateOrZero code programCounter = MemoryStackControl.zeroByte :=
  MemoryStackControl.readByte_zero_extended code programCounter hPastEnd

structure SingleAdapterResult where
  isValid : Bool
  depth : Nat
  nextProgramCounter : Nat
  deriving DecidableEq, Repr

structure PairAdapterResult where
  isValid : Bool
  firstPosition : Nat
  secondPosition : Nat
  nextProgramCounter : Nat
  deriving DecidableEq, Repr

/-- Pure projection of production `TryDecodeSingle` after the adapter reads its immediate. -/
def productionSingleAdapter (code : List Byte) (programCounter : Nat) : SingleAdapterResult :=
  let decoded := Generated.decodeSingle (readImmediateOrZero code programCounter).val
  { isValid := decoded.isValid
    depth := decoded.depth
    nextProgramCounter := if decoded.isValid then programCounter + 1 else programCounter }

/-- Pure projection of production `TryDecodePair` after the adapter reads its immediate. -/
def productionPairAdapter (code : List Byte) (programCounter : Nat) : PairAdapterResult :=
  let decoded := Generated.decodePair (readImmediateOrZero code programCounter).val
  { isValid := decoded.isValid
    firstPosition := decoded.firstPosition
    secondPosition := decoded.secondPosition
    nextProgramCounter := if decoded.isValid then programCounter + 1 else programCounter }

structure SingleSuccess where
  depth : Nat
  nextProgramCounter : Nat
  deriving DecidableEq, Repr

structure PairSuccess where
  firstPosition : Nat
  secondPosition : Nat
  nextProgramCounter : Nat
  deriving DecidableEq, Repr

def generatedPairPositions (result : Generated.PairDecode) : Option (Nat × Nat) :=
  if result.isValid then some (result.firstPosition, result.secondPosition) else none

theorem generated_pair_positions_refine_handwritten :
    ∀ immediate : Byte,
      generatedPairPositions (Generated.decodePair immediate.val) =
        (ExtendedStack.decodePair immediate).map fun (first, second) => (first + 1, second + 1) := by
  native_decide

def productionSingleSuccess (code : List Byte) (programCounter : Nat) : Option SingleSuccess :=
  (singleOption (Generated.decodeSingle (readImmediateOrZero code programCounter).val)).map fun depth =>
    { depth, nextProgramCounter := programCounter + 1 }

def handwrittenSingleSuccess (code : List Byte) (programCounter : Nat) : Option SingleSuccess :=
  (ExtendedStack.decodeSingle (readImmediateOrZero code programCounter)).map fun depth =>
    { depth, nextProgramCounter := programCounter + 1 }

def productionPairSuccess (code : List Byte) (programCounter : Nat) : Option PairSuccess :=
  (generatedPairPositions (Generated.decodePair (readImmediateOrZero code programCounter).val)).map
    fun (firstPosition, secondPosition) =>
      { firstPosition, secondPosition, nextProgramCounter := programCounter + 1 }

def handwrittenPairSuccess (code : List Byte) (programCounter : Nat) : Option PairSuccess :=
  (ExtendedStack.decodePair (readImmediateOrZero code programCounter)).map fun (first, second) =>
    { firstPosition := first + 1, secondPosition := second + 1, nextProgramCounter := programCounter + 1 }

theorem production_single_success_projects_adapter (code : List Byte) (programCounter : Nat) :
    productionSingleSuccess code programCounter =
      if (productionSingleAdapter code programCounter).isValid then
        some
          { depth := (productionSingleAdapter code programCounter).depth
            nextProgramCounter := (productionSingleAdapter code programCounter).nextProgramCounter }
      else
        none := by
  by_cases hValid : (Generated.decodeSingle (readImmediateOrZero code programCounter).val).isValid = true
  · simp [productionSingleSuccess, productionSingleAdapter, singleOption, hValid]
  · simp [productionSingleSuccess, productionSingleAdapter, singleOption, hValid]

theorem production_pair_success_projects_adapter (code : List Byte) (programCounter : Nat) :
    productionPairSuccess code programCounter =
      if (productionPairAdapter code programCounter).isValid then
        some
          { firstPosition := (productionPairAdapter code programCounter).firstPosition
            secondPosition := (productionPairAdapter code programCounter).secondPosition
            nextProgramCounter := (productionPairAdapter code programCounter).nextProgramCounter }
      else
        none := by
  by_cases hValid : (Generated.decodePair (readImmediateOrZero code programCounter).val).isValid = true
  · simp [productionPairSuccess, productionPairAdapter, generatedPairPositions, hValid]
  · simp [productionPairSuccess, productionPairAdapter, generatedPairPositions, hValid]

theorem production_single_success_refines_handwritten (code : List Byte) (programCounter : Nat) :
    productionSingleSuccess code programCounter = handwrittenSingleSuccess code programCounter := by
  unfold productionSingleSuccess handwrittenSingleSuccess
  rw [generated_single_refines_handwritten]

theorem production_pair_success_refines_handwritten (code : List Byte) (programCounter : Nat) :
    productionPairSuccess code programCounter = handwrittenPairSuccess code programCounter := by
  unfold productionPairSuccess handwrittenPairSuccess
  rw [generated_pair_positions_refine_handwritten]
  simp only [Option.map_map]
  congr 1

theorem production_single_invalid_does_not_advance (code : List Byte) (programCounter : Nat)
    (hInvalid : (productionSingleAdapter code programCounter).isValid = false) :
    (productionSingleAdapter code programCounter).nextProgramCounter = programCounter := by
  have hDecoded : (Generated.decodeSingle (readImmediateOrZero code programCounter).val).isValid = false := by
    simpa [productionSingleAdapter] using hInvalid
  simp [productionSingleAdapter, hDecoded]

theorem production_pair_invalid_does_not_advance (code : List Byte) (programCounter : Nat)
    (hInvalid : (productionPairAdapter code programCounter).isValid = false) :
    (productionPairAdapter code programCounter).nextProgramCounter = programCounter := by
  have hDecoded : (Generated.decodePair (readImmediateOrZero code programCounter).val).isValid = false := by
    simpa [productionPairAdapter] using hInvalid
  simp [productionPairAdapter, hDecoded]

theorem production_single_valid_advances (code : List Byte) (programCounter : Nat)
    (hValid : (productionSingleAdapter code programCounter).isValid = true) :
    (productionSingleAdapter code programCounter).nextProgramCounter = programCounter + 1 := by
  have hDecoded : (Generated.decodeSingle (readImmediateOrZero code programCounter).val).isValid = true := by
    simpa [productionSingleAdapter] using hValid
  simp [productionSingleAdapter, hDecoded]

theorem production_pair_valid_advances (code : List Byte) (programCounter : Nat)
    (hValid : (productionPairAdapter code programCounter).isValid = true) :
    (productionPairAdapter code programCounter).nextProgramCounter = programCounter + 1 := by
  have hDecoded : (Generated.decodePair (readImmediateOrZero code programCounter).val).isValid = true := by
    simpa [productionPairAdapter] using hValid
  simp [productionPairAdapter, hDecoded]

end Eip803x.Refinement.ExtendedStackDecoder
