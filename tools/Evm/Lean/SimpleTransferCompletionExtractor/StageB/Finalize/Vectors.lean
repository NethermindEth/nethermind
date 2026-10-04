-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Finalize.Entry

namespace SimpleTransferCompletionExtractor.StageB.Finalize.Vectors

open SimpleTransferCompletionExtractor.StageB.Runtime

set_option maxRecDepth 20000
set_option maxHeartbeats 16000000

private def index (symbol : String) : Nat :=
  ((arguments.zipIdx.find? (fun item => item.1.source.symbol == symbol)).map (·.2)).getD 99

private def gas : Runtime.Value := .struct "global::Nethermind.Evm.TransactionProcessing.GasConsumed"
  [("SpentGas", .unsigned 101), ("OperationGas", .unsigned 202), ("BlockGas", .unsigned 303),
   ("BlockStateGas", .unsigned 404), ("MaxUsedGas", .unsigned 505), ("GasRefund", .unsigned 606)]

-- Distinct abstract payloads detect positional copies; source Boolean-domain correspondence remains separate.
private def value (i : Nat) : Runtime.Value := if i = 10 then gas else .unsigned (100+i)
private def values (symbol : String) : Runtime.Value := value (index symbol)
private def receiver : Runtime.Value := .reference FeeHelper.processorType "processor"

private def caller : RuntimeFrame :=
  { id := 17, functionSymbol := StageB.Generated.function24.signature.symbol,
    thisOperand := .immediate receiver,
    cells := arguments.map (fun argument => (argument.source.symbol, index argument.source.symbol)),
    captures := [], carried := .signed 88 }

private def oldCell (i : Nat) (argument : Runtime.PostRefundCallArgument) : Cell :=
  { id := i, frame := 17, symbol := argument.source.symbol, provenance := "old-cell",
    value := value i, alias := if argument.readOnly then
      some {root := i+84, fields := ["unreadable"], readOnly := true, provenance := "first-hop"} else none }

private def machine : Machine :=
  { work := [], operands := [.immediate (.signed 777)],
    frames := [caller, {caller with carried := .signed 999}],
    cells := arguments.mapIdx oldCell ++
      [{id := 91, frame := 18, symbol := "payment-hop", provenance := "payment-hop", value := .unit,
        alias := some {root := 999, fields := [], readOnly := true, provenance := "missing-terminal"}},
       {id := 93, frame := 19, symbol := "substate-hop", provenance := "substate-hop", value := .unit,
        alias := some {root := 93, fields := [], readOnly := true, provenance := "cyclic-terminal"}}],
    statics := [("static", 1000)], tape := [{request := .incrementEmptyCalls, reply := .unit}],
    requestsRev := [.addBalance "history" 19], returned := some (.signed 88),
    nextFrame := 222, nextCell := 333, csharpFuel := 0 }

private def tail : List Runtime.Task := [.setLast 999, .setLast 998]

theorem ten_copies_two_aliases :
    (addedCells machine index values).map (fun cell => (cell.id, cell.value, cell.alias.map (·.root))) =
    [(333, .unsigned 100, none), (334, .unsigned 101, none), (335, .unsigned 102, none),
     (336, .unsigned 103, none), (337, .unsigned 104, none), (338, .unsigned 105, none),
     (339, .unsigned 106, none), (340, .uint256 0, some 7), (341, .unsigned 108, none),
     (342, Runtime.parameterDefaultValue "global::Nethermind.Evm.TransactionSubstate", some 9),
     (343, gas, none), (344, .unsigned 111, none)] := by rfl

theorem reverse_layout_counters_and_old_suffixes :
    ((bound machine receiver index values).cells.take 12).map (·.id) =
      [344,343,342,341,340,339,338,337,336,335,334,333] ∧
    (bound machine receiver index values).cells.drop 12 = machine.cells ∧
    (boundFrame machine receiver index values).cells.map (·.2) =
      [344,343,342,341,340,339,338,337,336,335,334,333] ∧
    (bound machine receiver index values).frames.drop 1 = machine.frames ∧
    (bound machine receiver index values).nextCell = 345 ∧
    (bound machine receiver index values).nextFrame = 223 := ⟨rfl,rfl,rfl,rfl,rfl,rfl⟩

theorem unreadable_aliases_retain_provenance :
    (addedCells machine index values)[7]?.map (·.alias) = some (some {
      root := 7, fields := [], readOnly := true, provenance := (arguments[7]'(by decide)).source.symbol }) ∧
    (addedCells machine index values)[9]?.map (·.alias) = some (some {
      root := 9, fields := [], readOnly := true, provenance := (arguments[9]'(by decide)).source.symbol }) ∧
    Runtime.operandReadResult machine (.location {root := 7, fields := [], readOnly := true, provenance := "probe"}) =
      .error "missing cell 999" ∧
    Runtime.operandReadResult machine (.location {root := 9, fields := [], readOnly := true, provenance := "probe"}) =
      .error "location alias cycle" := ⟨rfl,rfl,rfl,rfl⟩

theorem exact_zero_tick_binding (input : Runtime.Input) :
    Runtime.steps program input 1 (frontier machine caller receiver index values tail 0) =
      .next (scheduled machine caller receiver index values tail 0) := by rfl

theorem empty_entry_exact_and_one_short (input : Runtime.Input) :
    Runtime.steps program input 2 (frontier machine caller receiver index values tail 1) =
      .next (entered machine caller receiver index values tail 0) ∧
    Runtime.steps program input 2 (frontier machine caller receiver index values tail 0) =
      .done {
        outcome := .fuelExhausted [.addBalance "history" 19] [{request := .incrementEmptyCalls, reply := .unit}],
        remainingCSharpFuel := 0} := ⟨rfl,rfl⟩

theorem entered_not_body_and_duplicate_callers_retained :
    (entered machine caller receiver index values tail 0).work =
      [.finishBlock 222 Generated.signature.symbol entryBlock,
       .resume 17 222 (.local false (.immediate receiver) .value), .setLast 999, .setLast 998] ∧
    (entered machine caller receiver index values tail 0).frames.map (·.carried) =
      [.unit, .signed 88, .signed 999] ∧
    (entered machine caller receiver index values tail 0).returned = some (.signed 88) ∧
    (entered machine caller receiver index values tail 0).operands = [.immediate (.signed 777)] := ⟨rfl,rfl,rfl,rfl⟩

theorem old_read_preserved :
    Runtime.operandReadResult (entered machine caller receiver index values tail 0)
      (.location {root := 0, fields := [], readOnly := false, provenance := "old"}) = .ok (.unsigned 100) := rfl

theorem exact_53_steps_27_ticks (input : Runtime.Input) :
    Runtime.steps program input 53 {PayFees.finalizationScheduled machine caller tail with csharpFuel := 27} =
      .next (entered (PayFees.finalizationScheduled machine caller tail) {caller with carried := .unit}
        receiver index values (PayFees.finalizationContinuation caller tail) 0) := by rfl

theorem exact_56_steps_27_ticks (input : Runtime.Input) :
    Runtime.steps program input 56 (PayFees.returnHandoff {machine with csharpFuel := 27} caller 221 receiver tail) =
      .next (entered (PayFees.finalizationScheduled machine caller tail) {caller with carried := .unit}
        receiver index values (PayFees.finalizationContinuation caller tail) 0) := by rfl

private def frontierTag : Runtime.StepResult → Option (String × Nat) :=
  fun result => match result with
  | .next next => match next.work.head? with
    | some (.enterBlock _ _ _) => some ("scheduled", next.csharpFuel)
    | some (.finishBlock _ _ _) => some ("entered", next.csharpFuel)
    | _ => none
  | .done _ => none

theorem one_short_microstep_stops_before_entry (input : Runtime.Input) :
    frontierTag (Runtime.steps program input 52 {PayFees.finalizationScheduled machine caller tail with csharpFuel := 27}) =
      some ("scheduled",1) ∧
    frontierTag (Runtime.steps program input 55 (PayFees.returnHandoff {machine with csharpFuel := 27} caller 221 receiver tail)) =
      some ("scheduled",1) := ⟨rfl,rfl⟩

theorem one_short_tick_stops_at_entry (input : Runtime.Input) :
    Runtime.steps program input 53 {PayFees.finalizationScheduled machine caller tail with csharpFuel := 26} =
      .done {
        outcome := .fuelExhausted [.addBalance "history" 19] [{request := .incrementEmptyCalls, reply := .unit}],
        remainingCSharpFuel := 0} := by rfl

end SimpleTransferCompletionExtractor.StageB.Finalize.Vectors
