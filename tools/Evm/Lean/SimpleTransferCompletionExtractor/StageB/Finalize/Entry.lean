-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Finalize.Binding
import SimpleTransferCompletionExtractor.StageB.Finalize.Generated.FinalizeEntry
import SimpleTransferCompletionExtractor.StageB.PayFees.Return

namespace SimpleTransferCompletionExtractor.StageB.Finalize

open SimpleTransferCompletionExtractor.StageB.Runtime

set_option maxRecDepth 20000
set_option maxHeartbeats 16000000

def entryBlock : Block :=
  { ordinal := Generated.entryBlock, operations := [], branchValue := none, condition := .none,
    exit := .branch, fallThrough := Generated.sourceEntry.fallThrough, conditional := none }

/-- Only the empty entry is executable. Block 1 and every body operation remain absent. -/
def entryFunction : Function :=
  { signature := Generated.signature, entryBlock := Generated.entryBlock,
    entryOperationProvenance := Generated.entryOperation,
    bindings := [], captures := [], regions := [], entryFacts := [], captureModes := [],
    blocks := [entryBlock], calls := [], blockBounds := [], fuelBound := 1, mayReturn := false }

def program : Program :=
  { Runtime.generatedPostRefundFeeHelperEntryProgram with
    functions := [Runtime.generatedExecuteSimpleTransferPostRefundFunction,
      Runtime.generatedPostRefundFeeHelperFunction, entryFunction] }

def invocation : Node := Runtime.generatedExecuteSimpleTransferFinalizeNode
def call : Call := invocation.call.get (by decide)
def arguments : List Runtime.PostRefundCallArgument := Runtime.generatedExecuteSimpleTransferFinalizeArguments

theorem entry_source_admitted :
    Generated.signature = call.target.member ∧
    Generated.signature.symbol = call.target.body ∧ invocation.operator = "virtual=False" ∧
    Generated.sourceEntry =
      { ordinal := 0, kind := "Entry", reachable := true, conditionKind := "None", operations := [],
        branchValue := none, containsExcludedOperations := false, conditional := none,
        fallThrough := some { destination := 1, semantics := "Regular", leavingRegions := [], enteringRegions := [], finallyRegions := [] } } ∧
    entryFunction.blocks = [entryBlock] := ⟨rfl, rfl, rfl, rfl, rfl⟩

def parameters (cells : String → CellId) (values : String → Runtime.Value) : List EvaluatedParameter :=
  (Generated.signature.parameters.zip arguments).map (fun (parameter, argument) =>
    { parameter, value := values argument.source.symbol,
      alias := if argument.readOnly then some (argument.location cells) else none })

def addedCells (before : Machine) (cells : String → CellId) (values : String → Runtime.Value) : List Cell :=
  (parameters cells values).mapIdx (fun index argument => argument.cell (before.nextCell+index) before.nextFrame)

def boundFrame (before : Machine) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) : RuntimeFrame :=
  { id := before.nextFrame, functionSymbol := Generated.signature.symbol, thisOperand := .immediate receiver,
    cells := ((addedCells before cells values).map (fun cell => (cell.symbol, cell.id))).reverse,
    captures := [], carried := .unit }

def bound (before : Machine) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) : Machine :=
  { before with
    frames := boundFrame before receiver cells values :: before.frames,
    cells := (addedCells before cells values).reverse ++ before.cells,
    nextCell := before.nextCell+12, nextFrame := before.nextFrame+1 }

theorem parameter_layout (before : Machine) (cells : String → CellId) (values : String → Runtime.Value) :
    (addedCells before cells values).map (·.alias.isSome) =
      [false, false, false, false, false, false, false, true, false, true, false, false] ∧
    (addedCells before cells values).map (·.id) = (List.range 12).map (before.nextCell + ·) ∧
    (addedCells before cells values).map (·.frame) = List.replicate 12 before.nextFrame ∧
    (addedCells before cells values).map (·.symbol) = Generated.signature.parameters.map (·.symbol) :=
  ⟨rfl, rfl, rfl, rfl⟩

theorem binding_exact (before : Machine) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value)
    (fresh : ∀ old ∈ before.frames, old.id ≠ before.nextFrame) :
    Runtime.bindCallArguments before entryFunction call
      (.immediate receiver :: arguments.map (fun argument => argument.operand cells values)) (.immediate receiver) =
      .ok (bound before receiver cells values, before.nextFrame) := by
  have valid : ∀ argument ∈ parameters cells values, argument.Valid := by
    intro argument member
    have all : (parameters cells values).all (fun argument => argument.parameter.refKind ==
        if argument.alias.isSome then RefKind.inRef else .none) = true := rfl
    simpa [EvaluatedParameter.Valid] using List.all_eq_true.mp all argument member
  have exactLayout := Runtime.createFrame_evaluated_parameters_exact before entryFunction
    (.immediate receiver) (parameters cells values) valid fresh
  have select : Runtime.bindCallArguments before entryFunction call
      (.immediate receiver :: arguments.map (fun argument => argument.operand cells values)) (.immediate receiver) =
      Runtime.createFrame before entryFunction (.immediate receiver)
        ((parameters cells values).map EvaluatedParameter.binding) := rfl
  rw [select]
  exact exactLayout

def frontier (before : Machine) (caller : RuntimeFrame) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat) : Machine :=
  Runtime.generatedExecuteSimpleTransferFinalizeFrontier before caller receiver cells values tail fuel

def prepared (before : Machine) (tail : List Runtime.Task) (fuel : Nat) : Machine :=
  { before with work := tail, csharpFuel := fuel }

def scheduled (before : Machine) (caller : RuntimeFrame) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat) : Machine :=
  { bound (prepared before tail fuel) receiver cells values with
    work := .enterBlock before.nextFrame Generated.signature.symbol 0 ::
      .resume caller.id before.nextFrame (.local false (.immediate receiver) .value) :: tail }

def entered (before : Machine) (caller : RuntimeFrame) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat) : Machine :=
  { bound (prepared before tail fuel) receiver cells values with
    work := .finishBlock before.nextFrame Generated.signature.symbol entryBlock ::
      .resume caller.id before.nextFrame (.local false (.immediate receiver) .value) :: tail }

theorem bind_step (input : Runtime.Input) (before : Machine) (caller : RuntimeFrame)
    (receiver : Runtime.Value) (cells : String → CellId) (values : String → Runtime.Value)
    (tail : List Runtime.Task) (fuel : Nat)
    (fresh : ∀ old ∈ before.frames, old.id ≠ before.nextFrame) :
    Runtime.step program input (frontier before caller receiver cells values tail fuel) =
      .next (scheduled before caller receiver cells values tail fuel) := by
  let base := prepared before tail fuel
  let children := .immediate receiver :: arguments.map (fun argument => argument.operand cells values)
  have indices : Runtime.LocalCallIndicesValid entryFunction call children false := by
    change (false = true ∨ (0 : Int) < 0 ∨ 0 < 13) ∧
      ∀ argument ∈ call.arguments, argument.ordinal < 12 ∧ argument.child < 13
    decide +kernel
  have applied := Runtime.localCall_apply_success_step program input base (bound base receiver cells values)
    caller.id before.nextFrame entryFunction invocation call children before.operands tail
    (Or.inl rfl) rfl rfl rfl rfl indices (binding_exact base receiver cells values fresh)
  have nodeKind : invocation.kind = .invocation := rfl
  have nodeMode : invocation.mode = .value := rfl
  have arity : invocation.children.length = 13 := rfl
  have preparation : Runtime.localCallPreparation {base with work := tail, operands := before.operands}
      caller.id call children false = (base, .immediate receiver) := rfl
  have first := applied.2.2
  simp only [nodeKind, nodeMode, arity, show (OperationKind.invocation == .objectCreation) = false by rfl] at first
  rw [preparation] at first
  simpa only [frontier, Runtime.generatedExecuteSimpleTransferFinalizeFrontier,
    arguments, invocation, base, children, prepared, scheduled, entryFunction, Generated.entryBlock,
    List.reverse_cons, List.append_assoc, List.singleton_append, Int.natCast_zero] using first

theorem empty_entry_step (input : Runtime.Input) (before : Machine) (caller : RuntimeFrame)
    (receiver : Runtime.Value) (cells : String → CellId) (values : String → Runtime.Value)
    (tail : List Runtime.Task) (fuel : Nat)
    (fresh : ∀ old ∈ before.frames, old.id ≠ before.nextFrame)
    (bounded : fuel+1 ≤ 9223372036854775807) :
    Runtime.step program input (scheduled before caller receiver cells values tail (fuel+1)) =
      .next (entered before caller receiver cells values tail fuel) := by
  let base := prepared before tail (fuel+1)
  let allocated := bound base receiver cells values
  let frame := boundFrame base receiver cells values
  have present : allocated.frames.find? (fun old => old.id == frame.id) = some frame := by
    simp [allocated, frame, bound]
  have raw := (Runtime.enterBlock_success_step program input allocated frame entryFunction entryBlock 0
    (.resume caller.id before.nextFrame (.local false (.immediate receiver) .value) :: tail)
    fuel rfl present rfl (by decide) bounded).2.2
  have unchanged : allocated.frames.map (fun old => if old.id == frame.id then frame else old) = allocated.frames := by
    change (frame :: before.frames).map _ = frame :: before.frames
    simp only [List.map_cons, BEq.rfl, if_true, List.cons.injEq, true_and]
    conv => rhs; rw [← List.map_id before.frames]
    apply List.map_congr_left
    intro old member
    have different : old.id ≠ frame.id := fresh old member
    simp [different]
  change Runtime.step program input (scheduled before caller receiver cells values tail (fuel+1)) =
    .next {entered before caller receiver cells values tail fuel with
      frames := allocated.frames.map (fun old => if old.id == frame.id then frame else old)} at raw
  rw [unchanged] at raw
  exact raw

/-- One zero-tick application and one entry tick; the Warmup guard has not been scheduled. -/
theorem bind_empty_entry_exact (input : Runtime.Input) (before : Machine) (caller : RuntimeFrame)
    (receiver : Runtime.Value) (cells : String → CellId) (values : String → Runtime.Value)
    (tail : List Runtime.Task) (fuel : Nat)
    (fresh : ∀ old ∈ before.frames, old.id ≠ before.nextFrame)
    (bounded : fuel+1 ≤ 9223372036854775807) :
    Runtime.steps program input 2 (frontier before caller receiver cells values tail (fuel+1)) =
      .next (entered before caller receiver cells values tail fuel) := by
  simp only [Runtime.steps, bind_step input before caller receiver cells values tail (fuel+1) fresh,
    empty_entry_step input before caller receiver cells values tail fuel fresh bounded]

theorem binding_preserves_old_reads (before : Machine) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value)
    (fresh : ∀ old ∈ before.frames, old.id ≠ before.nextFrame)
    (freshCells : ∀ cell ∈ before.cells, cell.id < before.nextCell)
    (operand : Operand) (value : Runtime.Value) (readable : Runtime.operandReadResult before operand = .ok value) :
    Runtime.operandReadResult (bound before receiver cells values) operand = .ok value :=
  Runtime.bindCallArguments_success_preserves_read before (bound before receiver cells values) entryFunction call
    (.immediate receiver :: arguments.map (fun argument => argument.operand cells values))
    (.immediate receiver) before.nextFrame (binding_exact before receiver cells values fresh)
    freshCells operand value readable

theorem entered_preserves_context (before : Machine) (caller : RuntimeFrame) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat) :
    let after := entered before caller receiver cells values tail fuel
    after.cells = (addedCells before cells values).reverse ++ before.cells ∧
    after.frames = boundFrame before receiver cells values :: before.frames ∧
    after.operands = before.operands ∧ after.statics = before.statics ∧
    after.tape = before.tape ∧ after.requestsRev = before.requestsRev ∧ after.returned = before.returned ∧
    after.nextFrame = before.nextFrame+1 ∧ after.nextCell = before.nextCell+12 ∧
    after.work = .finishBlock before.nextFrame Generated.signature.symbol entryBlock ::
      .resume caller.id before.nextFrame (.local false (.immediate receiver) .value) :: tail :=
  ⟨rfl, rfl, rfl, rfl, rfl, rfl, rfl, rfl, rfl, rfl⟩

theorem entered_preserves_old_reads (before : Machine) (caller : RuntimeFrame) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat)
    (fresh : ∀ old ∈ before.frames, old.id ≠ before.nextFrame)
    (freshCells : ∀ cell ∈ before.cells, cell.id < before.nextCell)
    (operand : Operand) (value : Runtime.Value) (readable : Runtime.operandReadResult before operand = .ok value) :
    Runtime.operandReadResult (entered before caller receiver cells values tail fuel) operand = .ok value := by
  have retained := binding_preserves_old_reads before receiver cells values fresh freshCells operand value readable
  exact (Runtime.readOperand_cells_eq (entered before caller receiver cells values tail fuel)
    (bound before receiver cells values) rfl operand).trans retained

theorem scheduled_to_empty_entry_exact (input : Runtime.Input) (machine : Machine) (caller : RuntimeFrame)
    (receiver : Runtime.Value) (cells : String → CellId) (values : String → Runtime.Value)
    (tail : List Runtime.Task) (fuel : Nat)
    (present : (PayFees.finalizationScheduled machine caller tail).frames.find?
      (fun frame => frame.id == caller.id) = some {caller with carried := .unit})
    (receiverRead : Runtime.generatedFinalizeReceiverReady (PayFees.finalizationScheduled machine caller tail)
      {caller with carried := .unit} receiver)
    (ready : ∀ argument ∈ Runtime.generatedExecuteSimpleTransferFinalizeArguments,
      argument.Ready (PayFees.finalizationScheduled machine caller tail) {caller with carried := .unit} cells values)
    (fresh : ∀ old ∈ (PayFees.finalizationScheduled machine caller tail).frames, old.id ≠ machine.nextFrame)
    (bounded : fuel+27 ≤ 9223372036854775807) :
    Runtime.steps program input 53 {PayFees.finalizationScheduled machine caller tail with csharpFuel := fuel+27} =
      .next (entered (PayFees.finalizationScheduled machine caller tail) {caller with carried := .unit}
        receiver cells values (PayFees.finalizationContinuation caller tail) fuel) := by
  rw [show 53 = 51+2 by decide, Runtime.steps_add]
  rw [show fuel+27 = (fuel+1)+26 by omega,
    PayFees.scheduled_finalization_arguments_exact program input machine caller receiver cells values tail
      (fuel+1) present receiverRead ready]
  exact bind_empty_entry_exact input (PayFees.finalizationScheduled machine caller tail)
    {caller with carried := .unit} receiver cells values (PayFees.finalizationContinuation caller tail) fuel fresh (by omega)

theorem return_to_empty_entry_exact (input : Runtime.Input) (machine : Machine) (caller : RuntimeFrame)
    (callee : FrameId) (receiver : Runtime.Value) (cells : String → CellId) (values : String → Runtime.Value)
    (tail : List Runtime.Task) (fuel : Nat)
    (callerPresent : machine.frames.find? (fun frame => frame.id == caller.id) = some caller)
    (present : (PayFees.finalizationScheduled machine caller tail).frames.find?
      (fun frame => frame.id == caller.id) = some {caller with carried := .unit})
    (receiverRead : Runtime.generatedFinalizeReceiverReady (PayFees.finalizationScheduled machine caller tail)
      {caller with carried := .unit} receiver)
    (ready : ∀ argument ∈ Runtime.generatedExecuteSimpleTransferFinalizeArguments,
      argument.Ready (PayFees.finalizationScheduled machine caller tail) {caller with carried := .unit} cells values)
    (fresh : ∀ old ∈ (PayFees.finalizationScheduled machine caller tail).frames, old.id ≠ machine.nextFrame)
    (bounded : fuel+27 ≤ 9223372036854775807) :
    Runtime.steps program input 56 (PayFees.returnHandoff {machine with csharpFuel := fuel+27}
      caller callee receiver tail) =
      .next (entered (PayFees.finalizationScheduled machine caller tail) {caller with carried := .unit}
        receiver cells values (PayFees.finalizationContinuation caller tail) fuel) := by
  rw [show 56 = 3+53 by decide, Runtime.steps_add,
    PayFees.generic_return_resume_cleanup_exact program input {machine with csharpFuel := fuel+27}
      caller callee receiver tail callerPresent]
  exact scheduled_to_empty_entry_exact input machine caller receiver cells values tail fuel present receiverRead ready fresh bounded

end SimpleTransferCompletionExtractor.StageB.Finalize
