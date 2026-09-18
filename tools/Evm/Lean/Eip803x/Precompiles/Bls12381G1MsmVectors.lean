-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381G1Msm
import Eip803x.Precompiles.Bls12381G2AddVectors

namespace Eip803x.Precompiles.Bls12381G1MsmVectors

open Evm.MemoryStackControl
open Bls12381G1Msm

-- Reuse the existing fail-closed fixture parser; this is not a production decoder.
abbrev parsedBytes := Bls12381G2AddVectors.parsedBytes

def schedule : Schedule := Schedule.amsterdam

def generatorHex : String := "0000000000000000000000000000000017f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb0000000000000000000000000000000008b3f481e3aaa0f1a09e30ed741d8ae4fcf5e095d5d00af600db18cb2c04b3edd03cc744a2888ae40caa232946c5e7e1"
def secondPointHex : String := "00000000000000000000000000000000112b98340eee2777cc3c14163dea3ec97977ac3dc5c70da32e6e87578f44912e902ccef9efe28d4a78b8999dfbca942600000000000000000000000000000000186b28d92356c4dfec4b5201ad099dbdede3781f8998ddf929b4cd7756192185ca7b8f4ef7088f813270ac3d48868a21"

def generator : EncodedPoint := ⟨parsedBytes generatorHex, by native_decide⟩
def secondPoint : EncodedPoint := ⟨parsedBytes secondPointHex, by native_decide⟩

def expectedDoubleHex : String := "000000000000000000000000000000000572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e00000000000000000000000000000000166a9d8cabc673a322fda673779d8e3822ba3ecb8670e461f73bb9021d5fd76a4c56d9d4cd16bd1bba86881979749d28"
def oracleDoubleHex : String := "000000000000000000000000000000000572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e00000000000000000000000000000000166a9d8cabc673a322fda673779d8e3822ba3ecb8670e461f73bb9021d5fd76a4c56d9d4cd16bd1bba86881979749d28"
def expectedRandomHex : String := "000000000000000000000000000000000491d1b0ecd9bb917989f0e74f0dea0422eac4a873e5e2644f368dffb9a6e20fd6e10c1b77654d067c0618f6e5a7f79a0000000000000000000000000000000017cd7061575d3e8034fcea62adaa1a3bc38dca4b50e4c5c01d04dd78037c9cee914e17944ea99e7ad84278e5d49f36c4"
def oracleRandomHex : String := "000000000000000000000000000000000491d1b0ecd9bb917989f0e74f0dea0422eac4a873e5e2644f368dffb9a6e20fd6e10c1b77654d067c0618f6e5a7f79a0000000000000000000000000000000017cd7061575d3e8034fcea62adaa1a3bc38dca4b50e4c5c01d04dd78037c9cee914e17944ea99e7ad84278e5d49f36c4"
def expectedSumHex : String := "00000000000000000000000000000000148f92dced907361b4782ab542a75281d4b6f71f65c8abf94a5a9082388c64662d30fd6a01ced724feef3e284752038c0000000000000000000000000000000015c3634c3b67bc18e19150e12bfd8a1769306ed010f59be645a0823acb5b38f39e8e0d86e59b6353fdafc59ca971b769"
def oracleSumHex : String := "00000000000000000000000000000000148f92dced907361b4782ab542a75281d4b6f71f65c8abf94a5a9082388c64662d30fd6a01ced724feef3e284752038c0000000000000000000000000000000015c3634c3b67bc18e19150e12bfd8a1769306ed010f59be645a0823acb5b38f39e8e0d86e59b6353fdafc59ca971b769"

def expectedDouble : EncodedPoint := ⟨parsedBytes expectedDoubleHex, by native_decide⟩
def oracleDouble : EncodedPoint := ⟨parsedBytes oracleDoubleHex, by native_decide⟩
def expectedRandom : EncodedPoint := ⟨parsedBytes expectedRandomHex, by native_decide⟩
def oracleRandom : EncodedPoint := ⟨parsedBytes oracleRandomHex, by native_decide⟩
def expectedSum : EncodedPoint := ⟨parsedBytes expectedSumHex, by native_decide⟩
def oracleSum : EncodedPoint := ⟨parsedBytes oracleSumHex, by native_decide⟩

def smallScalar (value : Byte) : Scalar :=
  ⟨List.replicate 31 zeroByte ++ [value], by native_decide +revert⟩

def zeroScalar : Scalar := smallScalar zeroByte
def oneScalar : Scalar := smallScalar (byte 1)
def twoScalar : Scalar := smallScalar (byte 2)
def maxScalar : Scalar := ⟨List.replicate 32 (byte 255), by native_decide⟩
def randomScalar : Scalar :=
  ⟨parsedBytes "263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3", by native_decide⟩
def unnormalizedScalar : Scalar :=
  ⟨parsedBytes "9a2b64cc58f8992cb21237914262ca9ada6cb13dc7b7d3f11c278fe0462040e4", by native_decide⟩

def invalidTop : EncodedPoint :=
  ⟨byte 0x10 :: generator.bytes.drop 1, by native_decide⟩
def invalidFieldHex : String := "0000000000000000000000000000000031f2e5916b17be2e71b10b4292f558e727dfd7d48af9cbc5087f0ce00dcca27c8b01e83eaace1aefb539f00adb2271660000000000000000000000000000000008b3f481e3aaa0f1a09e30ed741d8ae4fcf5e095d5d00af600db18cb2c04b3edd03cc744a2888ae40caa232946c5e7e1"
def invalidField : EncodedPoint := ⟨parsedBytes invalidFieldHex, by native_decide⟩
def invalidCurve : EncodedPoint :=
  ⟨generator.bytes.take 64 ++ secondPoint.bytes.drop 64, by native_decide⟩
def invalidSubgroupHex : String := "000000000000000000000000000000000123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00000000000000000000000000000000193fb7cedb32b2c3adc06ec11a96bc0d661869316f5e4a577a9f7c179593987beb4fb2ee424dbb2f5dd891e228b46c4a"
def invalidSubgroup : EncodedPoint := ⟨parsedBytes invalidSubgroupHex, by native_decide⟩

def pair (point : EncodedPoint) (scalar : Scalar) : Pair := ⟨point, scalar⟩
def encodePair (entry : Pair) : List Byte := entry.point.bytes ++ entry.scalar.bytes
def inputFor (pairs : List Pair) : List Byte := pairs.flatMap encodePair

def generatorOne : Pair := pair generator oneScalar
def generatorTwo : Pair := pair generator twoScalar
def generatorZero : Pair := pair generator zeroScalar
def infinityMax : Pair := pair infinity maxScalar

def knownMultiply (point : EncodedPoint) (scalar : Scalar) : EncodedPoint :=
  if point == generator then
    if scalar == oneScalar then generator
    else if scalar == twoScalar then oracleDouble
    else if scalar == randomScalar || scalar == unnormalizedScalar then oracleRandom
    else infinity
  else infinity

def knownMsm (pairs : List Pair) : EncodedPoint :=
  if pairs == [generatorTwo, pair secondPoint twoScalar] then oracleSum
  else if pairs == [generatorOne, generatorOne] || pairs == [generatorTwo, generatorZero] then oracleDouble
  else if pairs == [generatorZero, generatorZero] then infinity
  else match pairs with
    | [entry] => knownMultiply entry.point entry.scalar
    | _ => infinity

def knownAnswerOracle : MsmOracle :=
  { validPoint := fun point =>
      point == infinity || point == generator || point == secondPoint || point == invalidSubgroup
    inSubgroup := fun point => point != invalidSubgroup
    multiply := knownMultiply
    multiScalarMultiply := knownMsm }

def resultEq : Result → Result → Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => left.bytes == right.bytes
  | _, _ => false

structure Vector where
  name : String
  input : List Byte
  expected : Result
  expectedGas : Nat

def passes (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas

def vectors : List Vector :=
  [ ⟨"empty input", [], .error .invalidInputLength, 0⟩
  , ⟨"159 bytes floor to zero gas", List.replicate 159 zeroByte, .error .invalidInputLength, 0⟩
  , ⟨"161 bytes still price one pair", List.replicate 161 zeroByte, .error .invalidInputLength, 12000⟩
  , ⟨"319 bytes price one pair", List.replicate 319 zeroByte, .error .invalidInputLength, 12000⟩
  , ⟨"321 bytes price two pairs", List.replicate 321 zeroByte, .error .invalidInputLength, 22776⟩
  , ⟨"generator times one", inputFor [generatorOne], .ok generator, 12000⟩
  , ⟨"generator times two", inputFor [generatorTwo], .ok expectedDouble, 12000⟩
  , ⟨"valid zero scalar", inputFor [generatorZero], .ok infinity, 12000⟩
  , ⟨"infinity accepts full uint256 scalar", inputFor [infinityMax], .ok infinity, 12000⟩
  , ⟨"random scalar", inputFor [pair generator randomScalar], .ok expectedRandom, 12000⟩
  , ⟨"scalar above subgroup order", inputFor [pair generator unnormalizedScalar], .ok expectedRandom, 12000⟩
  , ⟨"top-byte violation", inputFor [pair invalidTop twoScalar], .error .invalidPoint, 12000⟩
  , ⟨"noncanonical field", inputFor [pair invalidField twoScalar], .error .invalidPoint, 12000⟩
  , ⟨"off-curve point", inputFor [pair invalidCurve twoScalar], .error .invalidPoint, 12000⟩
  , ⟨"wrong subgroup", inputFor [pair invalidSubgroup twoScalar], .error .invalidPoint, 12000⟩
  , ⟨"zero scalar does not bypass point validation", inputFor [pair invalidCurve zeroScalar], .error .invalidPoint, 12000⟩
  , ⟨"zero scalar does not bypass subgroup", inputFor [pair invalidSubgroup zeroScalar], .error .invalidPoint, 12000⟩
  , ⟨"two nontrivial terms", inputFor [generatorTwo, pair secondPoint twoScalar], .ok expectedSum, 22776⟩
  , ⟨"two generator terms", inputFor [generatorOne, generatorOne], .ok expectedDouble, 22776⟩
  , ⟨"finite zero scalar remains in MSM", inputFor [generatorTwo, generatorZero], .ok expectedDouble, 22776⟩
  , ⟨"two finite zero scalars", inputFor [generatorZero, generatorZero], .ok infinity, 22776⟩
  , ⟨"two raw infinity points", inputFor [infinityMax, infinityMax], .ok infinity, 22776⟩
  , ⟨"interleaved infinity compaction", inputFor [infinityMax, generatorTwo, infinityMax], .ok expectedDouble, 30528⟩
  , ⟨"invalid first MSM point", inputFor [pair invalidCurve zeroScalar, generatorTwo], .error .invalidPoint, 22776⟩
  , ⟨"invalid later MSM subgroup", inputFor [generatorTwo, pair invalidSubgroup zeroScalar], .error .invalidPoint, 22776⟩ ]

theorem vector_count : vectors.length = 25 := by native_decide
theorem all_vectors_pass : vectors.all passes = true := by native_decide
theorem independent_tables_agree :
    expectedDouble = oracleDouble ∧ expectedRandom = oracleRandom ∧ expectedSum = oracleSum := by
  native_decide

def gasVectors : List (Nat × Nat) :=
  [(0, 0), (1, 12000), (2, 22776), (3, 30528), (7, 61992),
    (127, 792480), (128, 797184), (129, 803412)]

theorem gas_boundary_vectors :
    gasVectors.all (fun (count, expected) => gasForLength schedule (160 * count) == expected) = true := by
  native_decide

example : scalarValue unnormalizedScalar >
    52435875175126190479447740508185965837690552500527637822603658699938581184513 := by native_decide
example : scalarValue maxScalar = 2 ^ 256 - 1 := by native_decide
example : decodeInput? (inputFor [generatorTwo, generatorOne]) = some [generatorTwo, generatorOne] := by native_decide
example : collectFinite knownAnswerOracle [infinityMax, generatorTwo, infinityMax] = some [generatorTwo] := by native_decide
example : collectFinite knownAnswerOracle [generatorZero] = some [generatorZero] := by native_decide

/- Adversarial alternatives are local sentinels, not a production mutation campaign. -/
example : (13 : Nat) ≠ address := by decide
example : "BLS12_G1MUL" ≠ name := by decide
example : false ≠ supportsCaching := by decide
example : (159 : Nat) ≠ itemLength := by decide
example : (12001 : Nat) ≠ gasForLength schedule 160 := by native_decide
example : 12000 * 2 * discount 1 / 1000 ≠ gasForLength schedule 320 := by native_decide
example : 12000 * 128 * 520 / 1000 ≠ gasForLength schedule 20480 := by native_decide
example : 12000 * 127 * 519 / 1000 ≠ gasForLength schedule 20320 := by native_decide
example : gasForLength schedule 320 ≠ gasForLength schedule 161 := by native_decide
example : gasForLength schedule 480 ≠ gasForLength schedule 160 := by native_decide

def zeroBeforeValidation (oracle : MsmOracle) (entry : Pair) : Result :=
  if scalarIsZero entry.scalar then .ok infinity else runSingle oracle entry

example : resultEq (zeroBeforeValidation knownAnswerOracle (pair invalidCurve zeroScalar))
    (runSingle knownAnswerOracle (pair invalidCurve zeroScalar)) = false := by native_decide

def withoutSubgroup : MsmOracle := { knownAnswerOracle with inSubgroup := fun _ => true }
example : resultEq (runSingle withoutSubgroup (pair invalidSubgroup zeroScalar))
    (runSingle knownAnswerOracle (pair invalidSubgroup zeroScalar)) = false := by native_decide

def boundScalarToSubgroup (oracle : MsmOracle) (entry : Pair) : Result :=
  if scalarValue entry.scalar ≥ 52435875175126190479447740508185965837690552500527637822603658699938581184513
  then .error .invalidPoint else runSingle oracle entry

example : resultEq (boundScalarToSubgroup knownAnswerOracle (pair generator unnormalizedScalar))
    (runSingle knownAnswerOracle (pair generator unnormalizedScalar)) = false := by native_decide

def reversedScalar (scalar : Scalar) : Scalar :=
  ⟨scalar.bytes.reverse, by simpa using scalar.length_eq⟩
example : resultEq (runSingle knownAnswerOracle (pair generator (reversedScalar twoScalar)))
    (runSingle knownAnswerOracle generatorTwo) = false := by native_decide

example : resultEq (.ok (knownMsm [infinityMax, generatorTwo, infinityMax]))
    (runMultiple knownAnswerOracle [infinityMax, generatorTwo, infinityMax]) = false := by native_decide

example : resultEq (runMultiple knownAnswerOracle ([generatorTwo, pair invalidSubgroup zeroScalar].filter
      (fun entry => !scalarIsZero entry.scalar)))
    (runMultiple knownAnswerOracle [generatorTwo, pair invalidSubgroup zeroScalar]) = false := by native_decide

def changedExpected : EncodedPoint :=
  ⟨byte 1 :: expectedDouble.bytes.drop 1, by native_decide⟩
def corruptedOracle : MsmOracle := { knownAnswerOracle with multiply := fun _ _ => generator }

theorem expected_literal_mutation_detected : changedExpected ≠ oracleDouble := by native_decide
theorem oracle_literal_mutation_detected :
    resultEq (runSingle corruptedOracle generatorTwo) (.ok expectedDouble) = false := by native_decide

end Eip803x.Precompiles.Bls12381G1MsmVectors
