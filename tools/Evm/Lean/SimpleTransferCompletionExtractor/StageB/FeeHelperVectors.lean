-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.FeeHelper

namespace SimpleTransferCompletionExtractor.StageB.FeeHelper.Vectors

set_option maxRecDepth 20000
set_option maxHeartbeats 4000000

private def machine : Runtime.Machine :=
  { work := [], operands := [], frames := [], cells := [], statics := [], tape := [], requestsRev := [],
    returned := none, nextFrame := 0, nextCell := 0, csharpFuel := 23 }

private def observed (execution state blockExecution blockState paid : Nat) : Observations :=
  { objects := {
      processorIdentity := "processor-object", headerIdentity := "header-object",
      parallel := false, execution, state, headerGasUsed := 987 },
    options := 1, eip8037 := true,
    spent := {
      spentGas := paid, operationGas := 700, blockGas := blockExecution, blockStateGas := blockState,
      maxUsedGas := 800, gasRefund := 900 },
    values := fun index => .unsigned (1000 + index) }

private def execute (execution state blockExecution blockState paid : Nat) : Except Stop (Nat × Nat × Nat × Nat) :=
  (run 17 (start machine 70 (observed execution state blockExecution blockState paid) 17)).map
    (fun result => (result.objects.execution, result.objects.state, result.objects.headerGasUsed, result.fuel))

theorem counter_vectors :
    [execute 0 0 0 5 9, execute 0 0 0 0 9, execute 10 2 7 1 9, execute 2 10 1 7 9,
     execute 10 10 7 7 9, execute 18446744073709551615 2 1 0 9,
     execute 2 18446744073709551615 0 1 9, execute 18446744073709551615 18446744073709551615 1 1 9] =
    [.ok (0, 5, 5, 0), .ok (9, 0, 9, 0), .ok (17, 3, 17, 0), .ok (3, 17, 17, 0),
     .ok (17, 17, 17, 0), .ok (0, 2, 2, 0), .ok (2, 0, 2, 0), .ok (0, 0, 0, 0)] := by
  rfl

theorem routing_vectors :
    ([0, 1, 4, 5, 12, 17, 2147483648, 4294967295] : List UInt32).map skipValidation =
      [false, false, true, true, true, false, false, true] := by decide +kernel

theorem excluded_vectors :
    [(run 17 (start machine 70 { observed 1 2 3 4 5 with options := 5 } 17)).map (·.phase),
     (run 17 (start machine 70 { observed 1 2 3 4 5 with
       objects := { (observed 1 2 3 4 5).objects with parallel := true } } 17)).map (·.phase),
     (run 17 (start machine 70 { observed 1 2 3 4 5 with eip8037 := false } 17)).map (·.phase),
     (run 17 (start machine 70 (observed 1 2 3 4 5) 16)).map (·.phase)] =
      [.error .outside, .error .outside, .error .outside, .error .fuel] := by rfl

theorem pending_and_write_order :
    (run 17 (start machine 70 (observed 1 2 3 4 5) 17)).map (fun result => (result.phase, result.writes)) =
      .ok (.applyPayFees, [.execution "processor-object" 4, .state "processor-object" 6, .header "header-object" 6]) := by
  rfl

private def readonlyProjection : Runtime.Operand → Option (Nat × Bool × String)
  | .location location => some (location.root, location.readOnly, location.provenance)
  | _ => none

theorem alias_identity_vector :
    ((List.range 10).map (payArgument (start machine 70 (observed 1 2 3 4 5) 17))).filterMap readonlyProjection =
      [(75, true, (parameter 5).symbol), (77, true, (parameter 7).symbol),
       (78, true, (parameter 8).symbol), (79, true, (parameter 9).symbol)] := by
  rfl

theorem paid_gas_not_effective_block_gas :
    payArgument (start machine 70 (observed 1 2 0 19 31) 17) 5 = .immediate (.unsigned 31) := rfl

private def location (root : Nat) : Runtime.Location :=
  { root, fields := [], readOnly := true, provenance := "readonly-chain" }

private def baseCell : Runtime.Cell :=
  { id := 3, frame := 0, symbol := "original", provenance := "original", value := .unsigned 37, alias := none }

private def secondCell : Runtime.Cell :=
  { baseCell with id := 2, value := .unit, alias := some (location 3) }

private def firstCell : Runtime.Cell :=
  { baseCell with id := 1, value := .unit, alias := some (location 2) }

private def aliases : Runtime.Machine := { machine with cells := [firstCell, secondCell, baseCell] }

theorem nested_alias_observation : OperandObserved aliases (.location (location 1)) (.unsigned 37) := by
  apply OperandObserved.alias (location 1) (location 2) firstCell (.unsigned 37) rfl rfl
  apply OperandObserved.alias (location 2) (location 3) secondCell (.unsigned 37) rfl rfl
  exact OperandObserved.direct (location 3) baseCell (.unsigned 37) rfl rfl (.root _)

end SimpleTransferCompletionExtractor.StageB.FeeHelper.Vectors
