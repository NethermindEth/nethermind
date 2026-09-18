-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x

open Eip803x

private def accessName : AccessStatus → String
  | .cold => "cold"
  | .warm => "warm"

private def render (item : NormativeVector) : String :=
  let effect := SStore.price GasSchedule.amsterdam item.input
  s!"{item.name},{item.input.original.val},{item.input.current.val},{item.input.newValue.val},{accessName item.input.access},{effect.executionCharge},{effect.executionRefund},{effect.stateCharge},{effect.stateRefill}"

def main : IO Unit := do
  IO.println "name,original,current,new,access,execution_charge,execution_refund,state_charge,state_refill"
  for item in NormativeVector.all GasSchedule.amsterdam do
    IO.println (render item)
