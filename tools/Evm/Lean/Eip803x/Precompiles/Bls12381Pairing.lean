-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381G1Add
import Eip803x.Precompiles.Bls12381G2Add

namespace Eip803x.Precompiles.Bls12381Pairing

open Evm.MemoryStackControl

/-!
  Candidate handwritten EIP-2537 pairing-check reference. It owns framing,
  pricing, validation before raw-infinity compaction, Boolean encoding, and
  metadata. Field, curve, subgroup, Miller, final-exponentiation, and backend
  behavior remain explicit oracles.
-/

abbrev G1Point := Bls12381G1Add.EncodedPoint
abbrev G2Point := Bls12381G2Add.EncodedPoint

def g1Length : Nat := 128
def g2Length : Nat := 256
def pairLength : Nat := 384
def outputLength : Nat := 32
def address : Nat := 15
def name : String := "BLS12_PAIRING_CHECK"
def supportsCaching : Bool := true

def baseGasCost : Nat := 37700
def pairGasCost : Nat := 32600

def pairCount (input : List Byte) : Nat := input.length / pairLength

def dataGasCost (input : List Byte) : Nat := pairGasCost * pairCount input

def totalGasCost (input : List Byte) : Nat := baseGasCost + dataGasCost input

def normalizeInput (input : List Byte) : List Byte := input

def validInputLength (length : Nat) : Bool :=
  length != 0 && length % pairLength == 0

def encodedBoolean (value : Bool) : List Byte :=
  List.replicate 31 zeroByte ++ [if value then byte 0x01 else zeroByte]

def g1Infinity : G1Point :=
  Bls12381G1Add.pointOfBytes (List.replicate g1Length zeroByte) (by native_decide)

def g2Infinity : G2Point :=
  Bls12381G2Add.pointOfBytes (List.replicate g2Length zeroByte) (by native_decide)

def g1IsInfinity (point : G1Point) : Bool := point.bytes.all (· == zeroByte)
def g2IsInfinity (point : G2Point) : Bool := point.bytes.all (· == zeroByte)

structure Pair where
  g1 : G1Point
  g2 : G2Point
  deriving DecidableEq, Repr

def pairHasInfinity (pair : Pair) : Bool := g1IsInfinity pair.g1 || g2IsInfinity pair.g2

def decodePairs : Nat → List Byte → Option (List Pair)
  | 0, input => if input.isEmpty then some [] else none
  | count + 1, input =>
      if hLength : pairLength ≤ input.length then
        let g1Bytes := input.take g1Length
        let g2Bytes := input.drop g1Length |>.take g2Length
        have hG1 : g1Bytes.length = Bls12381G1Add.pointLength := by
          simp only [g1Bytes, List.length_take]
          simp only [pairLength, g1Length, Bls12381G1Add.pointLength] at hLength ⊢
          omega
        have hG2 : g2Bytes.length = Bls12381G2Add.pointLength := by
          simp only [g2Bytes, List.length_take, List.length_drop]
          simp only [pairLength, g1Length, g2Length, Bls12381G2Add.pointLength] at hLength ⊢
          omega
        match decodePairs count (input.drop pairLength) with
        | none => none
        | some rest =>
            some ({ g1 := ⟨g1Bytes, hG1⟩, g2 := ⟨g2Bytes, hG2⟩ } :: rest)
      else none

def decodeInput? (input : List Byte) : Option (List Pair) :=
  if validInputLength input.length then decodePairs (pairCount input) input else none

inductive Error where
  | invalidInputLength
  | invalidPoint
  deriving DecidableEq, Repr

abbrev Result := Except Error (List Byte)
abbrev MillerValue := Nat

/--
  The cryptographic and backend observations are deliberately external. The
  candidate makes no equivalence claim between either backend and this model.
-/
structure PairingOracle where
  validG1Field : G1Point → Bool
  validG1Curve : G1Point → Bool
  validG1Subgroup : G1Point → Bool
  validG2Field : G2Point → Bool
  validG2Curve : G2Point → Bool
  validG2Subgroup : G2Point → Bool
  millerLoopProduct : List Pair → MillerValue
  finalExponentiationIsOne : MillerValue → Bool
  nativeStandard : List Byte → Result
  nativeZkEvm : List Byte → Result

def validPair (oracle : PairingOracle) (pair : Pair) : Bool :=
  oracle.validG1Field pair.g1 &&
    oracle.validG1Curve pair.g1 &&
    oracle.validG2Field pair.g2 &&
    oracle.validG2Curve pair.g2 &&
    oracle.validG1Subgroup pair.g1 &&
    oracle.validG2Subgroup pair.g2

/--
  Validation precedes compaction. The single invalid-point result deliberately
  avoids claiming an observable failure ordering for parallel native decode.
-/
def collectActivePairs (oracle : PairingOracle) : List Pair → Option (List Pair)
  | [] => some []
  | pair :: rest =>
      if !validPair oracle pair then none
      else if pairHasInfinity pair then collectActivePairs oracle rest
      else (collectActivePairs oracle rest).map (pair :: ·)

def runNativeStandard (oracle : PairingOracle) (input : List Byte) : Result :=
  oracle.nativeStandard input

def runNativeZkEvm (oracle : PairingOracle) (input : List Byte) : Result :=
  oracle.nativeZkEvm input

def runPairs (oracle : PairingOracle) (pairs : List Pair) : Result :=
  match collectActivePairs oracle pairs with
  | none => .error .invalidPoint
  | some [] => .ok (encodedBoolean true)
  | some active =>
      .ok (encodedBoolean (oracle.finalExponentiationIsOne (oracle.millerLoopProduct active)))

def run (oracle : PairingOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some pairs => runPairs oracle pairs

theorem metadata_contract :
    address = 15 ∧ name = "BLS12_PAIRING_CHECK" ∧ supportsCaching = true := by decide

theorem normalization_is_identity (input : List Byte) : normalizeInput input = input := rfl

theorem length_admission (length : Nat) :
    validInputLength length = true ↔ length ≠ 0 ∧ length % pairLength = 0 := by
  simp [validInputLength]

theorem sub_pair_pricing (length : Nat) (h : length < pairLength) :
    totalGasCost (List.replicate length zeroByte) = baseGasCost := by
  have hDivision : length / pairLength = 0 := Nat.div_eq_of_lt h
  simp [totalGasCost, dataGasCost, pairCount, hDivision]

theorem pricing_ignores_incomplete_tail (count tail : Nat) (hTail : tail < pairLength) :
    totalGasCost (List.replicate (pairLength * count + tail) zeroByte) =
      baseGasCost + pairGasCost * count := by
  have hCount : (pairLength * count + tail) / pairLength = count := by
    simp only [pairLength] at hTail ⊢
    omega
  simp [totalGasCost, dataGasCost, pairCount, hCount]

theorem invalid_length_run (oracle : PairingOracle) (input : List Byte)
    (h : validInputLength input.length = false) :
    run oracle input = .error .invalidInputLength := by
  simp [run, decodeInput?, h]

theorem decoded_pair_lengths (pair : Pair) :
    pair.g1.bytes.length = g1Length ∧ pair.g2.bytes.length = g2Length :=
  ⟨pair.g1.length_eq, pair.g2.length_eq⟩

theorem exact_length_decodes (count : Nat) (input : List Byte)
    (hLength : input.length = pairLength * count) :
    ∃ pairs, decodePairs count input = some pairs ∧ pairs.length = count := by
  induction count generalizing input with
  | zero =>
      cases input with
      | nil => exact ⟨[], rfl, rfl⟩
      | cons head tail => simp at hLength
  | succ count ih =>
      have hBound : pairLength ≤ input.length := by
        simp only [pairLength] at hLength ⊢
        omega
      have hDrop : (input.drop pairLength).length = pairLength * count := by
        simp only [List.length_drop, pairLength] at hLength ⊢
        omega
      obtain ⟨rest, hRest, hCount⟩ := ih (input.drop pairLength) hDrop
      simp only [decodePairs, dif_pos hBound, hRest]
      exact ⟨_, rfl, by simp [hCount]⟩

theorem valid_input_decodes_all_pairs (input : List Byte)
    (hValid : validInputLength input.length = true) :
    ∃ pairs, decodeInput? input = some pairs ∧ pairs.length = pairCount input := by
  have hMod := (length_admission input.length).mp hValid
  have hLength : input.length = pairLength * pairCount input := by
    simp only [pairCount, pairLength] at hMod ⊢
    omega
  obtain ⟨pairs, hDecode, hCount⟩ := exact_length_decodes (pairCount input) input hLength
  exact ⟨pairs, by simp [decodeInput?, hValid, hDecode], hCount⟩

theorem invalid_pair_rejected_before_compaction (oracle : PairingOracle) (pair : Pair)
    (rest : List Pair) (hInvalid : validPair oracle pair = false) :
    collectActivePairs oracle (pair :: rest) = none := by
  simp [collectActivePairs, hInvalid]

theorem valid_infinity_pair_is_compacted (oracle : PairingOracle) (pair : Pair)
    (rest : List Pair) (hValid : validPair oracle pair = true)
    (hInfinity : pairHasInfinity pair = true) :
    collectActivePairs oracle (pair :: rest) = collectActivePairs oracle rest := by
  simp [collectActivePairs, hValid, hInfinity]

theorem valid_finite_pair_is_retained (oracle : PairingOracle) (pair : Pair)
    (rest : List Pair) (hValid : validPair oracle pair = true)
    (hFinite : pairHasInfinity pair = false) :
    collectActivePairs oracle (pair :: rest) = (collectActivePairs oracle rest).map (pair :: ·) := by
  simp [collectActivePairs, hValid, hFinite]

theorem all_compacted_pairs_return_true (oracle : PairingOracle) (pairs : List Pair)
    (hEmpty : collectActivePairs oracle pairs = some []) :
    runPairs oracle pairs = .ok (encodedBoolean true) := by
  simp [runPairs, hEmpty]

theorem valid_active_pairs_reach_miller_and_final_exponentiation (oracle : PairingOracle)
    (pairs active : List Pair) (hActive : collectActivePairs oracle pairs = some active)
    (hNonempty : active ≠ []) :
    runPairs oracle pairs =
      .ok (encodedBoolean (oracle.finalExponentiationIsOne (oracle.millerLoopProduct active))) := by
  cases active with
  | nil => contradiction
  | cons first rest => simp [runPairs, hActive]

theorem true_output_is_big_endian_one :
    encodedBoolean true = List.replicate 31 zeroByte ++ [byte 0x01] := rfl

theorem false_output_is_all_zero :
    encodedBoolean false = List.replicate outputLength zeroByte := by native_decide

theorem output_length (value : Bool) : (encodedBoolean value).length = outputLength := by
  cases value <;> native_decide

theorem successful_output_length (oracle : PairingOracle) (input output : List Byte)
    (hRun : run oracle input = .ok output) : output.length = outputLength := by
  unfold run at hRun
  split at hRun
  · contradiction
  · unfold runPairs at hRun
    split at hRun
    · contradiction
    · cases hRun
      exact output_length true
    · cases hRun
      exact output_length _

theorem run_pairs_classification (oracle : PairingOracle) (pairs : List Pair) :
    runPairs oracle pairs = .error .invalidPoint ∨
      runPairs oracle pairs = .ok (encodedBoolean false) ∨
      runPairs oracle pairs = .ok (encodedBoolean true) := by
  unfold runPairs
  generalize hCollect : collectActivePairs oracle pairs = collected
  cases collected with
  | none => simp
  | some active =>
      cases active with
      | nil => simp
      | cons first rest =>
          cases hFinal : oracle.finalExponentiationIsOne (oracle.millerLoopProduct (first :: rest)) <;>
            simp [hFinal]

theorem run_classification (oracle : PairingOracle) (input : List Byte) :
    run oracle input = .error .invalidInputLength ∨
      run oracle input = .error .invalidPoint ∨
      run oracle input = .ok (encodedBoolean false) ∨
      run oracle input = .ok (encodedBoolean true) := by
  unfold run
  split
  · left; rfl
  · exact Or.inr (run_pairs_classification oracle _)

end Eip803x.Precompiles.Bls12381Pairing
