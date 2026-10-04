-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.PayFees.Residual

namespace SimpleTransferCompletionExtractor.StageB.PayFees

open SimpleTransferCompletionExtractor.StageB.Runtime

set_option maxRecDepth 20000

def callerBlock : Block := Runtime.generatedExecuteSimpleTransferPostRefundCallBlock false
def callerStatement : Node := callerBlock.operations.headD (Runtime.generatedExecuteSimpleTransferPostRefundCallNode false)
def finalizeInvocation : Node := callerBlock.branchValue.getD (Runtime.generatedExecuteSimpleTransferPostRefundCallNode false)

def finalizationTail (caller : RuntimeFrame) (tail : List Runtime.Task) : List Runtime.Task :=
  [.eval caller.id finalizeInvocation, .setLast caller.id,
   .finishBlock caller.id StageB.Generated.function24.signature.symbol callerBlock] ++ tail

/-- Explicit projected normal-completion handoff; it does not derive a concrete world-state execution. -/
def returnHandoff (machine : Machine) (caller : RuntimeFrame) (callee : FrameId)
    (receiver : Runtime.Value) (tail : List Runtime.Task) : Machine :=
  { machine with
    returned := some .unit,
    work := [.resume caller.id callee (.local false (.immediate receiver) .value),
      .apply caller.id callerStatement 1, .setLast caller.id] ++ finalizationTail caller tail }

def finalizationScheduled (machine : Machine) (caller : RuntimeFrame) (tail : List Runtime.Task) : Machine :=
  { machine with
    work := finalizationTail caller tail, returned := none,
    frames := machine.frames.map (fun frame => if frame.id == caller.id then { caller with carried := .unit } else frame) }

theorem helper_exit_source_admitted : StageB.Generated.feeHelperSourceBlock6 =
    { ordinal := 6, kind := "Exit", reachable := true, conditionKind := "None", operations := [],
      branchValue := none, containsExcludedOperations := false, fallThrough := none, conditional := none } := rfl

theorem finalize_invocation_source_admitted :
    finalizeInvocation.kind = .invocation ∧
    finalizeInvocation.symbol = "global::Nethermind.Evm.TransactionProcessing.TransactionResult global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.FinalizeTransaction(global::Nethermind.Core.Transaction tx, global::Nethermind.Core.Specs.IReleaseSpec spec, global::Nethermind.Evm.Tracing.ITxTracer tracer, global::Nethermind.Evm.TransactionProcessing.ExecutionOptions opts, bool restore, bool commit, bool deleteCallerAccount, in global::Nethermind.Int256.UInt256 senderReservedGasPayment, global::Nethermind.Core.Address executingAccount, in global::Nethermind.Evm.TransactionSubstate substate, global::Nethermind.Evm.TransactionProcessing.GasConsumed spentGas, int statusCode)" ∧
    finalizeInvocation.children.length = 13 := ⟨rfl, rfl, rfl⟩

theorem handoff_uses_retained_continuation (machine : Machine) (o : FeeHelper.Observations)
    (entry : FeeHelper.EntryWitness machine o) :
    (returnHandoff machine entry.caller entry.before.nextFrame
      (.reference FeeHelper.processorType o.objects.processorIdentity) entry.tail).work = machine.work.drop 1 := by
  exact (congrArg (fun value : Machine => value.work.drop 1) entry.accepted).symm

set_option maxRecDepth 20000 in
theorem generic_return_resume_cleanup_exact (program : Program) (input : Runtime.Input)
    (machine : Machine) (caller : RuntimeFrame) (callee : FrameId) (receiver : Runtime.Value)
    (tail : List Runtime.Task)
    (present : machine.frames.find? (fun frame => frame.id == caller.id) = some caller) :
    Runtime.steps program input 3 (returnHandoff machine caller callee receiver tail) =
      .next (finalizationScheduled machine caller tail) := by
  let base : Machine := { machine with returned := none }
  let cleanup := [.apply caller.id callerStatement 1, .setLast caller.id] ++ finalizationTail caller tail
  have resumed := (Runtime.generatedLocalReturn_resume_step program input machine caller.id callee false
    (.immediate receiver) .value .unit receiver cleanup (by intro h; contradiction)).2
  have wrapper : Runtime.NonConvertingTransparent callerStatement := by
    exact Or.inr (Or.inr rfl)
  have finished := Runtime.generatedTransparent_setLast_two_step program input base caller callerStatement
    (finalizationTail caller tail) (.immediate .unit) machine.operands .unit wrapper present rfl
  have first : Runtime.step program input (returnHandoff machine caller callee receiver tail) =
      .next { base with work := cleanup, operands := .immediate .unit :: machine.operands } := by
    simpa [returnHandoff, cleanup, base, Runtime.expectedLocalOperand] using resumed
  have second := finished.2.1
  have third := finished.2.2
  have reads : StageB.Control.Generated.ShouldReadTransparent (Runtime.transparentPreservesOperand callerStatement)
      (callerStatement.mode == .value) = true := rfl
  simp only [reads, ite_true] at second third
  rw [show 3 = 1+2 by decide, Runtime.steps_add]
  simp only [Runtime.steps, first]
  change (match Runtime.step program input {base with work := cleanup, operands := .immediate .unit :: machine.operands} with
    | .next next => Runtime.steps program input 1 next
    | .done result => .done result) = _
  dsimp only [cleanup]
  simp only [List.cons_append, List.nil_append]
  rw [second]
  simp only [Runtime.steps, third]
  rfl

theorem returned_frontier_is_unentered (machine : Machine) (caller : RuntimeFrame) (tail : List Runtime.Task) :
    (finalizationScheduled machine caller tail).work.head? = some (.eval caller.id finalizeInvocation) ∧
    (finalizationScheduled machine caller tail).cells = machine.cells ∧
    (finalizationScheduled machine caller tail).tape = machine.tape ∧
    (finalizationScheduled machine caller tail).requestsRev = machine.requestsRev ∧
    (finalizationScheduled machine caller tail).csharpFuel = machine.csharpFuel := ⟨rfl, rfl, rfl, rfl, rfl⟩

/-- Noninterference of external effects with the retained interpreter machine is an explicit adapter obligation. -/
structure NormalCompletionWitness {World : Type} (apply : World → Effect → World → Prop)
    (initial final : World) (helper : FeeHelper.Residual) (o : Observations)
    (observes : Nat → Read → Prop) where
  boundary : Boundary helper o observes
  observeInWorld : World → Read → Prop
  eventsReturned : EventExecution observeInWorld apply initial (events o) final
  machineProjection : World → Machine
  retainedMachine : Machine
  initialProjection : machineProjection initial = boundary.machine
  finalProjection : machineProjection final = retainedMachine
  effectNoninterference : machineProjection final = machineProjection initial

theorem payFees_and_caller_resume_compose {World : Type} (apply : World → Effect → World → Prop)
    (initial final : World) (helper : FeeHelper.Residual) (o : Observations)
    (observes : Nat → Read → Prop)
    (witness : NormalCompletionWitness apply initial final helper o observes) (fuel : Nat) :
    run 10 (start helper o (fuel+10)) = .ok (completed helper o fuel) ∧
    EffectExecution apply initial (completed helper o fuel).effectLog final ∧
    Runtime.steps Runtime.generatedPostRefundFeeHelperEntryProgram witness.boundary.entry.input 3
      (returnHandoff witness.retainedMachine witness.boundary.entry.caller witness.boundary.entry.before.nextFrame
        (.reference FeeHelper.processorType witness.boundary.helperObservations.objects.processorIdentity)
        witness.boundary.entry.tail) = .next
      (finalizationScheduled witness.retainedMachine witness.boundary.entry.caller witness.boundary.entry.tail) := by
  refine ⟨projected_payFees_exact helper o fuel witness.boundary.empty, ?_, ?_⟩
  · have ordered := event_execution_preserves_effect_order witness.observeInWorld apply initial final
      (events o) witness.eventsReturned
    simpa only [events_project_effects, completed] using ordered
  apply generic_return_resume_cleanup_exact
  have retained : witness.retainedMachine = witness.boundary.machine :=
    witness.finalProjection.symm.trans (witness.effectNoninterference.trans witness.initialProjection)
  rw [retained]
  exact witness.boundary.callerPresent

def finalizationContinuation (caller : RuntimeFrame) (tail : List Runtime.Task) : List Runtime.Task :=
  [.setLast caller.id, .finishBlock caller.id StageB.Generated.function24.signature.symbol callerBlock] ++ tail

def finalizationApplyFrontier (machine : Machine) (caller : RuntimeFrame) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat) : Machine :=
  Runtime.generatedExecuteSimpleTransferFinalizeFrontier (finalizationScheduled machine caller tail)
    {caller with carried := .unit} receiver cells values (finalizationContinuation caller tail) fuel

/-- The accepted return endpoint now evaluates the invocation, but never applies or enters the callee. -/
theorem scheduled_finalization_arguments_exact (program : Program) (input : Runtime.Input)
    (machine : Machine) (caller : RuntimeFrame) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat)
    (present : (finalizationScheduled machine caller tail).frames.find?
      (fun frame => frame.id == caller.id) = some {caller with carried := .unit})
    (receiverRead : Runtime.generatedFinalizeReceiverReady (finalizationScheduled machine caller tail)
      {caller with carried := .unit} receiver)
    (ready : ∀ argument ∈ Runtime.generatedExecuteSimpleTransferFinalizeArguments,
      argument.Ready (finalizationScheduled machine caller tail) {caller with carried := .unit} cells values) :
    Runtime.steps program input 51 {finalizationScheduled machine caller tail with csharpFuel := fuel+26} =
      .next (finalizationApplyFrontier machine caller receiver cells values tail fuel) := by
  exact Runtime.generated_finalize_call_frontier_exact program input
    (finalizationScheduled machine caller tail) {caller with carried := .unit} receiver cells values
    (finalizationContinuation caller tail) fuel present receiverRead ready

/-- Three zero-tick return/cleanup steps followed by 51 argument steps consume exactly 26 C# ticks. -/
theorem return_to_finalization_apply_exact (program : Program) (input : Runtime.Input)
    (machine : Machine) (caller : RuntimeFrame) (callee : FrameId) (receiver : Runtime.Value)
    (cells : String → CellId) (values : String → Runtime.Value) (tail : List Runtime.Task) (fuel : Nat)
    (callerPresent : machine.frames.find? (fun frame => frame.id == caller.id) = some caller)
    (present : (finalizationScheduled machine caller tail).frames.find?
      (fun frame => frame.id == caller.id) = some {caller with carried := .unit})
    (receiverRead : Runtime.generatedFinalizeReceiverReady (finalizationScheduled machine caller tail)
      {caller with carried := .unit} receiver)
    (ready : ∀ argument ∈ Runtime.generatedExecuteSimpleTransferFinalizeArguments,
      argument.Ready (finalizationScheduled machine caller tail) {caller with carried := .unit} cells values) :
    Runtime.steps program input 54 (returnHandoff {machine with csharpFuel := fuel+26}
      caller callee receiver tail) =
      .next (finalizationApplyFrontier machine caller receiver cells values tail fuel) := by
  rw [show 54 = 3+51 by decide, Runtime.steps_add,
    generic_return_resume_cleanup_exact program input {machine with csharpFuel := fuel+26}
      caller callee receiver tail callerPresent]
  exact scheduled_finalization_arguments_exact program input machine caller receiver cells values tail fuel
    present receiverRead ready

end SimpleTransferCompletionExtractor.StageB.PayFees
