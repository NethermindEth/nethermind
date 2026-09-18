-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.ChargeVectors

open Eip803x

private def emitResponses : IO Unit := do
  for vector in ChargeVector.all do
    IO.println (ChargeVector.render
      (ProductionGas.tryConsumeStateGasNat vector.amount vector.state))

private def emitRequests : IO Unit := do
  for vector in ChargeVector.all do
    IO.println (ChargeVector.renderRequest vector)

def main (args : List String) : IO Unit := do
  match args with
  | [] => emitResponses
  | ["requests"] => emitRequests
  | _ =>
    IO.eprintln "usage: eip803x-charge-state [requests]"
    IO.Process.exit 2
