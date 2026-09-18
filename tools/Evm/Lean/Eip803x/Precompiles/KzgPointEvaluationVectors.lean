-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.KzgPointEvaluation

namespace Eip803x
namespace Precompiles
namespace KzgPointEvaluationVectors

open Evm.MemoryStackControl
open KzgPointEvaluation

def schedule : Schedule := Schedule.amsterdam

def zero32 : List Byte := List.replicate 32 zeroByte

def zero47 : List Byte := List.replicate 47 zeroByte

def identityCommitment : List Byte := byte 0xc0 :: zero47

def invalidCommitment : List Byte := byte 0xc1 :: zero47

def invalidProof : List Byte := byte 0xc2 :: zero47

/- The expected input table is kept separate from the oracle's commitment-hash
   table so a one-sided fixture mutation breaks the vector relation. -/
def expectedIdentityVersionedHash : List Byte :=
  [byte 0x01, byte 0x06, byte 0x57, byte 0xf3, byte 0x75, byte 0x54, byte 0xc7, byte 0x81,
    byte 0x40, byte 0x2a, byte 0x22, byte 0x91, byte 0x7d, byte 0xee, byte 0x2f, byte 0x75,
    byte 0xde, byte 0xf7, byte 0xab, byte 0x96, byte 0x6d, byte 0x7b, byte 0x77, byte 0x09,
    byte 0x05, byte 0x39, byte 0x8e, byte 0xba, byte 0x3c, byte 0x44, byte 0x40, byte 0x14]

def oracleIdentityVersionedHash : List Byte :=
  [byte 0x01, byte 0x06, byte 0x57, byte 0xf3, byte 0x75, byte 0x54, byte 0xc7, byte 0x81,
    byte 0x40, byte 0x2a, byte 0x22, byte 0x91, byte 0x7d, byte 0xee, byte 0x2f, byte 0x75,
    byte 0xde, byte 0xf7, byte 0xab, byte 0x96, byte 0x6d, byte 0x7b, byte 0x77, byte 0x09,
    byte 0x05, byte 0x39, byte 0x8e, byte 0xba, byte 0x3c, byte 0x44, byte 0x40, byte 0x14]

def secondValidZ : List Byte :=
  [byte 0x5e, byte 0xb7, byte 0x00, byte 0x4f, byte 0xe5, byte 0x73, byte 0x83, byte 0xe6,
    byte 0xc8, byte 0x8b, byte 0x99, byte 0xd8, byte 0x39, byte 0x93, byte 0x7f, byte 0xdd,
    byte 0xf3, byte 0xf9, byte 0x92, byte 0x79, byte 0x35, byte 0x3a, byte 0xaf, byte 0x8d,
    byte 0x5c, byte 0x9a, byte 0x75, byte 0xf9, byte 0x1c, byte 0xe3, byte 0x3c, byte 0x62]

def expectedSuccessOutput : Output :=
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

def inputFor (versionedHash z y commitment proof : List Byte) : List Byte :=
  versionedHash ++ z ++ y ++ commitment ++ proof

def validIdentityInput : List Byte :=
  inputFor expectedIdentityVersionedHash zero32 zero32 identityCommitment identityCommitment

def validSecondZInput : List Byte :=
  inputFor expectedIdentityVersionedHash secondValidZ zero32 identityCommitment identityCommitment

def wrongVersionInput : List Byte :=
  inputFor (byte 0xff :: expectedIdentityVersionedHash.drop 1) zero32 zero32
    identityCommitment identityCommitment

def invalidProofInput : List Byte :=
  inputFor expectedIdentityVersionedHash zero32 zero32 identityCommitment invalidProof

def hashFailureInput : List Byte :=
  inputFor expectedIdentityVersionedHash zero32 zero32 invalidCommitment identityCommitment

def knownAnswerOracle : KzgOracle :=
  { tryCommitmentHashV1 := fun commitment =>
      if commitment == identityCommitment then some oracleIdentityVersionedHash else none
    verifyProof := fun commitment z y proof =>
      commitment == identityCommitment && y == zero32 && proof == identityCommitment &&
        (z == zero32 || z == secondValidZ) }

structure Vector where
  name : String
  input : List Byte
  expected : Result
  expectedGas : Nat

def resultEq : Result -> Result -> Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => left.bytes == right.bytes
  | _, _ => false

def passes (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas

def vectors : List Vector :=
  [ { name := "empty input is rejected", input := [],
      expected := .error .invalidInputLength, expectedGas := 50_000 }
  , { name := "191-byte input is rejected", input := List.replicate 191 zeroByte,
      expected := .error .invalidInputLength, expectedGas := 50_000 }
  , { name := "193-byte input is rejected", input := List.replicate 193 zeroByte,
      expected := .error .invalidInputLength, expectedGas := 50_000 }
  , { name := "identity proof at zero succeeds", input := validIdentityInput,
      expected := .ok expectedSuccessOutput, expectedGas := 50_000 }
  , { name := "identity proof at a nonzero point succeeds", input := validSecondZInput,
      expected := .ok expectedSuccessOutput, expectedGas := 50_000 }
  , { name := "versioned hash mismatch is rejected", input := wrongVersionInput,
      expected := .error .verificationFailed, expectedGas := 50_000 }
  , { name := "invalid proof is rejected", input := invalidProofInput,
      expected := .error .verificationFailed, expectedGas := 50_000 }
  , { name := "commitment hash failure is rejected", input := hashFailureInput,
      expected := .error .verificationFailed, expectedGas := 50_000 } ]

theorem vector_count : vectors.length = 8 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem fixture_tables_agree :
    expectedIdentityVersionedHash = oracleIdentityVersionedHash ∧
      expectedSuccessOutput = successOutput := by
  native_decide

example : validIdentityInput.length = inputLength := by native_decide

example : validSecondZInput.length = inputLength := by native_decide

example : (decode validSecondZInput).z = secondValidZ := by native_decide

example : (decode validSecondZInput).y = zero32 := by native_decide

example : resultEq (run knownAnswerOracle validIdentityInput) (.ok expectedSuccessOutput) = true := by
  native_decide

example : resultEq (run knownAnswerOracle validSecondZInput) (.ok expectedSuccessOutput) = true := by
  native_decide

example : resultEq (run knownAnswerOracle wrongVersionInput) (.error .verificationFailed) = true := by
  native_decide

example : resultEq (run knownAnswerOracle invalidProofInput) (.error .verificationFailed) = true := by
  native_decide

example : resultEq (run knownAnswerOracle hashFailureInput) (.error .verificationFailed) = true := by
  native_decide

def mutatedAddress : Nat := 11

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "KZG_POINT_EVAL"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedInputLength : Nat := 191

example : mutatedInputLength ≠ inputLength := by native_decide

def mutatedFixedGas : Nat := 49_999

example : mutatedFixedGas ≠ totalGasCost schedule validIdentityInput := by native_decide

def mutatedDecode (input : List Byte) : DecodedInput :=
  { versionedHash := input.take 32
    z := input.drop 64 |>.take 32
    y := input.drop 32 |>.take 32
    commitment := input.drop 96 |>.take 48
    proof := input.drop 144 |>.take 48 }

example : resultEq (runDecoded knownAnswerOracle (mutatedDecode validSecondZInput))
    (run knownAnswerOracle validSecondZInput) = false := by
  native_decide

def runWithoutHashMatch (oracle : KzgOracle) (input : List Byte) : Result :=
  if input.length = inputLength then
    let decoded := decode input
    match oracle.tryCommitmentHashV1 decoded.commitment with
    | none => .error .verificationFailed
    | some _ =>
        if oracle.verifyProof decoded.commitment decoded.z decoded.y decoded.proof then
          .ok successOutput
        else
          .error .verificationFailed
  else
    .error .invalidInputLength

example : resultEq (runWithoutHashMatch knownAnswerOracle wrongVersionInput)
    (run knownAnswerOracle wrongVersionInput) = false := by
  native_decide

def runWithoutProof (oracle : KzgOracle) (input : List Byte) : Result :=
  if input.length = inputLength then
    let decoded := decode input
    match oracle.tryCommitmentHashV1 decoded.commitment with
    | none => .error .verificationFailed
    | some commitmentHash =>
        if commitmentHash == decoded.versionedHash then .ok successOutput
        else .error .verificationFailed
  else
    .error .invalidInputLength

example : resultEq (runWithoutProof knownAnswerOracle invalidProofInput)
    (run knownAnswerOracle invalidProofInput) = false := by
  native_decide

def runWithoutLengthCheck (oracle : KzgOracle) (input : List Byte) : Result :=
  runDecoded oracle (decode input)

example : resultEq (runWithoutLengthCheck knownAnswerOracle []) (run knownAnswerOracle []) = false := by
  native_decide

def mutatedExpectedSuccessOutput : Output :=
  outputOfBytes (byte 0x01 :: expectedSuccessOutput.bytes.drop 1) (by native_decide)

theorem mutated_success_output_is_detected :
    mutatedExpectedSuccessOutput ≠ successOutput ∧
      mutatedExpectedSuccessOutput ≠ expectedSuccessOutput := by
  native_decide

def mutatedOracleVersionedHash : List Byte :=
  byte 0x02 :: oracleIdentityVersionedHash.drop 1

theorem mutated_oracle_hash_is_detected :
    mutatedOracleVersionedHash ≠ expectedIdentityVersionedHash ∧
      mutatedOracleVersionedHash ≠ oracleIdentityVersionedHash := by
  native_decide

end KzgPointEvaluationVectors
end Precompiles
end Eip803x
