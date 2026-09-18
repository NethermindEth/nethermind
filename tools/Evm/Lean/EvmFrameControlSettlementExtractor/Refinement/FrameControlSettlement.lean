-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameControlSettlementExtractor.Generated.EvmFrameControlSettlementKernel
import EvmFrameControlSettlementExtractor.Specification.Reference

/-!
Stage D checks control agreement for one admitted `ExecuteTransaction` loop
iteration over one shared `CanonicalLeaves` record. It deliberately does not
claim that production C# implements those leaves: `ProductionLeafSimulation`
states that separate, source-scoped obligation. Whole-loop induction/fuel,
transaction-processor settlement, and external world-state rollback remain
outside this package.
-/

namespace EvmFrameControlSettlementExtractor.Refinement

open Eip803x.Evm.FrameMachineState
open EvmFrameControlSettlementExtractor

/- An admitted-state predicate scopes every canonical leaf boundary. Output
closure is supplied explicitly because this package proves one iteration only. -/
structure StepAdmission (leaves : CanonicalLeaves) (input : Input) where
  admits : Machine → Prop
  inputAdmitted : admits input.machine
  sourceLoopPhase : ∀ machine, admits machine → isDriverLoopPhase machine
  freshReturnDataClearedAdmitted : ∀ machine, admits machine → machine.current.phase = .fresh →
    admits (leaves.preparation.clearReturnData machine)
  freshIntermediateAdmitted : ∀ machine, admits machine → machine.current.phase = .fresh →
    admits (leaves.preparation.prepareFresh (leaves.preparation.clearReturnData machine))
  preparedAdmitted : ∀ machine, admits machine →
    admits (Generated.prepare leaves machine)
  bytecodeDispatchOutputAdmitted : ∀ machine, admits machine → subjectOf machine = .bytecode →
    admits (machineAfterInvocation machine (leaves.dispatch.executeBytecodeFrame machine).invocation)
  fullPrecompileDispatchOutputAdmitted : ∀ machine, admits machine → subjectOf machine = .fullPrecompile →
    admits (machineAfterInvocation machine (leaves.dispatch.executeFullPrecompileFrame machine))
  bytecodeSettlementOutputAdmitted : ∀ machine, admits machine → subjectOf machine = .bytecode →
    admits (Generated.settleRoute leaves
      (Generated.classify machine (leaves.dispatch.executeBytecodeFrame machine).invocation)
      (leaves.dispatch.executeBytecodeFrame machine).invocation machine).machine
  fullPrecompileSettlementOutputAdmitted : ∀ machine, admits machine → subjectOf machine = .fullPrecompile →
    admits (Generated.settleRoute leaves
      (Generated.classify machine (leaves.dispatch.executeFullPrecompileFrame machine))
      (leaves.dispatch.executeFullPrecompileFrame machine) machine).machine
  iterationOutputAdmitted : ∀ machine, admits machine →
    admits (Generated.driveIteration leaves machine).machine
  topLevelParentShape : ∀ machine, admits machine →
    (machine.current.isTopLevel = true ↔ machine.parents = [])

/- Fixed-width bounds are produced for each source-reachable canonical leaf
output in the single iteration, rather than only for the input. -/
structure AdmittedAdapterOutputBounds (leaves : CanonicalLeaves) (admitted : Machine → Prop)
    (fixedWidth : FixedWidthFacts admitted) : Prop where
  freshReturnDataCleared : ∀ machine, admitted machine → machine.current.phase = .fresh →
    FrameFieldBounds (leaves.preparation.clearReturnData machine)
  freshIntermediate : ∀ machine, admitted machine → machine.current.phase = .fresh →
    FrameFieldBounds (leaves.preparation.prepareFresh (leaves.preparation.clearReturnData machine))
  prepared : ∀ machine, admitted machine → FrameFieldBounds (Generated.prepare leaves machine)
  bytecodeDispatch : ∀ machine, admitted machine → subjectOf machine = .bytecode →
    FrameFieldBounds (machineAfterInvocation machine (leaves.dispatch.executeBytecodeFrame machine).invocation)
  fullPrecompileDispatch : ∀ machine, admitted machine → subjectOf machine = .fullPrecompile →
    FrameFieldBounds (machineAfterInvocation machine (leaves.dispatch.executeFullPrecompileFrame machine))
  bytecodeSettlement : ∀ machine, admitted machine → subjectOf machine = .bytecode →
    FrameFieldBounds (Generated.settleRoute leaves
      (Generated.classify machine (leaves.dispatch.executeBytecodeFrame machine).invocation)
      (leaves.dispatch.executeBytecodeFrame machine).invocation machine).machine
  fullPrecompileSettlement : ∀ machine, admitted machine → subjectOf machine = .fullPrecompile →
    FrameFieldBounds (Generated.settleRoute leaves
      (Generated.classify machine (leaves.dispatch.executeFullPrecompileFrame machine))
      (leaves.dispatch.executeFullPrecompileFrame machine) machine).machine
  iteration : ∀ machine, admitted machine → FrameFieldBounds (Generated.driveIteration leaves machine).machine

theorem admitted_adapter_output_bounds
    (leaves : CanonicalLeaves) {input : Input} (admission : StepAdmission leaves input)
    (fixedWidth : FixedWidthFacts admission.admits) :
    AdmittedAdapterOutputBounds leaves admission.admits fixedWidth :=
  { freshReturnDataCleared := fun machine state phase =>
      fixedWidth.allAdmittedStatesFit _ (admission.freshReturnDataClearedAdmitted machine state phase)
    freshIntermediate := fun machine state phase =>
      fixedWidth.allAdmittedStatesFit _ (admission.freshIntermediateAdmitted machine state phase)
    prepared := fun machine state =>
      fixedWidth.allAdmittedStatesFit _ (admission.preparedAdmitted machine state)
    bytecodeDispatch := fun machine state subject =>
      fixedWidth.allAdmittedStatesFit _ (admission.bytecodeDispatchOutputAdmitted machine state subject)
    fullPrecompileDispatch := fun machine state subject =>
      fixedWidth.allAdmittedStatesFit _ (admission.fullPrecompileDispatchOutputAdmitted machine state subject)
    bytecodeSettlement := fun machine state subject =>
      fixedWidth.allAdmittedStatesFit _ (admission.bytecodeSettlementOutputAdmitted machine state subject)
    fullPrecompileSettlement := fun machine state subject =>
      fixedWidth.allAdmittedStatesFit _ (admission.fullPrecompileSettlementOutputAdmitted machine state subject)
    iteration := fun machine state =>
      fixedWidth.allAdmittedStatesFit _ (admission.iterationOutputAdmitted machine state) }

/- A derived step is the only domain on which production settlement and cleanup
legality may be stated. In particular, it cannot pair an arbitrary invocation
or route with an otherwise admitted frame. -/
structure AdmittedStep (leaves : CanonicalLeaves) (admitted : Machine → Prop)
    (input : Machine) (invocation : InvocationResult) (route : SettlementRoute) : Prop where
  inputAdmitted : admitted input
  invocationDerived : invocation = (Generated.dispatch leaves input).invocation
  routeDerived : route = Generated.classify input invocation

def settledOutcome (leaves : CanonicalLeaves) (input : Machine) (invocation : InvocationResult)
    (route : SettlementRoute) : ShellResult :=
  let dispatched := Generated.dispatch leaves input
  let settled := Generated.settleRoute leaves route invocation input
  { settled with directInlineStaticPrecompile := dispatched.directInlineStaticPrecompile }

/- Each production settlement leaf is an explicit future proof obligation. The
leaf only receives a dispatch-derived `AdmittedStep`; nested CREATE deposit leaves
also require the actual deposit decision that selects them. -/
structure ProductionSettlementLeafSimulation (production canonical : CanonicalLeaves)
    (admitted : Machine → Prop) : Prop where
  continueStep : ∀ {input invocation},
    AdmittedStep production admitted input invocation .continued →
      production.settlement.continueStep invocation input = canonical.settlement.continueStep invocation input
  suspendStep : ∀ {input invocation},
    AdmittedStep production admitted input invocation .suspend →
      production.settlement.suspendStep invocation input = canonical.settlement.suspendStep invocation input
  regularSuccessNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .regularSuccessNested →
      production.settlement.regularSuccessNested invocation input =
        canonical.settlement.regularSuccessNested invocation input
  topLevelSuccess : ∀ {input invocation},
    AdmittedStep production admitted input invocation .topLevelSuccess →
      production.settlement.topLevelSuccess invocation input = canonical.settlement.topLevelSuccess invocation input
  nestedCreateDecision : ∀ {input invocation},
    AdmittedStep production admitted input invocation .createSuccessNested →
      production.settlement.classifyNestedCreateCodeDeposit invocation input =
        canonical.settlement.classifyNestedCreateCodeDeposit invocation input
  createSuccessNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .createSuccessNested →
      production.settlement.classifyNestedCreateCodeDeposit invocation input = .deposited →
        production.settlement.createSuccessNested invocation input =
          canonical.settlement.createSuccessNested invocation input
  codeDepositInvalidNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .createSuccessNested →
      production.settlement.classifyNestedCreateCodeDeposit invocation input = .invalidCode →
        production.settlement.codeDepositInvalidNested invocation input =
          canonical.settlement.codeDepositInvalidNested invocation input
  codeDepositOutOfGasNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .createSuccessNested →
      production.settlement.classifyNestedCreateCodeDeposit invocation input = .outOfGas →
        production.settlement.codeDepositOutOfGasNested invocation input =
          canonical.settlement.codeDepositOutOfGasNested invocation input
  revertNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .revertNested →
      production.settlement.revertNested invocation input = canonical.settlement.revertNested invocation input
  revertTop : ∀ {input invocation},
    AdmittedStep production admitted input invocation .revertTop →
      production.settlement.revertTop invocation input = canonical.settlement.revertTop invocation input
  exceptionTop : ∀ {input invocation},
    AdmittedStep production admitted input invocation .exceptionTop →
      production.settlement.exceptionTop invocation input = canonical.settlement.exceptionTop invocation input
  exceptionNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .exceptionNested →
      production.settlement.exceptionNested invocation input = canonical.settlement.exceptionNested invocation input
  fullPrecompileOutOfGasNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .fullPrecompileOutOfGasNested →
      production.settlement.fullPrecompileOutOfGasNested invocation input =
        canonical.settlement.fullPrecompileOutOfGasNested invocation input
  fullPrecompileOutOfGasTop : ∀ {input invocation},
    AdmittedStep production admitted input invocation .fullPrecompileOutOfGasTop →
      production.settlement.fullPrecompileOutOfGasTop invocation input =
        canonical.settlement.fullPrecompileOutOfGasTop invocation input
  fullPrecompileReturnedFailureNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .fullPrecompileReturnedFailureNested →
      production.settlement.fullPrecompileReturnedFailureNested invocation input =
        canonical.settlement.fullPrecompileReturnedFailureNested invocation input
  fullPrecompileReturnedFailureTop : ∀ {input invocation},
    AdmittedStep production admitted input invocation .fullPrecompileReturnedFailureTop →
      production.settlement.fullPrecompileReturnedFailureTop invocation input =
        canonical.settlement.fullPrecompileReturnedFailureTop invocation input
  fullPrecompileManagedExceptionNested : ∀ {input invocation},
    AdmittedStep production admitted input invocation .fullPrecompileManagedExceptionNested →
      production.settlement.fullPrecompileManagedExceptionNested invocation input =
        canonical.settlement.fullPrecompileManagedExceptionNested invocation input
  fullPrecompileManagedExceptionTop : ∀ {input invocation},
    AdmittedStep production admitted input invocation .fullPrecompileManagedExceptionTop →
      production.settlement.fullPrecompileManagedExceptionTop invocation input =
        canonical.settlement.fullPrecompileManagedExceptionTop invocation input

/- This record is deliberately not an argument to the public control-agreement
theorem. Supplying it is the open production-to-canonical simulation work. -/
structure ProductionLeafSimulation (production canonical : CanonicalLeaves)
    (admitted : Machine → Prop) : Prop where
  prepareFresh : ∀ input, admitted input → input.current.phase = .fresh →
    production.preparation.prepareFresh (production.preparation.clearReturnData input) =
      canonical.preparation.prepareFresh (canonical.preparation.clearReturnData input)
  prepareContinuation : ∀ input, admitted input → input.current.phase = .continuation →
    production.preparation.prepareContinuation input = canonical.preparation.prepareContinuation input
  clearReturnData : ∀ input, admitted input → input.current.phase = .fresh →
    production.preparation.clearReturnData input = canonical.preparation.clearReturnData input
  bytecodeDispatch : ∀ input, admitted input → subjectOf input = .bytecode →
    production.dispatch.executeBytecodeFrame input = canonical.dispatch.executeBytecodeFrame input
  fullPrecompileDispatch : ∀ input, admitted input → subjectOf input = .fullPrecompile →
    production.dispatch.executeFullPrecompileFrame input = canonical.dispatch.executeFullPrecompileFrame input
  directInlineEligibility : ∀ input, admitted input → subjectOf input = .bytecode →
    production.dispatch.directInlineStaticPrecompileEligible input =
      canonical.dispatch.directInlineStaticPrecompileEligible input
  settlement : StateGasRefundTheorem → PrecompilePricingTheorem →
    ProductionSettlementLeafSimulation production canonical admitted
  cleanup : ∀ {input invocation route},
    AdmittedStep production admitted input invocation route →
      production.cleanup (settledOutcome production input invocation route) =
        canonical.cleanup (settledOutcome production input invocation route)

/- These are observable source-boundary properties. They do not assert that
opaque leaves prove journal, gas, trace, or lifecycle effects internally. -/
structure DriverBoundarySound (leaves : CanonicalLeaves) (admitted : Machine → Prop)
    (oracles : ExternalOracles) : Prop where
  bytecodeFrameBound : ∀ machine, admitted machine → subjectOf machine = .bytecode →
    leaves.dispatch.executeBytecodeFrame machine = oracles.opcodeFrame machine
  fullPrecompileFrameBound : ∀ machine, admitted machine → subjectOf machine = .fullPrecompile →
    leaves.dispatch.executeFullPrecompileFrame machine = oracles.fullPrecompileFrame machine
  bytecodeOutcomeHasBytecodeDomain : ∀ machine, admitted machine → subjectOf machine = .bytecode →
    isBytecodeInvocation (leaves.dispatch.executeBytecodeFrame machine).invocation
  fullPrecompileOutcomeCompletesCurrentFrame : ∀ machine, admitted machine →
    subjectOf machine = .fullPrecompile →
      isFullPrecompileInvocation machine (leaves.dispatch.executeFullPrecompileFrame machine)
  directInlineIsBytecodeOpcode : ∀ machine, admitted machine → subjectOf machine = .bytecode →
    (leaves.dispatch.executeBytecodeFrame machine).directInlineStaticPrecompile ≠ none →
      machine.current.kind = .bytecode ∧
        leaves.dispatch.directInlineStaticPrecompileEligible machine = true
  fullFrameUsesCurrentPrecompile : ∀ machine, admitted machine →
    subjectOf machine = .fullPrecompile →
      ∃ address, machine.current.kind = .precompile address
  bytecodeCancellationHasExactBoundary : ∀ machine, admitted machine → subjectOf machine = .bytecode → ∀ boundary reason,
    (leaves.dispatch.executeBytecodeFrame machine).invocation = .cancelled boundary reason →
      machine.current.dispatchTable.cancelable = true ∧ reason ≠ "" ∧
        match boundary with
        | .beforeFirstOpcode => machine.current.pc < machine.current.code.length
        | .afterCompleteBatch completedOpcodeCount successorPc =>
            0 < completedOpcodeCount ∧ completedOpcodeCount % 1024 = 0 ∧
              successorPc < machine.current.code.length
  fullFrameHasNoDriverCancellation : ∀ machine, admitted machine → ∀ boundary reason,
    subjectOf machine = .fullPrecompile →
      leaves.dispatch.executeFullPrecompileFrame machine ≠ .cancelled boundary reason

theorem prepare_control_agrees
    (leaves : CanonicalLeaves) {input : Input} (admission : StepAdmission leaves input)
    (machine : Machine) (admitted : admission.admits machine) :
    Generated.prepare leaves machine = Reference.enterFrame leaves machine := by
  rcases admission.sourceLoopPhase machine admitted with fresh | continuation
  · simp [Generated.prepare, Reference.enterFrame, Reference.enactEntry, Reference.planEntry, fresh]
  · simp [Generated.prepare, Reference.enterFrame, Reference.enactEntry, Reference.planEntry, continuation]

theorem canonical_dispatch_agrees
    (leaves : CanonicalLeaves) (admitted : Machine → Prop) (oracles : ExternalOracles)
    (bindings : SourceAdapterBindings leaves admitted oracles) (machine : Machine)
    (state : admitted machine) :
    Generated.dispatch leaves machine = (Reference.chooseInvocation leaves machine).asDriverInvocation := by
  cases kind : machine.current.kind with
  | bytecode =>
      have subject : subjectOf machine = .bytecode := by simp [subjectOf, kind]
      have bound := bindings.opcodeFrameBound bindings.stageARoutingTheorem
        bindings.stageBInlineTheorem machine state subject
      let oracleDriver : DriverInvocation :=
        { invocation := (oracles.opcodeFrame machine).invocation
          directInlineStaticPrecompile := (oracles.opcodeFrame machine).directInlineStaticPrecompile }
      have lifted := congrArg (fun outcome : BytecodeFrameOutcome =>
        ({ invocation := outcome.invocation
           directInlineStaticPrecompile := outcome.directInlineStaticPrecompile } : DriverInvocation)) bound
      calc
        Generated.dispatch leaves machine = oracleDriver := by
          simpa [oracleDriver, Generated.dispatch, subjectOf, kind] using lifted
        _ = (Reference.chooseInvocation leaves machine).asDriverInvocation := by
          simpa [oracleDriver, Reference.chooseInvocation, Reference.planDispatch, kind,
            Reference.Decision.asDriverInvocation] using lifted.symm
  | precompile address =>
      have subject : subjectOf machine = .fullPrecompile := by simp [subjectOf, kind]
      have bound := bindings.fullPrecompileFrameBound bindings.stageCFullTheorem
        bindings.precompilePricingTheorem machine state subject
      let oracleDriver : DriverInvocation :=
        { invocation := oracles.fullPrecompileFrame machine
          directInlineStaticPrecompile := none }
      have lifted := congrArg (fun invocation : InvocationResult =>
        ({ invocation, directInlineStaticPrecompile := none } : DriverInvocation)) bound
      calc
        Generated.dispatch leaves machine = oracleDriver := by
          simpa [oracleDriver, Generated.dispatch, subjectOf, kind] using lifted
        _ = (Reference.chooseInvocation leaves machine).asDriverInvocation := by
          simpa [oracleDriver, Reference.chooseInvocation, Reference.planDispatch, kind,
            Reference.Decision.asDriverInvocation] using lifted.symm

theorem classify_control_agrees (machine : Machine) (invocation : InvocationResult)
    (inline : Option DirectInlineStaticPrecompileOutcome) :
    Generated.classify machine invocation =
      Reference.routeForDecision { frame := machine, invocation, inlineStaticPrecompile := inline } := by
  cases invocation <;> rfl

theorem classify_decision_control_agrees (decision : Reference.Decision) :
    Generated.classify decision.frame decision.invocation = Reference.routeForDecision decision := by
  cases decision with
  | mk frame invocation inline => exact classify_control_agrees frame invocation inline

theorem settle_route_control_agrees (leaves : CanonicalLeaves) (route : SettlementRoute)
    (invocation : InvocationResult) (machine : Machine)
    (inline : Option DirectInlineStaticPrecompileOutcome) :
    Generated.settleRoute leaves route invocation machine =
      Reference.enactSettlement leaves
        { frame := machine, invocation, inlineStaticPrecompile := inline }
        (Reference.planSettlement route) := by
  cases route <;>
    simp [Generated.settleRoute, Generated.settleNestedCreate, Generated.tagged,
      Reference.enactSettlement, Reference.planSettlement, Reference.settleNestedCreation,
      Reference.withRoute] <;> rfl

theorem settle_decision_control_agrees (leaves : CanonicalLeaves) (decision : Reference.Decision) :
    Generated.settleRoute leaves (Reference.routeForDecision decision) decision.invocation decision.frame =
      Reference.settleDecision leaves decision := by
  cases decision with
  | mk frame invocation inline =>
      exact settle_route_control_agrees leaves (Reference.routeForDecision
        { frame, invocation, inlineStaticPrecompile := inline }) invocation frame inline

theorem cleanup_terminal_control_agrees (leaves : CanonicalLeaves) (outcome : ShellResult) :
    Generated.cleanupTerminal leaves outcome = Reference.closeTerminal leaves outcome := by
  cases termination : outcome.termination <;>
    simp [Generated.cleanupTerminal, Reference.closeTerminal, Reference.planClosing, termination]

theorem boundary_sound
    (leaves : CanonicalLeaves) (admitted : Machine → Prop) (oracles : ExternalOracles)
    (bindings : SourceAdapterBindings leaves admitted oracles) :
    DriverBoundarySound leaves admitted oracles :=
  { bytecodeFrameBound := fun machine state subject =>
      bindings.opcodeFrameBound bindings.stageARoutingTheorem bindings.stageBInlineTheorem machine state subject
    fullPrecompileFrameBound := fun machine state subject =>
      bindings.fullPrecompileFrameBound bindings.stageCFullTheorem bindings.precompilePricingTheorem machine state subject
    bytecodeOutcomeHasBytecodeDomain := bindings.bytecodeOutcomeHasBytecodeDomain
    fullPrecompileOutcomeCompletesCurrentFrame := bindings.fullPrecompileOutcomeCompletesCurrentFrame
    directInlineIsBytecodeOpcode := bindings.directInlineOnlyFromBytecodeOpcode
    fullFrameUsesCurrentPrecompile := fun machine _ subject => full_subject_selects_current_precompile machine subject
    bytecodeCancellationHasExactBoundary := bindings.bytecodeCancellationUsesDispatchLoopBoundary
    fullFrameHasNoDriverCancellation := bindings.fullPrecompileDoesNotYieldDriverCancellation }

/- Hash-pinned canonical control agreement for one admitted loop iteration.
The shared canonical leaf record makes this a check of independently organized
handwritten control transcriptions, not a claim that actual production leaf
implementations have been simulated. -/
theorem hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement
    (leaves : CanonicalLeaves) (input : Input)
    (admission : StepAdmission leaves input)
    (oracles : ExternalOracles) (bindings : SourceAdapterBindings leaves admission.admits oracles)
    (fixedWidth : FixedWidthFacts admission.admits) :
    observe (Generated.driveIteration leaves input.machine) =
    observe (Reference.evaluateIteration leaves input.machine) ∧
    FrameFieldBounds (Generated.driveIteration leaves input.machine).machine ∧
    AdmittedAdapterOutputBounds leaves admission.admits fixedWidth ∧
    isDriverLoopPhase input.machine ∧
    isDriverLoopPhase (Generated.driveIteration leaves input.machine).machine ∧
    (input.machine.current.isTopLevel = true ↔ input.machine.parents = []) ∧
    ((Generated.driveIteration leaves input.machine).machine.current.isTopLevel = true ↔
      (Generated.driveIteration leaves input.machine).machine.parents = []) ∧
    DriverBoundarySound leaves admission.admits oracles := by
  have inputAdmitted := admission.inputAdmitted
  have preparation := prepare_control_agrees leaves admission input.machine inputAdmitted
  have preparedAdmitted : admission.admits (Reference.enterFrame leaves input.machine) := by
    simpa only [preparation] using admission.preparedAdmitted input.machine inputAdmitted
  have dispatch := canonical_dispatch_agrees leaves admission.admits oracles bindings
    (Reference.enterFrame leaves input.machine) preparedAdmitted
  have decisionFrame := Reference.chooseInvocation_preserves_frame leaves
    (Reference.enterFrame leaves input.machine)
  have decisionAdmitted : admission.admits
      (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine)).frame := by
    rw [decisionFrame]
    exact preparedAdmitted
  have decisionClass := classify_decision_control_agrees
    (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine))
  rw [decisionFrame] at decisionClass
  have classifiedDriver :
      Generated.classify (Reference.enterFrame leaves input.machine)
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine)).asDriverInvocation.invocation =
        Reference.routeForDecision (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine)) := by
    simpa only [Reference.Decision.asDriverInvocation] using decisionClass
  have settlementDriver :
      Generated.settleRoute leaves
          (Reference.routeForDecision (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine)))
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine)).asDriverInvocation.invocation
          (Reference.enterFrame leaves input.machine) =
        Reference.settleDecision leaves
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine)) := by
    have settled := settle_decision_control_agrees leaves
      (Reference.chooseInvocation leaves (Reference.enterFrame leaves input.machine))
    rw [decisionFrame] at settled
    simpa only [Reference.Decision.asDriverInvocation] using settled
  have outputAdmitted := admission.iterationOutputAdmitted input.machine inputAdmitted
  refine ⟨?_, fixedWidth.allAdmittedStatesFit _ outputAdmitted,
    admitted_adapter_output_bounds leaves admission fixedWidth,
    admission.sourceLoopPhase input.machine inputAdmitted,
    admission.sourceLoopPhase _ outputAdmitted,
    admission.topLevelParentShape input.machine inputAdmitted,
    admission.topLevelParentShape _ outputAdmitted,
    boundary_sound leaves admission.admits oracles bindings⟩
  simp only [Generated.driveIteration, Reference.evaluateIteration, Reference.settleDecision]
  rw [preparation]
  rw [dispatch]
  rw [classifiedDriver]
  rw [settlementDriver]
  exact congrArg observe (cleanup_terminal_control_agrees leaves _)

end EvmFrameControlSettlementExtractor.Refinement
