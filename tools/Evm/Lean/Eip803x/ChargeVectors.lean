-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Production

namespace Eip803x

structure ChargeVector where
  name : String
  state : ProductionGasState
  amount : Nat
  expected : ProductionStateGasResult
  expectedLine : String
  deriving DecidableEq, Repr

namespace ChargeVector

private def int64Max : Int := 9223372036854775807
private def int64MaxNat : Nat := 9223372036854775807
private def uint64Max : Nat := 18446744073709551615

def all : List ChargeVector :=
  [ { name := "zero"
      state :=
        { gasLeft := 10
          stateReservoir := 7
          stateGasUsed := 11
          stateGasSpill := 13
          stateGasSpillRefunded := 5 }
      amount := 0
      expected :=
        { outcome := .success
          gasLeft := 10
          stateReservoir := 7
          stateGasUsed := 11
          stateGasSpill := 13
          stateGasSpillRefunded := 5 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"10\",\"stateReservoir\":\"7\",\"stateGasUsed\":\"11\",\"stateGasSpill\":\"13\",\"stateGasSpillRefunded\":\"5\"}}" }
  , { name := "exact-reservoir"
      state :=
        { gasLeft := 10
          stateReservoir := 7
          stateGasUsed := 11
          stateGasSpill := 13
          stateGasSpillRefunded := 5 }
      amount := 7
      expected :=
        { outcome := .success
          gasLeft := 10
          stateReservoir := 0
          stateGasUsed := 18
          stateGasSpill := 13
          stateGasSpillRefunded := 5 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"10\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"18\",\"stateGasSpill\":\"13\",\"stateGasSpillRefunded\":\"5\"}}" }
  , { name := "partial-spill"
      state :=
        { gasLeft := 9
          stateReservoir := 4
          stateGasUsed := 10
          stateGasSpill := 3
          stateGasSpillRefunded := 1 }
      amount := 7
      expected :=
        { outcome := .success
          gasLeft := 6
          stateReservoir := 0
          stateGasUsed := 17
          stateGasSpill := 6
          stateGasSpillRefunded := 1 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"6\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"17\",\"stateGasSpill\":\"6\",\"stateGasSpillRefunded\":\"1\"}}" }
  , { name := "exact-gas"
      state :=
        { gasLeft := 3
          stateReservoir := 0
          stateGasUsed := 5
          stateGasSpill := 2
          stateGasSpillRefunded := 1 }
      amount := 3
      expected :=
        { outcome := .success
          gasLeft := 0
          stateReservoir := 0
          stateGasUsed := 8
          stateGasSpill := 5
          stateGasSpillRefunded := 1 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"0\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"8\",\"stateGasSpill\":\"5\",\"stateGasSpillRefunded\":\"1\"}}" }
  , { name := "one-short-oog"
      state :=
        { gasLeft := 2
          stateReservoir := 0
          stateGasUsed := 5
          stateGasSpill := 2
          stateGasSpillRefunded := 1 }
      amount := 3
      expected :=
        { outcome := .outOfGas
          gasLeft := 2
          stateReservoir := 0
          stateGasUsed := 5
          stateGasSpill := 2
          stateGasSpillRefunded := 1 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"outOfGas\",\"state\":{\"gasLeft\":\"2\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"5\",\"stateGasSpill\":\"2\",\"stateGasSpillRefunded\":\"1\"}}" }
  , { name := "negative-reservoir"
      state :=
        { gasLeft := 8
          stateReservoir := -2
          stateGasUsed := 9
          stateGasSpill := 4
          stateGasSpillRefunded := 2 }
      amount := 5
      expected :=
        { outcome := .success
          gasLeft := 3
          stateReservoir := -2
          stateGasUsed := 14
          stateGasSpill := 9
          stateGasSpillRefunded := 2 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"3\",\"stateReservoir\":\"-2\",\"stateGasUsed\":\"14\",\"stateGasSpill\":\"9\",\"stateGasSpillRefunded\":\"2\"}}" }
  , { name := "max-gas-left-safe"
      state :=
        { gasLeft := uint64Max
          stateReservoir := 0
          stateGasUsed := 0
          stateGasSpill := 0
          stateGasSpillRefunded := 0 }
      amount := 1
      expected :=
        { outcome := .success
          gasLeft := 18446744073709551614
          stateReservoir := 0
          stateGasUsed := 1
          stateGasSpill := 1
          stateGasSpillRefunded := 0 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"18446744073709551614\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"1\",\"stateGasSpill\":\"1\",\"stateGasSpillRefunded\":\"0\"}}" }
  , { name := "max-used-safe"
      state :=
        { gasLeft := 0
          stateReservoir := 1
          stateGasUsed := 9223372036854775806
          stateGasSpill := 0
          stateGasSpillRefunded := 0 }
      amount := 1
      expected :=
        { outcome := .success
          gasLeft := 0
          stateReservoir := 0
          stateGasUsed := int64Max
          stateGasSpill := 0
          stateGasSpillRefunded := 0 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"0\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"9223372036854775807\",\"stateGasSpill\":\"0\",\"stateGasSpillRefunded\":\"0\"}}" }
  , { name := "max-spill-safe"
      state :=
        { gasLeft := 1
          stateReservoir := 0
          stateGasUsed := 0
          stateGasSpill := 9223372036854775806
          stateGasSpillRefunded := 0 }
      amount := 1
      expected :=
        { outcome := .success
          gasLeft := 0
          stateReservoir := 0
          stateGasUsed := 1
          stateGasSpill := int64Max
          stateGasSpillRefunded := 0 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"0\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"1\",\"stateGasSpill\":\"9223372036854775807\",\"stateGasSpillRefunded\":\"0\"}}" }
  , { name := "max-cost-reservoir-safe"
      state :=
        { gasLeft := 0
          stateReservoir := int64Max
          stateGasUsed := 0
          stateGasSpill := 0
          stateGasSpillRefunded := 0 }
      amount := int64MaxNat
      expected :=
        { outcome := .success
          gasLeft := 0
          stateReservoir := 0
          stateGasUsed := int64Max
          stateGasSpill := 0
          stateGasSpillRefunded := 0 }
      expectedLine :=
        "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"success\",\"state\":{\"gasLeft\":\"0\",\"stateReservoir\":\"0\",\"stateGasUsed\":\"9223372036854775807\",\"stateGasSpill\":\"0\",\"stateGasSpillRefunded\":\"0\"}}" }
  ]

private def outcomeName : ProductionChargeOutcome → String
  | .success => "success"
  | .outOfGas => "outOfGas"

private def decimalNat (value : Nat) : String := s!"{value}"

private def decimalInt (value : Int) : String := s!"{value}"

def render (result : ProductionStateGasResult) : String :=
  "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"outcome\":\"" ++
    outcomeName result.outcome ++
    "\",\"state\":{\"gasLeft\":\"" ++ decimalNat result.gasLeft ++
    "\",\"stateReservoir\":\"" ++ decimalInt result.stateReservoir ++
    "\",\"stateGasUsed\":\"" ++ decimalInt result.stateGasUsed ++
    "\",\"stateGasSpill\":\"" ++ decimalInt result.stateGasSpill ++
    "\",\"stateGasSpillRefunded\":\"" ++ decimalInt result.stateGasSpillRefunded ++
    "\"}}"

def renderRequest (vector : ChargeVector) : String :=
  "{\"schemaVersion\":\"1\",\"operation\":\"charge-state\",\"input\":{\"gasLeft\":\"" ++
    decimalNat vector.state.gasLeft ++
    "\",\"stateReservoir\":\"" ++ decimalInt vector.state.stateReservoir ++
    "\",\"stateGasUsed\":\"" ++ decimalInt vector.state.stateGasUsed ++
    "\",\"stateGasSpill\":\"" ++ decimalInt vector.state.stateGasSpill ++
    "\",\"stateGasSpillRefunded\":\"" ++
      decimalInt vector.state.stateGasSpillRefunded ++
    "\",\"stateGasCost\":\"" ++ decimalNat vector.amount ++
    "\"}}"

def passes (vector : ChargeVector) : Bool :=
  let actual := ProductionGas.tryConsumeStateGasNat vector.amount vector.state
  actual == vector.expected && render actual == vector.expectedLine

theorem all_pass : all.all passes = true := by
  native_decide

theorem row_count : all.length = 10 := by
  rfl

end ChargeVector
end Eip803x
