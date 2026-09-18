-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl

namespace Eip803x
namespace Evm
namespace AccountReadTypes

open MemoryStackControl

/-- Neutral adapter view shared by the extracted and handwritten account-read machines. -/
structure AccountView where
  balance : UInt256
  codeHash : UInt256
  code : List Byte
  dead : Bool
  precompile : Bool
  isContract : Bool
  deriving DecidableEq, Repr

abbrev Provider := UInt256 → AccountView

inductive Status where
  | ok
  | outOfGas
  | stackUnderflow
  | badInstruction
  deriving DecidableEq, Repr

/-- Handler-local state; frame publication and rollback remain separate obligations. -/
structure State where
  gas : GasState
  stack : Stack
  memory : Memory
  warmAccounts : List UInt256
  accountReads : List UInt256
  bytecodeReads : List UInt256
  pc : Nat
  opcodeCount : Nat
  deriving Repr

structure Outcome where
  status : Status
  state : State
  deriving Repr

end AccountReadTypes
end Evm
end Eip803x
