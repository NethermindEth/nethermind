-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.PayFees.Return

namespace SimpleTransferCompletionExtractor.StageB.PayFees.Vectors

set_option maxRecDepth 20000
set_option maxHeartbeats 8000000

private def word (value : Nat) : Word := ⟨value % (2^256), Nat.mod_lt _ (by decide)⟩

private def observed : Observations :=
  { receiver := .ethereum, premium := word 5, paid := 3, effectivePrice := word 7, blobFee := word 11,
    destroyProbeBeneficiary := "same", beneficiaryArgument := "same", beneficiaryWorld := "world",
    baseFeeAfterBeneficiary := word 100, freeAfterBeneficiary := false, eip1559AfterBeneficiary := true,
    supportsBlobsAfterBeneficiary := true, blobCollectorAfterBeneficiary := true,
    collectorGuard := some "same", collectorArgument := "same", collectorWorld := "world",
    tracingAfterCollector := true, tracerIdentity := "tracer", specIdentity := "spec", emptyDestroyList := true }

private def amounts (o : Observations) : Nat × Nat × Nat × Nat :=
  (premiumAmount o, baseAmount o, collectedAmount o, burntAmount o)

theorem arithmetic_vectors :
    [amounts observed,
     amounts { observed with freeAfterBeneficiary := true },
     amounts { observed with eip1559AfterBeneficiary := false },
     amounts { observed with supportsBlobsAfterBeneficiary := false },
     amounts { observed with blobCollectorAfterBeneficiary := false },
     amounts { observed with baseFeeAfterBeneficiary := word 2 },
     amounts { observed with paid := 0 },
     amounts { observed with
       premium := word (2^256-1), paid := 2,
       baseFeeAfterBeneficiary := word (2^256-1), effectivePrice := word (2^256-1), blobFee := word 2 },
     amounts { observed with
       premium := word (2^255), paid := 2,
       baseFeeAfterBeneficiary := word 0, blobFee := word 0 },
     amounts { observed with
       premium := word 1, paid := 18446744073709551615,
       baseFeeAfterBeneficiary := word 0, blobFee := word 0 }] =
    [(15,21,32,32), (15,0,11,11), (15,21,11,32), (15,21,21,32), (15,21,21,32),
     (15,6,17,17), (0,0,11,11), (2^256-2,2^256-2,0,0), (0,0,0,0),
     (18446744073709551615,0,0,0)] := by rfl

theorem same_account_keeps_both_credits : effects observed =
    [.creditAndCreate "world" "same" 15 "spec", .creditAndCreate "world" "same" 32 "spec",
     .reportFees "tracer" 15 32] := rfl

theorem zero_premium_still_calls_credit :
    effects { observed with paid := 0, blobFee := word 0 } =
      [.creditAndCreate "world" "same" 0 "spec", .reportFees "tracer" 0 0] := rfl

theorem collector_and_tracer_branches :
    [effects { observed with collectorGuard := none },
     effects { observed with tracingAfterCollector := false },
     effects { observed with eip1559AfterBeneficiary := false, supportsBlobsAfterBeneficiary := false },
     effects { observed with collectorArgument := "second-read-address" }] =
    [[.creditAndCreate "world" "same" 15 "spec", .reportFees "tracer" 15 32],
     [.creditAndCreate "world" "same" 15 "spec", .creditAndCreate "world" "same" 32 "spec"],
     [.creditAndCreate "world" "same" 15 "spec", .reportFees "tracer" 15 32],
     [.creditAndCreate "world" "same" 15 "spec", .creditAndCreate "world" "second-read-address" 32 "spec",
       .reportFees "tracer" 15 32]] := rfl

theorem actual_interleaving : (events observed).drop 4 =
    [.read (.reference "beneficiary.WorldState" "world"), .read (.address "beneficiary.header.GasBeneficiary" "same"),
     .effect (.creditAndCreate "world" "same" 15 "spec"),
     .read (.word "header.BaseFeePerGas" (word 100)), .read (.word "effectiveGasPrice" (word 7)),
     .read (.flag "tx.IsFree" false), .read (.flag "spec.IsEip1559Enabled" true),
     .read (.flag "tx.SupportsBlobs" true), .read (.flag "spec.IsEip4844FeeCollectorEnabled" true),
     .read (.word "collector.blobBaseFee" (word 11)), .read (.collector (some "same")),
     .read (.reference "collector.WorldState" "world"), .read (.address "collector.spec.FeeCollector" "same"),
     .effect (.creditAndCreate "world" "same" 32 "spec"),
     .read (.flag "tracer.IsTracingFees" true), .read (.word "report.blobBaseFee" (word 11)),
     .effect (.reportFees "tracer" 15 32), .returnedVoid] := rfl

theorem conditional_getters_are_not_read :
    (reads { observed with supportsBlobsAfterBeneficiary := false, tracingAfterCollector := false }).length = 15 := rfl

theorem projected_fuel_vectors (helper : FeeHelper.Residual) :
    (run 10 (start helper observed 10)).map (fun state => (state.fuel, state.returnedVoid, state.effectLog)) =
      .ok (0,true,[.creditAndCreate "world" "same" 15 "spec", .creditAndCreate "world" "same" 32 "spec", .reportFees "tracer" 15 32]) ∧
    (run 10 (start helper observed 9)).map (·.fuel) = .error .fuel ∧
    (run 10 (start helper { observed with emptyDestroyList := false } 10)).map (·.fuel) = .error .outside :=
  ⟨rfl,rfl,rfl⟩

private def caller : Runtime.RuntimeFrame :=
  { id := 17, functionSymbol := StageB.Generated.function24.signature.symbol,
    thisOperand := .immediate (.reference FeeHelper.processorType "processor"),
    cells := [], captures := [], carried := .boolean true }

private def machine : Runtime.Machine :=
  { work := [], operands := [.immediate (.unsigned 37)], frames := [caller, { caller with carried := .unsigned 9 }],
    cells := [], statics := [], tape := [], requestsRev := [], returned := none, nextFrame := 30,
    nextCell := 40, csharpFuel := 0 }

theorem exact_return_zero_csharp_fuel (input : Runtime.Input) :
    Runtime.steps Runtime.generatedPostRefundFeeHelperEntryProgram input 3
      (returnHandoff machine caller 29 (.reference FeeHelper.processorType "processor") []) =
      .next (finalizationScheduled machine caller []) :=
  generic_return_resume_cleanup_exact _ _ _ _ _ _ _ rfl

theorem resumed_machine_keeps_heap_and_duplicate_frame_semantics :
    (finalizationScheduled machine caller []).frames.map (·.carried) = [.unit,.unit] ∧
    (finalizationScheduled machine caller []).operands = [.immediate (.unsigned 37)] ∧
    (finalizationScheduled machine caller []).nextFrame = 30 ∧
    (finalizationScheduled machine caller []).nextCell = 40 := ⟨rfl,rfl,rfl,rfl⟩

private def finalArgs := Runtime.generatedExecuteSimpleTransferFinalizeArguments

private def finalIndex (symbol : String) : Nat :=
  ((finalArgs.zipIdx.find? (fun item => item.1.source.symbol == symbol)).map (·.2)).getD 99

private def distinctGas : Runtime.Value := .struct "global::Nethermind.Evm.TransactionProcessing.GasConsumed"
  [("SpentGas", .unsigned 101), ("OperationGas", .unsigned 202), ("BlockGas", .unsigned 303),
   ("BlockStateGas", .unsigned 404), ("MaxUsedGas", .unsigned 505), ("GasRefund", .unsigned 606)]

private def finalValue (index : Nat) : Runtime.Value := match index with
  | 0 => .reference "global::Nethermind.Core.Transaction" "tx-sentinel"
  | 1 => .reference "global::Nethermind.Core.Specs.IReleaseSpec" "spec-sentinel"
  | 2 => .reference "global::Nethermind.Evm.Tracing.ITxTracer" "tracer-sentinel"
  | 3 => .enum "global::Nethermind.Evm.TransactionProcessing.ExecutionOptions" 37
  | 4 => .boolean true
  | 5 => .boolean false
  | 6 => .boolean true
  | 8 => .address "recipient-sentinel"
  | 10 => distinctGas
  | 11 => .signed 71
  | _ => .unit

private def finalCaller : Runtime.RuntimeFrame :=
  { caller with cells := finalArgs.map (fun argument => (argument.source.symbol, finalIndex argument.source.symbol)) }

private def finalCell (index : Nat) (argument : Runtime.PostRefundCallArgument) : Runtime.Cell :=
  { id := index, frame := 17, symbol := argument.source.symbol, provenance := "original-provenance",
    value := finalValue index,
    alias := if argument.readOnly then some {
      root := index+84, fields := ["unreadable"], readOnly := true, provenance := "intermediate-alias"} else none }

private def finalMachine : Runtime.Machine :=
  { machine with
    frames := [finalCaller, {finalCaller with carried := .unsigned 999}],
    cells := finalArgs.mapIdx finalCell ++
      [{id := 91, frame := 18, symbol := "payment-hop", provenance := "payment-hop-provenance", value := .unit,
        alias := some {root := 999, fields := [], readOnly := true, provenance := "missing-terminal"}},
       {id := 93, frame := 19, symbol := "substate-hop", provenance := "substate-hop-provenance", value := .unit,
        alias := some {root := 93, fields := [], readOnly := true, provenance := "cyclic-terminal"}}],
    statics := [("static-sentinel", 1000)],
    tape := [{request := .incrementEmptyCalls, reply := .unit}], requestsRev := [.addBalance "history" 19],
    returned := some (.signed 88), nextFrame := 222, nextCell := 333 }

private def finalTail : List Runtime.Task := [.setLast 999, .setLast 998]
private def finalStart (fuel : Nat) : Runtime.Machine :=
  { finalMachine with work := .eval 17 Runtime.generatedExecuteSimpleTransferFinalizeNode :: finalTail, csharpFuel := fuel }

private def finalExpected (fuel : Nat) : Runtime.Machine :=
  Runtime.generatedExecuteSimpleTransferFinalizeFrontier finalMachine finalCaller
    (.reference FeeHelper.processorType "processor") finalIndex (fun symbol => finalValue (finalIndex symbol)) finalTail fuel

theorem finalization_exact_51_steps_26_ticks (program : StageB.Program) (input : Runtime.Input) :
    Runtime.steps program input 51 (finalStart 26) = .next (finalExpected 0) := by rfl

theorem finalization_extra_fuel_retained (program : StageB.Program) (input : Runtime.Input) :
    Runtime.steps program input 51 (finalStart 31) = .next (finalExpected 5) := by rfl

theorem finalization_boolean_positions (program : StageB.Program) (input : Runtime.Input)
    (restore commit deleteCaller : Bool) :
    let value := fun index => match index with
      | 4 => Runtime.Value.boolean restore
      | 5 => .boolean commit
      | 6 => .boolean deleteCaller
      | _ => finalValue index
    let before := {finalMachine with
      cells := finalMachine.cells.map
        (fun cell => if cell.id = 4 ∨ cell.id = 5 ∨ cell.id = 6 then {cell with value := value cell.id} else cell)}
    Runtime.steps program input 51 {before with
      work := .eval 17 Runtime.generatedExecuteSimpleTransferFinalizeNode :: finalTail, csharpFuel := 26} =
      .next (Runtime.generatedExecuteSimpleTransferFinalizeFrontier before finalCaller
        (.reference FeeHelper.processorType "processor") finalIndex
        (fun symbol => value (finalIndex symbol)) finalTail 0) := by rfl

private def frontierTag : Runtime.StepResult → Option (StageB.OperationKind × Nat × Nat)
  | .next next => match next.work.head? with
    | some (.apply _ node count) => some (node.kind, count, next.csharpFuel)
    | _ => none
  | .done _ => none

theorem finalization_50_is_not_pending_invocation (program : StageB.Program) (input : Runtime.Input) :
    frontierTag (Runtime.steps program input 50 (finalStart 26)) = some (.argument, 1, 0) ∧
    frontierTag (Runtime.steps program input 51 (finalStart 26)) = some (.invocation, 13, 0) := ⟨rfl, rfl⟩

theorem finalization_25_ticks_is_exhausted (program : StageB.Program) (input : Runtime.Input) :
    Runtime.steps program input 51 (finalStart 25) =
      .done {
        outcome := .fuelExhausted [.addBalance "history" 19] [{request := .incrementEmptyCalls, reply := .unit}],
        remainingCSharpFuel := 0} := by rfl

theorem finalization_operand_sentinels_and_readonly_roots : (finalExpected 0).operands =
    [.immediate (.signed 71), .immediate distinctGas,
     .location {root := 9, fields := [], readOnly := true, provenance := (finalArgs[9]'(by decide)).source.symbol},
     .immediate (.address "recipient-sentinel"),
     .location {root := 7, fields := [], readOnly := true, provenance := (finalArgs[7]'(by decide)).source.symbol},
     .immediate (.boolean true), .immediate (.boolean false), .immediate (.boolean true),
     .immediate (.enum "global::Nethermind.Evm.TransactionProcessing.ExecutionOptions" 37),
     .immediate (.reference "global::Nethermind.Evm.Tracing.ITxTracer" "tracer-sentinel"),
     .immediate (.reference "global::Nethermind.Core.Specs.IReleaseSpec" "spec-sentinel"),
     .immediate (.reference "global::Nethermind.Core.Transaction" "tx-sentinel"),
     .immediate (.reference FeeHelper.processorType "processor"), .immediate (.unsigned 37)] := by rfl

theorem finalization_preserves_nonempty_tails_and_duplicate_ids :
    (finalExpected 0).work.drop 1 = finalTail ∧
    (finalExpected 0).frames.map (·.carried) = [.boolean true, .unsigned 999] ∧
    (finalExpected 0).cells = finalMachine.cells ∧ (finalExpected 0).statics = finalMachine.statics ∧
    (finalExpected 0).tape = finalMachine.tape ∧ (finalExpected 0).requestsRev = finalMachine.requestsRev ∧
    (finalExpected 0).returned = some (.signed 88) ∧
    (finalExpected 0).nextFrame = 222 ∧ (finalExpected 0).nextCell = 333 :=
  ⟨rfl, rfl, rfl, rfl, rfl, rfl, rfl, rfl, rfl⟩

theorem finalization_full_return_54_steps (program : StageB.Program) (input : Runtime.Input) :
    Runtime.steps program input 54 (returnHandoff {finalMachine with csharpFuel := 26}
      finalCaller 29 (.reference FeeHelper.processorType "processor") finalTail) =
      .next (finalizationApplyFrontier finalMachine finalCaller (.reference FeeHelper.processorType "processor")
        finalIndex (fun symbol => finalValue (finalIndex symbol)) finalTail 0) := by rfl

end SimpleTransferCompletionExtractor.StageB.PayFees.Vectors
