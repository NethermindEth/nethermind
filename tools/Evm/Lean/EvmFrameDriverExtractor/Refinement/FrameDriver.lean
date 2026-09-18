-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameDriverExtractor.Generated.EvmFrameDriverKernel
import EvmFrameDriverExtractor.Specification.Reference

/-!
The Stage E theorem is deliberately a source-attached, one-step parametric
same-leaf algebra agreement. It proves that the generated branch algebra and
the independent reference make the same choice and call the same leaf for one
input when supplied the same leaf functions. It does not establish that
production C# effects satisfy those equalities, does not induct over fuel, and
does not establish reachability of a later frame.
-/

namespace Eip803x.Evm.FrameDriver.Refinement

open Eip803x.Evm.FrameMachineState
open Eip803x.Evm.FrameDriver

structure SameLeafAgreement (production canonical : DriverLeaves) : Prop where
  clearReturnData : production.preparation.clearReturnData = canonical.preparation.clearReturnData
  prepareFresh : production.preparation.prepareFresh = canonical.preparation.prepareFresh
  prepareContinuation : production.preparation.prepareContinuation = canonical.preparation.prepareContinuation
  runBytecode : production.dispatch.runBytecode = canonical.dispatch.runBytecode
  runFullPrecompile : production.dispatch.runFullPrecompile = canonical.dispatch.runFullPrecompile
  classifyNestedCreateDeposit :
    production.classifyNestedCreateDeposit = canonical.classifyNestedCreateDeposit
  continueStep : production.settlement.continueStep = canonical.settlement.continueStep
  suspendStep : production.settlement.suspendStep = canonical.settlement.suspendStep
  childReturn : production.settlement.childReturn = canonical.settlement.childReturn
  topLevelReturn : production.settlement.topLevelReturn = canonical.settlement.topLevelReturn
  fullPrecompileFailure :
    production.settlement.fullPrecompileFailure = canonical.settlement.fullPrecompileFailure
  cancelled : production.settlement.cancelled = canonical.settlement.cancelled
  escaped : production.settlement.escaped = canonical.settlement.escaped
  cleanup : production.settlement.cleanup = canonical.settlement.cleanup

theorem same_leaf_eq
    (production canonical : DriverLeaves) (agreement : SameLeafAgreement production canonical) :
    production = canonical := by
  cases production with
  | mk productionPreparation productionDispatch productionSettlement productionDeposit =>
    cases canonical with
    | mk canonicalPreparation canonicalDispatch canonicalSettlement canonicalDeposit =>
      have preparation : productionPreparation = canonicalPreparation := by
        cases productionPreparation with
        | mk productionClear productionFresh productionContinuation =>
          cases canonicalPreparation with
          | mk canonicalClear canonicalFresh canonicalContinuation =>
            cases agreement.clearReturnData
            cases agreement.prepareFresh
            cases agreement.prepareContinuation
            rfl
      have dispatch : productionDispatch = canonicalDispatch := by
        cases productionDispatch with
        | mk productionBytecode productionPrecompile =>
          cases canonicalDispatch with
          | mk canonicalBytecode canonicalPrecompile =>
            cases agreement.runBytecode
            cases agreement.runFullPrecompile
            rfl
      have settlement : productionSettlement = canonicalSettlement := by
        cases productionSettlement with
        | mk productionContinue productionSuspend productionChild productionTop
            productionPrecompileFailure productionCancelled productionEscaped productionCleanup =>
          cases canonicalSettlement with
          | mk canonicalContinue canonicalSuspend canonicalChild canonicalTop
              canonicalPrecompileFailure canonicalCancelled canonicalEscaped canonicalCleanup =>
            cases agreement.continueStep
            cases agreement.suspendStep
            cases agreement.childReturn
            cases agreement.topLevelReturn
            cases agreement.fullPrecompileFailure
            cases agreement.cancelled
            cases agreement.escaped
            cases agreement.cleanup
            rfl
      cases preparation
      cases dispatch
      cases settlement
      cases agreement.classifyNestedCreateDeposit
      rfl

def sourceAdmitted (machine : Machine) : Prop := admitted machine

theorem prepare_refines (leaves : DriverLeaves) (machine : Machine) :
    Generated.prepare leaves machine = Reference.enterFrame leaves machine := by
  cases phase : machine.current.phase <;>
    simp [Generated.prepare, Reference.enterFrame, Reference.planEntry, phase]

theorem dispatch_refines (leaves : DriverLeaves) (machine : Machine) :
    Generated.dispatch leaves machine = (Reference.chooseInvocation leaves machine).invocation := by
  cases kind : machine.current.kind <;>
    simp [Generated.dispatch, Reference.chooseInvocation, Reference.planDispatch, subjectOf, kind]

theorem classification_refines (machine : Machine) (invocation : Invocation) :
    Generated.classifyInvocation machine invocation = classify machine invocation := by
  cases invocation with
  | bytecode outcome inline =>
      cases outcome with
      | returned step =>
          cases result : step.result with
          | «continue» frame => simp [Generated.classifyInvocation, Generated.classifyBytecodeStep,
              classify, classifyBytecodeReturned, result]
          | suspend parent child => simp [Generated.classifyInvocation, Generated.classifyBytecodeStep,
              classify, classifyBytecodeReturned, result]
          | halt frameResult =>
              cases exit : frameResult.exit <;>
                simp [Generated.classifyInvocation, Generated.classifyBytecodeStep,
                  Generated.classifyReturnedHalt, classify, classifyBytecodeReturned,
                  returnedRoute, result, exit]
      | thrownEvm kind =>
          simp [Generated.classifyInvocation, classify]
      | thrownOverflow =>
          simp [Generated.classifyInvocation, classify]
      | escaped reason =>
          simp [Generated.classifyInvocation, classify]
      | cancelled boundary reason =>
          simp [Generated.classifyInvocation, classify]
  | fullPrecompile outcome =>
      cases outcome with
      | returned step =>
          cases result : step.result with
          | «continue» frame => simp [Generated.classifyInvocation, Generated.classifyPrecompileStep,
              classify, classifyPrecompileReturned, result]
          | suspend parent child => simp [Generated.classifyInvocation, Generated.classifyPrecompileStep,
              classify, classifyPrecompileReturned, result]
          | halt frameResult =>
              cases exit : frameResult.exit <;>
                cases precompile : frameResult.precompileSuccess <;>
                  simp [Generated.classifyInvocation, Generated.classifyPrecompileStep,
                    Generated.classifyReturnedHalt, classify, classifyPrecompileReturned,
                    returnedRoute, result, exit, precompile] <;>
                  cases ‹Bool› <;>
                    simp
      | outOfGas =>
          simp [Generated.classifyInvocation, classify]
      | returnedFailure reason =>
          simp [Generated.classifyInvocation, classify]
      | managedException reason =>
          simp [Generated.classifyInvocation, classify]
      | escaped reason =>
          simp [Generated.classifyInvocation, classify]

theorem classify_bytecode_reference (machine : Machine) (outcome : BytecodeInvocation)
    (inline : Option DirectInlineStaticPrecompileOutcome) :
    classify machine (.bytecode outcome inline) = Reference.classifyBytecode machine outcome := by
  cases outcome with
  | returned step =>
      cases result : step.result with
      | «continue» _ =>
          simp [classify, Reference.classifyBytecode, classifyBytecodeReturned, result]
      | suspend _ _ =>
          simp [classify, Reference.classifyBytecode, classifyBytecodeReturned, result]
      | halt frameResult =>
          cases exit : frameResult.exit with
          | success =>
              cases precompile : frameResult.precompileSuccess with
              | none =>
                  simp [classify, Reference.classifyBytecode, classifyBytecodeReturned,
                    Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit] <;> rfl
              | some value =>
                  cases value <;>
                    simp [classify, Reference.classifyBytecode, classifyBytecodeReturned,
                      Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit] <;> rfl
          | revert =>
              cases precompile : frameResult.precompileSuccess with
              | none =>
                  simp [classify, Reference.classifyBytecode, classifyBytecodeReturned,
                    Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit] <;> rfl
              | some value =>
                  cases value <;>
                    simp [classify, Reference.classifyBytecode, classifyBytecodeReturned,
                      Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit] <;> rfl
          | exception kind =>
              cases precompile : frameResult.precompileSuccess with
              | none =>
                  simp [classify, Reference.classifyBytecode, classifyBytecodeReturned,
                    Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit] <;> rfl
              | some value =>
                  cases value <;>
                    simp [classify, Reference.classifyBytecode, classifyBytecodeReturned,
                      Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit] <;> rfl
  | thrownEvm _ =>
      simp [classify, Reference.classifyBytecode] <;> rfl
  | thrownOverflow =>
      simp [classify, Reference.classifyBytecode] <;> rfl
  | escaped _ =>
      simp [classify, Reference.classifyBytecode] <;> rfl
  | cancelled _ _ =>
      simp [classify, Reference.classifyBytecode] <;> rfl

theorem classify_precompile_reference (machine : Machine) (outcome : PrecompileInvocation) :
    classify machine (.fullPrecompile outcome) = Reference.classifyPrecompile machine outcome := by
  cases outcome with
  | returned step =>
      cases result : step.result with
      | «continue» _ =>
          simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned, result]
      | suspend _ _ =>
          simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned, result]
      | halt frameResult =>
          cases exit : frameResult.exit with
          | success =>
              cases precompile : frameResult.precompileSuccess with
              | none =>
                  simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned,
                    Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit, precompile] <;> rfl
              | some value =>
                  cases value <;>
                    simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned,
                      Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit, precompile] <;> rfl
          | revert =>
              cases precompile : frameResult.precompileSuccess with
              | none =>
                  simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned,
                    Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit, precompile] <;> rfl
              | some value =>
                  cases value <;>
                    simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned,
                      Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit, precompile] <;> rfl
          | exception kind =>
              cases precompile : frameResult.precompileSuccess with
              | none =>
                  simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned,
                    Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit, precompile] <;> rfl
              | some value =>
                  cases value <;>
                    simp [classify, Reference.classifyPrecompile, classifyPrecompileReturned,
                      Reference.classifyReturnedHalt, topLevel, returnedRoute, result, exit, precompile] <;> rfl
  | outOfGas =>
      simp [classify, Reference.classifyPrecompile] <;> rfl
  | returnedFailure _ =>
      simp [classify, Reference.classifyPrecompile] <;> rfl
  | managedException _ =>
      simp [classify, Reference.classifyPrecompile] <;> rfl
  | escaped _ =>
      simp [classify, Reference.classifyPrecompile] <;> rfl

theorem settlement_refines (leaves : DriverLeaves) (route : SettlementRoute)
    (invocation : Invocation) (machine : Machine) :
    Generated.settleInvocation leaves route invocation machine =
      Reference.enactSettlement leaves (Reference.planSettlement machine route)
        invocation machine := by
  cases route <;> rfl

theorem cleanup_refines (leaves : DriverLeaves) (result : DriverResult) :
    Generated.cleanup leaves result = Reference.closeTerminal leaves result := by
  cases termination : result.termination <;>
    simp [Generated.cleanup, Reference.closeTerminal, cleanupTerminal, termination]

theorem admitted_source_control_ready : Generated.sourceControlReady = true := by
  native_decide

theorem admitted_control_topology_ready : Generated.controlTopologyReady = true := by
  native_decide

theorem admitted_control_plan_ready : Generated.controlPlanReady = true := by
  native_decide

theorem generated_step_agrees_reference (leaves : DriverLeaves) (machine : Machine) :
    Generated.stepOnce leaves machine =
      Reference.evaluateStep Generated.admittedSourceControl leaves machine := by
  have preparation : Generated.prepare leaves machine = Reference.enterFrame leaves machine :=
    prepare_refines leaves machine
  have dispatchEntered :
      Generated.dispatch leaves (Reference.enterFrame leaves machine) =
        (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)).invocation :=
    dispatch_refines leaves (Reference.enterFrame leaves machine)
  have invocationFrame :
      (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)).frame =
        Reference.enterFrame leaves machine := by
    cases plan : Reference.planDispatch (Reference.enterFrame leaves machine) <;>
      simp [Reference.chooseInvocation, plan]
  have classification :
      Generated.classifyInvocation (Reference.enterFrame leaves machine)
          (Generated.dispatch leaves (Reference.enterFrame leaves machine)) =
        Reference.routeForDecision
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)) := by
    rw [dispatchEntered]
    calc
      Generated.classifyInvocation (Reference.enterFrame leaves machine)
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)).invocation =
        classify (Reference.enterFrame leaves machine)
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)).invocation :=
        classification_refines _ _
      _ = Reference.routeForDecision
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)) := by
        cases plan : Reference.planDispatch (Reference.enterFrame leaves machine) <;>
          simp [Reference.routeForDecision, Reference.chooseInvocation, plan,
            classify_bytecode_reference, classify_precompile_reference]
  have settlement :
      Generated.settleInvocation leaves
          (Generated.classifyInvocation (Reference.enterFrame leaves machine)
            (Generated.dispatch leaves (Reference.enterFrame leaves machine)))
          (Generated.dispatch leaves (Reference.enterFrame leaves machine))
          (Reference.enterFrame leaves machine) =
        Reference.enactSettlement leaves
          (Reference.planSettlement
            (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)).frame
            (Reference.routeForDecision
              (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine))))
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)).invocation
          (Reference.chooseInvocation leaves (Reference.enterFrame leaves machine)).frame := by
    rw [classification, dispatchEntered, invocationFrame]
    exact settlement_refines leaves _ _ _
  have referenceSourceControlReady :
      Eip803x.Evm.FrameDriver.sourceControlReady Generated.admittedSourceControl = true := by
    simpa [Generated.sourceControlReady] using admitted_source_control_ready
  have executePlanAgreesBody :
      Generated.executePlan leaves machine Generated.admittedControlPlan =
        Generated.stepOnceBody leaves machine := by
    rfl
  have bodyAgreesReference :
      Generated.stepOnceBody leaves machine =
        Reference.evaluateStepBody leaves machine := by
    simp only [Generated.stepOnceBody, Reference.evaluateStepBody]
    rw [preparation, settlement, dispatchEntered, cleanup_refines]
  by_cases phase : phaseReady machine = true
  · simp only [Generated.stepOnce, Reference.evaluateStep,
      admitted_source_control_ready, admitted_control_topology_ready,
      admitted_control_plan_ready, referenceSourceControlReady,
      Bool.and_true, phase, if_true]
    exact executePlanAgreesBody.trans bodyAgreesReference
  · simp only [Generated.stepOnce, Reference.evaluateStep,
      admitted_source_control_ready, admitted_control_topology_ready,
      admitted_control_plan_ready, referenceSourceControlReady,
      Bool.true_and, Bool.and_true, phase, Bool.false_eq_true]
    rfl

theorem admitted_plan_agrees_reference
    (production canonical : DriverLeaves) (agreement : SameLeafAgreement production canonical)
    (machine : Machine) (admittedInput : sourceAdmitted machine) :
    Generated.stepOnce production machine =
      Reference.evaluateStep Generated.admittedSourceControl canonical machine := by
  have phaseReadyInput : phaseReady machine = true := by
    cases admittedInput with
    | inl fresh => simp [phaseReady, fresh]
    | inr continuation => simp [phaseReady, continuation]
  have leavesEqual : production = canonical := same_leaf_eq production canonical agreement
  subst production
  simpa [Generated.stepOnce, Reference.evaluateStep, phaseReadyInput] using
     generated_step_agrees_reference canonical machine

theorem admitted_plan_run_agrees_reference
    (production canonical : DriverLeaves) (agreement : SameLeafAgreement production canonical)
    (fuel : Nat) (machine : Machine) (admittedInput : sourceAdmitted machine) :
    Generated.runOne fuel production machine =
      Reference.runOne fuel Generated.admittedSourceControl canonical machine := by
  have leavesEqual : production = canonical := same_leaf_eq production canonical agreement
  subst production
  cases fuel with
  | zero => rfl
  | succ fuel =>
      simp only [Generated.runOne, Reference.runOne]
      exact congrArg FuelResult.stepped
        (admitted_plan_agrees_reference canonical canonical agreement machine admittedInput)

end Eip803x.Evm.FrameDriver.Refinement
