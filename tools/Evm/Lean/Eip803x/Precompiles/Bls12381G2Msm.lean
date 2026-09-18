-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381G2Add

namespace Eip803x.Precompiles.Bls12381G2Msm

open Evm.MemoryStackControl

/-!
  Candidate handwritten EIP-2537 G2 MSM reference. It owns framing, pricing,
  raw-infinity compaction, validation ordering, result shape, and metadata.
  Fp/Fp2/curve/subgroup and group-operation behavior remain explicit oracles.
-/

abbrev EncodedPoint := Bls12381G2Add.EncodedPoint

def pointLength : Nat := 256
def scalarLength : Nat := 32
def itemLength : Nat := 288
def address : Nat := 14
def name : String := "BLS12_G2MSM"
def supportsCaching : Bool := true
def normalizeInput (input : List Byte) : List Byte := input

/-- Index zero is the production zero-pair sentinel, not an admitted MSM. -/
def discounts : List Nat :=
  [0, 1000, 1000, 923, 884, 855, 832, 812, 796, 782, 770, 759, 749, 740,
    732, 724, 717, 711, 704, 699, 693, 688, 683, 679, 674, 670, 666, 663,
    659, 655, 652, 649, 646, 643, 640, 637, 634, 632, 629, 627, 624, 622,
    620, 618, 615, 613, 611, 609, 607, 606, 604, 602, 600, 598, 597, 595,
    593, 592, 590, 589, 587, 586, 584, 583, 582, 580, 579, 578, 576, 575,
    574, 573, 571, 570, 569, 568, 567, 566, 565, 563, 562, 561, 560, 559,
    558, 557, 556, 555, 554, 553, 552, 552, 551, 550, 549, 548, 547, 546,
    545, 545, 544, 543, 542, 541, 541, 540, 539, 538, 537, 537, 536, 535,
    535, 534, 533, 532, 532, 531, 530, 530, 529, 528, 528, 527, 526, 526,
    525, 524, 524]

def discount (count : Nat) : Nat :=
  if 128 ≤ count then 524 else discounts[count]?.getD 0

structure Schedule where
  multiplicationGas : Nat
  deriving DecidableEq, Repr

def Schedule.amsterdam : Schedule := { multiplicationGas := 22500 }
def baseGasCost : Nat := 0

def gasForLength (schedule : Schedule) (length : Nat) : Nat :=
  let count := length / itemLength
  schedule.multiplicationGas * count * discount count / 1000

def dataGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  gasForLength schedule input.length

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost + dataGasCost schedule input

def validInputLength (length : Nat) : Bool :=
  length != 0 && length % itemLength == 0

structure Scalar where
  bytes : List Byte
  length_eq : bytes.length = scalarLength
  deriving DecidableEq, Repr

structure Pair where
  point : EncodedPoint
  scalar : Scalar
  deriving DecidableEq, Repr

def scalarValue (scalar : Scalar) : Nat :=
  scalar.bytes.foldl (fun accumulator value => accumulator * 256 + value.val) 0

def nativeScalarBytes (scalar : Scalar) : List Byte := scalar.bytes.reverse
def scalarIsZero (scalar : Scalar) : Bool := scalar.bytes.all (· == zeroByte)
def isInfinity (point : EncodedPoint) : Bool := point.bytes.all (· == zeroByte)

def infinity : EncodedPoint :=
  Bls12381G2Add.pointOfBytes (List.replicate pointLength zeroByte) (by native_decide)

def decodePairs : Nat → List Byte → Option (List Pair)
  | 0, input => if input.isEmpty then some [] else none
  | count + 1, input =>
      if hLength : itemLength ≤ input.length then
        let pointBytes := input.take pointLength
        let scalarBytes := input.drop pointLength |>.take scalarLength
        have hPoint : pointBytes.length = Bls12381G2Add.pointLength := by
          simp only [pointBytes, List.length_take]
          simp only [itemLength, pointLength, Bls12381G2Add.pointLength] at hLength ⊢
          omega
        have hScalar : scalarBytes.length = scalarLength := by
          simp only [scalarBytes, List.length_take, List.length_drop]
          simp only [itemLength, pointLength, scalarLength] at hLength ⊢
          omega
        match decodePairs count (input.drop itemLength) with
        | none => none
        | some rest =>
            let pair : Pair :=
              { point := ⟨pointBytes, hPoint⟩
                scalar := ⟨scalarBytes, hScalar⟩ }
            some (pair :: rest)
      else none

def decodeInput? (input : List Byte) : Option (List Pair) :=
  if validInputLength input.length then decodePairs (input.length / itemLength) input
  else none

inductive Error where
  | invalidInputLength
  | invalidPoint
  deriving DecidableEq, Repr

abbrev Result := Except Error EncodedPoint

/--
  Cryptographic and backend observations intentionally stay external. In
  particular, this candidate makes no equivalence claim between the standard
  native and zkEVM backends.
-/
structure MsmOracle where
  validFp : EncodedPoint → Bool
  validFp2 : EncodedPoint → Bool
  onCurve : EncodedPoint → Bool
  inSubgroup : EncodedPoint → Bool
  multiply : EncodedPoint → Scalar → EncodedPoint
  multiScalarMultiply : List Pair → EncodedPoint
  nativeStandard : List Byte → Result
  nativeZkEvm : List Byte → Result

def validPoint (oracle : MsmOracle) (point : EncodedPoint) : Bool :=
  oracle.validFp point && oracle.validFp2 point && oracle.onCurve point

def runNativeStandard (oracle : MsmOracle) (input : List Byte) : Result :=
  oracle.nativeStandard input

def runNativeZkEvm (oracle : MsmOracle) (input : List Byte) : Result :=
  oracle.nativeZkEvm input

def runSingle (oracle : MsmOracle) (pair : Pair) : Result :=
  if !validPoint oracle pair.point then .error .invalidPoint
  else if !oracle.inSubgroup pair.point then .error .invalidPoint
  else if scalarIsZero pair.scalar || isInfinity pair.point then .ok infinity
  else .ok (oracle.multiply pair.point pair.scalar)

/-- Raw infinity entries are omitted, but every finite entry is validated. -/
def collectFinite (oracle : MsmOracle) : List Pair → Option (List Pair)
  | [] => some []
  | pair :: rest =>
      if isInfinity pair.point then collectFinite oracle rest
      else if !validPoint oracle pair.point then none
      else if !oracle.inSubgroup pair.point then none
      else (collectFinite oracle rest).map (pair :: ·)

def runMultiple (oracle : MsmOracle) (pairs : List Pair) : Result :=
  match collectFinite oracle pairs with
  | none => .error .invalidPoint
  | some [] => .ok infinity
  | some (first :: rest) => .ok (oracle.multiScalarMultiply (first :: rest))

def runPairs (oracle : MsmOracle) : List Pair → Result
  | [] => .error .invalidInputLength
  | [pair] => runSingle oracle pair
  | first :: second :: rest => runMultiple oracle (first :: second :: rest)

def run (oracle : MsmOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some pairs => runPairs oracle pairs

theorem metadata_contract :
    address = 14 ∧ name = "BLS12_G2MSM" ∧ supportsCaching = true := by decide

theorem normalization_is_identity (input : List Byte) : normalizeInput input = input := rfl

theorem discount_table_length : discounts.length = 129 := by native_decide

theorem capped_discount (count : Nat) (h : 128 ≤ count) : discount count = 524 := by
  simp [discount, h]

theorem sub_item_pricing (schedule : Schedule) (length : Nat) (h : length < itemLength) :
    gasForLength schedule length = 0 := by
  simp [gasForLength, Nat.div_eq_of_lt h]

theorem pricing_ignores_incomplete_tail (schedule : Schedule) (count tail : Nat)
    (hTail : tail < itemLength) :
    gasForLength schedule (itemLength * count + tail) =
      schedule.multiplicationGas * count * discount count / 1000 := by
  have hCount : (itemLength * count + tail) / itemLength = count := by
    simp only [itemLength] at hTail ⊢
    omega
  simp [gasForLength, hCount]

theorem length_admission (length : Nat) :
    validInputLength length = true ↔ length ≠ 0 ∧ length % itemLength = 0 := by
  simp [validInputLength]

theorem invalid_length_run (oracle : MsmOracle) (input : List Byte)
    (h : validInputLength input.length = false) :
    run oracle input = .error .invalidInputLength := by
  simp [run, decodeInput?, h]

theorem decoded_pair_lengths (pair : Pair) :
    pair.point.bytes.length = pointLength ∧ pair.scalar.bytes.length = scalarLength :=
  ⟨pair.point.length_eq, pair.scalar.length_eq⟩

theorem exact_length_decodes (count : Nat) (input : List Byte)
    (hLength : input.length = itemLength * count) :
    ∃ pairs, decodePairs count input = some pairs ∧ pairs.length = count := by
  induction count generalizing input with
  | zero =>
      cases input with
      | nil => exact ⟨[], rfl, rfl⟩
      | cons head tail => simp at hLength
  | succ count ih =>
      have hBound : itemLength ≤ input.length := by
        simp only [itemLength] at hLength ⊢
        omega
      have hDrop : (input.drop itemLength).length = itemLength * count := by
        simp only [List.length_drop, itemLength] at hLength ⊢
        omega
      obtain ⟨rest, hRest, hCount⟩ := ih (input.drop itemLength) hDrop
      simp only [decodePairs, dif_pos hBound, hRest]
      exact ⟨_, rfl, by simp [hCount]⟩

theorem valid_input_decodes_all_pairs (input : List Byte)
    (hValid : validInputLength input.length = true) :
    ∃ pairs, decodeInput? input = some pairs ∧ pairs.length = input.length / itemLength := by
  have hMod := (length_admission input.length).mp hValid
  have hLength : input.length = itemLength * (input.length / itemLength) := by
    simp only [itemLength] at hMod ⊢
    omega
  obtain ⟨pairs, hDecode, hCount⟩ := exact_length_decodes (input.length / itemLength) input hLength
  exact ⟨pairs, by simp [decodeInput?, hValid, hDecode], hCount⟩

theorem native_scalar_reversal_preserves_bytes (scalar : Scalar) :
    (nativeScalarBytes scalar).reverse = scalar.bytes := by
  simp [nativeScalarBytes]

theorem invalid_point_precedes_zero_scalar (oracle : MsmOracle) (pair : Pair)
    (hInvalid : validPoint oracle pair.point = false) :
    runSingle oracle pair = .error .invalidPoint := by
  simp [runSingle, hInvalid]

theorem subgroup_check_precedes_zero_scalar (oracle : MsmOracle) (pair : Pair)
    (hValid : validPoint oracle pair.point = true)
    (hSubgroup : oracle.inSubgroup pair.point = false) :
    runSingle oracle pair = .error .invalidPoint := by
  simp [runSingle, hValid, hSubgroup]

theorem valid_zero_scalar_returns_infinity (oracle : MsmOracle) (pair : Pair)
    (hValid : validPoint oracle pair.point = true)
    (hSubgroup : oracle.inSubgroup pair.point = true)
    (hZero : scalarIsZero pair.scalar = true) :
    runSingle oracle pair = .ok infinity := by
  simp [runSingle, hValid, hSubgroup, hZero]

theorem valid_infinity_returns_infinity (oracle : MsmOracle) (pair : Pair)
    (hValid : validPoint oracle pair.point = true)
    (hSubgroup : oracle.inSubgroup pair.point = true)
    (hInfinity : isInfinity pair.point = true) :
    runSingle oracle pair = .ok infinity := by
  simp [runSingle, hValid, hSubgroup, hInfinity]

theorem finite_nonzero_scalar_reaches_oracle (oracle : MsmOracle) (pair : Pair)
    (hValid : validPoint oracle pair.point = true)
    (hSubgroup : oracle.inSubgroup pair.point = true)
    (hZero : scalarIsZero pair.scalar = false)
    (hFinite : isInfinity pair.point = false) :
    runSingle oracle pair = .ok (oracle.multiply pair.point pair.scalar) := by
  simp [runSingle, hValid, hSubgroup, hZero, hFinite]

theorem collect_infinity_skips_entry (oracle : MsmOracle) (pair : Pair) (rest : List Pair)
    (hInfinity : isInfinity pair.point = true) :
    collectFinite oracle (pair :: rest) = collectFinite oracle rest := by
  simp [collectFinite, hInfinity]

theorem collect_invalid_finite_fails (oracle : MsmOracle) (pair : Pair) (rest : List Pair)
    (hFinite : isInfinity pair.point = false)
    (hInvalid : validPoint oracle pair.point = false) :
    collectFinite oracle (pair :: rest) = none := by
  simp [collectFinite, hFinite, hInvalid]

theorem collect_preserves_valid_finite (oracle : MsmOracle) (pair : Pair) (rest : List Pair)
    (hFinite : isInfinity pair.point = false)
    (hValid : validPoint oracle pair.point = true)
    (hSubgroup : oracle.inSubgroup pair.point = true) :
    collectFinite oracle (pair :: rest) = (collectFinite oracle rest).map (pair :: ·) := by
  simp [collectFinite, hFinite, hValid, hSubgroup]

theorem no_finite_points_returns_infinity (oracle : MsmOracle) (pairs : List Pair)
    (hEmpty : collectFinite oracle pairs = some []) :
    runMultiple oracle pairs = .ok infinity := by
  simp [runMultiple, hEmpty]

theorem invalid_multiple_fails_before_msm (oracle : MsmOracle) (pairs : List Pair)
    (hInvalid : collectFinite oracle pairs = none) :
    runMultiple oracle pairs = .error .invalidPoint := by
  simp [runMultiple, hInvalid]

theorem successful_output_length (oracle : MsmOracle) (input : List Byte)
    (output : EncodedPoint) (_hRun : run oracle input = .ok output) :
    output.bytes.length = pointLength := output.length_eq

end Eip803x.Precompiles.Bls12381G2Msm
