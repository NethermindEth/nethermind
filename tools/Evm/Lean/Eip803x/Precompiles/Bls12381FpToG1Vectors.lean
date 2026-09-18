-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381FpToG1

namespace Eip803x
namespace Precompiles
namespace Bls12381FpToG1Vectors

open Evm.MemoryStackControl
open Bls12381FpToG1

/-
  These are the five success and five failure literals from the pinned
  Nethermind execution-spec-compatible assets.  The parser is deliberately
  fail-closed: malformed or odd hexadecimal text becomes no bytes, and every
  exact-width fixture carries a checked length proof.
-/

def hexNibble : Char -> Option Nat
  | '0' => some 0
  | '1' => some 1
  | '2' => some 2
  | '3' => some 3
  | '4' => some 4
  | '5' => some 5
  | '6' => some 6
  | '7' => some 7
  | '8' => some 8
  | '9' => some 9
  | 'a' => some 10
  | 'b' => some 11
  | 'c' => some 12
  | 'd' => some 13
  | 'e' => some 14
  | 'f' => some 15
  | _ => none

def parseHexAux : List Char -> Option (List Byte)
  | [] => some []
  | high :: low :: tail =>
      match hexNibble high, hexNibble low, parseHexAux tail with
      | some highValue, some lowValue, some bytes =>
          some (byte (16 * highValue + lowValue) :: bytes)
      | _, _, _ => none
  | [_] => none

def parseHex (text : String) : Option (List Byte) :=
  parseHexAux text.toList

def parsedBytes (text : String) : List Byte :=
  match parseHex text with
  | some bytes => bytes
  | none => []

def valueFromRawHex (text : String) (hLength : (parsedBytes text).length = inputLength) : FpValue :=
  { bytes := (parsedBytes text).drop fpPaddingLength
    length_eq := by
      simp only [List.length_drop]
      simp only [inputLength, fpPaddingLength, fpValueLength] at hLength ⊢
      omega }

def outputFromRawHex (text : String)
    (hLength : (parsedBytes text).length = outputLength) : G1Output :=
  let xBytes := (parsedBytes text).drop fpPaddingLength |>.take fpValueLength
  let yBytes := (parsedBytes text).drop (fpPaddingLength + fpValueLength + fpPaddingLength)
  |>.take fpValueLength
  have hx : xBytes.length = fpValueLength := by
    simp only [xBytes, List.length_take, List.length_drop]
    simp only [outputLength, fpPaddingLength, fpValueLength] at hLength ⊢
    omega
  have hy : yBytes.length = fpValueLength := by
    simp only [yBytes, List.length_take, List.length_drop]
    simp only [outputLength, fpPaddingLength, fpValueLength] at hLength ⊢
    omega
  { x := { bytes := xBytes, length_eq := hx }
    y := { bytes := yBytes, length_eq := hy } }

def inputOneHex : String :=
  "00000000000000000000000000000000156c8a6a2c184569d69a76be144b5cdc5141d2d2ca4fe341f011e25e3969c55ad9e9b9ce2eb833c81a908e5fa4ac5f03"

def inputTwoHex : String :=
  "00000000000000000000000000000000147e1ed29f06e4c5079b9d14fc89d2820d32419b990c1c7bb7dbea2a36a045124b31ffbde7c99329c05c559af1c6cc82"

def inputThreeHex : String :=
  "0000000000000000000000000000000004090815ad598a06897dd89bcda860f25837d54e897298ce31e6947378134d3761dc59a572154963e8c954919ecfa82d"

def inputFourHex : String :=
  "0000000000000000000000000000000008dccd088ca55b8bfbc96fb50bb25c592faa867a8bb78d4e94a8cc2c92306190244532e91feba2b7fed977e3c3bb5a1f"

def inputFiveHex : String :=
  "000000000000000000000000000000000dd824886d2123a96447f6c56e3a3fa992fbfefdba17b6673f9f630ff19e4d326529db37e1c1be43f905bf9202e0278d"

def expectedOneHex : String :=
  "00000000000000000000000000000000184bb665c37ff561a89ec2122dd343f20e0f4cbcaec84e3c3052ea81d1834e192c426074b02ed3dca4e7676ce4ce48ba0000000000000000000000000000000004407b8d35af4dacc809927071fc0405218f1401a6d15af775810e4e460064bcc9468beeba82fdc751be70476c888bf3"

def expectedTwoHex : String :=
  "00000000000000000000000000000000009769f3ab59bfd551d53a5f846b9984c59b97d6842b20a2c565baa167945e3d026a3755b6345df8ec7e6acb6868ae6d000000000000000000000000000000001532c00cf61aa3d0ce3e5aa20c3b531a2abd2c770a790a2613818303c6b830ffc0ecf6c357af3317b9575c567f11cd2c"

def expectedThreeHex : String :=
  "000000000000000000000000000000001974dbb8e6b5d20b84df7e625e2fbfecb2cdb5f77d5eae5fb2955e5ce7313cae8364bc2fff520a6c25619739c6bdcb6a0000000000000000000000000000000015f9897e11c6441eaa676de141c8d83c37aab8667173cbe1dfd6de74d11861b961dccebcd9d289ac633455dfcc7013a3"

def expectedFourHex : String :=
  "000000000000000000000000000000000a7a047c4a8397b3446450642c2ac64d7239b61872c9ae7a59707a8f4f950f101e766afe58223b3bff3a19a7f754027c000000000000000000000000000000001383aebba1e4327ccff7cf9912bda0dbc77de048b71ef8c8a81111d71dc33c5e3aa6edee9cf6f5fe525d50cc50b77cc9"

def expectedFiveHex : String :=
  "000000000000000000000000000000000e7a16a975904f131682edbb03d9560d3e48214c9986bd50417a77108d13dc957500edf96462a3d01e62dc6cd468ef11000000000000000000000000000000000ae89e677711d05c30a48d6d75e76ca9fb70fe06c6dd6ff988683d89ccde29ac7d46c53bb97a59b1901abf1db66052db"

def oracleOneHex : String :=
  "00000000000000000000000000000000184bb665c37ff561a89ec2122dd343f20e0f4cbcaec84e3c3052ea81d1834e192c426074b02ed3dca4e7676ce4ce48ba0000000000000000000000000000000004407b8d35af4dacc809927071fc0405218f1401a6d15af775810e4e460064bcc9468beeba82fdc751be70476c888bf3"

def oracleTwoHex : String :=
  "00000000000000000000000000000000009769f3ab59bfd551d53a5f846b9984c59b97d6842b20a2c565baa167945e3d026a3755b6345df8ec7e6acb6868ae6d000000000000000000000000000000001532c00cf61aa3d0ce3e5aa20c3b531a2abd2c770a790a2613818303c6b830ffc0ecf6c357af3317b9575c567f11cd2c"

def oracleThreeHex : String :=
  "000000000000000000000000000000001974dbb8e6b5d20b84df7e625e2fbfecb2cdb5f77d5eae5fb2955e5ce7313cae8364bc2fff520a6c25619739c6bdcb6a0000000000000000000000000000000015f9897e11c6441eaa676de141c8d83c37aab8667173cbe1dfd6de74d11861b961dccebcd9d289ac633455dfcc7013a3"

def oracleFourHex : String :=
  "000000000000000000000000000000000a7a047c4a8397b3446450642c2ac64d7239b61872c9ae7a59707a8f4f950f101e766afe58223b3bff3a19a7f754027c000000000000000000000000000000001383aebba1e4327ccff7cf9912bda0dbc77de048b71ef8c8a81111d71dc33c5e3aa6edee9cf6f5fe525d50cc50b77cc9"

def oracleFiveHex : String :=
  "000000000000000000000000000000000e7a16a975904f131682edbb03d9560d3e48214c9986bd50417a77108d13dc957500edf96462a3d01e62dc6cd468ef11000000000000000000000000000000000ae89e677711d05c30a48d6d75e76ca9fb70fe06c6dd6ff988683d89ccde29ac7d46c53bb97a59b1901abf1db66052db"

def emptyInputHex : String := ""

def shortInputHex : String :=
  "00000000000000000000000000000000156c8a6a2c184569d69a76be144b5cdc5141d2d2ca4fe341f011e25e3969c55ad9e9b9ce2eb833c81a908e5fa4ac5f"

def largeInputHex : String :=
  "0000000000000000000000000000000000156c8a6a2c184569d69a76be144b5cdc5141d2d2ca4fe341f011e25e3969c55ad9e9b9ce2eb833c81a908e5fa4ac5f03"

def topBytesInputHex : String :=
  "1000000000000000000000000000000000156c8a6a2c184569d69a76be144b5cdc5141d2d2ca4fe341f011e25e3969c55ad9e9b9ce2eb833c81a908e5fa4ac5f"

def invalidFieldInputHex : String :=
  "000000000000000000000000000000002f6d9c5465982c0421b61e74579709b3b5b91e57bdd4f6015742b4ff301abb7ef895b9cce00c33c7d48f8e5fa4ac09ae"

def inputOne : List Byte := parsedBytes inputOneHex
def inputTwo : List Byte := parsedBytes inputTwoHex
def inputThree : List Byte := parsedBytes inputThreeHex
def inputFour : List Byte := parsedBytes inputFourHex
def inputFive : List Byte := parsedBytes inputFiveHex

def expectedOne : G1Output := outputFromRawHex expectedOneHex (by native_decide)
def expectedTwo : G1Output := outputFromRawHex expectedTwoHex (by native_decide)
def expectedThree : G1Output := outputFromRawHex expectedThreeHex (by native_decide)
def expectedFour : G1Output := outputFromRawHex expectedFourHex (by native_decide)
def expectedFive : G1Output := outputFromRawHex expectedFiveHex (by native_decide)

def oracleOne : G1Output := outputFromRawHex oracleOneHex (by native_decide)
def oracleTwo : G1Output := outputFromRawHex oracleTwoHex (by native_decide)
def oracleThree : G1Output := outputFromRawHex oracleThreeHex (by native_decide)
def oracleFour : G1Output := outputFromRawHex oracleFourHex (by native_decide)
def oracleFive : G1Output := outputFromRawHex oracleFiveHex (by native_decide)

def valueOne : FpValue := valueFromRawHex inputOneHex (by native_decide)
def valueTwo : FpValue := valueFromRawHex inputTwoHex (by native_decide)
def valueThree : FpValue := valueFromRawHex inputThreeHex (by native_decide)
def valueFour : FpValue := valueFromRawHex inputFourHex (by native_decide)
def valueFive : FpValue := valueFromRawHex inputFiveHex (by native_decide)

def invalidFieldValue : FpValue := valueFromRawHex invalidFieldInputHex (by native_decide)

def knownAnswerOracle : MapOracle :=
  { validField := fun value =>
      value == valueOne || value == valueTwo || value == valueThree ||
        value == valueFour || value == valueFive
    mapToG1 := fun value =>
      if value == valueOne then oracleOne
      else if value == valueTwo then oracleTwo
      else if value == valueThree then oracleThree
      else if value == valueFour then oracleFour
      else if value == valueFive then oracleFive
      else oracleOne }

def resultEq : Result -> Result -> Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => encodeOutput left == encodeOutput right
  | _, _ => false

def schedule : Schedule := Schedule.amsterdam

structure Vector where
  name : String
  input : List Byte
  expected : Result
  expectedGas : Nat

def passes (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas

def vectors : List Vector :=
  [ { name := "bls_g1map_", input := inputOne, expected := .ok expectedOne, expectedGas := 5500 }
  , { name := "bls_g1map_616263", input := inputTwo,
      expected := .ok expectedTwo, expectedGas := 5500 }
  , { name := "bls_g1map_6162636465663031", input := inputThree,
      expected := .ok expectedThree, expectedGas := 5500 }
  , { name := "bls_g1map_713132385f717171", input := inputFour,
      expected := .ok expectedFour, expectedGas := 5500 }
  , { name := "bls_g1map_613531325f616161", input := inputFive,
      expected := .ok expectedFive, expectedGas := 5500 }
  , { name := "bls_mapg1_empty_input", input := parsedBytes emptyInputHex,
      expected := .error .invalidInputLength, expectedGas := 5500 }
  , { name := "bls_mapg1_short_input", input := parsedBytes shortInputHex,
      expected := .error .invalidInputLength, expectedGas := 5500 }
  , { name := "bls_mapg1_large_input", input := parsedBytes largeInputHex,
      expected := .error .invalidInputLength, expectedGas := 5500 }
  , { name := "bls_mapg1_top_bytes", input := parsedBytes topBytesInputHex,
      expected := .error .invalidFieldElementTopBytes, expectedGas := 5500 }
  , { name := "bls_invalid_fq_element", input := parsedBytes invalidFieldInputHex,
      expected := .error .invalidFieldElement, expectedGas := 5500 } ]

theorem vector_count : vectors.length = 10 := by
  native_decide

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem success_asset_names :
    ["bls_g1map_", "bls_g1map_616263", "bls_g1map_6162636465663031",
      "bls_g1map_713132385f717171", "bls_g1map_613531325f616161"].length = 5 := by
  native_decide

theorem failure_asset_names :
    ["bls_mapg1_empty_input", "bls_mapg1_short_input", "bls_mapg1_large_input",
      "bls_mapg1_top_bytes", "bls_invalid_fq_element"].length = 5 := by
  native_decide

theorem known_answer_tables_agree :
    oracleOne = expectedOne ∧ oracleTwo = expectedTwo ∧ oracleThree = expectedThree ∧
      oracleFour = expectedFour ∧ oracleFive = expectedFive := by
  native_decide

theorem input_asset_lengths :
    inputOne.length = inputLength ∧ inputTwo.length = inputLength ∧
      inputThree.length = inputLength ∧ inputFour.length = inputLength ∧
      inputFive.length = inputLength ∧ (parsedBytes emptyInputHex).length = 0 ∧
      (parsedBytes shortInputHex).length = 63 ∧ (parsedBytes largeInputHex).length = 65 ∧
      (parsedBytes topBytesInputHex).length = inputLength ∧
      (parsedBytes invalidFieldInputHex).length = inputLength := by
  native_decide

theorem output_asset_lengths :
    (parsedBytes expectedOneHex).length = outputLength ∧
      (parsedBytes expectedTwoHex).length = outputLength ∧
      (parsedBytes expectedThreeHex).length = outputLength ∧
      (parsedBytes expectedFourHex).length = outputLength ∧
      (parsedBytes expectedFiveHex).length = outputLength ∧
      (parsedBytes oracleOneHex).length = outputLength ∧
      (parsedBytes oracleTwoHex).length = outputLength ∧
      (parsedBytes oracleThreeHex).length = outputLength ∧
      (parsedBytes oracleFourHex).length = outputLength ∧
      (parsedBytes oracleFiveHex).length = outputLength := by
  native_decide

theorem encoded_expected_literals :
    encodeOutput expectedOne = parsedBytes expectedOneHex ∧
      encodeOutput expectedTwo = parsedBytes expectedTwoHex ∧
      encodeOutput expectedThree = parsedBytes expectedThreeHex ∧
      encodeOutput expectedFour = parsedBytes expectedFourHex ∧
      encodeOutput expectedFive = parsedBytes expectedFiveHex := by
  native_decide

example : parseHex "0" = none := by native_decide

example : parseHex "zz" = none := by native_decide

example : resultEq (run knownAnswerOracle inputOne) (.ok expectedOne) = true := by
  native_decide

example : resultEq (run knownAnswerOracle inputTwo) (.ok expectedTwo) = true := by
  native_decide

example : resultEq (run knownAnswerOracle inputThree) (.ok expectedThree) = true := by
  native_decide

example : resultEq (run knownAnswerOracle inputFour) (.ok expectedFour) = true := by
  native_decide

example : resultEq (run knownAnswerOracle inputFive) (.ok expectedFive) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (parsedBytes emptyInputHex))
    (.error .invalidInputLength) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (parsedBytes shortInputHex))
    (.error .invalidInputLength) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (parsedBytes largeInputHex))
    (.error .invalidInputLength) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (parsedBytes topBytesInputHex))
    (.error .invalidFieldElementTopBytes) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (parsedBytes invalidFieldInputHex))
    (.error .invalidFieldElement) = true := by
  native_decide

/- Mutation sentinels keep metadata, pricing, framing, validation order,
   output shape, and independent answer literals observable. -/

def mutatedAddress : Nat := 17

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "BLS12_MAP_FP_TO_G1_WRONG"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedRegisteredAddress : Nat := 15

example : mutatedRegisteredAddress ≠ registeredAddress := by native_decide

def mutatedForkGate : Bool := false

example : mutatedForkGate ≠ eip2537Gated := by native_decide

def mutatedFixedGas : Nat := 5501

example : mutatedFixedGas ≠ totalGasCost schedule inputOne := by native_decide

def mutatedInputLength : Nat := 63

example : mutatedInputLength ≠ inputLength := by native_decide

def valueWithWrongOffset (input : List Byte) : List Byte :=
  input.take fpValueLength

example : valueWithWrongOffset inputOne ≠ valueOne.bytes := by native_decide

def runWithoutPaddingValidation (oracle : MapOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some decoded =>
      if !oracle.validField decoded.value then
        .error .invalidFieldElement
      else
        .ok (oracle.mapToG1 decoded.value)

example : resultEq
    (runWithoutPaddingValidation knownAnswerOracle (parsedBytes topBytesInputHex))
    (run knownAnswerOracle (parsedBytes topBytesInputHex)) = false := by
  native_decide

def rejectingOracle : MapOracle :=
  { knownAnswerOracle with validField := fun _ => false }

def topDecoded : DecodedInput :=
  { padding :=
      { bytes := parsedBytes "10000000000000000000000000000000"
        length_eq := by native_decide }
    value := valueOne }

def runFieldBeforePadding (oracle : MapOracle) (input : DecodedInput) : Result :=
  if !oracle.validField input.value then
    .error .invalidFieldElement
  else if !hasCanonicalPadding input then
    .error .invalidFieldElementTopBytes
  else
    .ok (oracle.mapToG1 input.value)

example : resultEq
    (runFieldBeforePadding rejectingOracle topDecoded)
    (runDecoded rejectingOracle topDecoded) = false := by
  native_decide

def invalidFieldDecoded : DecodedInput :=
  { padding := { bytes := zeroPadding, length_eq := by native_decide }
    value := invalidFieldValue }

def runWithoutFieldValidation (oracle : MapOracle) (input : DecodedInput) : Result :=
  if !hasCanonicalPadding input then
    .error .invalidFieldElementTopBytes
  else
    .ok (oracle.mapToG1 input.value)

example : resultEq
    (runWithoutFieldValidation knownAnswerOracle invalidFieldDecoded)
    (runDecoded knownAnswerOracle invalidFieldDecoded) = false := by
  native_decide

def encodeOutputWithoutYPadding (output : G1Output) : List Byte :=
  zeroPadding ++ output.x.bytes ++ output.y.bytes

example : (encodeOutputWithoutYPadding oracleOne).length ≠ outputLength := by
  native_decide

def encodeOutputSwapped (output : G1Output) : List Byte :=
  zeroPadding ++ output.y.bytes ++ zeroPadding ++ output.x.bytes

example : encodeOutputSwapped oracleOne ≠ encodeOutput oracleOne := by
  native_decide

def mutatedExpectedOne : G1Output :=
  { x :=
      { bytes := byte 0x01 :: expectedOne.x.bytes.drop 1
        length_eq := by native_decide }
    y := expectedOne.y }

example : mutatedExpectedOne ≠ expectedOne := by native_decide

def corruptedOracle : MapOracle :=
  { knownAnswerOracle with
    mapToG1 := fun value => if value == valueOne then mutatedExpectedOne else
      knownAnswerOracle.mapToG1 value }

example : resultEq (run corruptedOracle inputOne) (.ok expectedOne) = false := by
  native_decide

end Bls12381FpToG1Vectors
end Precompiles
end Eip803x
