-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameControlSettlementExtractor.Generated.EvmFrameControlSettlementKernel
import EvmFrameControlSettlementExtractor.Refinement.FrameControlSettlement

/-!
Constructive fixtures showing that Stage D's scoped admissions are inhabited. These are
shape witnesses for the one-step boundary, not claims that arbitrary synthetic machines
are production-reachable or that their adapters satisfy the production simulations.
-/

namespace EvmFrameControlSettlementExtractor.Specification.AdmissionWitnesses

open Eip803x
open Eip803x.Evm.FrameMachineState
open EvmFrameControlSettlementExtractor

private def zeroGas : FrameGasState :=
  { gas := { gasLeft := 0, stateReservoir := 0, stateFromGasLeft := 0, stateUsed := 0, refundCounter := 0 }
    stateGasBaseline := 0
    stateUsedBaseline := 0
    refundCounterBaseline := 0 }

private def zeroWorld : WorldToken :=
  { durable := 0
    reversible := 0
    accessedAccounts := 0
    accessedStorage := 0
    logs := []
    destroyList := [] }

private def frame (kind : FrameKind) (phase : FramePhase) (isTopLevel : Bool)
    (code : List Byte) : Frame :=
  { kind
    executionType := .transaction
    phase
    dispatchTable := .noTraceCancelable
    code
    input := []
    returnData := []
    output := []
    outputDestination := 0
    outputLength := 0
    pc := 0
    opcodeCount := 0
    gas := zeroGas
    refund := 0
    initialStateGasUsed := 0
    stateGasRefundAdvanced := 0
    isTopLevel
    isStatic := false
    isCreateOnPreExistingAccount := false
    isCreateStateGasCharged := false
    newAccountCharged := false
    stack := Eip803x.Evm.MemoryStackControl.Stack.empty
    memory := Eip803x.Evm.MemoryStackControl.Memory.empty
    world := zeroWorld
    snapshot := zeroWorld
    callDepth := 0
    trace := []
    cancellationRequested := false }

private def machine (current : Frame) (parents : List SuspendedFrame := []) : Machine :=
  { current
    parents
    previousCallResult := { createdAddress := none, success := none }
    previousCallOutputDestination := 0
    previousCallOutputLength := 0
    returnDataBuffer := []
    shouldRestoreRipemdTouch := false
    isTracingActions := false
    transactionTrace := []
    controlTrace := [] }

def bytecodeTerminal : Machine := machine (frame .bytecode .fresh true [])

private def topLevelParent : SuspendedFrame :=
  { parent := frame .bytecode .fresh true []
    gasEntry := { pausedParent := zeroGas.gas, child := zeroGas }
    childBaseline :=
      { executionType := .call
        snapshot := zeroWorld
        initialStateGasUsed := 0
        stateGasRefundAdvancedAtEntry := 0
        isCreateOnPreExistingAccount := false
        isCreateStateGasCharged := false
        newAccountCharged := false }
    actionTraceOpen := false }

def bytecodeContinuation : Machine :=
  machine (frame .bytecode .continuation false [Eip803x.Evm.MemoryStackControl.zeroByte]) [topLevelParent]

def fullPrecompile : Machine := machine (frame (.precompile 1) .fresh true [])

private def suspension : SuspendedFrame :=
  { parent := bytecodeContinuation.current
    gasEntry := { pausedParent := zeroGas.gas, child := zeroGas }
    childBaseline :=
      { executionType := .call
        snapshot := zeroWorld
        initialStateGasUsed := 0
        stateGasRefundAdvancedAtEntry := 0
        isCreateOnPreExistingAccount := false
        isCreateStateGasCharged := false
        newAccountCharged := false }
    actionTraceOpen := false }

private def terminalResult : FrameResult :=
  { exit := .success
    controlRoute := .ordinary
    frame := bytecodeTerminal.current
    output := []
    createdAddress := none
    precompileSuccess := none
    substateError := none
    shouldRestoreRipemdTouch := false
    controlTrace := [] }

private def terminalStep : MachineStep :=
  { machine := bytecodeTerminal
    controlRoute := .ordinary
    result := .halt terminalResult }

private def fullPrecompileTerminalResult : FrameResult :=
  { exit := .success
    controlRoute := .ordinary
    frame := fullPrecompile.current
    output := []
    createdAddress := none
    precompileSuccess := some true
    substateError := none
    shouldRestoreRipemdTouch := false
    controlTrace := [] }

private def fullPrecompileSuccessStep : MachineStep :=
  { machine := fullPrecompile
    controlRoute := .ordinary
    result := .halt fullPrecompileTerminalResult }

def fullPrecompileSuccessOutcome : InvocationResult :=
  .fullPrecompileSuccess fullPrecompileSuccessStep

private def suspendStep : MachineStep :=
  { machine := bytecodeContinuation
    controlRoute := .ordinary
    result := .suspend suspension bytecodeContinuation.current }

def bytecodeSuspendOutcome : BytecodeFrameOutcome :=
  { invocation := .returned suspendStep
    directInlineStaticPrecompile := none }

def directInlineOpcodeOutcome : BytecodeFrameOutcome :=
  { invocation := .returned terminalStep
    directInlineStaticPrecompile := some .succeeded }

def representativeAdmitted (candidate : Machine) : Prop :=
  candidate = bytecodeTerminal ∨ candidate = bytecodeContinuation ∨ candidate = fullPrecompile

theorem representativeFixedWidth : FixedWidthFacts representativeAdmitted :=
  { allAdmittedStatesFit := by
      intro candidate admitted
      rcases admitted with terminal | continuation | precompile
      · subst candidate
        simp [FrameFieldBounds, bytecodeTerminal, machine, frame, zeroGas]
      · subst candidate
        simp [FrameFieldBounds, bytecodeContinuation, machine, frame, zeroGas]
      · subst candidate
        simp [FrameFieldBounds, fullPrecompile, machine, frame, zeroGas] }

/- A small concrete adapter set witnesses that the Stage D premise records are
inhabited. It is intentionally not a claim about production adapter semantics:
the production bridge remains a separately reviewed obligation. Its purpose is
to rule out an empty-domain/fixed-width encoding before using the public theorem. -/
private def completedOutcome : ShellResult :=
  { termination := .completed
    route := .topLevelSuccess
    machine := bytecodeTerminal
    result := some terminalResult
    reason := none
    status := none
    directInlineStaticPrecompile := none
    disposal := DisposalObservation.notObserved }

private def suspendedOutcome : ShellResult :=
  { termination := .suspended
    route := .suspend
    machine := bytecodeContinuation
    result := none
    reason := none
    status := none
    directInlineStaticPrecompile := none
    disposal := DisposalObservation.notObserved }

private def syntheticBytecodeOutcome (machine : Machine) : BytecodeFrameOutcome :=
  match machine.current.phase with
  | .continuation => bytecodeSuspendOutcome
  | .fresh | .running => directInlineOpcodeOutcome

private def syntheticSettlement : SettlementAdapters :=
  { continueStep := fun _ _ => completedOutcome
    suspendStep := fun _ _ => suspendedOutcome
    regularSuccessNested := fun _ _ => completedOutcome
    topLevelSuccess := fun _ _ => completedOutcome
    classifyNestedCreateCodeDeposit := fun _ _ => .deposited
    createSuccessNested := fun _ _ => completedOutcome
    codeDepositInvalidNested := fun _ _ => completedOutcome
    codeDepositOutOfGasNested := fun _ _ => completedOutcome
    revertNested := fun _ _ => completedOutcome
    revertTop := fun _ _ => completedOutcome
    exceptionTop := fun _ _ => completedOutcome
    exceptionNested := fun _ _ => completedOutcome
    fullPrecompileOutOfGasNested := fun _ _ => completedOutcome
    fullPrecompileOutOfGasTop := fun _ _ => completedOutcome
    fullPrecompileReturnedFailureNested := fun _ _ => completedOutcome
    fullPrecompileReturnedFailureTop := fun _ _ => completedOutcome
    fullPrecompileManagedExceptionNested := fun _ _ => completedOutcome
    fullPrecompileManagedExceptionTop := fun _ _ => completedOutcome }

private def syntheticAdapters : CanonicalLeaves :=
  { preparation :=
      { prepareFresh := fun machine => machine
        prepareContinuation := fun machine => machine
        clearReturnData := fun machine => machine }
    dispatch :=
      { executeBytecodeFrame := syntheticBytecodeOutcome
        executeFullPrecompileFrame := fun _ => fullPrecompileSuccessOutcome
        directInlineStaticPrecompileEligible := fun _ => true }
    settlement := syntheticSettlement
    cleanup := fun outcome => (outcome.disposal, outcome.machine) }

private def representativeStepAdmission (input : Input)
    (inputAdmitted : representativeAdmitted input.machine) :
    Refinement.StepAdmission syntheticAdapters input :=
  { admits := representativeAdmitted
    inputAdmitted
    sourceLoopPhase := by
      intro machine admitted
      rcases admitted with terminal | continuation | precompile
      · subst machine
        exact Or.inl rfl
      · subst machine
        exact Or.inr rfl
      · subst machine
        exact Or.inl rfl
    freshReturnDataClearedAdmitted := by
      intro machine admitted _
      simpa [syntheticAdapters] using admitted
    freshIntermediateAdmitted := by
      intro machine admitted _
      simpa [syntheticAdapters] using admitted
    preparedAdmitted := by
      intro machine admitted
      cases phase : machine.current.phase <;>
        simpa [Generated.prepare, syntheticAdapters, phase] using admitted
    bytecodeDispatchOutputAdmitted := by
      intro machine admitted subject
      rcases admitted with terminal | continuation | precompile
      · subst machine
        exact Or.inl rfl
      · subst machine
        exact Or.inr (Or.inl rfl)
      · subst machine
        simp [subjectOf, fullPrecompile, machine, frame] at subject
    fullPrecompileDispatchOutputAdmitted := by
      intro machine admitted subject
      rcases admitted with terminal | continuation | precompile
      · subst machine
        simp [subjectOf, bytecodeTerminal, machine, frame] at subject
      · subst machine
        simp [subjectOf, bytecodeContinuation, machine, frame] at subject
      · subst machine
        exact Or.inr (Or.inr rfl)
    bytecodeSettlementOutputAdmitted := by
      intro machine admitted subject
      rcases admitted with terminal | continuation | precompile
      · subst machine
        exact Or.inl rfl
      · subst machine
        exact Or.inr (Or.inl rfl)
      · subst machine
        simp [subjectOf, fullPrecompile, machine, frame] at subject
    fullPrecompileSettlementOutputAdmitted := by
      intro machine admitted subject
      rcases admitted with terminal | continuation | precompile
      · subst machine
        simp [subjectOf, bytecodeTerminal, machine, frame] at subject
      · subst machine
        simp [subjectOf, bytecodeContinuation, machine, frame] at subject
      · subst machine
        exact Or.inl rfl
    iterationOutputAdmitted := by
      intro machine admitted
      rcases admitted with terminal | continuation | precompile
      · subst machine
        exact Or.inl rfl
      · subst machine
        exact Or.inr (Or.inl rfl)
      · subst machine
        exact Or.inl rfl
    topLevelParentShape := by
      intro machine admitted
      rcases admitted with terminal | continuation | precompile
      · subst machine
        simp [bytecodeTerminal, machine, frame]
      · subst machine
        simp [bytecodeContinuation, machine, frame, topLevelParent]
      · subst machine
        simp [fullPrecompile, machine, frame] }

private def syntheticOracles : ExternalOracles :=
  { opcodeFrame := syntheticAdapters.dispatch.executeBytecodeFrame
    fullPrecompileFrame := syntheticAdapters.dispatch.executeFullPrecompileFrame }

private theorem syntheticSourceAdapterBindings :
    SourceAdapterBindings syntheticAdapters representativeAdmitted syntheticOracles :=
  { stageARoutingTheorem := stage_a_routing_theorem_is_constructible
    stageBInlineTheorem := stage_b_inline_theorem_is_constructible
    stageCFullTheorem := stage_c_full_theorem_is_constructible
    stateGasRefundTheorem := state_gas_refund_theorem_is_constructible
    precompilePricingTheorem := precompile_pricing_theorem_is_constructible
    opcodeFrameBound := by intros; rfl
    fullPrecompileFrameBound := by intros; rfl
    bytecodeOutcomeHasBytecodeDomain := by
      intro machine _ _
      cases phase : machine.current.phase <;>
        simp [syntheticAdapters, syntheticBytecodeOutcome, phase, isBytecodeInvocation,
          directInlineOpcodeOutcome, bytecodeSuspendOutcome, terminalStep, suspendStep]
    fullPrecompileOutcomeCompletesCurrentFrame := by
      intro machine admitted subject
      rcases admitted with terminal | continuation | precompile
      · subst machine
        simp [subjectOf, bytecodeTerminal, machine, frame] at subject
      · subst machine
        simp [subjectOf, bytecodeContinuation, machine, frame] at subject
      · subst machine
        exact ⟨fullPrecompileTerminalResult, rfl, rfl, rfl, rfl⟩
    directInlineOnlyFromBytecodeOpcode := by
      intro machine _ subject _
      cases kind : machine.current.kind with
      | bytecode => exact ⟨rfl, rfl⟩
      | precompile address => simp [subjectOf, kind] at subject
    bytecodeCancellationUsesDispatchLoopBoundary := by
      intro machine _ _ boundary reason cancelled
      cases phase : machine.current.phase <;>
        simp [syntheticAdapters, syntheticBytecodeOutcome, phase,
          directInlineOpcodeOutcome, bytecodeSuspendOutcome] at cancelled
    fullPrecompileDoesNotYieldDriverCancellation := by
      intros
      simp [syntheticAdapters, fullPrecompileSuccessOutcome] }

/- This establishes constructibility of the *interface* used for future
production simulation. It is intentionally the same synthetic leaf record on
both sides and is not evidence about Nethermind production adapters. -/
private theorem syntheticProductionLeafSimulation :
    Refinement.ProductionLeafSimulation syntheticAdapters syntheticAdapters representativeAdmitted :=
  { prepareFresh := by intros; rfl
    prepareContinuation := by intros; rfl
    clearReturnData := by intros; rfl
    bytecodeDispatch := by intros; rfl
    fullPrecompileDispatch := by intros; rfl
    directInlineEligibility := by intros; rfl
    settlement := by
      intros
      exact
        { continueStep := by intros; rfl
          suspendStep := by intros; rfl
          regularSuccessNested := by intros; rfl
          topLevelSuccess := by intros; rfl
          nestedCreateDecision := by intros; rfl
          createSuccessNested := by intros; rfl
          codeDepositInvalidNested := by intros; rfl
          codeDepositOutOfGasNested := by intros; rfl
          revertNested := by intros; rfl
          revertTop := by intros; rfl
          exceptionTop := by intros; rfl
          exceptionNested := by intros; rfl
          fullPrecompileOutOfGasNested := by intros; rfl
          fullPrecompileOutOfGasTop := by intros; rfl
          fullPrecompileReturnedFailureNested := by intros; rfl
          fullPrecompileReturnedFailureTop := by intros; rfl
          fullPrecompileManagedExceptionNested := by intros; rfl
          fullPrecompileManagedExceptionTop := by intros; rfl }
    cleanup := by intros; rfl }

theorem representative_admission_is_inhabited :
    ∃ admitted : Machine → Prop,
      admitted bytecodeTerminal ∧ admitted bytecodeContinuation ∧ admitted fullPrecompile ∧
        FixedWidthFacts admitted :=
  ⟨representativeAdmitted, Or.inl rfl, Or.inr (Or.inl rfl), Or.inr (Or.inr rfl), representativeFixedWidth⟩

/- These are constructive premise witnesses, including the source-binding and
future production-simulation interface. They establish logical inhabitation;
they do not discharge a production adapter simulation. -/
theorem representative_stage_d_assumptions_are_inhabited :
    ∃ (leaves : CanonicalLeaves) (oracles : ExternalOracles)
      (terminal : Refinement.StepAdmission leaves { machine := bytecodeTerminal })
      (continuation : Refinement.StepAdmission leaves { machine := bytecodeContinuation })
      (precompile : Refinement.StepAdmission leaves { machine := fullPrecompile }),
      terminal.admits = representativeAdmitted ∧
      continuation.admits = representativeAdmitted ∧
      precompile.admits = representativeAdmitted ∧
      FixedWidthFacts representativeAdmitted ∧
      SourceAdapterBindings leaves representativeAdmitted oracles ∧
      Refinement.ProductionLeafSimulation leaves leaves representativeAdmitted := by
  exact ⟨syntheticAdapters, syntheticOracles,
    representativeStepAdmission _ (Or.inl rfl),
    representativeStepAdmission _ (Or.inr (Or.inl rfl)),
    representativeStepAdmission _ (Or.inr (Or.inr rfl)),
    rfl, rfl, rfl, representativeFixedWidth, syntheticSourceAdapterBindings,
    syntheticProductionLeafSimulation⟩

theorem representative_single_iteration_control_agreement_is_inhabited :
    observe (Generated.driveIteration syntheticAdapters bytecodeTerminal) =
      observe (Reference.evaluateIteration syntheticAdapters bytecodeTerminal) ∧
    observe (Generated.driveIteration syntheticAdapters bytecodeContinuation) =
      observe (Reference.evaluateIteration syntheticAdapters bytecodeContinuation) ∧
    observe (Generated.driveIteration syntheticAdapters fullPrecompile) =
      observe (Reference.evaluateIteration syntheticAdapters fullPrecompile) := by
  refine ⟨?_, ?_, ?_⟩
  · exact (Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement
      syntheticAdapters { machine := bytecodeTerminal }
      (representativeStepAdmission _ (Or.inl rfl)) syntheticOracles
      syntheticSourceAdapterBindings representativeFixedWidth).1
  · exact (Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement
      syntheticAdapters { machine := bytecodeContinuation }
      (representativeStepAdmission _ (Or.inr (Or.inl rfl))) syntheticOracles
      syntheticSourceAdapterBindings representativeFixedWidth).1
  · exact (Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement
      syntheticAdapters { machine := fullPrecompile }
      (representativeStepAdmission _ (Or.inr (Or.inr rfl))) syntheticOracles
      syntheticSourceAdapterBindings representativeFixedWidth).1

theorem bytecode_terminal_subject : subjectOf bytecodeTerminal = .bytecode := rfl

theorem bytecode_terminal_route :
    Generated.classify bytecodeTerminal (.returned terminalStep) = .topLevelSuccess := rfl

theorem bytecode_continuation_subject : subjectOf bytecodeContinuation = .bytecode := rfl

theorem bytecode_suspend_route :
    Generated.classify bytecodeContinuation bytecodeSuspendOutcome.invocation = .suspend := rfl

theorem direct_inline_is_an_opcode_outcome :
    directInlineOpcodeOutcome.directInlineStaticPrecompile = some .succeeded := rfl

theorem full_precompile_subject : subjectOf fullPrecompile = .fullPrecompile := rfl

theorem full_precompile_uses_current_top_level_flag : topLevel fullPrecompile = true := rfl

theorem full_precompile_success_is_current_frame_completion :
    isFullPrecompileInvocation fullPrecompile fullPrecompileSuccessOutcome := by
  exact ⟨fullPrecompileTerminalResult, rfl, rfl, rfl, rfl⟩

theorem full_precompile_success_route :
    Generated.classify fullPrecompile fullPrecompileSuccessOutcome = .topLevelSuccess := rfl

private def nestedFullPrecompile : Machine :=
  machine (frame (.precompile 1) .fresh false []) [topLevelParent]

theorem full_precompile_managed_exception_topology_split :
    Generated.classify fullPrecompile (.fullPrecompileManagedException none) =
      .fullPrecompileManagedExceptionTop ∧
    Generated.classify nestedFullPrecompile (.fullPrecompileManagedException none) =
      .fullPrecompileManagedExceptionNested := by
  exact ⟨rfl, rfl⟩

/- A cancellation thrown by a tracing callback is not a `RunDispatchLoop` poll.
The current full-precompile frame may therefore escape abruptly, but it cannot
be classified as a full-frame driver-cancellation outcome. -/
theorem full_precompile_tracer_cancellation_is_escaped_not_driver_poll :
    isFullPrecompileInvocation fullPrecompile (.escapedInvocation "tracerCancelled") := by
  rfl

private def createMarkedPrecompile : Machine :=
  { fullPrecompile with current := { fullPrecompile.current with executionType := .create } }

theorem create_marked_precompile_cannot_enter_full_frame_success_domain :
    ¬ isFullPrecompileInvocation createMarkedPrecompile fullPrecompileSuccessOutcome := by
  simp [isFullPrecompileInvocation, createMarkedPrecompile, fullPrecompileSuccessOutcome,
    fullPrecompileSuccessStep, fullPrecompileTerminalResult, fullPrecompile, machine, frame] <;> rfl

theorem representative_bounds_include_bytecode_terminal : FrameFieldBounds bytecodeTerminal :=
  representativeFixedWidth.allAdmittedStatesFit _ (Or.inl rfl)

theorem representative_bounds_include_bytecode_continuation : FrameFieldBounds bytecodeContinuation :=
  representativeFixedWidth.allAdmittedStatesFit _ (Or.inr (Or.inl rfl))

theorem representative_bounds_include_full_precompile : FrameFieldBounds fullPrecompile :=
  representativeFixedWidth.allAdmittedStatesFit _ (Or.inr (Or.inr rfl))

end EvmFrameControlSettlementExtractor.Specification.AdmissionWitnesses
