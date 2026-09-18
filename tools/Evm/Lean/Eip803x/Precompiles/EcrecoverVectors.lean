-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Ecrecover

namespace Eip803x
namespace Precompiles
namespace EcrecoverVectors

open Evm.MemoryStackControl
open Ecrecover

/-!
  The known-answer literals in this file deliberately have no parser fallback:
  each carries a proof of its exact byte length.  The expected-result and oracle
  tables are separately declared, but their repeated address bytes are the same
  asserted known answer, not independent cryptographic evidence.
-/

structure ExactBytes (expectedLength : Nat) where
  bytes : List Byte
  length_eq : bytes.length = expectedLength
  deriving DecidableEq, Repr

def exact (bytes : List Byte) (length_eq : bytes.length = 32) : ExactBytes 32 :=
  { bytes, length_eq }

def zeroWord : ExactBytes 32 :=
  exact (List.replicate 32 zeroByte) (by native_decide)

def v27 : ExactBytes 32 :=
  exact (List.replicate 31 zeroByte ++ [byte 0x1b]) (by native_decide)

def v28 : ExactBytes 32 :=
  exact (List.replicate 31 zeroByte ++ [byte 0x1c]) (by native_decide)

def v26 : ExactBytes 32 :=
  exact (List.replicate 31 zeroByte ++ [byte 0x1a]) (by native_decide)

def v29 : ExactBytes 32 :=
  exact (List.replicate 31 zeroByte ++ [byte 0x1d]) (by native_decide)

def invalidHighV : ExactBytes 32 :=
  exact (byte 0x01 :: List.replicate 30 zeroByte ++ [byte 0x1c]) (by native_decide)

def messageA : ExactBytes 32 :=
  exact
    [byte 0x38, byte 0xd1, byte 0x8a, byte 0xcb, byte 0x67, byte 0xd2, byte 0x5c, byte 0x8b,
      byte 0xb9, byte 0x94, byte 0x27, byte 0x64, byte 0xb6, byte 0x2f, byte 0x18, byte 0xe1,
      byte 0x70, byte 0x54, byte 0xf6, byte 0x6a, byte 0x81, byte 0x7b, byte 0xd4, byte 0x29,
      byte 0x54, byte 0x23, byte 0xad, byte 0xf9, byte 0xed, byte 0x98, byte 0x87, byte 0x3e]
    (by native_decide)

def rA : ExactBytes 32 := messageA

def sA : ExactBytes 32 :=
  exact
    [byte 0x78, byte 0x9d, byte 0x1d, byte 0xd4, byte 0x23, byte 0xd2, byte 0x5f, byte 0x07,
      byte 0x72, byte 0xd2, byte 0x74, byte 0x8d, byte 0x60, byte 0xf7, byte 0xe4, byte 0xb8,
      byte 0x1b, byte 0xb1, byte 0x4d, byte 0x08, byte 0x6e, byte 0xba, byte 0x8e, byte 0x8e,
      byte 0x8e, byte 0xfb, byte 0x6d, byte 0xcf, byte 0xf8, byte 0xa4, byte 0xae, byte 0x02]
    (by native_decide)

def messageB : ExactBytes 32 :=
  exact
    [byte 0x18, byte 0xc5, byte 0x47, byte 0xe4, byte 0xf7, byte 0xb0, byte 0xf3, byte 0x25,
      byte 0xad, byte 0x1e, byte 0x56, byte 0xf5, byte 0x7e, byte 0x26, byte 0xc7, byte 0x45,
      byte 0xb0, byte 0x9a, byte 0x3e, byte 0x50, byte 0x3d, byte 0x86, byte 0xe0, byte 0x0e,
      byte 0x52, byte 0x55, byte 0xff, byte 0x7f, byte 0x71, byte 0x5d, byte 0x3d, byte 0x1c]
    (by native_decide)

def rB : ExactBytes 32 :=
  exact
    [byte 0x73, byte 0xb1, byte 0x69, byte 0x38, byte 0x92, byte 0x21, byte 0x9d, byte 0x73,
      byte 0x6c, byte 0xab, byte 0xa5, byte 0x5b, byte 0xdb, byte 0x67, byte 0x21, byte 0x6e,
      byte 0x48, byte 0x55, byte 0x57, byte 0xea, byte 0x6b, byte 0x6a, byte 0xf7, byte 0x5f,
      byte 0x37, byte 0x09, byte 0x6c, byte 0x9a, byte 0xa6, byte 0xa5, byte 0xa7, byte 0x5f]
    (by native_decide)

def sB : ExactBytes 32 :=
  exact
    [byte 0xee, byte 0xb9, byte 0x40, byte 0xb1, byte 0xd0, byte 0x3b, byte 0x21, byte 0xe3,
      byte 0x6b, byte 0x0e, byte 0x47, byte 0xe7, byte 0x97, byte 0x69, byte 0xf0, byte 0x95,
      byte 0xfe, byte 0x2a, byte 0xb8, byte 0x55, byte 0xbd, byte 0x91, byte 0xe3, byte 0xa3,
      byte 0x87, byte 0x56, byte 0xb7, byte 0xd7, byte 0x5a, byte 0x9c, byte 0x45, byte 0x49]
    (by native_decide)

def secp256k1OrderWord : ExactBytes 32 :=
  exact
    [byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xfe,
      byte 0xba, byte 0xae, byte 0xdc, byte 0xe6, byte 0xaf, byte 0x48, byte 0xa0, byte 0x3b,
      byte 0xbf, byte 0xd2, byte 0x5e, byte 0x8c, byte 0xd0, byte 0x36, byte 0x41, byte 0x41]
    (by native_decide)

def scalarOneWord : ExactBytes 32 :=
  exact (List.replicate 31 zeroByte ++ [byte 0x01]) (by native_decide)

def secp256k1OrderMinusOneWord : ExactBytes 32 :=
  exact
    [byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xfe,
      byte 0xba, byte 0xae, byte 0xdc, byte 0xe6, byte 0xaf, byte 0x48, byte 0xa0, byte 0x3b,
      byte 0xbf, byte 0xd2, byte 0x5e, byte 0x8c, byte 0xd0, byte 0x36, byte 0x41, byte 0x40]
    (by native_decide)

def secp256k1OrderPlusOneWord : ExactBytes 32 :=
  exact
    [byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xfe,
      byte 0xba, byte 0xae, byte 0xdc, byte 0xe6, byte 0xaf, byte 0x48, byte 0xa0, byte 0x3b,
      byte 0xbf, byte 0xd2, byte 0x5e, byte 0x8c, byte 0xd0, byte 0x36, byte 0x41, byte 0x42]
    (by native_decide)

def inputWith (message v r s : ExactBytes 32) : ExactBytes 128 :=
  { bytes := message.bytes ++ v.bytes ++ r.bytes ++ s.bytes
    length_eq := by
      simp [message.length_eq, v.length_eq, r.length_eq, s.length_eq] }

def inputA : ExactBytes 128 :=
  { bytes := messageA.bytes ++ v27.bytes ++ rA.bytes ++ sA.bytes
    length_eq := by native_decide }

def inputB : ExactBytes 128 :=
  { bytes := messageB.bytes ++ v28.bytes ++ rB.bytes ++ sB.bytes
    length_eq := by native_decide }

def v26Input : ExactBytes 128 := inputWith messageA v26 rA sA

def v29Input : ExactBytes 128 := inputWith messageB v29 rB sB

def rScalarZeroInput : ExactBytes 128 := inputWith messageA v27 zeroWord sA

def rScalarOneInput : ExactBytes 128 := inputWith messageA v27 scalarOneWord sA

def rScalarOrderMinusOneInput : ExactBytes 128 :=
  inputWith messageA v27 secp256k1OrderMinusOneWord sA

def rScalarOrderInput : ExactBytes 128 :=
  inputWith messageA v27 secp256k1OrderWord sA

def rScalarOrderPlusOneInput : ExactBytes 128 :=
  inputWith messageA v27 secp256k1OrderPlusOneWord sA

def sScalarZeroInput : ExactBytes 128 := inputWith messageA v27 rA zeroWord

def sScalarOneInput : ExactBytes 128 := inputWith messageA v27 rA scalarOneWord

def sScalarOrderMinusOneInput : ExactBytes 128 :=
  inputWith messageA v27 rA secp256k1OrderMinusOneWord

def sScalarOrderInput : ExactBytes 128 :=
  inputWith messageA v27 rA secp256k1OrderWord

def sScalarOrderPlusOneInput : ExactBytes 128 :=
  inputWith messageA v27 rA secp256k1OrderPlusOneWord

def shortInputA64 : ExactBytes 64 :=
  { bytes := messageA.bytes ++ v27.bytes
    length_eq := by native_decide }

def shortInputA96 : ExactBytes 96 :=
  { bytes := messageA.bytes ++ v27.bytes ++ rA.bytes
    length_eq := by native_decide }

def invalidVInput : ExactBytes 128 :=
  { bytes := messageA.bytes ++ invalidHighV.bytes ++ rA.bytes ++ sA.bytes
    length_eq := by native_decide }

def invalidRInput : ExactBytes 128 :=
  { bytes := messageA.bytes ++ v27.bytes ++ zeroWord.bytes ++ sA.bytes
    length_eq := by native_decide }

def invalidSInput : ExactBytes 128 :=
  { bytes := messageB.bytes ++ v28.bytes ++ rB.bytes ++ secp256k1OrderWord.bytes
    length_eq := by native_decide }

def overlongInputA : ExactBytes 129 :=
  { bytes := inputA.bytes ++ [byte 0x42]
    length_eq := by native_decide }

/- Expected 32-byte result literals duplicate the asserted oracle answer below. -/
def expectedAOutput : ExactBytes 32 :=
  exact
    [byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0xce, byte 0xac, byte 0xca, byte 0xc6,
      byte 0x40, byte 0xad, byte 0xf5, byte 0x5b, byte 0x20, byte 0x28, byte 0x46, byte 0x9b,
      byte 0xd3, byte 0x6b, byte 0xa5, byte 0x01, byte 0xf2, byte 0x8b, byte 0x69, byte 0x9d]
    (by native_decide)

def expectedBOutput : ExactBytes 32 :=
  exact
    [byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0xa9, byte 0x4f, byte 0x53, byte 0x74,
      byte 0xfc, byte 0xe5, byte 0xed, byte 0xbc, byte 0x8e, byte 0x2a, byte 0x86, byte 0x97,
      byte 0xc1, byte 0x53, byte 0x31, byte 0x67, byte 0x7e, byte 0x6e, byte 0xbf, byte 0x0b]
    (by native_decide)

/- The oracle table is separately declared, not independently cryptographically derived. -/
def oracleAAddress : Address :=
  addressOfBytes
    [byte 0xce, byte 0xac, byte 0xca, byte 0xc6, byte 0x40, byte 0xad, byte 0xf5, byte 0x5b,
      byte 0x20, byte 0x28, byte 0x46, byte 0x9b, byte 0xd3, byte 0x6b, byte 0xa5, byte 0x01,
      byte 0xf2, byte 0x8b, byte 0x69, byte 0x9d]
    (by native_decide)

def oracleBAddress : Address :=
  addressOfBytes
    [byte 0xa9, byte 0x4f, byte 0x53, byte 0x74, byte 0xfc, byte 0xe5, byte 0xed, byte 0xbc,
      byte 0x8e, byte 0x2a, byte 0x86, byte 0x97, byte 0xc1, byte 0x53, byte 0x31, byte 0x67,
      byte 0x7e, byte 0x6e, byte 0xbf, byte 0x0b]
    (by native_decide)

def knownAnswerOracle : Secp256k1KeccakOracle :=
  { recoverAddress := fun message recovery r s =>
      if message == messageA.bytes && recovery == 0 && r == rA.bytes && s == sA.bytes then
        some oracleAAddress
      else if message == messageB.bytes && recovery == 1 && r == rB.bytes && s == sB.bytes then
        some oracleBAddress
      else none }

def rejectingOracle : Secp256k1KeccakOracle :=
  { recoverAddress := fun _ _ _ _ => none }

def boundaryInput (length : Nat) : List Byte :=
  (List.range length).map byte

structure BoundaryVector where
  input : List Byte
  inputLength : Nat
  input_length_eq : input.length = inputLength
  expectedOutput : List Byte
  expected_output_shape : expectedOutput.length = 0 ∨ expectedOutput.length = 32

def emptyBoundary (length : Nat) : BoundaryVector :=
  { input := boundaryInput length
    inputLength := length
    input_length_eq := by simp [boundaryInput]
    expectedOutput := []
    expected_output_shape := Or.inl rfl }

def emptyExactVector (input : ExactBytes 128) : BoundaryVector :=
  { input := input.bytes
    inputLength := 128
    input_length_eq := input.length_eq
    expectedOutput := []
    expected_output_shape := Or.inl rfl }

def boundaryVectors : List BoundaryVector :=
  [ emptyBoundary 0
  , emptyBoundary 1
  , emptyBoundary 31
  , emptyBoundary 32
  , emptyBoundary 63
  , { input := shortInputA64.bytes
      inputLength := 64
      input_length_eq := shortInputA64.length_eq
      expectedOutput := []
      expected_output_shape := Or.inl rfl }
  , emptyBoundary 95
  , { input := shortInputA96.bytes
      inputLength := 96
      input_length_eq := shortInputA96.length_eq
      expectedOutput := []
      expected_output_shape := Or.inl rfl }
  , emptyBoundary 127
  , emptyExactVector v26Input
  , emptyExactVector v29Input
  , emptyExactVector rScalarZeroInput
  , emptyExactVector rScalarOneInput
  , emptyExactVector rScalarOrderMinusOneInput
  , emptyExactVector rScalarOrderInput
  , emptyExactVector rScalarOrderPlusOneInput
  , emptyExactVector sScalarZeroInput
  , emptyExactVector sScalarOneInput
  , emptyExactVector sScalarOrderMinusOneInput
  , emptyExactVector sScalarOrderInput
  , emptyExactVector sScalarOrderPlusOneInput
  , { input := inputA.bytes
      inputLength := 128
      input_length_eq := inputA.length_eq
      expectedOutput := expectedAOutput.bytes
      expected_output_shape := Or.inr expectedAOutput.length_eq }
  , { input := inputB.bytes
      inputLength := 128
      input_length_eq := inputB.length_eq
      expectedOutput := expectedBOutput.bytes
      expected_output_shape := Or.inr expectedBOutput.length_eq }
  , { input := overlongInputA.bytes
      inputLength := 129
      input_length_eq := overlongInputA.length_eq
      expectedOutput := expectedAOutput.bytes
      expected_output_shape := Or.inr expectedAOutput.length_eq }
  ]

def passes (vector : BoundaryVector) : Bool :=
  (run knownAnswerOracle vector.input).success == true &&
    (run knownAnswerOracle vector.input).output == vector.expectedOutput

theorem all_boundary_vectors_pass : boundaryVectors.all passes = true := by
  native_decide

theorem boundary_vector_count : boundaryVectors.length = 24 := rfl

example : address = 1 := by native_decide

example : name = "ECREC" := by native_decide

example : supportsCaching = true := by native_decide

example : baseGasCost = 3000 := by native_decide

example : dataGasCost = 0 := by native_decide

example : totalGasCost = 3000 := by native_decide

example : inputA.bytes.length = 128 := inputA.length_eq

example : inputB.bytes.length = 128 := inputB.length_eq

example : shortInputA64.bytes.length = 64 := shortInputA64.length_eq

example : shortInputA96.bytes.length = 96 := shortInputA96.length_eq

example : expectedAOutput.bytes.length = 32 := expectedAOutput.length_eq

example : expectedBOutput.bytes.length = 32 := expectedBOutput.length_eq

/- Exact recovery-id boundaries: only 27 and 28 are accepted. -/
example : validV (decode v26Input.bytes).v = false := by native_decide

example : validV (decode inputA.bytes).v = true := by native_decide

example : validV (decode inputB.bytes).v = true := by native_decide

example : validV (decode v29Input.bytes).v = false := by native_decide

example : validScalar (decode inputA.bytes).r = true := by native_decide

example : validScalar (decode inputA.bytes).s = true := by native_decide

example : validScalar (decode inputB.bytes).r = true := by native_decide

example : validScalar (decode inputB.bytes).s = true := by native_decide

example : wordValue secp256k1OrderWord.bytes = secp256k1Order := by native_decide

/- Exact scalar-range boundaries for both decoded positions. -/
example : wordValue zeroWord.bytes = 0 := by native_decide

example : wordValue scalarOneWord.bytes = 1 := by native_decide

example : wordValue secp256k1OrderMinusOneWord.bytes = secp256k1Order - 1 := by native_decide

example : wordValue secp256k1OrderPlusOneWord.bytes = secp256k1Order + 1 := by native_decide

example : validScalar (decode rScalarZeroInput.bytes).r = false := by native_decide

example : validScalar (decode rScalarOneInput.bytes).r = true := by native_decide

example : validScalar (decode rScalarOrderMinusOneInput.bytes).r = true := by native_decide

example : validScalar (decode rScalarOrderInput.bytes).r = false := by native_decide

example : validScalar (decode rScalarOrderPlusOneInput.bytes).r = false := by native_decide

example : validScalar (decode sScalarZeroInput.bytes).s = false := by native_decide

example : validScalar (decode sScalarOneInput.bytes).s = true := by native_decide

example : validScalar (decode sScalarOrderMinusOneInput.bytes).s = true := by native_decide

example : validScalar (decode sScalarOrderInput.bytes).s = false := by native_decide

example : validScalar (decode sScalarOrderPlusOneInput.bytes).s = false := by native_decide

example : (run knownAnswerOracle inputA.bytes).success = true := by native_decide

example : (run knownAnswerOracle inputA.bytes).output = expectedAOutput.bytes := by native_decide

example : (run knownAnswerOracle inputB.bytes).success = true := by native_decide

example : (run knownAnswerOracle inputB.bytes).output = expectedBOutput.bytes := by native_decide

example : knownAnswerOracle.recoverAddress (decode inputA.bytes).message
    (recoveryId (decode inputA.bytes)) (decode inputA.bytes).r (decode inputA.bytes).s =
    some oracleAAddress := by native_decide

example : run knownAnswerOracle inputA.bytes = addressResult oracleAAddress ∧
    (run knownAnswerOracle inputA.bytes).output = paddedAddress oracleAAddress ∧
    (run knownAnswerOracle inputA.bytes).output.drop 12 = oracleAAddress.bytes := by
  exact oracle_success_returns_exact_address_result knownAnswerOracle inputA.bytes oracleAAddress
    (by native_decide) (by native_decide) (by native_decide) (by native_decide)

/- Valid scalars with a failing oracle still return the successful empty result. -/
example : run rejectingOracle inputA.bytes = emptyResult := by native_decide

example : (run rejectingOracle inputA.bytes).success = true := by native_decide

example : (run rejectingOracle inputA.bytes).output = [] := by native_decide

/- Every exact scalar boundary is run in both the `r` and `s` positions. -/
example : run knownAnswerOracle v26Input.bytes = emptyResult := by native_decide

example : run knownAnswerOracle v29Input.bytes = emptyResult := by native_decide

example : run knownAnswerOracle rScalarZeroInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle rScalarOneInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle rScalarOrderMinusOneInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle rScalarOrderInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle rScalarOrderPlusOneInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle sScalarZeroInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle sScalarOneInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle sScalarOrderMinusOneInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle sScalarOrderInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle sScalarOrderPlusOneInput.bytes = emptyResult := by native_decide

/- The requested invalid `v`, `r`, and `s` cases are successful EVM calls with empty output. -/
example : validV (decode invalidVInput.bytes).v = false := by native_decide

example : validScalar (decode invalidRInput.bytes).r = false := by native_decide

example : validScalar (decode invalidSInput.bytes).s = false := by native_decide

example : run knownAnswerOracle invalidVInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle invalidRInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle invalidSInput.bytes = emptyResult := by native_decide

example : (run knownAnswerOracle invalidVInput.bytes).success = true := by native_decide

example : (run knownAnswerOracle invalidRInput.bytes).success = true := by native_decide

example : (run knownAnswerOracle invalidSInput.bytes).success = true := by native_decide

example : (run knownAnswerOracle invalidVInput.bytes).output.length = 0 := by native_decide

example : (run knownAnswerOracle invalidRInput.bytes).output.length = 0 := by native_decide

example : (run knownAnswerOracle invalidSInput.bytes).output.length = 0 := by native_decide

example : normalizedInput overlongInputA.bytes = normalizedInput inputA.bytes := by native_decide

example : run knownAnswerOracle overlongInputA.bytes = run knownAnswerOracle inputA.bytes := by native_decide

example : normalizedInput shortInputA64.bytes =
    messageA.bytes ++ v27.bytes ++ List.replicate 64 zeroByte := by native_decide

example : normalizedInput shortInputA96.bytes =
    messageA.bytes ++ v27.bytes ++ rA.bytes ++ List.replicate 32 zeroByte := by native_decide

/- Mutation sentinels make accidental changes to literals, range checks, padding, and truncation observable. -/
def widenedValidV (word : List Byte) : Bool :=
  allZero (word.take 31) &&
    (terminalByte word == byte 0x1a || terminalByte word == byte 0x1b ||
      terminalByte word == byte 0x1c || terminalByte word == byte 0x1d)

def narrowedValidV (word : List Byte) : Bool :=
  allZero (word.take 31) && terminalByte word == byte 0x1b

def widenedValidScalar (word : List Byte) : Bool :=
  wordValue word < secp256k1Order

def widenedOrderValidScalar (word : List Byte) : Bool :=
  0 < wordValue word && wordValue word ≤ secp256k1Order

def narrowedValidScalar (word : List Byte) : Bool :=
  0 < wordValue word && wordValue word < secp256k1Order - 1

example : widenedValidV v26.bytes ≠ validV v26.bytes := by native_decide

example : widenedValidV v29.bytes ≠ validV v29.bytes := by native_decide

example : narrowedValidV v28.bytes ≠ validV v28.bytes := by native_decide

example : widenedValidScalar zeroWord.bytes ≠ validScalar zeroWord.bytes := by native_decide

example : widenedOrderValidScalar secp256k1OrderWord.bytes ≠
    validScalar secp256k1OrderWord.bytes := by native_decide

example : narrowedValidScalar secp256k1OrderMinusOneWord.bytes ≠
    validScalar secp256k1OrderMinusOneWord.bytes := by native_decide

def mutatedAOutput : ExactBytes 32 :=
  exact
    [byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0xce, byte 0xac, byte 0xca, byte 0xc6,
      byte 0x40, byte 0xad, byte 0xf5, byte 0x5b, byte 0x20, byte 0x28, byte 0x46, byte 0x9b,
      byte 0xd3, byte 0x6b, byte 0xa5, byte 0x01, byte 0xf2, byte 0x8b, byte 0x69, byte 0x9c]
    (by native_decide)

example : mutatedAOutput.bytes ≠ expectedAOutput.bytes := by native_decide

def mutatedVInput : ExactBytes 128 :=
  { bytes := messageA.bytes ++ v28.bytes ++ rA.bytes ++ sA.bytes
    length_eq := by native_decide }

example : run knownAnswerOracle mutatedVInput.bytes = emptyResult := by native_decide

example : run knownAnswerOracle mutatedVInput.bytes ≠ run knownAnswerOracle inputA.bytes := by native_decide

def mutatedTruncationInput : ExactBytes 129 :=
  { bytes := inputA.bytes ++ [byte 0x43]
    length_eq := by native_decide }

example : normalizedInput mutatedTruncationInput.bytes = normalizedInput inputA.bytes := by native_decide

example : run knownAnswerOracle mutatedTruncationInput.bytes = run knownAnswerOracle inputA.bytes := by native_decide

def mutatedFailureResult : Result := { success := false, output := [] }

example : mutatedFailureResult ≠ emptyResult := by native_decide

end EcrecoverVectors
end Precompiles
end Eip803x
