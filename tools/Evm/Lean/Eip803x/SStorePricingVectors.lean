-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.SStorePricingKernel
import Eip803x.Refinement.SStorePricing

namespace Eip803x.SStorePricingVector

open Eip803x.Generated.SStorePricingKernel

structure PricingVector where
  name : String
  input : Input
  accessStatus : Eip803x.Generated.SStorePricingKernel.AccessStatus
  expected : Result
  deriving DecidableEq, Repr

private def accessSchedule : AccessSchedule :=
  { coldStorageAccessGas := 2100
    warmAccessGas := 100 }

private def postAccessSchedule : PostAccessSchedule :=
  { storageWriteGas := 10000
    storageClearRefund := 11616
    storageSetStateGas := 97920 }

private def input
    (originalIsZero currentIsZero newIsZero currentSameAsOriginal newSameAsCurrent newSameAsOriginal : Bool) : Input :=
  { originalIsZero
    currentIsZero
    newIsZero
    currentSameAsOriginal
    newSameAsCurrent
    newSameAsOriginal }

private def post
    (executionWriteGas : Nat)
    (storageClearRefund storageClearRefundReversal restoreOriginalRefund stateGasCharge stateGasRefund : Int) : PostAccessResult :=
  { executionWriteGas
    storageClearRefund
    storageClearRefundReversal
    restoreOriginalRefund
    stateGasCharge
    stateGasRefund }

private def both (name : String) (input : Input) (postAccess : PostAccessResult) : List PricingVector :=
  [ { name := name ++ "-cold", input, accessStatus := .cold, expected := { accessGas := 2100, postAccess } }
  , { name := name ++ "-warm", input, accessStatus := .warm, expected := { accessGas := 100, postAccess } } ]

/-- One cold and one warm vector for each semantic SSTORE equality class. -/
def vectors : List PricingVector :=
  List.flatten
    [ both "new-slot" (input true true false true false false) (post 10000 0 0 0 97920 0)
    , both "first-update" (input false false false true false false) (post 10000 0 0 0 0 0)
    , both "first-clear" (input false false true true false false) (post 10000 11616 0 0 0 0)
    , both "reset-created" (input true false true false false true) (post 0 0 0 10000 0 97920)
    , both "rewrite-created" (input true false false false false false) (post 0 0 0 0 0 0)
    , both "restore-dirty" (input false false false false false true) (post 0 0 0 10000 0 0)
    , both "clear-dirty" (input false false true false false false) (post 0 11616 0 0 0 0)
    , both "restore-cleared" (input false true false false false true) (post 0 0 (-11616) 10000 0 0)
    , both "rewrite-cleared" (input false true false false false false) (post 0 0 (-11616) 0 0 0)
    , both "rewrite-dirty" (input false false false false false false) (post 0 0 0 0 0 0)
    , both "no-op" (input false false false true true true) (post 0 0 0 0 0 0) ]

private def passes (vector : PricingVector) : Bool :=
  price vector.input vector.accessStatus accessSchedule postAccessSchedule == vector.expected

theorem all_pass : vectors.all passes = true := by
  native_decide

theorem vector_count : vectors.length = 22 := rfl

theorem amsterdam_schedule_no_wrap :
    Eip803x.Refinement.SStorePricing.NoWrap accessSchedule postAccessSchedule := by
  unfold Eip803x.Refinement.SStorePricing.NoWrap
  unfold Eip803x.Refinement.SStorePricing.PostAccessNoWrap
  unfold Eip803x.Refinement.SStorePricing.FitsUInt64
  unfold Eip803x.Refinement.SStorePricing.FitsInt64
  native_decide

end Eip803x.SStorePricingVector
