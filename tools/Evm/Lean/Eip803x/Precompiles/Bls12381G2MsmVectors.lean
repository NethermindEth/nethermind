-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381G2Msm
import Eip803x.Precompiles.Bls12381G2AddVectors

namespace Eip803x.Precompiles.Bls12381G2MsmVectors

open Evm.MemoryStackControl
open Bls12381G2Msm

-- Reuse the existing fail-closed fixture parser; this is not a production decoder.
abbrev parsedBytes := Bls12381G2AddVectors.parsedBytes

def schedule : Schedule := Schedule.amsterdam

/--
  The mul_G2_bls.json asset supplies the generator and multiplication outputs;
  multiexp_G2_bls.json supplies the second point and MSM output.
-/
def generatorHex : String := "00000000000000000000000000000000024aa2b2f08f0a91260805272dc51051c6e47ad4fa403b02b4510b647ae3d1770bac0326a805bbefd48056c8c121bdb80000000000000000000000000000000013e02b6052719f607dacd3a088274f65596bd0d09920b61ab5da61bbdc7f5049334cf11213945d57e5ac7d055d042b7e000000000000000000000000000000000ce5d527727d6e118cc9cdc6da2e351aadfd9baa8cbdd3a76d429a695160d12c923ac9cc3baca289e193548608b82801000000000000000000000000000000000606c4a02ea734cc32acd2b02bc28b99cb3e287e85a763af267492ab572e99ab3f370d275cec1da1aaa9075ff05f79be"
def secondPointHex : String := "00000000000000000000000000000000103121a2ceaae586d240843a398967325f8eb5a93e8fea99b62b9f88d8556c80dd726a4b30e84a36eeabaf3592937f2700000000000000000000000000000000086b990f3da2aeac0a36143b7d7c824428215140db1bb859338764cb58458f081d92664f9053b50b3fbd2e4723121b68000000000000000000000000000000000f9e7ba9a86a8f7624aa2b42dcc8772e1af4ae115685e60abc2c9b90242167acef3d0be4050bf935eed7c3b6fc7ba77e000000000000000000000000000000000d22c3652d0dc6f0fc9316e14268477c2049ef772e852108d269d9c38dba1d4802e8dae479818184c08f9a569d878451"

def generator : EncodedPoint := ⟨parsedBytes generatorHex, by native_decide⟩
def secondPoint : EncodedPoint := ⟨parsedBytes secondPointHex, by native_decide⟩

/-- Expected tables are kept separate from the oracle tables on purpose. -/
def expectedDoubleHex : String := "000000000000000000000000000000001638533957d540a9d2370f17cc7ed5863bc0b995b8825e0ee1ea1e1e4d00dbae81f14b0bf3611b78c952aacab827a053000000000000000000000000000000000a4edef9c1ed7f729f520e47730a124fd70662a904ba1074728114d1031e1572c6c886f6b57ec72a6178288c47c33577000000000000000000000000000000000468fb440d82b0630aeb8dca2b5256789a66da69bf91009cbfe6bd221e47aa8ae88dece9764bf3bd999d95d71e4c9899000000000000000000000000000000000f6d4552fa65dd2638b361543f887136a43253d9c66c411697003f7a13c308f5422e1aa0a59c8967acdefd8b6e36ccf3"
def oracleDoubleHex : String := "000000000000000000000000000000001638533957d540a9d2370f17cc7ed5863bc0b995b8825e0ee1ea1e1e4d00dbae81f14b0bf3611b78c952aacab827a053000000000000000000000000000000000a4edef9c1ed7f729f520e47730a124fd70662a904ba1074728114d1031e1572c6c886f6b57ec72a6178288c47c33577000000000000000000000000000000000468fb440d82b0630aeb8dca2b5256789a66da69bf91009cbfe6bd221e47aa8ae88dece9764bf3bd999d95d71e4c9899000000000000000000000000000000000f6d4552fa65dd2638b361543f887136a43253d9c66c411697003f7a13c308f5422e1aa0a59c8967acdefd8b6e36ccf3"
def expectedRandomHex : String := "0000000000000000000000000000000014856c22d8cdb2967c720e963eedc999e738373b14172f06fc915769d3cc5ab7ae0a1b9c38f48b5585fb09d4bd2733bb000000000000000000000000000000000c400b70f6f8cd35648f5c126cce5417f3be4d8eefbd42ceb4286a14df7e03135313fe5845e3a575faab3e8b949d248800000000000000000000000000000000149a0aacc34beba2beb2f2a19a440166e76e373194714f108e4ab1c3fd331e80f4e73e6b9ea65fe3ec96d7136de81544000000000000000000000000000000000e4622fef26bdb9b1e8ef6591a7cc99f5b73164500c1ee224b6a761e676b8799b09a3fd4fa7e242645cc1a34708285e4"
def oracleRandomHex : String := "0000000000000000000000000000000014856c22d8cdb2967c720e963eedc999e738373b14172f06fc915769d3cc5ab7ae0a1b9c38f48b5585fb09d4bd2733bb000000000000000000000000000000000c400b70f6f8cd35648f5c126cce5417f3be4d8eefbd42ceb4286a14df7e03135313fe5845e3a575faab3e8b949d248800000000000000000000000000000000149a0aacc34beba2beb2f2a19a440166e76e373194714f108e4ab1c3fd331e80f4e73e6b9ea65fe3ec96d7136de81544000000000000000000000000000000000e4622fef26bdb9b1e8ef6591a7cc99f5b73164500c1ee224b6a761e676b8799b09a3fd4fa7e242645cc1a34708285e4"
def expectedSumHex : String := "00000000000000000000000000000000009cc9ed6635623ba19b340cbc1b0eb05c3a58770623986bb7e041645175b0a38d663d929afb9a949f7524656043bccc000000000000000000000000000000000c0fb19d3f083fd5641d22a861a11979da258003f888c59c33005cb4a2df4df9e5a2868832063ac289dfa3e997f21f8a00000000000000000000000000000000168bf7d87cef37cf1707849e0a6708cb856846f5392d205ae7418dd94d94ef6c8aa5b424af2e99d957567654b9dae1d90000000000000000000000000000000017e0fa3c3b2665d52c26c7d4cea9f35443f4f9007840384163d3aa3c7d4d18b21b65ff4380cf3f3b48e94b5eecb221dd"
def oracleSumHex : String := "00000000000000000000000000000000009cc9ed6635623ba19b340cbc1b0eb05c3a58770623986bb7e041645175b0a38d663d929afb9a949f7524656043bccc000000000000000000000000000000000c0fb19d3f083fd5641d22a861a11979da258003f888c59c33005cb4a2df4df9e5a2868832063ac289dfa3e997f21f8a00000000000000000000000000000000168bf7d87cef37cf1707849e0a6708cb856846f5392d205ae7418dd94d94ef6c8aa5b424af2e99d957567654b9dae1d90000000000000000000000000000000017e0fa3c3b2665d52c26c7d4cea9f35443f4f9007840384163d3aa3c7d4d18b21b65ff4380cf3f3b48e94b5eecb221dd"

def expectedDouble : EncodedPoint := ⟨parsedBytes expectedDoubleHex, by native_decide⟩
def oracleDouble : EncodedPoint := ⟨parsedBytes oracleDoubleHex, by native_decide⟩
def expectedRandom : EncodedPoint := ⟨parsedBytes expectedRandomHex, by native_decide⟩
def oracleRandom : EncodedPoint := ⟨parsedBytes oracleRandomHex, by native_decide⟩
def expectedSum : EncodedPoint := ⟨parsedBytes expectedSumHex, by native_decide⟩
def oracleSum : EncodedPoint := ⟨parsedBytes oracleSumHex, by native_decide⟩

def expectedOutputs : List EncodedPoint := [expectedDouble, expectedRandom, expectedSum]
def oracleOutputs : List EncodedPoint := [oracleDouble, oracleRandom, oracleSum]

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

/-- These point literals are taken verbatim from the fail-mul G2 asset. -/
def invalidTopHex : String := "10000000000000000000000000000000024aa2b2f08f0a91260805272dc51051c6e47ad4fa403b02b4510b647ae3d1770bac0326a805bbefd48056c8c121bdb80000000000000000000000000000000013e02b6052719f607dacd3a088274f65596bd0d09920b61ab5da61bbdc7f5049334cf11213945d57e5ac7d055d042b7e000000000000000000000000000000000ce5d527727d6e118cc9cdc6da2e351aadfd9baa8cbdd3a76d429a695160d12c923ac9cc3baca289e193548608b82801000000000000000000000000000000000606c4a02ea734cc32acd2b02bc28b99cb3e287e85a763af267492ab572e99ab3f370d275cec1da1aaa9075ff05f79be"
def invalidFieldHex : String := "000000000000000000000000000000001c4bb49d2a0ef12b7123acdd7110bd292b5bc659edc54dc21b81de057194c79b2a5803255959bbef8e7f56c8c12168630000000000000000000000000000000013e02b6052719f607dacd3a088274f65596bd0d09920b61ab5da61bbdc7f5049334cf11213945d57e5ac7d055d042b7e000000000000000000000000000000000ce5d527727d6e118cc9cdc6da2e351aadfd9baa8cbdd3a76d429a695160d12c923ac9cc3baca289e193548608b82801000000000000000000000000000000000606c4a02ea734cc32acd2b02bc28b99cb3e287e85a763af267492ab572e99ab3f370d275cec1da1aaa9075ff05f79be"
def invalidCurveHex : String := "00000000000000000000000000000000024aa2b2f08f0a91260805272dc51051c6e47ad4fa403b02b4510b647ae3d1770bac0326a805bbefd48056c8c121bdb800000000000000000000000000000000086b990f3da2aeac0a36143b7d7c824428215140db1bb859338764cb58458f081d92664f9053b50b3fbd2e4723121b68000000000000000000000000000000000ce5d527727d6e118cc9cdc6da2e351aadfd9baa8cbdd3a76d429a695160d12c923ac9cc3baca289e193548608b82801000000000000000000000000000000000606c4a02ea734cc32acd2b02bc28b99cb3e287e85a763af267492ab572e99ab3f370d275cec1da1aaa9075ff05f79be"
def invalidSubgroupHex : String := "00000000000000000000000000000000197bfd0342bbc8bee2beced2f173e1a87be576379b343e93232d6cef98d84b1d696e5612ff283ce2cfdccb2cfb65fa0c00000000000000000000000000000000184e811f55e6f9d84d77d2f79102fd7ea7422f4759df5bf7f6331d550245e3f1bcf6a30e3b29110d85e0ca16f9f6ae7a000000000000000000000000000000000f10e1eb3c1e53d2ad9cf2d398b2dc22c5842fab0a74b174f691a7e914975da3564d835cd7d2982815b8ac57f507348f000000000000000000000000000000000767d1c453890f1b9110fda82f5815c27281aba3f026ee868e4176a0654feea41a96575e0c4d58a14dbfbcc05b5010b1"

def invalidTop : EncodedPoint := ⟨parsedBytes invalidTopHex, by native_decide⟩
def invalidField : EncodedPoint := ⟨parsedBytes invalidFieldHex, by native_decide⟩
def invalidCurve : EncodedPoint := ⟨parsedBytes invalidCurveHex, by native_decide⟩
def invalidSubgroup : EncodedPoint := ⟨parsedBytes invalidSubgroupHex, by native_decide⟩

def pair (point : EncodedPoint) (scalar : Scalar) : Pair := ⟨point, scalar⟩
def encodePair (entry : Pair) : List Byte := entry.point.bytes ++ entry.scalar.bytes
def inputFor (pairs : List Pair) : List Byte := pairs.flatMap encodePair

def generatorOne : Pair := pair generator oneScalar
def generatorTwo : Pair := pair generator twoScalar
def generatorZero : Pair := pair generator zeroScalar
def secondTwo : Pair := pair secondPoint twoScalar
def infinityMax : Pair := pair infinity maxScalar

def knownMultiply (point : EncodedPoint) (scalar : Scalar) : EncodedPoint :=
  if point == generator then
    if scalar == oneScalar then generator
    else if scalar == twoScalar then oracleDouble
    else if scalar == randomScalar || scalar == unnormalizedScalar then oracleRandom
    else infinity
  else infinity

def knownMsm (pairs : List Pair) : EncodedPoint :=
  if pairs == [generatorTwo, secondTwo] then oracleSum
  else if pairs == [generatorOne, generatorOne] || pairs == [generatorTwo, generatorZero] then oracleDouble
  else if pairs == [generatorZero, generatorZero] then infinity
  else match pairs with
    | [entry] => knownMultiply entry.point entry.scalar
    | _ => infinity

def isModeledPoint (point : EncodedPoint) : Bool :=
  point == infinity || point == generator || point == secondPoint ||
    point == invalidTop || point == invalidField || point == invalidCurve || point == invalidSubgroup

def knownAnswerOracle : MsmOracle :=
  { validFp := fun point => isModeledPoint point && point != invalidTop
    validFp2 := fun point => isModeledPoint point && point != invalidField
    onCurve := fun point => isModeledPoint point && point != invalidCurve
    inSubgroup := fun point => point != invalidSubgroup
    multiply := knownMultiply
    multiScalarMultiply := knownMsm
    nativeStandard := fun _ => .error .invalidInputLength
    nativeZkEvm := fun _ => .error .invalidInputLength }

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
  , ⟨"287-byte fail-mul short frame", List.replicate 287 zeroByte, .error .invalidInputLength, 0⟩
  , ⟨"289-byte fail-mul large frame", List.replicate 289 zeroByte, .error .invalidInputLength, 22500⟩
  , ⟨"575-byte fail-multiexp short frame", List.replicate 575 zeroByte, .error .invalidInputLength, 22500⟩
  , ⟨"577-byte fail-multiexp long frame", List.replicate 577 zeroByte, .error .invalidInputLength, 45000⟩
  , ⟨"mul G2 generator times one", inputFor [generatorOne], .ok generator, 22500⟩
  , ⟨"mul G2 generator times two", inputFor [generatorTwo], .ok expectedDouble, 22500⟩
  , ⟨"valid zero scalar", inputFor [generatorZero], .ok infinity, 22500⟩
  , ⟨"infinity accepts maximum uint256 scalar", inputFor [infinityMax], .ok infinity, 22500⟩
  , ⟨"mul G2 random scalar", inputFor [pair generator randomScalar], .ok expectedRandom, 22500⟩
  , ⟨"mul G2 scalar above subgroup order", inputFor [pair generator unnormalizedScalar], .ok expectedRandom, 22500⟩
  , ⟨"fail-mul top-byte violation", inputFor [pair invalidTop twoScalar], .error .invalidPoint, 22500⟩
  , ⟨"fail-mul noncanonical field", inputFor [pair invalidField twoScalar], .error .invalidPoint, 22500⟩
  , ⟨"fail-mul off-curve point", inputFor [pair invalidCurve twoScalar], .error .invalidPoint, 22500⟩
  , ⟨"fail-mul wrong subgroup", inputFor [pair invalidSubgroup twoScalar], .error .invalidPoint, 22500⟩
  , ⟨"zero scalar does not bypass point validation", inputFor [pair invalidCurve zeroScalar], .error .invalidPoint, 22500⟩
  , ⟨"zero scalar does not bypass subgroup", inputFor [pair invalidSubgroup zeroScalar], .error .invalidPoint, 22500⟩
  , ⟨"multiexp 2g2 plus 2p2", inputFor [generatorTwo, secondTwo], .ok expectedSum, 45000⟩
  , ⟨"multiexp two generator terms", inputFor [generatorOne, generatorOne], .ok expectedDouble, 45000⟩
  , ⟨"finite zero scalar remains in MSM", inputFor [generatorTwo, generatorZero], .ok expectedDouble, 45000⟩
  , ⟨"two finite zero scalars", inputFor [generatorZero, generatorZero], .ok infinity, 45000⟩
  , ⟨"two raw infinity points", inputFor [infinityMax, infinityMax], .ok infinity, 45000⟩
  , ⟨"repeated raw infinity compaction", inputFor [infinityMax, infinityMax, generatorTwo], .ok expectedDouble, 62302⟩
  , ⟨"interleaved raw infinity compaction", inputFor [infinityMax, generatorTwo, infinityMax], .ok expectedDouble, 62302⟩
  , ⟨"interleaved infinity preserves zero scalar validation", inputFor [infinityMax, generatorTwo, generatorZero, infinityMax], .ok expectedDouble, 79560⟩
  , ⟨"fail-multiexp invalid first point", inputFor [pair invalidCurve zeroScalar, secondTwo], .error .invalidPoint, 45000⟩
  , ⟨"fail-multiexp invalid later subgroup", inputFor [generatorTwo, pair invalidSubgroup zeroScalar], .error .invalidPoint, 45000⟩
  , ⟨"fail-multiexp invalid later field", inputFor [generatorTwo, pair invalidField zeroScalar], .error .invalidPoint, 45000⟩ ]

theorem vector_count : vectors.length = 28 := by native_decide
theorem all_vectors_pass : vectors.all passes = true := by native_decide
theorem independent_tables_agree : expectedOutputs = oracleOutputs := by native_decide

def gasVectors : List (Nat × Nat) :=
  [(0, 0), (1, 22500), (2, 45000), (3, 62302), (7, 127890),
    (125, 1479375), (126, 1488375), (127, 1497330), (128, 1509120),
    (129, 1520910), (524, 6177960)]

theorem gas_boundary_vectors :
    gasVectors.all (fun (count, expected) => gasForLength schedule (288 * count) == expected) = true := by
  native_decide

example : discount 3 = 923 ∧ discount 125 = 526 ∧ discount 126 = 525 ∧
    discount 127 = 524 ∧ discount 128 = 524 := by native_decide
example : scalarValue unnormalizedScalar >
    52435875175126190479447740508185965837690552500527637822603658699938581184513 := by native_decide
example : scalarValue maxScalar = 2 ^ 256 - 1 := by native_decide
example : decodeInput? (inputFor [generatorTwo, generatorOne]) = some [generatorTwo, generatorOne] := by
  native_decide
example : collectFinite knownAnswerOracle [infinityMax, generatorTwo, infinityMax] = some [generatorTwo] := by
  native_decide
example : collectFinite knownAnswerOracle [generatorZero] = some [generatorZero] := by native_decide
example : parsedBytes "0" = [] := by native_decide
example : runNativeStandard knownAnswerOracle [] = .error .invalidInputLength := rfl
example : runNativeZkEvm knownAnswerOracle [] = .error .invalidInputLength := rfl

/- Adversarial alternatives are local sentinels, not a production mutation campaign. -/
example : (15 : Nat) ≠ address := by decide
example : "BLS12_G2MUL" ≠ name := by decide
example : false ≠ supportsCaching := by decide
example : (287 : Nat) ≠ itemLength := by decide
example : (22501 : Nat) ≠ gasForLength schedule 288 := by native_decide
example : 22500 * 2 * 949 / 1000 ≠ gasForLength schedule 576 := by native_decide
example : 22500 * 3 * 924 / 1000 ≠ gasForLength schedule 864 := by native_decide
example : 22500 * 125 * 525 / 1000 ≠ gasForLength schedule 36000 := by native_decide
example : 22500 * 128 * 523 / 1000 ≠ gasForLength schedule 36864 := by native_decide
example : gasForLength schedule 576 ≠ gasForLength schedule 289 := by native_decide
example : gasForLength schedule 864 ≠ gasForLength schedule 288 := by native_decide

def zeroBeforeValidation (oracle : MsmOracle) (entry : Pair) : Result :=
  if scalarIsZero entry.scalar then .ok infinity else runSingle oracle entry

example : resultEq (zeroBeforeValidation knownAnswerOracle (pair invalidCurve zeroScalar))
    (runSingle knownAnswerOracle (pair invalidCurve zeroScalar)) = false := by native_decide

def withoutFp : MsmOracle := { knownAnswerOracle with validFp := fun _ => true }
def withoutFp2 : MsmOracle := { knownAnswerOracle with validFp2 := fun _ => true }
def withoutCurve : MsmOracle := { knownAnswerOracle with onCurve := fun _ => true }
def withoutSubgroup : MsmOracle := { knownAnswerOracle with inSubgroup := fun _ => true }

example : resultEq (runSingle withoutFp (pair invalidTop zeroScalar))
    (runSingle knownAnswerOracle (pair invalidTop zeroScalar)) = false := by native_decide
example : resultEq (runSingle withoutFp2 (pair invalidField zeroScalar))
    (runSingle knownAnswerOracle (pair invalidField zeroScalar)) = false := by native_decide
example : resultEq (runSingle withoutCurve (pair invalidCurve zeroScalar))
    (runSingle knownAnswerOracle (pair invalidCurve zeroScalar)) = false := by native_decide
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

def changedExpectedDouble : EncodedPoint :=
  ⟨byte 1 :: expectedDouble.bytes.drop 1, by native_decide⟩
def changedExpectedRandom : EncodedPoint :=
  ⟨byte 1 :: expectedRandom.bytes.drop 1, by native_decide⟩
def changedExpectedSum : EncodedPoint :=
  ⟨byte 1 :: expectedSum.bytes.drop 1, by native_decide⟩
def corruptedMultiplyOracle : MsmOracle := { knownAnswerOracle with multiply := fun _ _ => generator }
def corruptedMsmOracle : MsmOracle := { knownAnswerOracle with multiScalarMultiply := fun _ => generator }

theorem expected_double_literal_mutation_detected : changedExpectedDouble ≠ oracleDouble := by native_decide
theorem expected_random_literal_mutation_detected : changedExpectedRandom ≠ oracleRandom := by native_decide
theorem expected_sum_literal_mutation_detected : changedExpectedSum ≠ oracleSum := by native_decide
theorem multiply_oracle_mutation_detected :
    resultEq (runSingle corruptedMultiplyOracle generatorTwo) (.ok expectedDouble) = false := by native_decide
theorem msm_oracle_mutation_detected :
    resultEq (runMultiple corruptedMsmOracle [generatorTwo, secondTwo]) (.ok expectedSum) = false := by native_decide

end Eip803x.Precompiles.Bls12381G2MsmVectors
