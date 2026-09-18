-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.P256Verify

namespace Eip803x
namespace Precompiles
namespace P256VerifyVectors

open Evm.MemoryStackControl
open P256Verify

/-!
  The successful vector is copied from the EIP-7951 pinned test-vector asset.
  The oracle recognizes that exact tuple; this checks the reference's framing
  and result contract but is not an independent proof of P-256 arithmetic.
-/

def exactWord (bytes : List Byte) (length_eq : bytes.length = wordLength) : Word :=
  wordOfBytes bytes length_eq

def zeroWord : Word := exactWord (List.replicate 32 zeroByte) (by native_decide)

def oneWord : Word :=
  exactWord (List.replicate 31 zeroByte ++ [byte 0x01]) (by native_decide)

def message : Word :=
  exactWord
    [byte 0xbb, byte 0x5a, byte 0x52, byte 0xf4, byte 0x2f, byte 0x9c, byte 0x92, byte 0x61,
      byte 0xed, byte 0x43, byte 0x61, byte 0xf5, byte 0x94, byte 0x22, byte 0xa1, byte 0xe3,
      byte 0x00, byte 0x36, byte 0xe7, byte 0xc3, byte 0x2b, byte 0x27, byte 0x0c, byte 0x88,
      byte 0x07, byte 0xa4, byte 0x19, byte 0xfe, byte 0xca, byte 0x60, byte 0x50, byte 0x23]
    (by native_decide)

def signatureR : Word :=
  exactWord
    [byte 0x2b, byte 0xa3, byte 0xa8, byte 0xbe, byte 0x6b, byte 0x94, byte 0xd5, byte 0xec,
      byte 0x80, byte 0xa6, byte 0xd9, byte 0xd1, byte 0x19, byte 0x0a, byte 0x43, byte 0x6e,
      byte 0xff, byte 0xe5, byte 0x0d, byte 0x85, byte 0xa1, byte 0xee, byte 0xe8, byte 0x59,
      byte 0xb8, byte 0xcc, byte 0x6a, byte 0xf9, byte 0xbd, byte 0x5c, byte 0x2e, byte 0x18]
    (by native_decide)

def signatureS : Word :=
  exactWord
    [byte 0x4c, byte 0xd6, byte 0x0b, byte 0x85, byte 0x5d, byte 0x44, byte 0x2f, byte 0x5b,
      byte 0x3c, byte 0x7b, byte 0x11, byte 0xeb, byte 0x6c, byte 0x4e, byte 0x0a, byte 0xe7,
      byte 0x52, byte 0x5f, byte 0xe7, byte 0x10, byte 0xfa, byte 0xb9, byte 0xaa, byte 0x7c,
      byte 0x77, byte 0xa6, byte 0x7f, byte 0x79, byte 0xe6, byte 0xfa, byte 0xdd, byte 0x76]
    (by native_decide)

def publicKeyX : Word :=
  exactWord
    [byte 0x29, byte 0x27, byte 0xb1, byte 0x05, byte 0x12, byte 0xba, byte 0xe3, byte 0xed,
      byte 0xdc, byte 0xfe, byte 0x46, byte 0x78, byte 0x28, byte 0x12, byte 0x8b, byte 0xad,
      byte 0x29, byte 0x03, byte 0x26, byte 0x99, byte 0x19, byte 0xf7, byte 0x08, byte 0x60,
      byte 0x69, byte 0xc8, byte 0xc4, byte 0xdf, byte 0x6c, byte 0x73, byte 0x28, byte 0x38]
    (by native_decide)

def publicKeyY : Word :=
  exactWord
    [byte 0xc7, byte 0x78, byte 0x79, byte 0x64, byte 0xea, byte 0xac, byte 0x00, byte 0xe5,
      byte 0x92, byte 0x1f, byte 0xb1, byte 0x49, byte 0x8a, byte 0x60, byte 0xf4, byte 0x60,
      byte 0x67, byte 0x66, byte 0xb3, byte 0xd9, byte 0x68, byte 0x50, byte 0x01, byte 0x55,
      byte 0x8d, byte 0x1a, byte 0x97, byte 0x4e, byte 0x73, byte 0x41, byte 0x51, byte 0x3e]
    (by native_decide)

def subgroupOrderWord : Word :=
  exactWord
    [byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff,
      byte 0xbc, byte 0xe6, byte 0xfa, byte 0xad, byte 0xa7, byte 0x17, byte 0x9e, byte 0x84,
      byte 0xf3, byte 0xb9, byte 0xca, byte 0xc2, byte 0xfc, byte 0x63, byte 0x25, byte 0x51]
    (by native_decide)

def subgroupOrderMinusOneWord : Word :=
  exactWord
    [byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff,
      byte 0xbc, byte 0xe6, byte 0xfa, byte 0xad, byte 0xa7, byte 0x17, byte 0x9e, byte 0x84,
      byte 0xf3, byte 0xb9, byte 0xca, byte 0xc2, byte 0xfc, byte 0x63, byte 0x25, byte 0x50]
    (by native_decide)

def fieldModulusWord : Word :=
  exactWord
    [byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0x00, byte 0x00, byte 0x00, byte 0x01,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0xff, byte 0xff, byte 0xff, byte 0xff,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff]
    (by native_decide)

def fieldModulusMinusOneWord : Word :=
  exactWord
    [byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0x00, byte 0x00, byte 0x00, byte 0x01,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0x00,
      byte 0x00, byte 0x00, byte 0x00, byte 0x00, byte 0xff, byte 0xff, byte 0xff, byte 0xff,
      byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xfe]
    (by native_decide)

def inputFor (messageHash r s qx qy : Word) : List Byte :=
  messageHash.bytes ++ r.bytes ++ s.bytes ++ qx.bytes ++ qy.bytes

def validInput : List Byte := inputFor message signatureR signatureS publicKeyX publicKeyY

def knownAnswerOracle : CryptoOracle :=
  { isOnCurve := fun qx qy =>
      (qx.bytes == publicKeyX.bytes && qy.bytes == publicKeyY.bytes) ||
        (qx.bytes == zeroWord.bytes && qy.bytes == zeroWord.bytes)
    verifySignature := fun messageHash r s qx qy =>
      messageHash.bytes == message.bytes && r.bytes == signatureR.bytes &&
        s.bytes == signatureS.bytes && qx.bytes == publicKeyX.bytes &&
        qy.bytes == publicKeyY.bytes }

def rejectingOracle : CryptoOracle :=
  { isOnCurve := fun qx qy => qx.bytes == publicKeyX.bytes && qy.bytes == publicKeyY.bytes
    verifySignature := fun _ _ _ _ _ => false }

def schedule : Schedule := Schedule.amsterdam

def resultEq (left right : Result) : Bool :=
  left.success == right.success && left.output == right.output

structure Vector where
  name : String
  input : List Byte
  expected : Result
  expectedGas : Nat

def passes (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas

def vectors : List Vector :=
  [ { name := "empty input", input := [], expected := emptyResult, expectedGas := 6900 }
  , { name := "159-byte input", input := validInput.take 159,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "161-byte input", input := validInput ++ [byte 0x42],
      expected := emptyResult, expectedGas := 6900 }
  , { name := "pinned EIP known answer", input := validInput,
      expected := verifiedResult, expectedGas := 6900 }
  , { name := "zero r", input := inputFor message zeroWord signatureS publicKeyX publicKeyY,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "r equal to subgroup order",
      input := inputFor message subgroupOrderWord signatureS publicKeyX publicKeyY,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "zero s", input := inputFor message signatureR zeroWord publicKeyX publicKeyY,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "s equal to subgroup order",
      input := inputFor message signatureR subgroupOrderWord publicKeyX publicKeyY,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "x equal to field modulus",
      input := inputFor message signatureR signatureS fieldModulusWord publicKeyY,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "y equal to field modulus",
      input := inputFor message signatureR signatureS publicKeyX fieldModulusWord,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "point at infinity",
      input := inputFor message signatureR signatureS zeroWord zeroWord,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "off-curve point", input := inputFor message signatureR signatureS oneWord oneWord,
      expected := emptyResult, expectedGas := 6900 }
  , { name := "failed signature verification",
      input := inputFor oneWord signatureR signatureS publicKeyX publicKeyY,
      expected := emptyResult, expectedGas := 6900 } ]

theorem vector_count : vectors.length = 13 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

example : validInput.length = inputLength := by native_decide

example : normalizeInput validInput = validInput := by native_decide

example : normalizeInput (validInput.take 159) = [] := by native_decide

example : normalizeInput (validInput ++ [byte 0x42]) = [] := by native_decide

example : validScalar oneWord = true := by native_decide

example : validScalar subgroupOrderMinusOneWord = true := by native_decide

example : validScalar subgroupOrderWord = false := by native_decide

example : validFieldElement fieldModulusMinusOneWord = true := by native_decide

example : validFieldElement fieldModulusWord = false := by native_decide

example : successOutput.length = 32 := by native_decide

example : successOutput.take 31 = List.replicate 31 zeroByte := by native_decide

example : successOutput.drop 31 = [byte 0x01] := by native_decide

example : run knownAnswerOracle validInput = verifiedResult := by native_decide

example : run rejectingOracle validInput = emptyResult := by native_decide

/- Mutation sentinels constrain metadata, strict bounds, normalization, checks, and output. -/

def mutatedAddress : Nat := 0x101

example : mutatedAddress ≠ address := by native_decide

def mutatedFixedGas : Nat := 3450

example : mutatedFixedGas ≠ totalGasCost schedule validInput := by native_decide

def mutatedName : String := "P256"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedInputLength : Nat := 159

example : mutatedInputLength ≠ inputLength := by native_decide

def inclusiveScalar (word : Word) : Bool :=
  0 < wordValue word && wordValue word ≤ subgroupOrder

example : inclusiveScalar subgroupOrderWord ≠ validScalar subgroupOrderWord := by native_decide

def inclusiveFieldElement (word : Word) : Bool := wordValue word ≤ fieldModulus

example : inclusiveFieldElement fieldModulusWord ≠ validFieldElement fieldModulusWord := by
  native_decide

def paddedNormalization (input : List Byte) : List Byte :=
  input.take inputLength ++ List.replicate (inputLength - input.length) zeroByte

example : paddedNormalization (validInput.take 159) ≠ normalizeInput (validInput.take 159) := by
  native_decide

def truncatedNormalization (input : List Byte) : List Byte := input.take inputLength

example : truncatedNormalization (validInput ++ [byte 0x42]) ≠
    normalizeInput (validInput ++ [byte 0x42]) := by
  native_decide

def runDecodedWithoutCurveCheck (oracle : CryptoOracle) (input : DecodedInput) : Result :=
  if !validScalar input.r || !validScalar input.s ||
      !validFieldElement input.publicKeyX || !validFieldElement input.publicKeyY ||
      isInfinity input.publicKeyX input.publicKeyY then
    emptyResult
  else if oracle.verifySignature input.messageHash input.r input.s
      input.publicKeyX input.publicKeyY then
    verifiedResult
  else
    emptyResult

def permissiveOracle : CryptoOracle :=
  { isOnCurve := fun _ _ => false
    verifySignature := fun _ _ _ _ _ => true }

def offCurveDecoded : DecodedInput :=
  { messageHash := message, r := signatureR, s := signatureS,
    publicKeyX := oneWord, publicKeyY := oneWord }

example : runDecodedWithoutCurveCheck permissiveOracle offCurveDecoded ≠
    runDecoded permissiveOracle offCurveDecoded := by
  native_decide

def runDecodedWithoutInfinityCheck (oracle : CryptoOracle) (input : DecodedInput) : Result :=
  if !validScalar input.r || !validScalar input.s ||
      !validFieldElement input.publicKeyX || !validFieldElement input.publicKeyY ||
      !oracle.isOnCurve input.publicKeyX input.publicKeyY then
    emptyResult
  else if oracle.verifySignature input.messageHash input.r input.s
      input.publicKeyX input.publicKeyY then
    verifiedResult
  else
    emptyResult

def infinityAcceptingOracle : CryptoOracle :=
  { isOnCurve := fun _ _ => true
    verifySignature := fun _ _ _ _ _ => true }

def infinityDecoded : DecodedInput :=
  { messageHash := message, r := signatureR, s := signatureS,
    publicKeyX := zeroWord, publicKeyY := zeroWord }

example : runDecodedWithoutInfinityCheck infinityAcceptingOracle infinityDecoded ≠
    runDecoded infinityAcceptingOracle infinityDecoded := by
  native_decide

def mutatedSuccessOutput : List Byte := List.replicate 30 zeroByte ++ [byte 0x01, byte 0x00]

example : mutatedSuccessOutput ≠ successOutput := by native_decide

def mutatedFailureResult : Result := { success := false, output := [] }

example : mutatedFailureResult ≠ emptyResult := by native_decide

end P256VerifyVectors
end Precompiles
end Eip803x
