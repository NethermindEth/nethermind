-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.SStore

namespace Eip803x

structure NormativeVector where
  name : String
  input : StorageSituation
  expected : StorageGasEffect
  deriving DecidableEq, Repr

namespace NormativeVector

def word (value : Nat) (h : value < 2 ^ 256 := by decide) : UInt256 :=
  ⟨value, h⟩

def make (name : String) (original current newValue : UInt256)
    (executionCharge : Nat) (executionRefund : Int)
    (stateCharge stateRefill : Nat) (access : AccessStatus := .warm) : NormativeVector :=
  { name
    input := { original, current, newValue, access }
    expected := { executionCharge, executionRefund, stateCharge, stateRefill } }

def makeBoth (schedule : GasSchedule) (name : String)
    (original current newValue : UInt256) (executionCharge : Nat)
    (executionRefund : Int) (stateCharge stateRefill : Nat) : List NormativeVector :=
  [ make (name ++ "-cold") original current newValue
      (schedule.coldStorageAccess + executionCharge) executionRefund stateCharge stateRefill .cold
  , make (name ++ "-warm") original current newValue
      (schedule.warmAccess + executionCharge) executionRefund stateCharge stateRefill .warm ]

/--
One cold and one warm representative for every semantic equality class. Cold
dirty-slot combinations are deliberate access-input cross-products, not reachable traces.
-/
def all (schedule : GasSchedule) : List NormativeVector :=
  let zero := word 0
  let x := word 1
  let y := word 2
  let z := word 3
  List.flatten
    [ makeBoth schedule "new-slot" zero zero x schedule.storageWrite
        0 schedule.storageSetGas 0
    , makeBoth schedule "existing-first-update" x x y schedule.storageWrite 0 0 0
    , makeBoth schedule "existing-first-clear" x x zero schedule.storageWrite
        schedule.storageClearRefund 0 0
    , makeBoth schedule "reset-created" zero x zero 0 schedule.storageWrite
        0 schedule.storageSetGas
    , makeBoth schedule "rewrite-created" zero x y 0 0 0 0
    , makeBoth schedule "restore-dirty" x y x 0 schedule.storageWrite 0 0
    , makeBoth schedule "clear-dirty" x y zero 0 schedule.storageClearRefund 0 0
    , makeBoth schedule "restore-cleared" x zero x 0
        ((schedule.storageWrite : Int) - schedule.storageClearRefund) 0 0
    , makeBoth schedule "rewrite-cleared" x zero y 0
        (-(schedule.storageClearRefund : Int)) 0 0
    , makeBoth schedule "rewrite-dirty" x y z 0 0 0 0
    , makeBoth schedule "no-op" x x x 0 0 0 0 ]

def passes (schedule : GasSchedule) (vector : NormativeVector) : Bool :=
  SStore.price schedule vector.input == vector.expected

theorem all_pass (schedule : GasSchedule) : (all schedule).all (passes schedule) = true := by
  simp [all, passes, makeBoth, make, word, SStore.price, SStore.accessCharge,
    GasSchedule.storageSetGas, SStore.isFirstChange, SStore.clearsOriginal,
    SStore.reversesClear, SStore.restoresOriginal, SStore.createsSlot,
    SStore.removesCreatedSlot, Int.sub_eq_add_neg, Int.add_comm]

end NormativeVector
end Eip803x
