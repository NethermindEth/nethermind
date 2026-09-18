-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import PrecompileFullFrameExtractor.Generated.PrecompileFullFrame
import PrecompileFullFrameExtractor.Specification.Reference
import Eip803x.Refinement.PrecompileGasPricing

namespace Eip803x.PrecompileFullFrame.Refinement

open Evm.FrameMachineState

theorem prepare_refines (front : FrontOperations) (input : Input) :
    Generated.prepare front input = Reference.enter front input := by
  unfold Generated.prepare Reference.enter actionAddress
  split <;> rfl

theorem source_execute_precompile_refines_reference
    (front : FrontOperations) (oracle : LeafOracle) (input : Input) :
    Generated.executePrecompile front oracle input = Reference.executePrecompile front oracle input := by
  unfold Generated.executePrecompile Reference.executePrecompile Reference.select
  rw [prepare_refines]
  cases pricing : (Eip803x.Generated.PrecompileGasPricingKernel.tryConsume
    input.machine.current.gas.gas.gasLeft input.baseCost input.dataCost).outcome <;>
    simp only [pricing]
  · cases leaf : oracle (actionAddress input) input.machine.current.input <;>
      simp [Reference.project, Generated.hard, Generated.result, Generated.installGas, Generated.prefixEvents, Reference.exitFor, Reference.routeFor]
    split <;> rfl
  · rfl
  · rfl

theorem source_execute_refines_reference
    (front : FrontOperations) (oracle : LeafOracle) (entry : Entry) :
    Generated.execute front oracle entry = Reference.execute front oracle entry := by
  cases entry with
  | fullFrame input => exact source_execute_precompile_refines_reference front oracle input
  | outerEvmException machine kind => rfl
  | outerOverflow machine => rfl

def RawAdmitted (raw : RawResult) : Prop :=
  (match raw.machine.parents with
    | [] => True
    | suspended :: _ => suspended.childBaseline.executionType.isCreate = false) ∧
  (match raw.result.exit with
    | .success => raw.result.controlRoute = .ordinary ∧ raw.result.precompileSuccess = some true
    | .revert => raw.result.controlRoute = .nestedPrecompileSoftFailure
    | .exception _ => raw.result.controlRoute = .handleFailure)

def FrontPreservesParents (front : FrontOperations) : Prop :=
  ∀ input, (Reference.enter front input).parents = input.machine.parents

theorem generated_execute_is_raw_admitted
    (front : FrontOperations) (oracle : LeafOracle) (entry : Entry)
    (admitted : entry.Admitted) (frontParents : FrontPreservesParents front) (raw : RawResult)
    (execution : Generated.execute front oracle entry = some raw) : RawAdmitted raw := by
  cases entry with
  | fullFrame input =>
    simp only [Entry.Admitted, Input.Admitted] at admitted
    obtain ⟨_, _, _, _, parentsRegular, _, _, _⟩ := admitted
    have preparedRegular :
        match (Generated.prepare front input).parents with
        | [] => True
        | suspended :: _ => suspended.childBaseline.executionType.isCreate = false := by
      rw [prepare_refines, frontParents input]
      exact parentsRegular
    simp only [Generated.execute] at execution
    unfold Generated.executePrecompile at execution
    generalize hp : Eip803x.Generated.PrecompileGasPricingKernel.tryConsume
      input.machine.current.gas.gas.gasLeft input.baseCost input.dataCost = pricing at execution
    cases outcome : pricing.outcome with
    | success =>
      cases leaf : oracle (actionAddress input) input.machine.current.input with
      | success output =>
        simp [outcome, leaf] at execution
        subst raw
        exact ⟨by simpa [Generated.installGas] using preparedRegular, ⟨rfl, rfl⟩⟩
      | returnedFailure error =>
        simp [outcome, leaf, Generated.hard, Generated.result] at execution
        subst raw
        exact ⟨by simpa [Generated.installGas] using preparedRegular, rfl⟩
      | managedException =>
        cases top : input.machine.current.isTopLevel <;>
          simp [outcome, leaf, top, Generated.hard, Generated.result] at execution
        · subst raw
          exact ⟨by simpa [Generated.installGas] using preparedRegular, rfl⟩
        · subst raw
          exact ⟨by simpa [Generated.installGas] using preparedRegular, rfl⟩
      | missingNativeDependency => simp [outcome, leaf] at execution
    | baseDataOverflow =>
      simp [outcome, Generated.hard, Generated.result] at execution
      subst raw
      exact ⟨preparedRegular, rfl⟩
    | outOfGas =>
      simp [outcome, Generated.hard, Generated.result] at execution
      subst raw
      exact ⟨preparedRegular, rfl⟩
  | outerEvmException machine kind =>
    simp only [Entry.Admitted, AdmittedPrecompileBoundary] at admitted
    obtain ⟨_, _, _, parentsRegular⟩ := admitted
    simp [Generated.execute, Generated.hard, Generated.result] at execution
    subst raw
    exact ⟨parentsRegular, rfl⟩
  | outerOverflow machine =>
    simp only [Entry.Admitted, AdmittedPrecompileBoundary] at admitted
    obtain ⟨_, _, _, parentsRegular⟩ := admitted
    simp [Generated.execute, Generated.hard, Generated.result] at execution
    subst raw
    exact ⟨parentsRegular, rfl⟩

def TopAdaptersValid (front : FrontOperations) : Prop :=
  ∀ raw : RawResult, raw.machine.parents = [] → raw.result.exit = .success →
    let ended := if raw.machine.isTracingActions then front.traceActionEnd raw.machine raw.result else raw.machine
    ended.parents = [] ∧ (front.prepareTopLevelSubstate ended raw.result).exit = .success ∧
      (front.prepareTopLevelSubstate ended raw.result).controlRoute = .ordinary

theorem settlement_refines_target (front : FrontOperations) (semantics : Semantics)
    (raw : RawResult) (admitted : RawAdmitted raw) (top : TopAdaptersValid front) :
    Generated.settle front semantics.settlement raw = Reference.settle front semantics raw := by
  obtain ⟨parentsRegular, control⟩ := admitted
  cases exit : raw.result.exit with
  | success =>
    simp only [exit] at control
    obtain ⟨route, success⟩ := control
    cases parents : raw.machine.parents with
    | nil =>
      obtain ⟨endedParents, preparedExit, preparedRoute⟩ := top raw parents exit
      simp [Generated.settle, Reference.settle, exit, parents, Evm.FrameMachineExecution.settleChild, endedParents,
        Evm.FrameMachineExecution.settleTop, preparedExit, preparedRoute]
    | cons suspended rest =>
      have regular : suspended.childBaseline.executionType.isCreate = false := by simpa [parents] using parentsRegular
      simp [Generated.settle, Reference.settle, exit, parents, Evm.FrameMachineExecution.settleChild, route, Evm.FrameMachineExecution.settleSuccess,
        regular, Generated.successSettlement, Evm.FrameMachineExecution.settleRegularSuccess, Evm.FrameMachineExecution.withGas, Evm.FrameMachineExecution.resumeParent,
        Generated.resumed, Evm.FrameMachineExecution.finishRegularReturn, success, Evm.FrameMachineExecution.precompileSucceeded]
  | revert =>
    simp only [exit] at control
    cases parents : raw.machine.parents with
    | nil => simp [Generated.settle, Reference.settle, exit, parents, Evm.FrameMachineExecution.settleChild, Evm.FrameMachineExecution.settleTop, control]
    | cons suspended rest =>
      have regular : suspended.childBaseline.executionType.isCreate = false := by simpa [parents] using parentsRegular
      simp [Generated.settle, Reference.settle, exit, parents, Evm.FrameMachineExecution.settleChild, control,
        Generated.softSettlement, Evm.FrameMachineExecution.settleRevert, Generated.record, Evm.FrameMachineExecution.recordControl,
        Generated.creditNewAccount, Evm.FrameMachineExecution.creditFailedCreation, regular, Evm.FrameMachineExecution.restoreFailedWorld,
        Generated.resumed, Evm.FrameMachineExecution.resumeParent, Evm.FrameMachineExecution.finishRevert]
  | exception kind =>
    simp only [exit] at control
    cases parents : raw.machine.parents with
    | nil =>
      simp [Generated.settle, Reference.settle, exit, parents, Evm.FrameMachineExecution.settleChild, Evm.FrameMachineExecution.settleTop, control,
        Generated.hardSettlement, Generated.restoreFailure, Evm.FrameMachineExecution.restoreFailureControl,
        Generated.traceFailure, Evm.FrameMachineExecution.traceHandleFailure, Generated.record, Evm.FrameMachineExecution.recordControl, Evm.FrameMachineExecution.clearFrameRefund]
    | cons suspended rest =>
      have regular : suspended.childBaseline.executionType.isCreate = false := by simpa [parents] using parentsRegular
      simp [Generated.settle, Reference.settle, exit, parents, Evm.FrameMachineExecution.settleChild, control,
        Generated.hardSettlement, Evm.FrameMachineExecution.settleHandleFailure, Generated.restoreFailure, Evm.FrameMachineExecution.restoreFailureControl,
        Generated.traceFailure, Evm.FrameMachineExecution.traceHandleFailure, Generated.record, Evm.FrameMachineExecution.recordControl,
        Evm.FrameMachineExecution.finishExceptionSettlement, Generated.failedCall, Evm.FrameMachineExecution.finishException,
        Generated.creditNewAccount, Evm.FrameMachineExecution.creditFailedCreation, regular, Generated.resumed, Evm.FrameMachineExecution.resumeParent]

theorem source_full_frame_refines_reference
    (front referenceFront : FrontOperations) (semantics : Semantics)
    (oracle referenceOracle : LeafOracle) (entry : Entry)
    (frontAgreement : FrontAgrees front referenceFront)
    (oracleAgreement : OracleAgrees oracle referenceOracle)
    (admitted : entry.Admitted)
    (frontParents : FrontPreservesParents front)
    (top : TopAdaptersValid front) :
    Generated.fullFrame front semantics.settlement oracle entry =
      Reference.fullFrame referenceFront semantics referenceOracle entry := by
  have oracleEq : oracle = referenceOracle := by
    funext address input
    exact oracleAgreement address input
  have frontEq : front = referenceFront := by
    cases front
    cases referenceFront
    simp only [FrontAgrees] at frontAgreement
    obtain ⟨a, b, c, d, e⟩ := frontAgreement
    cases a
    cases b
    cases c
    cases d
    cases e
    rfl
  subst referenceFront
  subst referenceOracle
  unfold Generated.fullFrame Reference.fullFrame
  rw [← source_execute_refines_reference]
  cases execution : Generated.execute front oracle entry with
  | none => rfl
  | some raw =>
    simp only [Option.map_some]
    rw [settlement_refines_target front semantics raw
      (generated_execute_is_raw_admitted front oracle entry admitted frontParents raw execution) top]

theorem pricing_refines_wrapper
    (gas : ProductionGasState) (baseCost dataCost : Nat)
    (gasFits : gas.gasLeft ≤ Eip803x.Generated.PrecompileGasPricingKernel.uint64Max)
    (baseFits : baseCost ≤ Eip803x.Generated.PrecompileGasPricingKernel.uint64Max)
    (dataFits : dataCost ≤ Eip803x.Generated.PrecompileGasPricingKernel.uint64Max) :
    Eip803x.Refinement.PrecompileGasPricing.toWrapperPricingResult gas
      (Eip803x.Generated.PrecompileGasPricingKernel.tryConsume gas.gasLeft baseCost dataCost) =
      Precompiles.Wrapper.tryConsumePrecompileGas ⟨baseCost, dataCost⟩ gas :=
  Eip803x.Refinement.PrecompileGasPricing.tryConsume_refines_wrapper gas baseCost dataCost gasFits baseFits dataFits

end Eip803x.PrecompileFullFrame.Refinement
