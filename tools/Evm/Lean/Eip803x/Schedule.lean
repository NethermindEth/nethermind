-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace Eip803x

/-- Fork-dependent inputs used by the EIP-8037/8038 reference model. -/
structure GasSchedule where
  cpsb : Nat
  storageSetBytes : Nat
  newAccountBytes : Nat
  coldAccountAccess : Nat
  coldStorageAccess : Nat
  warmAccess : Nat
  accountWrite : Nat
  storageWrite : Nat
  createAccess : Nat
  storageClearRefund : Nat
  accessListAddress : Nat
  accessListStorageKey : Nat
  deriving DecidableEq, Repr

namespace GasSchedule

def storageSetGas (schedule : GasSchedule) : Nat :=
  schedule.storageSetBytes * schedule.cpsb

def newAccountGas (schedule : GasSchedule) : Nat :=
  schedule.newAccountBytes * schedule.cpsb

/-- The pinned Amsterdam schedule described in `SPEC.md`. -/
def amsterdam : GasSchedule where
  cpsb := 1530
  storageSetBytes := 64
  newAccountBytes := 120
  coldAccountAccess := 3000
  coldStorageAccess := 2100
  warmAccess := 100
  accountWrite := 9000
  storageWrite := 10000
  createAccess := 12000
  storageClearRefund := 11616
  accessListAddress := 2900
  accessListStorageKey := 2000

end GasSchedule
end Eip803x
