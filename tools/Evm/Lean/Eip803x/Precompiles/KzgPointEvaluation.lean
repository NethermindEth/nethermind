-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace KzgPointEvaluation

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the EIP-4844 point-evaluation
  precompile leaf. Input framing, commitment-hash comparison, proof-call
  ordering, result shape, pricing, and metadata are modeled here. SHA-256,
  BLS12-381 decoding, and KZG proof verification are an explicit oracle.
-/

structure Schedule where
  fixedGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { fixedGas := 50_000 }

end Schedule

def address : Nat := 10

def name : String := "KZG_POINT_EVALUATION"

def supportsCaching : Bool := true

def inputLength : Nat := 192

def versionedHashLength : Nat := 32

def fieldElementLength : Nat := 32

def commitmentLength : Nat := 48

def proofLength : Nat := 48

def outputLength : Nat := 64

def fieldElementsPerBlob : Nat := 4096

def blsModulus : Nat :=
  52435875175126190479447740508185965837690552500527637822603658699938581184513

def bytesToNat (bytes : List Byte) : Nat :=
  bytes.foldl (fun accumulator value => accumulator * 256 + value.val) 0

def baseGasCost (schedule : Schedule) : Nat := schedule.fixedGas

def dataGasCost (_ : List Byte) : Nat := 0

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost input

def normalizeInput (input : List Byte) : List Byte := input

structure DecodedInput where
  versionedHash : List Byte
  z : List Byte
  y : List Byte
  commitment : List Byte
  proof : List Byte
  deriving DecidableEq, Repr

def decode (input : List Byte) : DecodedInput :=
  { versionedHash := input.take 32
    z := input.drop 32 |>.take 32
    y := input.drop 64 |>.take 32
    commitment := input.drop 96 |>.take 48
    proof := input.drop 144 |>.take 48 }

structure Output where
  bytes : List Byte
  length_eq : bytes.length = outputLength
  deriving DecidableEq, Repr

def outputOfBytes (bytes : List Byte) (length_eq : bytes.length = outputLength) : Output :=
  { bytes, length_eq }

def successOutput : Output :=
  outputOfBytes
    [byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x10, byte 0x00,
      byte 0x73, byte 0xed, byte 0xa7, byte 0x53, byte 0x29, byte 0x9d, byte 0x7d, byte 0x48,
      byte 0x33, byte 0x39, byte 0xd8, byte 0x08, byte 0x09, byte 0xa1, byte 0xd8, byte 0x05,
      byte 0x53, byte 0xbd, byte 0xa4, byte 0x02, byte 0xff, byte 0xfe, byte 0x5b, byte 0xfe,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0x00, byte 0x00, byte 0x00, byte 0x01]
    (by native_decide)

structure KzgOracle where
  tryCommitmentHashV1 : List Byte -> Option (List Byte)
  verifyProof : List Byte -> List Byte -> List Byte -> List Byte -> Bool

inductive Error where
  | invalidInputLength
  | verificationFailed
  deriving DecidableEq, Repr

abbrev Result := Except Error Output

def runDecoded (oracle : KzgOracle) (input : DecodedInput) : Result :=
  match oracle.tryCommitmentHashV1 input.commitment with
  | none => .error .verificationFailed
  | some commitmentHash =>
      if commitmentHash == input.versionedHash then
        if oracle.verifyProof input.commitment input.z input.y input.proof then
          .ok successOutput
        else
          .error .verificationFailed
      else
        .error .verificationFailed

def run (oracle : KzgOracle) (input : List Byte) : Result :=
  if input.length = inputLength then
    runDecoded oracle (decode input)
  else
    .error .invalidInputLength

theorem metadata_contract :
    address = 10 ∧ name = "KZG_POINT_EVALUATION" ∧ supportsCaching = true := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 50_000 := by
  rfl

theorem normalization_is_identity (input : List Byte) :
    normalizeInput input = input := by
  rfl

theorem success_output_length : successOutput.bytes.length = outputLength :=
  successOutput.length_eq

theorem success_output_values :
    bytesToNat (successOutput.bytes.take 32) = fieldElementsPerBlob ∧
      bytesToNat (successOutput.bytes.drop 32) = blsModulus := by
  native_decide

theorem decoded_field_lengths (input : List Byte) (hLength : input.length = inputLength) :
    (decode input).versionedHash.length = versionedHashLength ∧
      (decode input).z.length = fieldElementLength ∧
      (decode input).y.length = fieldElementLength ∧
      (decode input).commitment.length = commitmentLength ∧
      (decode input).proof.length = proofLength := by
  simp only [decode, List.length_take, List.length_drop]
  simp only [inputLength, versionedHashLength, fieldElementLength, commitmentLength,
    proofLength] at hLength ⊢
  omega

theorem invalid_length_run (oracle : KzgOracle) (input : List Byte)
    (hLength : input.length ≠ inputLength) :
    run oracle input = .error .invalidInputLength := by
  simp [run, hLength]

theorem commitment_hash_failure (oracle : KzgOracle) (input : DecodedInput)
    (hHash : oracle.tryCommitmentHashV1 input.commitment = none) :
    runDecoded oracle input = .error .verificationFailed := by
  simp [runDecoded, hHash]

theorem commitment_hash_mismatch (oracle : KzgOracle) (input : DecodedInput)
    (commitmentHash : List Byte)
    (hHash : oracle.tryCommitmentHashV1 input.commitment = some commitmentHash)
    (hMismatch : commitmentHash ≠ input.versionedHash) :
    runDecoded oracle input = .error .verificationFailed := by
  simp [runDecoded, hHash, hMismatch]

theorem proof_failure (oracle : KzgOracle) (input : DecodedInput)
    (commitmentHash : List Byte)
    (hHash : oracle.tryCommitmentHashV1 input.commitment = some commitmentHash)
    (hMatch : commitmentHash = input.versionedHash)
    (hProof : oracle.verifyProof input.commitment input.z input.y input.proof = false) :
    runDecoded oracle input = .error .verificationFailed := by
  simp [runDecoded, hHash, hMatch, hProof]

theorem verification_success (oracle : KzgOracle) (input : DecodedInput)
    (commitmentHash : List Byte)
    (hHash : oracle.tryCommitmentHashV1 input.commitment = some commitmentHash)
    (hMatch : commitmentHash = input.versionedHash)
    (hProof : oracle.verifyProof input.commitment input.z input.y input.proof = true) :
    runDecoded oracle input = .ok successOutput := by
  simp [runDecoded, hHash, hMatch, hProof]

theorem run_classification (oracle : KzgOracle) (input : List Byte) :
    run oracle input = .error .invalidInputLength ∨
      run oracle input = .error .verificationFailed ∨
      run oracle input = .ok successOutput := by
  unfold run
  split
  · unfold runDecoded
    split
    · right; left; rfl
    · split
      · split
        · right; right; rfl
        · right; left; rfl
      · right; left; rfl
  · left; rfl

end KzgPointEvaluation
end Precompiles
end Eip803x
