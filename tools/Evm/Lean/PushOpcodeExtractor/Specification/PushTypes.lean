-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl

namespace PushOpcodeExtractor.Specification.PushTypes

open Eip803x.Evm

abbrev Byte := MemoryStackControl.Byte
abbrev Stack := MemoryStackControl.Stack

inductive ExecStatus where
  | success
  | outOfGas
  | stackOverflow
  | stackUnderflow
  | invalidJump
  | unmodeledOpcode
  deriving DecidableEq, Repr

structure MachineState where
  gasLeft : Nat
  stack : Stack
  code : List Byte
  pc : Nat
  opcodeCount : Nat
  deriving Repr

structure Outcome where
  status : ExecStatus
  state : MachineState
  deriving Repr

end PushOpcodeExtractor.Specification.PushTypes
