-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.StateGasTransitionVectors

open Eip803x

private def emit (lines : List String) : IO Unit := do
  for line in lines do
    IO.println line

def main (args : List String) : IO Unit := do
  match args with
  | [] => emit TransitionVector.responses
  | ["requests"] => emit TransitionVector.requests
  | ["responses"] => emit TransitionVector.responses
  | _ =>
      IO.eprintln "usage: eip803x-state-gas-transition [requests|responses]"
      IO.Process.exit 2
