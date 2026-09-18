-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.ExtendedStackDecoderKernel
import Eip803x.Refinement.ExtendedStackDecoder

namespace Eip803x.ExtendedStackDecoderVector

open Eip803x
open Eip803x.Evm
open Eip803x.Refinement.ExtendedStackDecoder

namespace Generated

abbrev SingleDecode := Eip803x.Generated.ExtendedStackDecoderKernel.SingleDecode
abbrev PairDecode := Eip803x.Generated.ExtendedStackDecoderKernel.PairDecode
abbrev decodeSingle := Eip803x.Generated.ExtendedStackDecoderKernel.decodeSingle
abbrev decodePair := Eip803x.Generated.ExtendedStackDecoderKernel.decodePair

end Generated

def b (value : Nat) : Byte := MemoryStackControl.byte value

theorem generated_decoder_boundaries :
    Generated.decodeSingle 0 = { isValid := true, depth := 145 } ∧
      Generated.decodeSingle 90 = { isValid := true, depth := 235 } ∧
      Generated.decodeSingle 91 = { isValid := false, depth := 236 } ∧
      Generated.decodeSingle 127 = { isValid := false, depth := 16 } ∧
      Generated.decodeSingle 128 = { isValid := true, depth := 17 } ∧
      Generated.decodeSingle 255 = { isValid := true, depth := 144 } ∧
      Generated.decodePair 0 = { isValid := true, firstPosition := 10, secondPosition := 17 } ∧
      Generated.decodePair 81 = { isValid := true, firstPosition := 15, secondPosition := 16 } ∧
      Generated.decodePair 82 = { isValid := false, firstPosition := 15, secondPosition := 17 } ∧
      Generated.decodePair 127 = { isValid := false, firstPosition := 2, secondPosition := 15 } ∧
      Generated.decodePair 128 = { isValid := true, firstPosition := 2, secondPosition := 17 } ∧
      Generated.decodePair 255 = { isValid := true, firstPosition := 2, secondPosition := 23 } := by
  native_decide

/-- These literals kill the index-shift, XOR, forbidden-range, and one-based-position mutations. -/
theorem generated_decoder_mutation_boundaries :
    Generated.decodeSingle 0 ≠ { isValid := true, depth := 0 } ∧
      Generated.decodeSingle 128 ≠ { isValid := true, depth := 16 } ∧
      Generated.decodeSingle 91 ≠ { isValid := true, depth := 236 } ∧
      Generated.decodePair 0 ≠ { isValid := true, firstPosition := 9, secondPosition := 16 } ∧
      Generated.decodePair 128 ≠ { isValid := true, firstPosition := 1, secondPosition := 16 } ∧
      Generated.decodePair 82 ≠ { isValid := true, firstPosition := 15, secondPosition := 17 } := by
  native_decide

theorem adapter_read_decode_and_pc_boundaries :
    productionSingleAdapter [b 128] 0 =
      { isValid := true, depth := 17, nextProgramCounter := 1 } ∧
      productionSingleAdapter [b 91] 0 =
        { isValid := false, depth := 236, nextProgramCounter := 0 } ∧
      productionSingleAdapter [] 0 =
        { isValid := true, depth := 145, nextProgramCounter := 1 } ∧
      productionPairAdapter [b 128] 0 =
        { isValid := true, firstPosition := 2, secondPosition := 17, nextProgramCounter := 1 } ∧
      productionPairAdapter [b 82] 0 =
        { isValid := false, firstPosition := 15, secondPosition := 17, nextProgramCounter := 0 } ∧
      productionPairAdapter [] 0 =
        { isValid := true, firstPosition := 10, secondPosition := 17, nextProgramCounter := 1 } := by
  native_decide

theorem adapter_refinement_vectors :
    productionSingleSuccess [b 128] 0 = handwrittenSingleSuccess [b 128] 0 ∧
      productionSingleSuccess [b 91] 0 = handwrittenSingleSuccess [b 91] 0 ∧
      productionSingleSuccess [] 0 = handwrittenSingleSuccess [] 0 ∧
      productionPairSuccess [b 128] 0 = handwrittenPairSuccess [b 128] 0 ∧
      productionPairSuccess [b 82] 0 = handwrittenPairSuccess [b 82] 0 ∧
      productionPairSuccess [] 0 = handwrittenPairSuccess [] 0 := by
  native_decide

end Eip803x.ExtendedStackDecoderVector
