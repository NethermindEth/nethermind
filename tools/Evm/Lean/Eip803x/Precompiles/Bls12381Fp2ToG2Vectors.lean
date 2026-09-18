-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381Fp2ToG2

namespace Eip803x
namespace Precompiles
namespace Bls12381Fp2ToG2Vectors

open Evm.MemoryStackControl
open Bls12381Fp2ToG2

/-!
  The ten literals below are the five success and five failure cases from the
  pinned Nethermind BLS map-Fp2-to-G2 assets.  Parsing is deliberately
  fail-closed, so malformed or odd hexadecimal text is never silently padded.
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

structure ParsedFixture (text : String) where
  bytes : List Byte
  parse_eq : parseHex text = some bytes

def parseFixture (text : String)
    (hParse : parseHex text ≠ none) : ParsedFixture text :=
  match hBytes : parseHex text with
  | some bytes => { bytes := bytes, parse_eq := hBytes }
  | none => False.elim (hParse hBytes)

def firstValueFromRaw (raw : List Byte)
    (hLength : raw.length = inputLength) : FpValue :=
  let valueBytes := raw.take fpElementLength |>.drop fpPaddingLength
  have hValue : valueBytes.length = fpValueLength := by
    simp [valueBytes, List.length_take, List.length_drop, hLength, inputLength,
      fpElementLength, fpPaddingLength, fpValueLength]
  { bytes := valueBytes, length_eq := hValue }

def secondValueFromRaw (raw : List Byte)
    (hLength : raw.length = inputLength) : FpValue :=
  let valueBytes := raw.drop (fpElementLength + fpPaddingLength) |>.take fpValueLength
  have hValue : valueBytes.length = fpValueLength := by
    simp [valueBytes, List.length_take, List.length_drop, hLength, inputLength,
      fpElementLength, fpPaddingLength, fpValueLength]
  { bytes := valueBytes, length_eq := hValue }

def outputFromRaw (raw : List Byte)
    (hLength : raw.length = outputLength) : G2Output :=
  let wire0Bytes := raw.drop fpPaddingLength |>.take fpValueLength
  let wire1Bytes := raw.drop (fpElementLength + fpPaddingLength) |>.take fpValueLength
  let wire2Bytes := raw.drop (2 * fpElementLength + fpPaddingLength) |>.take fpValueLength
  let wire3Bytes := raw.drop (3 * fpElementLength + fpPaddingLength) |>.take fpValueLength
  have h0 : wire0Bytes.length = fpValueLength := by
    simp [wire0Bytes, List.length_take, List.length_drop, hLength, outputLength,
      fpPaddingLength, fpValueLength]
  have h1 : wire1Bytes.length = fpValueLength := by
    simp [wire1Bytes, List.length_take, List.length_drop, hLength, outputLength,
      fpElementLength, fpPaddingLength, fpValueLength]
  have h2 : wire2Bytes.length = fpValueLength := by
    simp [wire2Bytes, List.length_take, List.length_drop, hLength, outputLength,
      fpElementLength, fpPaddingLength, fpValueLength]
  have h3 : wire3Bytes.length = fpValueLength := by
    simp [wire3Bytes, List.length_take, List.length_drop, hLength, outputLength,
      fpElementLength, fpPaddingLength, fpValueLength]
  { wire0 := { bytes := wire0Bytes, length_eq := h0 }
    wire1 := { bytes := wire1Bytes, length_eq := h1 }
    wire2 := { bytes := wire2Bytes, length_eq := h2 }
    wire3 := { bytes := wire3Bytes, length_eq := h3 } }

def inputOneHex : String :=
  "0000000000000000000000000000000007355d25caf6e7f2f0cb2812ca0e513bd026ed09dda65b177500fa31714e09ea0ded3a078b526bed3307f804d4b93b040000000000000000000000000000000002829ce3c021339ccb5caf3e187f6370e1e2a311dec9b75363117063ab2015603ff52c3d3b98f19c2f65575e99e8b78c"

def inputTwoHex : String :=
  "00000000000000000000000000000000138879a9559e24cecee8697b8b4ad32cced053138ab913b99872772dc753a2967ed50aabc907937aefb2439ba06cc50c000000000000000000000000000000000a1ae7999ea9bab1dcc9ef8887a6cb6e8f1e22566015428d220b7eec90ffa70ad1f624018a9ad11e78d588bd3617f9f2"

def inputThreeHex : String :=
  "0000000000000000000000000000000018c16fe362b7dbdfa102e42bdfd3e2f4e6191d479437a59db4eb716986bf08ee1f42634db66bde97d6c16bbfd342b3b8000000000000000000000000000000000e37812ce1b146d998d5f92bdd5ada2a31bfd63dfe18311aa91637b5f279dd045763166aa1615e46a50d8d8f475f184e"

def inputFourHex : String :=
  "0000000000000000000000000000000008d4a0997b9d52fecf99427abb721f0fa779479963315fe21c6445250de7183e3f63bfdf86570da8929489e421d4ee950000000000000000000000000000000016cb4ccad91ec95aab070f22043916cd6a59c4ca94097f7f510043d48515526dc8eaaea27e586f09151ae613688d5a89"

def inputFiveHex : String :=
  "0000000000000000000000000000000003f80ce4ff0ca2f576d797a3660e3f65b274285c054feccc3215c879e2c0589d376e83ede13f93c32f05da0f68fd6a1000000000000000000000000000000000006488a837c5413746d868d1efb7232724da10eca410b07d8b505b9363bdccf0a1fc0029bad07d65b15ccfe6dd25e20d"

def expectedOneHex : String :=
  "0000000000000000000000000000000000e7f4568a82b4b7dc1f14c6aaa055edf51502319c723c4dc2688c7fe5944c213f510328082396515734b6612c4e7bb700000000000000000000000000000000126b855e9e69b1f691f816e48ac6977664d24d99f8724868a184186469ddfd4617367e94527d4b74fc86413483afb35b000000000000000000000000000000000caead0fd7b6176c01436833c79d305c78be307da5f6af6c133c47311def6ff1e0babf57a0fb5539fce7ee12407b0a42000000000000000000000000000000001498aadcf7ae2b345243e281ae076df6de84455d766ab6fcdaad71fab60abb2e8b980a440043cd305db09d283c895e3d"

def expectedTwoHex : String :=
  "00000000000000000000000000000000108ed59fd9fae381abfd1d6bce2fd2fa220990f0f837fa30e0f27914ed6e1454db0d1ee957b219f61da6ff8be0d6441f000000000000000000000000000000000296238ea82c6d4adb3c838ee3cb2346049c90b96d602d7bb1b469b905c9228be25c627bffee872def773d5b2a2eb57d00000000000000000000000000000000033f90f6057aadacae7963b0a0b379dd46750c1c94a6357c99b65f63b79e321ff50fe3053330911c56b6ceea08fee65600000000000000000000000000000000153606c417e59fb331b7ae6bce4fbf7c5190c33ce9402b5ebe2b70e44fca614f3f1382a3625ed5493843d0b0a652fc3f"

def expectedThreeHex : String :=
  "00000000000000000000000000000000038af300ef34c7759a6caaa4e69363cafeed218a1f207e93b2c70d91a1263d375d6730bd6b6509dcac3ba5b567e85bf3000000000000000000000000000000000da75be60fb6aa0e9e3143e40c42796edf15685cafe0279afd2a67c3dff1c82341f17effd402e4f1af240ea90f4b659b0000000000000000000000000000000019b148cbdf163cf0894f29660d2e7bfb2b68e37d54cc83fd4e6e62c020eaa48709302ef8e746736c0e19342cc1ce3df4000000000000000000000000000000000492f4fed741b073e5a82580f7c663f9b79e036b70ab3e51162359cec4e77c78086fe879b65ca7a47d34374c8315ac5e"

def expectedFourHex : String :=
  "000000000000000000000000000000000c5ae723be00e6c3f0efe184fdc0702b64588fe77dda152ab13099a3bacd3876767fa7bbad6d6fd90b3642e902b208f90000000000000000000000000000000012c8c05c1d5fc7bfa847f4d7d81e294e66b9a78bc9953990c358945e1f042eedafce608b67fdd3ab0cb2e6e263b9b1ad0000000000000000000000000000000004e77ddb3ede41b5ec4396b7421dd916efc68a358a0d7425bddd253547f2fb4830522358491827265dfc5bcc1928a5690000000000000000000000000000000011c624c56dbe154d759d021eec60fab3d8b852395a89de497e48504366feedd4662d023af447d66926a28076813dd646"

def expectedFiveHex : String :=
  "000000000000000000000000000000000ea4e7c33d43e17cc516a72f76437c4bf81d8f4eac69ac355d3bf9b71b8138d55dc10fd458be115afa798b55dac34be1000000000000000000000000000000001565c2f625032d232f13121d3cfb476f45275c303a037faa255f9da62000c2c864ea881e2bcddd111edc4a3c0da3e88d00000000000000000000000000000000043b6f5fe4e52c839148dc66f2b3751e69a0f6ebb3d056d6465d50d4108543ecd956e10fa1640dfd9bc0030cc2558d28000000000000000000000000000000000f8991d2a1ad662e7b6f58ab787947f1fa607fce12dde171bc17903b012091b657e15333e11701edcf5b63ba2a561247"

def oracleOneHex : String :=
  "0000000000000000000000000000000000e7f4568a82b4b7dc1f14c6aaa055edf51502319c723c4dc2688c7fe5944c213f510328082396515734b6612c4e7bb700000000000000000000000000000000126b855e9e69b1f691f816e48ac6977664d24d99f8724868a184186469ddfd4617367e94527d4b74fc86413483afb35b000000000000000000000000000000000caead0fd7b6176c01436833c79d305c78be307da5f6af6c133c47311def6ff1e0babf57a0fb5539fce7ee12407b0a42000000000000000000000000000000001498aadcf7ae2b345243e281ae076df6de84455d766ab6fcdaad71fab60abb2e8b980a440043cd305db09d283c895e3d"

def oracleTwoHex : String :=
  "00000000000000000000000000000000108ed59fd9fae381abfd1d6bce2fd2fa220990f0f837fa30e0f27914ed6e1454db0d1ee957b219f61da6ff8be0d6441f000000000000000000000000000000000296238ea82c6d4adb3c838ee3cb2346049c90b96d602d7bb1b469b905c9228be25c627bffee872def773d5b2a2eb57d00000000000000000000000000000000033f90f6057aadacae7963b0a0b379dd46750c1c94a6357c99b65f63b79e321ff50fe3053330911c56b6ceea08fee65600000000000000000000000000000000153606c417e59fb331b7ae6bce4fbf7c5190c33ce9402b5ebe2b70e44fca614f3f1382a3625ed5493843d0b0a652fc3f"

def oracleThreeHex : String :=
  "00000000000000000000000000000000038af300ef34c7759a6caaa4e69363cafeed218a1f207e93b2c70d91a1263d375d6730bd6b6509dcac3ba5b567e85bf3000000000000000000000000000000000da75be60fb6aa0e9e3143e40c42796edf15685cafe0279afd2a67c3dff1c82341f17effd402e4f1af240ea90f4b659b0000000000000000000000000000000019b148cbdf163cf0894f29660d2e7bfb2b68e37d54cc83fd4e6e62c020eaa48709302ef8e746736c0e19342cc1ce3df4000000000000000000000000000000000492f4fed741b073e5a82580f7c663f9b79e036b70ab3e51162359cec4e77c78086fe879b65ca7a47d34374c8315ac5e"

def oracleFourHex : String :=
  "000000000000000000000000000000000c5ae723be00e6c3f0efe184fdc0702b64588fe77dda152ab13099a3bacd3876767fa7bbad6d6fd90b3642e902b208f90000000000000000000000000000000012c8c05c1d5fc7bfa847f4d7d81e294e66b9a78bc9953990c358945e1f042eedafce608b67fdd3ab0cb2e6e263b9b1ad0000000000000000000000000000000004e77ddb3ede41b5ec4396b7421dd916efc68a358a0d7425bddd253547f2fb4830522358491827265dfc5bcc1928a5690000000000000000000000000000000011c624c56dbe154d759d021eec60fab3d8b852395a89de497e48504366feedd4662d023af447d66926a28076813dd646"

def oracleFiveHex : String :=
  "000000000000000000000000000000000ea4e7c33d43e17cc516a72f76437c4bf81d8f4eac69ac355d3bf9b71b8138d55dc10fd458be115afa798b55dac34be1000000000000000000000000000000001565c2f625032d232f13121d3cfb476f45275c303a037faa255f9da62000c2c864ea881e2bcddd111edc4a3c0da3e88d00000000000000000000000000000000043b6f5fe4e52c839148dc66f2b3751e69a0f6ebb3d056d6465d50d4108543ecd956e10fa1640dfd9bc0030cc2558d28000000000000000000000000000000000f8991d2a1ad662e7b6f58ab787947f1fa607fce12dde171bc17903b012091b657e15333e11701edcf5b63ba2a561247"

def emptyInputHex : String := ""

def shortInputHex : String :=
  "0000000000000000000000000000000007355d25caf6e7f2f0cb2812ca0e513bd026ed09dda65b177500fa31714e09ea0ded3a078b526bed3307f804d4b93b040000000000000000000000000000000002829ce3c021339ccb5caf3e187f6370e1e2a311dec9b75363117063ab2015603ff52c3d3b98f19c2f65575e99e8b7"

def longInputHex : String :=
  "000000000000000000000000000000000007355d25caf6e7f2f0cb2812ca0e513bd026ed09dda65b177500fa31714e09ea0ded3a078b526bed3307f804d4b93b040000000000000000000000000000000002829ce3c021339ccb5caf3e187f6370e1e2a311dec9b75363117063ab2015603ff52c3d3b98f19c2f65575e99e8b78c"

def topBytesInputHex : String :=
  "000000000000000000000000000000000007355d25caf6e7f2f0cb2812ca0e513bd026ed09dda65b177500fa31714e09ea0ded3a078b526bed3307f804d4b93b040000000000000000000000000000000002829ce3c021339ccb5caf3e187f6370e1e2a311dec9b75363117063ab2015603ff52c3d3b98f19c2f65575e99e8b7"

def invalidFieldInputHex : String :=
  "0000000000000000000000000000000021366f100476ce8d3be6cfc90d59fe13349e388ed12b6dd6dc31ccd267ff000e2c993a063ca66beced06f804d4b8e5af0000000000000000000000000000000002829ce3c021339ccb5caf3e187f6370e1e2a311dec9b75363117063ab2015603ff52c3d3b98f19c2f65575e99e8b78c"

def inputOneFixture : ParsedFixture inputOneHex :=
  parseFixture inputOneHex (by native_decide)

def inputTwoFixture : ParsedFixture inputTwoHex :=
  parseFixture inputTwoHex (by native_decide)

def inputThreeFixture : ParsedFixture inputThreeHex :=
  parseFixture inputThreeHex (by native_decide)

def inputFourFixture : ParsedFixture inputFourHex :=
  parseFixture inputFourHex (by native_decide)

def inputFiveFixture : ParsedFixture inputFiveHex :=
  parseFixture inputFiveHex (by native_decide)

def expectedOneFixture : ParsedFixture expectedOneHex :=
  parseFixture expectedOneHex (by native_decide)

def expectedTwoFixture : ParsedFixture expectedTwoHex :=
  parseFixture expectedTwoHex (by native_decide)

def expectedThreeFixture : ParsedFixture expectedThreeHex :=
  parseFixture expectedThreeHex (by native_decide)

def expectedFourFixture : ParsedFixture expectedFourHex :=
  parseFixture expectedFourHex (by native_decide)

def expectedFiveFixture : ParsedFixture expectedFiveHex :=
  parseFixture expectedFiveHex (by native_decide)

def oracleOneFixture : ParsedFixture oracleOneHex :=
  parseFixture oracleOneHex (by native_decide)

def oracleTwoFixture : ParsedFixture oracleTwoHex :=
  parseFixture oracleTwoHex (by native_decide)

def oracleThreeFixture : ParsedFixture oracleThreeHex :=
  parseFixture oracleThreeHex (by native_decide)

def oracleFourFixture : ParsedFixture oracleFourHex :=
  parseFixture oracleFourHex (by native_decide)

def oracleFiveFixture : ParsedFixture oracleFiveHex :=
  parseFixture oracleFiveHex (by native_decide)

def emptyInputFixture : ParsedFixture emptyInputHex :=
  parseFixture emptyInputHex (by native_decide)

def shortInputFixture : ParsedFixture shortInputHex :=
  parseFixture shortInputHex (by native_decide)

def longInputFixture : ParsedFixture longInputHex :=
  parseFixture longInputHex (by native_decide)

def topBytesInputFixture : ParsedFixture topBytesInputHex :=
  parseFixture topBytesInputHex (by native_decide)

def invalidFieldInputFixture : ParsedFixture invalidFieldInputHex :=
  parseFixture invalidFieldInputHex (by native_decide)

theorem inputOneFixture_parse_success :
    parseHex inputOneHex = some inputOneFixture.bytes := inputOneFixture.parse_eq

theorem inputTwoFixture_parse_success :
    parseHex inputTwoHex = some inputTwoFixture.bytes := inputTwoFixture.parse_eq

theorem inputThreeFixture_parse_success :
    parseHex inputThreeHex = some inputThreeFixture.bytes := inputThreeFixture.parse_eq

theorem inputFourFixture_parse_success :
    parseHex inputFourHex = some inputFourFixture.bytes := inputFourFixture.parse_eq

theorem inputFiveFixture_parse_success :
    parseHex inputFiveHex = some inputFiveFixture.bytes := inputFiveFixture.parse_eq

theorem expectedOneFixture_parse_success :
    parseHex expectedOneHex = some expectedOneFixture.bytes := expectedOneFixture.parse_eq

theorem expectedTwoFixture_parse_success :
    parseHex expectedTwoHex = some expectedTwoFixture.bytes := expectedTwoFixture.parse_eq

theorem expectedThreeFixture_parse_success :
    parseHex expectedThreeHex = some expectedThreeFixture.bytes := expectedThreeFixture.parse_eq

theorem expectedFourFixture_parse_success :
    parseHex expectedFourHex = some expectedFourFixture.bytes := expectedFourFixture.parse_eq

theorem expectedFiveFixture_parse_success :
    parseHex expectedFiveHex = some expectedFiveFixture.bytes := expectedFiveFixture.parse_eq

theorem oracleOneFixture_parse_success :
    parseHex oracleOneHex = some oracleOneFixture.bytes := oracleOneFixture.parse_eq

theorem oracleTwoFixture_parse_success :
    parseHex oracleTwoHex = some oracleTwoFixture.bytes := oracleTwoFixture.parse_eq

theorem oracleThreeFixture_parse_success :
    parseHex oracleThreeHex = some oracleThreeFixture.bytes := oracleThreeFixture.parse_eq

theorem oracleFourFixture_parse_success :
    parseHex oracleFourHex = some oracleFourFixture.bytes := oracleFourFixture.parse_eq

theorem oracleFiveFixture_parse_success :
    parseHex oracleFiveHex = some oracleFiveFixture.bytes := oracleFiveFixture.parse_eq

theorem emptyInputFixture_parse_success :
    parseHex emptyInputHex = some emptyInputFixture.bytes := emptyInputFixture.parse_eq

theorem emptyInputFixture_parse_empty : parseHex emptyInputHex = some [] := by
  native_decide

theorem shortInputFixture_parse_success :
    parseHex shortInputHex = some shortInputFixture.bytes := shortInputFixture.parse_eq

theorem longInputFixture_parse_success :
    parseHex longInputHex = some longInputFixture.bytes := longInputFixture.parse_eq

theorem topBytesInputFixture_parse_success :
    parseHex topBytesInputHex = some topBytesInputFixture.bytes := topBytesInputFixture.parse_eq

theorem invalidFieldInputFixture_parse_success :
    parseHex invalidFieldInputHex = some invalidFieldInputFixture.bytes :=
  invalidFieldInputFixture.parse_eq

def inputOne : List Byte := inputOneFixture.bytes
def inputTwo : List Byte := inputTwoFixture.bytes
def inputThree : List Byte := inputThreeFixture.bytes
def inputFour : List Byte := inputFourFixture.bytes
def inputFive : List Byte := inputFiveFixture.bytes

def expectedOne : G2Output := outputFromRaw expectedOneFixture.bytes (by native_decide)
def expectedTwo : G2Output := outputFromRaw expectedTwoFixture.bytes (by native_decide)
def expectedThree : G2Output := outputFromRaw expectedThreeFixture.bytes (by native_decide)
def expectedFour : G2Output := outputFromRaw expectedFourFixture.bytes (by native_decide)
def expectedFive : G2Output := outputFromRaw expectedFiveFixture.bytes (by native_decide)

def oracleOne : G2Output := outputFromRaw oracleOneFixture.bytes (by native_decide)
def oracleTwo : G2Output := outputFromRaw oracleTwoFixture.bytes (by native_decide)
def oracleThree : G2Output := outputFromRaw oracleThreeFixture.bytes (by native_decide)
def oracleFour : G2Output := outputFromRaw oracleFourFixture.bytes (by native_decide)
def oracleFive : G2Output := outputFromRaw oracleFiveFixture.bytes (by native_decide)

def firstOne : FpValue := firstValueFromRaw inputOne (by native_decide)
def secondOne : FpValue := secondValueFromRaw inputOne (by native_decide)
def firstTwo : FpValue := firstValueFromRaw inputTwo (by native_decide)
def secondTwo : FpValue := secondValueFromRaw inputTwo (by native_decide)
def firstThree : FpValue := firstValueFromRaw inputThree (by native_decide)
def secondThree : FpValue := secondValueFromRaw inputThree (by native_decide)
def firstFour : FpValue := firstValueFromRaw inputFour (by native_decide)
def secondFour : FpValue := secondValueFromRaw inputFour (by native_decide)
def firstFive : FpValue := firstValueFromRaw inputFive (by native_decide)
def secondFive : FpValue := secondValueFromRaw inputFive (by native_decide)

def invalidFieldFirst : FpValue :=
  firstValueFromRaw invalidFieldInputFixture.bytes (by native_decide)

def topBytesFirst : FpValue :=
  firstValueFromRaw topBytesInputFixture.bytes (by native_decide)

def knownAnswerOracle : MapOracle :=
  { validField := fun value =>
      value == firstOne || value == secondOne || value == firstTwo || value == secondTwo ||
        value == firstThree || value == secondThree || value == firstFour || value == secondFour ||
        value == firstFive || value == secondFive || value == topBytesFirst
    mapFp2ToG2 := fun first second =>
      if first == firstOne && second == secondOne then oracleOne
      else if first == firstTwo && second == secondTwo then oracleTwo
      else if first == firstThree && second == secondThree then oracleThree
      else if first == firstFour && second == secondFour then oracleFour
      else if first == firstFive && second == secondFive then oracleFive
      else oracleOne }

structure SuccessAssetBinding where
  name : String
  inputHex : String
  input : List Byte
  inputParse : parseHex inputHex = some input
  outputHex : String
  output : List Byte
  outputParse : parseHex outputHex = some output
  gas : Nat

structure FailureAssetBinding where
  name : String
  inputHex : String
  input : List Byte
  inputParse : parseHex inputHex = some input
  expectedErrorText : String
  expectedError : Error

def successAssetOne : SuccessAssetBinding :=
  { name := "bls_g2map_", inputHex := inputOneHex, input := inputOneFixture.bytes,
    inputParse := inputOneFixture_parse_success, outputHex := expectedOneHex,
    output := expectedOneFixture.bytes, outputParse := expectedOneFixture_parse_success, gas := 23800 }

def successAssetTwo : SuccessAssetBinding :=
  { name := "bls_g2map_616263", inputHex := inputTwoHex, input := inputTwoFixture.bytes,
    inputParse := inputTwoFixture_parse_success, outputHex := expectedTwoHex,
    output := expectedTwoFixture.bytes, outputParse := expectedTwoFixture_parse_success, gas := 23800 }

def successAssetThree : SuccessAssetBinding :=
  { name := "bls_g2map_6162636465663031", inputHex := inputThreeHex, input := inputThreeFixture.bytes,
    inputParse := inputThreeFixture_parse_success, outputHex := expectedThreeHex,
    output := expectedThreeFixture.bytes, outputParse := expectedThreeFixture_parse_success, gas := 23800 }

def successAssetFour : SuccessAssetBinding :=
  { name := "bls_g2map_713132385f717171", inputHex := inputFourHex, input := inputFourFixture.bytes,
    inputParse := inputFourFixture_parse_success, outputHex := expectedFourHex,
    output := expectedFourFixture.bytes, outputParse := expectedFourFixture_parse_success, gas := 23800 }

def successAssetFive : SuccessAssetBinding :=
  { name := "bls_g2map_613531325f616161", inputHex := inputFiveHex, input := inputFiveFixture.bytes,
    inputParse := inputFiveFixture_parse_success, outputHex := expectedFiveHex,
    output := expectedFiveFixture.bytes, outputParse := expectedFiveFixture_parse_success, gas := 23800 }

def failureAssetEmpty : FailureAssetBinding :=
  { name := "bls_mapg2_empty_input", inputHex := emptyInputHex, input := emptyInputFixture.bytes,
    inputParse := emptyInputFixture_parse_success, expectedErrorText := "invalid input length",
    expectedError := .invalidInputLength }

def failureAssetShort : FailureAssetBinding :=
  { name := "bls_mapg2_short_input", inputHex := shortInputHex, input := shortInputFixture.bytes,
    inputParse := shortInputFixture_parse_success, expectedErrorText := "invalid input length",
    expectedError := .invalidInputLength }

def failureAssetLong : FailureAssetBinding :=
  { name := "bls_mapg2_long_input", inputHex := longInputHex, input := longInputFixture.bytes,
    inputParse := longInputFixture_parse_success, expectedErrorText := "invalid input length",
    expectedError := .invalidInputLength }

def failureAssetTopBytes : FailureAssetBinding :=
  { name := "bls_mapg2_top_bytes", inputHex := topBytesInputHex, input := topBytesInputFixture.bytes,
    inputParse := topBytesInputFixture_parse_success,
    expectedErrorText := "invalid field element top bytes", expectedError := .invalidFieldElementTopBytes }

def failureAssetInvalidField : FailureAssetBinding :=
  { name := "bls_mapg2_invalid_fq_element", inputHex := invalidFieldInputHex,
    input := invalidFieldInputFixture.bytes, inputParse := invalidFieldInputFixture_parse_success,
    expectedErrorText := "invalid fp.Element encoding", expectedError := .invalidFieldElement }

def vendoredSuccessAssets : List SuccessAssetBinding :=
  [successAssetOne, successAssetTwo, successAssetThree, successAssetFour, successAssetFive]

def vendoredFailureAssets : List FailureAssetBinding :=
  [failureAssetEmpty, failureAssetShort, failureAssetLong, failureAssetTopBytes, failureAssetInvalidField]

theorem vendored_success_asset_count : vendoredSuccessAssets.length = 5 := by
  native_decide

theorem vendored_success_name_binding :
    vendoredSuccessAssets.map (fun asset => asset.name) =
      ["bls_g2map_", "bls_g2map_616263", "bls_g2map_6162636465663031",
        "bls_g2map_713132385f717171", "bls_g2map_613531325f616161"] := by
  native_decide

theorem vendored_success_input_binding :
    vendoredSuccessAssets.map (fun asset => asset.input) =
      [inputOneFixture.bytes, inputTwoFixture.bytes, inputThreeFixture.bytes,
        inputFourFixture.bytes, inputFiveFixture.bytes] := by
  native_decide

theorem vendored_success_output_binding :
    vendoredSuccessAssets.map (fun asset => asset.output) =
      [expectedOneFixture.bytes, expectedTwoFixture.bytes, expectedThreeFixture.bytes,
        expectedFourFixture.bytes, expectedFiveFixture.bytes] := by
  native_decide

theorem vendored_success_hex_binding :
    vendoredSuccessAssets.map (fun asset => asset.inputHex) =
      [inputOneHex, inputTwoHex, inputThreeHex, inputFourHex, inputFiveHex] ∧
      vendoredSuccessAssets.map (fun asset => asset.outputHex) =
        [expectedOneHex, expectedTwoHex, expectedThreeHex, expectedFourHex, expectedFiveHex] := by
  native_decide

theorem vendored_failure_asset_count : vendoredFailureAssets.length = 5 := by
  native_decide

theorem vendored_failure_name_binding :
    vendoredFailureAssets.map (fun asset => asset.name) =
      ["bls_mapg2_empty_input", "bls_mapg2_short_input", "bls_mapg2_long_input",
        "bls_mapg2_top_bytes", "bls_mapg2_invalid_fq_element"] := by
  native_decide

theorem vendored_failure_input_binding :
    vendoredFailureAssets.map (fun asset => asset.input) =
      [emptyInputFixture.bytes, shortInputFixture.bytes, longInputFixture.bytes,
        topBytesInputFixture.bytes, invalidFieldInputFixture.bytes] := by
  native_decide

theorem vendored_failure_hex_binding :
    vendoredFailureAssets.map (fun asset => asset.inputHex) =
      [emptyInputHex, shortInputHex, longInputHex, topBytesInputHex, invalidFieldInputHex] := by
  native_decide

theorem vendored_failure_error_binding :
    vendoredFailureAssets.map (fun asset => asset.expectedError) =
      [.invalidInputLength, .invalidInputLength, .invalidInputLength,
        .invalidFieldElementTopBytes, .invalidFieldElement] := by
  native_decide

theorem vendored_failure_error_text_binding :
    vendoredFailureAssets.map (fun asset => asset.expectedErrorText) =
      ["invalid input length", "invalid input length", "invalid input length",
        "invalid field element top bytes", "invalid fp.Element encoding"] := by
  native_decide

def resultEq : Result -> Result -> Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => encodeOutput left == encodeOutput right
  | _, _ => false

def schedule : Schedule := Schedule.amsterdam

structure Vector where
  name : String
  input : List Byte
  oracle : Result
  expected : Result
  expectedGas : Nat

def runMatchesOracle (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.oracle

def oracleMatchesExpected (vector : Vector) : Bool :=
  resultEq vector.oracle vector.expected

def gasMatches (vector : Vector) : Bool :=
  totalGasCost schedule vector.input == vector.expectedGas

def passes (vector : Vector) : Bool :=
  runMatchesOracle vector && oracleMatchesExpected vector && gasMatches vector

def vectors : List Vector :=
  [ { name := successAssetOne.name, input := successAssetOne.input,
      oracle := .ok oracleOne, expected := .ok expectedOne, expectedGas := successAssetOne.gas }
  , { name := successAssetTwo.name, input := successAssetTwo.input,
      oracle := .ok oracleTwo, expected := .ok expectedTwo, expectedGas := successAssetTwo.gas }
  , { name := successAssetThree.name, input := successAssetThree.input,
      oracle := .ok oracleThree, expected := .ok expectedThree, expectedGas := successAssetThree.gas }
  , { name := successAssetFour.name, input := successAssetFour.input,
      oracle := .ok oracleFour, expected := .ok expectedFour, expectedGas := successAssetFour.gas }
  , { name := successAssetFive.name, input := successAssetFive.input,
      oracle := .ok oracleFive, expected := .ok expectedFive, expectedGas := successAssetFive.gas }
  , { name := failureAssetEmpty.name, input := failureAssetEmpty.input,
      oracle := .error failureAssetEmpty.expectedError, expected := .error failureAssetEmpty.expectedError,
      expectedGas := 23800 }
  , { name := failureAssetShort.name, input := failureAssetShort.input,
      oracle := .error failureAssetShort.expectedError, expected := .error failureAssetShort.expectedError,
      expectedGas := 23800 }
  , { name := failureAssetLong.name, input := failureAssetLong.input,
      oracle := .error failureAssetLong.expectedError, expected := .error failureAssetLong.expectedError,
      expectedGas := 23800 }
  , { name := failureAssetTopBytes.name, input := failureAssetTopBytes.input,
      oracle := .error failureAssetTopBytes.expectedError, expected := .error failureAssetTopBytes.expectedError,
      expectedGas := 23800 }
  , { name := failureAssetInvalidField.name, input := failureAssetInvalidField.input,
      oracle := .error failureAssetInvalidField.expectedError,
      expected := .error failureAssetInvalidField.expectedError, expectedGas := 23800 } ]

theorem vector_count : vectors.length = 10 := by
  native_decide

theorem all_runs_match_oracles : vectors.all runMatchesOracle = true := by
  native_decide

theorem all_oracles_match_expected : vectors.all oracleMatchesExpected = true := by
  native_decide

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem independent_oracle_output_literals_match_expected :
    oracleOne = expectedOne && oracleTwo = expectedTwo && oracleThree = expectedThree &&
      oracleFour = expectedFour && oracleFive = expectedFive := by
  native_decide

theorem input_asset_lengths :
    inputOneFixture.bytes.length = inputLength && inputTwoFixture.bytes.length = inputLength &&
      inputThreeFixture.bytes.length = inputLength && inputFourFixture.bytes.length = inputLength &&
      inputFiveFixture.bytes.length = inputLength && emptyInputFixture.bytes.length = 0 &&
      shortInputFixture.bytes.length = 127 && longInputFixture.bytes.length = 129 &&
      topBytesInputFixture.bytes.length = inputLength &&
      invalidFieldInputFixture.bytes.length = inputLength := by
  native_decide

theorem output_asset_lengths :
    expectedOneFixture.bytes.length = outputLength && expectedTwoFixture.bytes.length = outputLength &&
      expectedThreeFixture.bytes.length = outputLength && expectedFourFixture.bytes.length = outputLength &&
      expectedFiveFixture.bytes.length = outputLength && oracleOneFixture.bytes.length = outputLength &&
      oracleTwoFixture.bytes.length = outputLength && oracleThreeFixture.bytes.length = outputLength &&
      oracleFourFixture.bytes.length = outputLength && oracleFiveFixture.bytes.length = outputLength := by
  native_decide

theorem encoded_expected_literals :
    encodeOutput expectedOne == expectedOneFixture.bytes &&
      encodeOutput expectedTwo == expectedTwoFixture.bytes &&
      encodeOutput expectedThree == expectedThreeFixture.bytes &&
      encodeOutput expectedFour == expectedFourFixture.bytes &&
      encodeOutput expectedFive == expectedFiveFixture.bytes := by
  native_decide

theorem encoded_oracle_literals :
    encodeOutput oracleOne == oracleOneFixture.bytes &&
      encodeOutput oracleTwo == oracleTwoFixture.bytes &&
      encodeOutput oracleThree == oracleThreeFixture.bytes &&
      encodeOutput oracleFour == oracleFourFixture.bytes &&
      encodeOutput oracleFive == oracleFiveFixture.bytes := by
  native_decide

example : parseHex "0" = none := by native_decide

example : parseHex "zz" = none := by native_decide

example : resultEq (run knownAnswerOracle successAssetOne.input) (.ok oracleOne) = true := by native_decide

example : resultEq (.ok oracleOne) (.ok expectedOne) = true := by native_decide

example : resultEq (run knownAnswerOracle successAssetTwo.input) (.ok oracleTwo) = true := by native_decide

example : resultEq (.ok oracleTwo) (.ok expectedTwo) = true := by native_decide

example : resultEq (run knownAnswerOracle successAssetThree.input) (.ok oracleThree) = true := by native_decide

example : resultEq (.ok oracleThree) (.ok expectedThree) = true := by native_decide

example : resultEq (run knownAnswerOracle successAssetFour.input) (.ok oracleFour) = true := by native_decide

example : resultEq (.ok oracleFour) (.ok expectedFour) = true := by native_decide

example : resultEq (run knownAnswerOracle successAssetFive.input) (.ok oracleFive) = true := by native_decide

example : resultEq (.ok oracleFive) (.ok expectedFive) = true := by native_decide

example : resultEq (run knownAnswerOracle failureAssetEmpty.input) (.error .invalidInputLength) = true := by
  native_decide

example : resultEq (run knownAnswerOracle failureAssetShort.input) (.error .invalidInputLength) = true := by
  native_decide

example : resultEq (run knownAnswerOracle failureAssetLong.input) (.error .invalidInputLength) = true := by
  native_decide

example : resultEq (run knownAnswerOracle failureAssetTopBytes.input)
    (.error .invalidFieldElementTopBytes) = true := by
  native_decide

example : resultEq (run knownAnswerOracle failureAssetInvalidField.input)
    (.error .invalidFieldElement) = true := by
  native_decide

/- Mutation sentinels retain metadata, pricing, field order, framing, and the
   independently encoded outputs as observable parts of the candidate. -/

def metadataWithWrongAddress : Metadata := { metadata with address := 18 }

example : metadataMatchesRegistration metadataWithWrongAddress = false := by native_decide

def metadataWithWrongName : Metadata := { metadata with name := "BLS12_MAP_FP2_TO_G2_WRONG" }

example : metadataMatchesRegistration metadataWithWrongName = false := by native_decide

def metadataWithCachingDisabled : Metadata := { metadata with supportsCaching := false }

example : metadataMatchesRegistration metadataWithCachingDisabled = false := by native_decide

def metadataWithWrongRegisteredAddress : Metadata := { metadata with registeredAddress := 16 }

example : metadataMatchesRegistration metadataWithWrongRegisteredAddress = false := by native_decide

def metadataWithWrongRegisteredName : Metadata := { metadata with registeredName := "WRONG" }

example : metadataMatchesRegistration metadataWithWrongRegisteredName = false := by native_decide

def metadataWithWrongCachedAddress : Metadata := { metadata with cachedAddress := 18 }

example : metadataMatchesRegistration metadataWithWrongCachedAddress = false := by native_decide

def metadataWithDisabledForkGate : Metadata := { metadata with eip2537Gated := false }

example : metadataMatchesRegistration metadataWithDisabledForkGate = false := by native_decide

def mutatedFixedGas : Nat := 23801

example : mutatedFixedGas != totalGasCost schedule inputOne := by native_decide

def mutatedDataSchedule : Schedule := { fixedGas := 23800, dataGas := 1 }

example : totalGasCost mutatedDataSchedule inputOne != totalGasCost schedule inputOne := by
  native_decide

def firstValueWithWrongOffset (input : List Byte) : List Byte :=
  input.take fpValueLength

example : firstValueWithWrongOffset inputOne != firstOne.bytes := by native_decide

def runWithoutPaddingValidation (oracle : MapOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some decoded =>
      if !oracle.validField decoded.first.value then
        .error .invalidFieldElement
      else if !oracle.validField decoded.second.value then
        .error .invalidFieldElement
      else
        .ok (oracle.mapFp2ToG2 decoded.first.value decoded.second.value)

example : resultEq
    (runWithoutPaddingValidation knownAnswerOracle topBytesInputFixture.bytes)
    (run knownAnswerOracle topBytesInputFixture.bytes) = false := by
  native_decide

def rejectingOracle : MapOracle :=
  { knownAnswerOracle with validField := fun _ => false }

def topPadding : Padding :=
  { bytes := byte 0x10 :: List.replicate 15 zeroByte, length_eq := by native_decide }

def topDecoded : Fp2Input :=
  { first := { padding := topPadding, value := firstOne }
    second := { padding := { bytes := zeroPadding, length_eq := by native_decide }, value := secondOne } }

def runFieldBeforePadding (oracle : MapOracle) (input : Fp2Input) : Result :=
  if !oracle.validField input.first.value then
    .error .invalidFieldElement
  else if !hasCanonicalPadding input.first then
    .error .invalidFieldElementTopBytes
  else if !oracle.validField input.second.value then
    .error .invalidFieldElement
  else if !hasCanonicalPadding input.second then
    .error .invalidFieldElementTopBytes
  else
    .ok (oracle.mapFp2ToG2 input.first.value input.second.value)

example : resultEq (runFieldBeforePadding rejectingOracle topDecoded)
    (runDecoded rejectingOracle topDecoded) = false := by
  native_decide

def secondTopDecoded : Fp2Input :=
  { first := { padding := { bytes := zeroPadding, length_eq := by native_decide }, value := firstOne }
    second := { padding := topPadding, value := secondOne } }

def firstFieldInvalidSecondPaddingInvalid : Fp2Input :=
  { first :=
      { padding := { bytes := zeroPadding, length_eq := by native_decide }
        value := invalidFieldFirst }
    second := { padding := topPadding, value := secondOne } }

theorem first_field_precedes_second_padding :
    resultEq (runDecoded knownAnswerOracle firstFieldInvalidSecondPaddingInvalid)
      (.error .invalidFieldElement) = true := by
  native_decide

def secondPaddingInvalidSecondFieldInvalid : Fp2Input :=
  { first :=
      { padding := { bytes := zeroPadding, length_eq := by native_decide }
        value := firstOne }
    second := { padding := topPadding, value := invalidFieldFirst } }

theorem second_padding_precedes_second_field :
    resultEq (runDecoded knownAnswerOracle secondPaddingInvalidSecondFieldInvalid)
      (.error .invalidFieldElementTopBytes) = true := by
  native_decide

def runWithoutSecondPaddingValidation (oracle : MapOracle) (input : Fp2Input) : Result :=
  if !hasCanonicalPadding input.first then
    .error .invalidFieldElementTopBytes
  else if !oracle.validField input.first.value then
    .error .invalidFieldElement
  else if !oracle.validField input.second.value then
    .error .invalidFieldElement
  else
    .ok (oracle.mapFp2ToG2 input.first.value input.second.value)

example : resultEq (runWithoutSecondPaddingValidation knownAnswerOracle secondTopDecoded)
    (runDecoded knownAnswerOracle secondTopDecoded) = false := by
  native_decide

def invalidFieldDecoded : Fp2Input :=
  { first := { padding := { bytes := zeroPadding, length_eq := by native_decide }, value := invalidFieldFirst }
    second := { padding := { bytes := zeroPadding, length_eq := by native_decide }, value := secondOne } }

def runWithoutFieldValidation (oracle : MapOracle) (input : Fp2Input) : Result :=
  if !hasCanonicalPadding input.first then
    .error .invalidFieldElementTopBytes
  else if !hasCanonicalPadding input.second then
    .error .invalidFieldElementTopBytes
  else
    .ok (oracle.mapFp2ToG2 input.first.value input.second.value)

example : resultEq (runWithoutFieldValidation knownAnswerOracle invalidFieldDecoded)
    (runDecoded knownAnswerOracle invalidFieldDecoded) = false := by
  native_decide

def runWithReversedFp2Order (oracle : MapOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some decoded =>
      if !hasCanonicalPadding decoded.first then
        .error .invalidFieldElementTopBytes
      else if !oracle.validField decoded.first.value then
        .error .invalidFieldElement
      else if !hasCanonicalPadding decoded.second then
        .error .invalidFieldElementTopBytes
      else if !oracle.validField decoded.second.value then
        .error .invalidFieldElement
      else
        .ok (oracle.mapFp2ToG2 decoded.second.value decoded.first.value)

example : resultEq (runWithReversedFp2Order knownAnswerOracle inputTwo) (.ok expectedTwo) = false := by
  native_decide

def encodeOutputWithoutSlotPadding (output : G2Output) : List Byte :=
  zeroPadding ++ output.wire0.bytes ++ output.wire1.bytes ++ output.wire2.bytes ++ output.wire3.bytes

example : (encodeOutputWithoutSlotPadding oracleOne).length != outputLength := by native_decide

def encodeOutputWithSwappedWires (output : G2Output) : List Byte :=
  encodeFpElement output.wire1 ++ encodeFpElement output.wire0 ++
    encodeFpElement output.wire2 ++ encodeFpElement output.wire3

example : encodeOutputWithSwappedWires oracleOne != encodeOutput oracleOne := by native_decide

def mutatedOracleOne : G2Output :=
  { wire0 :=
      { bytes := byte 0x01 :: oracleOne.wire0.bytes.drop 1
        length_eq := by native_decide }
    wire1 := oracleOne.wire1
    wire2 := oracleOne.wire2
    wire3 := oracleOne.wire3 }

def mutatedExpectedOne : G2Output :=
  { wire0 :=
      { bytes := byte 0x02 :: expectedOne.wire0.bytes.drop 1
        length_eq := by native_decide }
    wire1 := expectedOne.wire1
    wire2 := expectedOne.wire2
    wire3 := expectedOne.wire3 }

example : mutatedOracleOne != oracleOne := by native_decide

example : mutatedExpectedOne != expectedOne := by native_decide

def oracleOnlyMutationVector : Vector :=
  { name := successAssetOne.name, input := successAssetOne.input,
    oracle := .ok mutatedOracleOne, expected := .ok expectedOne, expectedGas := successAssetOne.gas }

example : runMatchesOracle oracleOnlyMutationVector = false := by native_decide

example : oracleMatchesExpected oracleOnlyMutationVector = false := by native_decide

example : passes oracleOnlyMutationVector = false := by native_decide

def expectedOnlyMutationVector : Vector :=
  { name := successAssetOne.name, input := successAssetOne.input,
    oracle := .ok oracleOne, expected := .ok mutatedExpectedOne, expectedGas := successAssetOne.gas }

example : runMatchesOracle expectedOnlyMutationVector = true := by native_decide

example : oracleMatchesExpected expectedOnlyMutationVector = false := by native_decide

example : passes expectedOnlyMutationVector = false := by native_decide

def corruptedOracle : MapOracle :=
  { knownAnswerOracle with
    mapFp2ToG2 := fun first second =>
      if first == firstOne && second == secondOne then mutatedOracleOne
      else knownAnswerOracle.mapFp2ToG2 first second }

example : resultEq (run corruptedOracle inputOne) (.ok expectedOne) = false := by native_decide

end Bls12381Fp2ToG2Vectors
end Precompiles
end Eip803x
