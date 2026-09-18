-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameDriverExtractor.Generated.Operational.EvmFrameDriverOperationalKernel
import EvmFrameDriverExtractor.Specification.OperationalReference

/-!
Stage F operational refinement boundary.

The generated and independent drivers are related only through explicit
source-derived obligations.  The finite-fuel equality theorem below consumes
the per-adapter equalities and admitted-state closure needed for its induction.
Exact route-table closure, reachability, and the captured parent relation are
separate domain obligations; the latter is intentionally only a stack-shape
relation in this stage.  No source hash, route string, caller flag, or
same-leaf equality is itself a production proof.
-/

namespace Eip803x.Evm.FrameDriver.Operational.Refinement

open Eip803x.Evm.FrameMachineState
open Eip803x.Evm.FrameDriver.Operational

structure ProductionAdapterObligations
    (production reference : OperationalSemantics) where
  /- Every callback remains a separate source-bound obligation.  A missing
     callback is still an explicit `none`/incomplete path; no field below
     turns a caller boolean or same-leaf equality into a production proof. -/
  admitted : Machine → Prop
  admittedReference : Machine → Prop
  admittedNonempty : ∃ machine, admitted machine
  admittedReferenceNonempty : ∃ machine, admittedReference machine
  clearReturnData : ∀ machine, admitted machine →
    ∃ productionMachine referenceMachine,
      production.clearReturnData machine = some productionMachine ∧
        reference.clearReturnData machine = some referenceMachine ∧
        productionMachine = referenceMachine
  clearReturnDataAdmitted : ∀ machine cleared, admitted machine →
    production.clearReturnData machine = some cleared → admitted cleared
  prepareFresh : ∀ machine, admitted machine →
    ∃ productionMachine referenceMachine,
      production.prepareFresh machine = some productionMachine ∧
        reference.prepareFresh machine = some referenceMachine ∧
        productionMachine = referenceMachine
  prepareFreshAdmitted : ∀ machine prepared, admitted machine →
    production.prepareFresh machine = some prepared → admitted prepared
  prepareContinuation : ∀ machine, admitted machine →
    ∃ productionMachine referenceMachine,
      production.prepareContinuation machine = some productionMachine ∧
        reference.prepareContinuation machine = some referenceMachine ∧
        productionMachine = referenceMachine
  prepareContinuationAdmitted : ∀ machine prepared, admitted machine →
    production.prepareContinuation machine = some prepared → admitted prepared
  /- These source-bound obligations bind route labels to the generated source
     tables only.  The route PC stream is the explicit
     `adapterSuppliedRouteEvidence` premise validated for shape by
     `bytecodeExecutionValid`; exact per-op successors/control,
     including PUSH widths, remain a separate production obligation. -/
  runNoTrace : ∀ machine, admitted machine →
    production.runNoTrace machine = reference.runNoTrace machine
  runNoTraceSourceBound : ∀ machine execution, admitted machine →
    production.runNoTrace machine = some execution →
      Generated.sourceBoundBytecodeRoutesValid machine execution
  runNoTraceOutputAdmitted : ∀ machine execution, admitted machine →
    production.runNoTrace machine = some execution →
      match execution.outcome with
      | .thrownEvm failureMachine _ | .thrownOverflow failureMachine |
        .escaped failureMachine _ | .cancelled failureMachine _ _ => admitted failureMachine
      | .returned step => admitted step.machine
  runNoTraceCancelable : ∀ machine, admitted machine →
    production.runNoTraceCancelable machine = reference.runNoTraceCancelable machine
  runNoTraceCancelableSourceBound : ∀ machine execution, admitted machine →
    production.runNoTraceCancelable machine = some execution →
      Generated.sourceBoundBytecodeRoutesValid machine execution
  runNoTraceCancelableOutputAdmitted : ∀ machine execution, admitted machine →
    production.runNoTraceCancelable machine = some execution →
      match execution.outcome with
      | .thrownEvm failureMachine _ | .thrownOverflow failureMachine |
        .escaped failureMachine _ | .cancelled failureMachine _ _ => admitted failureMachine
      | .returned step => admitted step.machine
  runTraced : ∀ machine, admitted machine →
    production.runTraced machine = reference.runTraced machine
  runTracedSourceBound : ∀ machine execution, admitted machine →
    production.runTraced machine = some execution →
      Generated.sourceBoundBytecodeRoutesValid machine execution
  runTracedOutputAdmitted : ∀ machine execution, admitted machine →
    production.runTraced machine = some execution →
      match execution.outcome with
      | .thrownEvm failureMachine _ | .thrownOverflow failureMachine |
        .escaped failureMachine _ | .cancelled failureMachine _ _ => admitted failureMachine
      | .returned step => admitted step.machine
  runTracedCancelable : ∀ machine, admitted machine →
    production.runTracedCancelable machine = reference.runTracedCancelable machine
  runTracedCancelableSourceBound : ∀ machine execution, admitted machine →
    production.runTracedCancelable machine = some execution →
      Generated.sourceBoundBytecodeRoutesValid machine execution
  runTracedCancelableOutputAdmitted : ∀ machine execution, admitted machine →
    production.runTracedCancelable machine = some execution →
      match execution.outcome with
      | .thrownEvm failureMachine _ | .thrownOverflow failureMachine |
        .escaped failureMachine _ | .cancelled failureMachine _ _ => admitted failureMachine
      | .returned step => admitted step.machine
  runFullPrecompile : ∀ machine, admitted machine →
    production.runFullPrecompile machine = reference.runFullPrecompile machine
  runFullPrecompileSourceBound : ∀ machine execution, admitted machine →
    production.runFullPrecompile machine = some execution →
      Generated.sourceBoundPrecompileRouteValid machine execution
  runFullPrecompileOutputAdmitted : ∀ machine execution, admitted machine →
    production.runFullPrecompile machine = some execution →
      match execution.outcome with
      | .outOfGas failureMachine | .returnedFailure failureMachine _ |
        .managedException failureMachine _ | .escaped failureMachine _ => admitted failureMachine
      | .returned step => admitted step.machine
  failureResult : ∀ kind machine, admitted machine →
    production.failureResult kind machine = reference.failureResult kind machine
  precompileFailureResult : ∀ outcome machine, admitted machine →
    production.precompileFailureResult outcome machine =
      reference.precompileFailureResult outcome machine
  settleChild : ∀ machine result, admitted machine →
    production.settleChild machine result = reference.settleChild machine result
  createDeposit : ∀ machine result, admitted machine →
    production.createDeposit machine result = reference.createDeposit machine result
  prepareTopLevelSubstate : ∀ machine result, admitted machine →
    production.prepareTopLevelSubstate machine result = reference.prepareTopLevelSubstate machine result
  settleTopLevel : ∀ machine result substate, admitted machine →
    production.settleTopLevel machine result substate =
      reference.settleTopLevel machine result substate
  cleanup : ∀ machine, production.cleanup machine = reference.cleanup machine
  /- The settlement stage may consume the machine carried by a returned
     handler/failure step.  Keep that closure explicit instead of deriving it
     from the eventual whole-loop theorem. -/
  evaluatedMachineAdmitted : ∀ machine, admitted machine →
    admitted (Generated.evaluateStep production machine).machine
  /- These are production-side closure obligations, not metadata checks.  The
     generated route tables, reachability, and stack-shape relation must be
     preserved by concrete adapters before a production instantiation can be
     considered.  The finite-fuel equality theorem below does not consume
     these fields implicitly. -/
  exactOpcodeRoutes : Generated.sourceOpcodeRoutes.length = 1024 ∧
    TableByteRoutesComplete Generated.sourceOpcodeRoutes
  exactPrecompileRoutes : Generated.sourcePrecompileRoutes.length = 18 ∧
    PrecompileAddressesUnique Generated.sourcePrecompileRoutes
  admittedContinue : ∀ machine next, admitted machine →
    Generated.settleStep production (Generated.evaluateStep production machine) =
      .continue next → admitted next
  admittedChild : ∀ machine beforePush parent child, admitted machine →
    Generated.settleStep production (Generated.evaluateStep production machine) =
      .suspend beforePush parent child → childEntryValid child →
      admitted (pushChild beforePush parent child)
  reachable : Machine → Prop
  reachableInitial : ∀ machine, reachable machine → admitted machine
  reachableContinue : ∀ machine next, reachable machine →
    Generated.settleStep production (Generated.evaluateStep production machine) =
      .continue next → reachable next
  frame : ∀ left right, admitted left → admittedReference right →
    left.current = right.current ∧ left.parents = right.parents
  framePreserved : ∀ machine next, admitted machine →
    Generated.settleStep production (Generated.evaluateStep production machine) =
      .continue next → admittedReference next

def BytecodeOutcomeAdmitted (admitted : Machine → Prop) : BytecodeRunOutcome → Prop
  | .returned step => admitted step.machine
  | .thrownEvm machine _ | .thrownOverflow machine | .escaped machine _ |
    .cancelled machine _ _ => admitted machine

def PrecompileOutcomeAdmitted (admitted : Machine → Prop) : PrecompileRunOutcome → Prop
  | .returned step => admitted step.machine
  | .outOfGas machine | .returnedFailure machine _ | .managedException machine _ |
    .escaped machine _ => admitted machine

theorem option_eq_of_agreement {α : Type} {left right : Option α}
    (agreement : ∃ leftValue rightValue,
      left = some leftValue ∧ right = some rightValue ∧ leftValue = rightValue) :
    left = right := by
  rcases agreement with ⟨leftValue, rightValue, hLeft, hRight, hValues⟩
  rw [hLeft, hRight, hValues]

theorem cleanupFailure_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (machine : Machine) :
    Generated.cleanupFailure production machine =
      Eip803x.Evm.FrameDriver.Operational.cleanupFailure reference machine := by
  simp [Generated.cleanupFailure,
    Eip803x.Evm.FrameDriver.Operational.cleanupFailure, obligations.cleanup machine]

theorem failure_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (machine : Machine) (kind : ExceptionKind)
    (admitted : obligations.admitted machine) :
    Generated.invokeFailure production machine kind =
      Reference.failureReference reference machine kind := by
  have hResult := obligations.failureResult kind machine admitted
  cases hProduction : production.failureResult kind machine with
  | none =>
      have hReference : reference.failureResult kind machine = none := by
        rw [← hResult, hProduction]
      simp [Generated.invokeFailure, Reference.failureReference, hProduction, hReference]
      exact cleanupFailure_agree production reference obligations machine
  | some result =>
      have hReference : reference.failureResult kind machine = some result := by
        rw [← hResult, hProduction]
      by_cases hControl : FrameResultControlValid result
      · simp [Generated.invokeFailure, Reference.failureReference, hProduction, hReference,
          hControl]
      · simp [Generated.invokeFailure, Reference.failureReference, hProduction, hReference,
          hControl]
        exact cleanupFailure_agree production reference obligations machine

theorem precompileFailure_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (machine : Machine) (outcome : PrecompileRunOutcome)
    (admitted : obligations.admitted machine) :
    Generated.invokePrecompileFailure production machine outcome =
      Reference.precompileFailureReference reference machine outcome := by
  have hResult := obligations.precompileFailureResult outcome machine admitted
  cases hProduction : production.precompileFailureResult outcome machine with
  | none =>
      have hReference : reference.precompileFailureResult outcome machine = none := by
        rw [← hResult, hProduction]
      simp [Generated.invokePrecompileFailure, Reference.precompileFailureReference,
        hProduction, hReference]
      exact cleanupFailure_agree production reference obligations machine
  | some result =>
      have hReference : reference.precompileFailureResult outcome machine = some result := by
        rw [← hResult, hProduction]
      by_cases hControl : FrameResultControlValid result
      · simp [Generated.invokePrecompileFailure, Reference.precompileFailureReference,
          hProduction, hReference, hControl]
      · simp [Generated.invokePrecompileFailure, Reference.precompileFailureReference,
          hProduction, hReference, hControl]
        exact cleanupFailure_agree production reference obligations machine

/- The generated and reference complete-settlement branches are separate
   definitions, but their only semantic leaves are the top-level substate and
   settlement adapters.  This helper discharges that branch without assuming
   a whole-step equality. -/
theorem settleComplete_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (machine : Machine) (result : FrameResult)
    (admitted : obligations.admitted machine) :
    Eip803x.Evm.FrameDriver.Operational.settleComplete production machine result =
      Reference.settleReferenceSettlement reference machine (.complete result) := by
  by_cases hControl : FrameResultControlValid result
  · cases hParents : machine.parents with
    | nil =>
        cases hTop : result.frame.isTopLevel with
        | false =>
            simp [Eip803x.Evm.FrameDriver.Operational.settleComplete,
              Reference.settleReferenceSettlement, hControl, hParents, hTop]
        | true =>
            cases hPrepare : production.prepareTopLevelSubstate machine result with
            | none =>
                have hReference : reference.prepareTopLevelSubstate machine result = none := by
                  rw [← obligations.prepareTopLevelSubstate machine result admitted, hPrepare]
                simp [Eip803x.Evm.FrameDriver.Operational.settleComplete,
                  Reference.settleReferenceSettlement, hControl, hParents, hTop,
                  hPrepare, hReference]
                exact cleanupFailure_agree production reference obligations machine
            | some substate =>
                have hReference : reference.prepareTopLevelSubstate machine result =
                    some substate := by
                  rw [← obligations.prepareTopLevelSubstate machine result admitted, hPrepare]
                by_cases hFields : transactionSubstateFieldsValid result substate
                · cases hSettle : production.settleTopLevel machine result substate with
                  | none =>
                      have hSettleReference :
                          reference.settleTopLevel machine result substate = none := by
                        rw [← obligations.settleTopLevel machine result substate admitted, hSettle]
                      simp [Eip803x.Evm.FrameDriver.Operational.settleComplete,
                        Reference.settleReferenceSettlement, hControl, hParents, hTop,
                        hPrepare, hReference, hFields, hSettle, hSettleReference]
                      exact cleanupFailure_agree production reference obligations machine
                  | some settled =>
                      have hSettleReference :
                          reference.settleTopLevel machine result substate = some settled := by
                        rw [← obligations.settleTopLevel machine result substate admitted, hSettle]
                      simp [Eip803x.Evm.FrameDriver.Operational.settleComplete,
                        Reference.settleReferenceSettlement, hControl, hParents, hTop,
                        hPrepare, hReference, hFields, hSettle, hSettleReference]
                · simp [Eip803x.Evm.FrameDriver.Operational.settleComplete,
                    Reference.settleReferenceSettlement, hControl, hParents, hTop,
                    hPrepare, hReference, hFields]
                  exact cleanupFailure_agree production reference obligations machine
    | cons suspended parents =>
        simp [Eip803x.Evm.FrameDriver.Operational.settleComplete,
          Reference.settleReferenceSettlement, hControl, hParents]
  · simp [Eip803x.Evm.FrameDriver.Operational.settleComplete,
      Reference.settleReferenceSettlement, hControl]

theorem settleSettlement_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (machine : Machine) (settlement : Settlement)
    (admitted : obligations.admitted machine) :
    Generated.settleSettlement production machine settlement =
      Reference.settleReferenceSettlement reference machine settlement := by
  cases settlement with
  | complete result =>
      exact settleComplete_agree production reference obligations machine result admitted
  | resume resumed => rfl
  | invalidControl invalid => rfl

/- A CREATE collision is intentionally absent here.  The accepted CALL/CREATE
   route supplies the pre-child collision decision; this driver only invokes
   `createDeposit` after a successful child result. -/
theorem settleHalt_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (machine : Machine) (result : FrameResult)
    (admitted : obligations.admitted machine) :
    Generated.settleHalt production machine result =
      Reference.settleReferenceHalt reference machine result := by
  by_cases hControl : FrameResultControlValid result
  · cases hParents : machine.parents with
    | nil =>
        cases hTop : result.frame.isTopLevel with
        | false =>
            simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
              hParents, hTop]
        | true =>
            cases hPrepare : production.prepareTopLevelSubstate machine result with
            | none =>
                have hReference : reference.prepareTopLevelSubstate machine result = none := by
                  rw [← obligations.prepareTopLevelSubstate machine result admitted, hPrepare]
                simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                  hParents, hTop, hPrepare, hReference]
                exact cleanupFailure_agree production reference obligations machine
            | some substate =>
                have hReference : reference.prepareTopLevelSubstate machine result =
                    some substate := by
                  rw [← obligations.prepareTopLevelSubstate machine result admitted, hPrepare]
                by_cases hFields : transactionSubstateFieldsValid result substate
                · cases hSettle : production.settleTopLevel machine result substate with
                  | none =>
                      have hSettleReference :
                          reference.settleTopLevel machine result substate = none := by
                        rw [← obligations.settleTopLevel machine result substate admitted, hSettle]
                      simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                        hParents, hTop, hPrepare, hReference, hFields, hSettle,
                        hSettleReference]
                      exact cleanupFailure_agree production reference obligations machine
                  | some settled =>
                      have hSettleReference :
                          reference.settleTopLevel machine result substate = some settled := by
                        rw [← obligations.settleTopLevel machine result substate admitted, hSettle]
                      simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                        hParents, hTop, hPrepare, hReference, hFields, hSettle,
                        hSettleReference]
                · simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                    hParents, hTop, hPrepare, hReference, hFields]
                  exact cleanupFailure_agree production reference obligations machine
    | cons suspended parents =>
        cases hCreate : machine.current.executionType.isCreate with
        | false =>
            cases hSettle : production.settleChild machine result with
            | none =>
                have hReference : reference.settleChild machine result = none := by
                  rw [← obligations.settleChild machine result admitted, hSettle]
                simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                  hParents, hCreate, hSettle, hReference]
                exact cleanupFailure_agree production reference obligations machine
            | some settlement =>
                have hReference : reference.settleChild machine result = some settlement := by
                  rw [← obligations.settleChild machine result admitted, hSettle]
                simpa [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                  hParents, hCreate, hSettle, hReference] using
                  settleSettlement_agree production reference obligations machine settlement admitted
        | true =>
            cases hExit : result.exit with
            | success =>
                cases hDeposit : production.createDeposit machine result with
                | none =>
                    have hReference : reference.createDeposit machine result = none := by
                      rw [← obligations.createDeposit machine result admitted, hDeposit]
                    simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                      hParents, hCreate, hExit, hDeposit, hReference]
                    exact cleanupFailure_agree production reference obligations machine
                | some execution =>
                    have hReference : reference.createDeposit machine result = some execution := by
                      rw [← obligations.createDeposit machine result admitted, hDeposit]
                    by_cases hDepositValid : createDepositOutcomeValid execution
                    · simpa [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                        hParents, hCreate, hExit, hDeposit, hReference, hDepositValid] using
                        settleSettlement_agree production reference obligations machine
                          execution.settlement admitted
                    · simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                        hParents, hCreate, hExit, hDeposit, hReference, hDepositValid]
                      exact cleanupFailure_agree production reference obligations machine
            | revert =>
                cases hSettle : production.settleChild machine result with
                | none =>
                    have hReference : reference.settleChild machine result = none := by
                      rw [← obligations.settleChild machine result admitted, hSettle]
                    simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                      hParents, hCreate, hExit, hSettle, hReference]
                    exact cleanupFailure_agree production reference obligations machine
                | some settlement =>
                    have hReference : reference.settleChild machine result = some settlement := by
                      rw [← obligations.settleChild machine result admitted, hSettle]
                    simpa [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                      hParents, hCreate, hExit, hSettle, hReference] using
                      settleSettlement_agree production reference obligations machine settlement admitted
            | exception kind =>
                cases hSettle : production.settleChild machine result with
                | none =>
                    have hReference : reference.settleChild machine result = none := by
                      rw [← obligations.settleChild machine result admitted, hSettle]
                    simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                      hParents, hCreate, hExit, hSettle, hReference]
                    exact cleanupFailure_agree production reference obligations machine
                | some settlement =>
                    have hReference : reference.settleChild machine result = some settlement := by
                      rw [← obligations.settleChild machine result admitted, hSettle]
                    simpa [Generated.settleHalt, Reference.settleReferenceHalt, hControl,
                      hParents, hCreate, hExit, hSettle, hReference] using
                      settleSettlement_agree production reference obligations machine settlement admitted
  · simp [Generated.settleHalt, Reference.settleReferenceHalt, hControl]
    exact cleanupFailure_agree production reference obligations machine

theorem settleStep_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (step : DriverStep)
    (admitted : obligations.admitted step.machine) :
    Generated.settleStep production step = Reference.settleStep reference step := by
  cases step with
  | continue machine => rfl
  | suspend machine parent child => rfl
  | halt machine result => exact settleHalt_agree production reference obligations machine result admitted
  | topLevelHalt machine result substate => rfl
  | cancelled machine reason => rfl
  | escaped machine reason => rfl
  | incomplete machine reason => rfl

theorem interpretBytecode_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (evidence : Generated.sourceEvidenceValid)
    (machine : Machine) (execution : BytecodeExecution)
    (admitted : obligations.admitted machine)
    (sourceBound : Generated.sourceBoundBytecodeRoutesValid machine execution)
    (outputAdmitted : BytecodeOutcomeAdmitted obligations.admitted execution.outcome) :
    Generated.interpretBytecode production machine execution =
      Reference.bytecodeReference reference machine execution := by
  by_cases hValid : bytecodeExecutionValid production machine execution
  · have hReferenceValid : bytecodeExecutionValid reference machine execution := by
      simpa [bytecodeExecutionValid, bytecodeRoutesValid] using hValid
    cases hOutcome : execution.outcome with
    | returned step =>
        simp [Generated.interpretBytecode, Reference.bytecodeReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome, Generated.classifyBytecode,
          Reference.classifyReference]
    | thrownEvm failureMachine kind =>
        have hFailureAdmitted : obligations.admitted failureMachine := by
          simpa [BytecodeOutcomeAdmitted, hOutcome] using outputAdmitted
        have hFailure := failure_agree production reference obligations failureMachine kind
          hFailureAdmitted
        simpa [Generated.interpretBytecode, Reference.bytecodeReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome] using hFailure
    | thrownOverflow failureMachine =>
        have hFailureAdmitted : obligations.admitted failureMachine := by
          simpa [BytecodeOutcomeAdmitted, hOutcome] using outputAdmitted
        have hFailure := failure_agree production reference obligations failureMachine .other
          hFailureAdmitted
        simpa [Generated.interpretBytecode, Reference.bytecodeReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome] using hFailure
    | escaped failureMachine reason =>
        simp [Generated.interpretBytecode, Reference.bytecodeReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome]
    | cancelled failureMachine point reason =>
        simp [Generated.interpretBytecode, Reference.bytecodeReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome]
  · have hReferenceValid : ¬ bytecodeExecutionValid reference machine execution := by
      simpa [bytecodeExecutionValid, bytecodeRoutesValid] using hValid
    simp [Generated.interpretBytecode, Reference.bytecodeReference, evidence,
      hValid, hReferenceValid]

theorem interpretPrecompile_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (evidence : Generated.sourceEvidenceValid)
    (machine : Machine) (execution : PrecompileExecution)
    (admitted : obligations.admitted machine)
    (sourceBound : Generated.sourceBoundPrecompileRouteValid machine execution)
    (outputAdmitted : PrecompileOutcomeAdmitted obligations.admitted execution.outcome) :
    Generated.interpretPrecompile production machine execution =
      Reference.precompileReference reference machine execution := by
  by_cases hValid : fullPrecompileExecutionValid production machine execution
  · have hReferenceValid : fullPrecompileExecutionValid reference machine execution := by
      simpa [fullPrecompileExecutionValid, precompileRouteValid] using hValid
    cases hOutcome : execution.outcome with
    | returned step =>
        simp [Generated.interpretPrecompile, Reference.precompileReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome]
    | outOfGas failureMachine =>
        have hFailureAdmitted : obligations.admitted failureMachine := by
          simpa [PrecompileOutcomeAdmitted, hOutcome] using outputAdmitted
        have hFailure := precompileFailure_agree production reference obligations failureMachine
          execution.outcome hFailureAdmitted
        simpa [Generated.interpretPrecompile, Reference.precompileReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome] using hFailure
    | returnedFailure failureMachine reason =>
        have hFailureAdmitted : obligations.admitted failureMachine := by
          simpa [PrecompileOutcomeAdmitted, hOutcome] using outputAdmitted
        have hFailure := precompileFailure_agree production reference obligations failureMachine
          execution.outcome hFailureAdmitted
        simpa [Generated.interpretPrecompile, Reference.precompileReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome] using hFailure
    | managedException failureMachine reason =>
        have hFailureAdmitted : obligations.admitted failureMachine := by
          simpa [PrecompileOutcomeAdmitted, hOutcome] using outputAdmitted
        have hFailure := precompileFailure_agree production reference obligations failureMachine
          execution.outcome hFailureAdmitted
        simpa [Generated.interpretPrecompile, Reference.precompileReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome] using hFailure
    | escaped failureMachine reason =>
        simp [Generated.interpretPrecompile, Reference.precompileReference, evidence,
          hValid, hReferenceValid, sourceBound, hOutcome]
  · have hReferenceValid : ¬ fullPrecompileExecutionValid reference machine execution := by
      simpa [fullPrecompileExecutionValid, precompileRouteValid] using hValid
    simp [Generated.interpretPrecompile, Reference.precompileReference, evidence,
      hValid, hReferenceValid]

/- The generated and reference evaluators share no whole-step theorem.  The
   following derivation follows the actual preparation, four dispatch modes,
   precompile branch, and source-bound output paths using only the callback
   equalities and closure obligations above. -/
theorem evaluateStep_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (evidence : Generated.sourceEvidenceValid)
    (machine : Machine)
    (admitted : obligations.admitted machine) :
    Generated.evaluateStep production machine =
      Reference.stepReference reference machine := by
  cases hPhase : machine.current.phase with
  | running =>
      simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
        Generated.prepareFrame, Reference.prepareReference]
  | fresh =>
      cases hClear : production.clearReturnData machine with
      | none =>
          have hClearAgreement := option_eq_of_agreement
            (obligations.clearReturnData machine admitted)
          have hReference : reference.clearReturnData machine = none := by
            rw [← hClearAgreement, hClear]
          simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
            Generated.prepareFrame, Reference.prepareReference, hClear, hReference]
      | some cleared =>
          have hClearAgreement := option_eq_of_agreement
            (obligations.clearReturnData machine admitted)
          have hClearedAdmitted := obligations.clearReturnDataAdmitted machine cleared admitted hClear
          have hPrepare := option_eq_of_agreement
            (obligations.prepareFresh cleared hClearedAdmitted)
          cases hFresh : production.prepareFresh cleared with
          | none =>
              have hReference : reference.prepareFresh cleared = none := by
                rw [← hPrepare, hFresh]
              simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                Generated.prepareFrame, Reference.prepareReference, hClear, hFresh,
                hReference, hClearAgreement]
          | some prepared =>
              have hReference : reference.clearReturnData machine = some cleared := by
                rw [← hClearAgreement, hClear]
              have hFreshReference : reference.prepareFresh cleared = some prepared := by
                rw [← hPrepare, hFresh]
              have hPreparedAdmitted := obligations.prepareFreshAdmitted cleared prepared
                hClearedAdmitted hFresh
              cases hKind : prepared.current.kind with
              | bytecode =>
                  cases hTable : prepared.current.dispatchTable with
                  | noTrace =>
                      cases hRun : production.runNoTrace prepared with
                      | none =>
                          have hRunReference : reference.runNoTrace prepared = none := by
                            rw [← obligations.runNoTrace prepared hPreparedAdmitted, hRun]
                          simp [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference]
                      | some execution =>
                          have hRunReference : reference.runNoTrace prepared = some execution := by
                            rw [← obligations.runNoTrace prepared hPreparedAdmitted, hRun]
                          have hSource := obligations.runNoTraceSourceBound prepared execution
                            hPreparedAdmitted hRun
                          have hOutput := obligations.runNoTraceOutputAdmitted prepared execution
                            hPreparedAdmitted hRun
                          have hInterpret := interpretBytecode_agree production reference obligations
                            evidence prepared execution hPreparedAdmitted hSource (by
                              simpa [BytecodeOutcomeAdmitted] using hOutput)
                          simpa [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference] using
                            hInterpret
                  | noTraceCancelable =>
                      cases hRun : production.runNoTraceCancelable prepared with
                      | none =>
                          have hRunReference : reference.runNoTraceCancelable prepared = none := by
                            rw [← obligations.runNoTraceCancelable prepared hPreparedAdmitted, hRun]
                          simp [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference]
                      | some execution =>
                          have hRunReference : reference.runNoTraceCancelable prepared = some execution := by
                            rw [← obligations.runNoTraceCancelable prepared hPreparedAdmitted, hRun]
                          have hSource := obligations.runNoTraceCancelableSourceBound prepared execution
                            hPreparedAdmitted hRun
                          have hOutput := obligations.runNoTraceCancelableOutputAdmitted prepared execution
                            hPreparedAdmitted hRun
                          have hInterpret := interpretBytecode_agree production reference obligations
                            evidence prepared execution hPreparedAdmitted hSource (by
                              simpa [BytecodeOutcomeAdmitted] using hOutput)
                          simpa [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference] using
                            hInterpret
                  | traced =>
                      cases hRun : production.runTraced prepared with
                      | none =>
                          have hRunReference : reference.runTraced prepared = none := by
                            rw [← obligations.runTraced prepared hPreparedAdmitted, hRun]
                          simp [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference]
                      | some execution =>
                          have hRunReference : reference.runTraced prepared = some execution := by
                            rw [← obligations.runTraced prepared hPreparedAdmitted, hRun]
                          have hSource := obligations.runTracedSourceBound prepared execution
                            hPreparedAdmitted hRun
                          have hOutput := obligations.runTracedOutputAdmitted prepared execution
                            hPreparedAdmitted hRun
                          have hInterpret := interpretBytecode_agree production reference obligations
                            evidence prepared execution hPreparedAdmitted hSource (by
                              simpa [BytecodeOutcomeAdmitted] using hOutput)
                          simpa [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference] using
                            hInterpret
                  | tracedCancelable =>
                      cases hRun : production.runTracedCancelable prepared with
                      | none =>
                          have hRunReference : reference.runTracedCancelable prepared = none := by
                            rw [← obligations.runTracedCancelable prepared hPreparedAdmitted, hRun]
                          simp [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference]
                      | some execution =>
                          have hRunReference : reference.runTracedCancelable prepared = some execution := by
                            rw [← obligations.runTracedCancelable prepared hPreparedAdmitted, hRun]
                          have hSource := obligations.runTracedCancelableSourceBound prepared execution
                            hPreparedAdmitted hRun
                          have hOutput := obligations.runTracedCancelableOutputAdmitted prepared execution
                            hPreparedAdmitted hRun
                          have hInterpret := interpretBytecode_agree production reference obligations
                            evidence prepared execution hPreparedAdmitted hSource (by
                              simpa [BytecodeOutcomeAdmitted] using hOutput)
                          simpa [Generated.evaluateStep, Reference.stepReference, hPhase,
                            evidence, Generated.prepareFrame, Reference.prepareReference,
                            Generated.invokeFrame, Reference.dispatchReference, hClear, hFresh,
                            hReference, hFreshReference, hKind, hTable, hRun, hRunReference] using
                            hInterpret
              | precompile address =>
                  cases hRun : production.runFullPrecompile prepared with
                  | none =>
                      have hRunReference : reference.runFullPrecompile prepared = none := by
                        rw [← obligations.runFullPrecompile prepared hPreparedAdmitted, hRun]
                      simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.invokeReference, hClear, hFresh, hReference, hFreshReference,
                        hKind, hRun, hRunReference]
                  | some execution =>
                      have hRunReference : reference.runFullPrecompile prepared = some execution := by
                        rw [← obligations.runFullPrecompile prepared hPreparedAdmitted, hRun]
                      have hSource := obligations.runFullPrecompileSourceBound prepared execution
                        hPreparedAdmitted hRun
                      have hOutput := obligations.runFullPrecompileOutputAdmitted prepared execution
                        hPreparedAdmitted hRun
                      have hInterpret := interpretPrecompile_agree production reference obligations
                        evidence prepared execution hPreparedAdmitted hSource (by
                          simpa [PrecompileOutcomeAdmitted] using hOutput)
                      simpa [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.invokeReference, hClear, hFresh, hReference, hFreshReference,
                        hKind, hRun, hRunReference] using
                        hInterpret
  | continuation =>
      cases hPrepare : production.prepareContinuation machine with
      | none =>
          have hPrepareAgreement := option_eq_of_agreement
            (obligations.prepareContinuation machine admitted)
          have hReference : reference.prepareContinuation machine = none := by
            rw [← hPrepareAgreement, hPrepare]
          simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
            Generated.prepareFrame, Reference.prepareReference, hPrepare, hReference]
      | some prepared =>
          have hPrepareAgreement := option_eq_of_agreement
            (obligations.prepareContinuation machine admitted)
          have hReference : reference.prepareContinuation machine = some prepared := by
            rw [← hPrepareAgreement, hPrepare]
          have hPreparedAdmitted := obligations.prepareContinuationAdmitted machine prepared
            admitted hPrepare
          cases hKind : prepared.current.kind with
          | bytecode =>
              cases hTable : prepared.current.dispatchTable with
              | noTrace =>
                  cases hRun : production.runNoTrace prepared with
                  | none =>
                      have hRunReference : reference.runNoTrace prepared = none := by
                        rw [← obligations.runNoTrace prepared hPreparedAdmitted, hRun]
                      simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference]
                  | some execution =>
                      have hRunReference : reference.runNoTrace prepared = some execution := by
                        rw [← obligations.runNoTrace prepared hPreparedAdmitted, hRun]
                      have hSource := obligations.runNoTraceSourceBound prepared execution
                        hPreparedAdmitted hRun
                      have hOutput := obligations.runNoTraceOutputAdmitted prepared execution
                        hPreparedAdmitted hRun
                      have hInterpret := interpretBytecode_agree production reference obligations
                        evidence prepared execution hPreparedAdmitted hSource (by
                          simpa [BytecodeOutcomeAdmitted] using hOutput)
                      simpa [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference] using
                        hInterpret
              | noTraceCancelable =>
                  cases hRun : production.runNoTraceCancelable prepared with
                  | none =>
                      have hRunReference : reference.runNoTraceCancelable prepared = none := by
                        rw [← obligations.runNoTraceCancelable prepared hPreparedAdmitted, hRun]
                      simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference]
                  | some execution =>
                      have hRunReference : reference.runNoTraceCancelable prepared = some execution := by
                        rw [← obligations.runNoTraceCancelable prepared hPreparedAdmitted, hRun]
                      have hSource := obligations.runNoTraceCancelableSourceBound prepared execution
                        hPreparedAdmitted hRun
                      have hOutput := obligations.runNoTraceCancelableOutputAdmitted prepared execution
                        hPreparedAdmitted hRun
                      have hInterpret := interpretBytecode_agree production reference obligations
                        evidence prepared execution hPreparedAdmitted hSource (by
                          simpa [BytecodeOutcomeAdmitted] using hOutput)
                      simpa [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference] using
                        hInterpret
              | traced =>
                  cases hRun : production.runTraced prepared with
                  | none =>
                      have hRunReference : reference.runTraced prepared = none := by
                        rw [← obligations.runTraced prepared hPreparedAdmitted, hRun]
                      simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference]
                  | some execution =>
                      have hRunReference : reference.runTraced prepared = some execution := by
                        rw [← obligations.runTraced prepared hPreparedAdmitted, hRun]
                      have hSource := obligations.runTracedSourceBound prepared execution
                        hPreparedAdmitted hRun
                      have hOutput := obligations.runTracedOutputAdmitted prepared execution
                        hPreparedAdmitted hRun
                      have hInterpret := interpretBytecode_agree production reference obligations
                        evidence prepared execution hPreparedAdmitted hSource (by
                          simpa [BytecodeOutcomeAdmitted] using hOutput)
                      simpa [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference] using
                        hInterpret
              | tracedCancelable =>
                  cases hRun : production.runTracedCancelable prepared with
                  | none =>
                      have hRunReference : reference.runTracedCancelable prepared = none := by
                        rw [← obligations.runTracedCancelable prepared hPreparedAdmitted, hRun]
                      simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference]
                  | some execution =>
                      have hRunReference : reference.runTracedCancelable prepared = some execution := by
                        rw [← obligations.runTracedCancelable prepared hPreparedAdmitted, hRun]
                      have hSource := obligations.runTracedCancelableSourceBound prepared execution
                        hPreparedAdmitted hRun
                      have hOutput := obligations.runTracedCancelableOutputAdmitted prepared execution
                        hPreparedAdmitted hRun
                      have hInterpret := interpretBytecode_agree production reference obligations
                        evidence prepared execution hPreparedAdmitted hSource (by
                          simpa [BytecodeOutcomeAdmitted] using hOutput)
                      simpa [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.dispatchReference, hPrepare, hReference, hKind, hTable, hRun,
                        hRunReference] using
                        hInterpret
          | precompile address =>
              cases hRun : production.runFullPrecompile prepared with
              | none =>
                  have hRunReference : reference.runFullPrecompile prepared = none := by
                    rw [← obligations.runFullPrecompile prepared hPreparedAdmitted, hRun]
                  simp [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                    Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                    Reference.invokeReference, hPrepare, hReference, hKind, hRun, hRunReference]
              | some execution =>
                  have hRunReference : reference.runFullPrecompile prepared = some execution := by
                    rw [← obligations.runFullPrecompile prepared hPreparedAdmitted, hRun]
                  have hSource := obligations.runFullPrecompileSourceBound prepared execution
                    hPreparedAdmitted hRun
                  have hOutput := obligations.runFullPrecompileOutputAdmitted prepared execution
                    hPreparedAdmitted hRun
                  have hInterpret := interpretPrecompile_agree production reference obligations
                    evidence prepared execution hPreparedAdmitted hSource (by
                      simpa [PrecompileOutcomeAdmitted] using hOutput)
                  simpa [Generated.evaluateStep, Reference.stepReference, hPhase, evidence,
                        Generated.prepareFrame, Reference.prepareReference, Generated.invokeFrame,
                        Reference.invokeReference, hPrepare, hReference, hKind, hRun, hRunReference] using
                    hInterpret

theorem adapters_step_agree
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (evidence : Generated.sourceEvidenceValid)
    (machine : Machine)
    (admitted : obligations.admitted machine) :
    Generated.settleStep production (Generated.evaluateStep production machine) =
      Reference.settleStep reference (Reference.stepReference reference machine) := by
  have hEvaluation := evaluateStep_agree production reference obligations evidence machine admitted
  have hGeneratedMachine := obligations.evaluatedMachineAdmitted machine admitted
  have hReferenceMachine : obligations.admitted
      (Reference.stepReference reference machine).machine := by
    rw [← congrArg DriverStep.machine hEvaluation]
    exact hGeneratedMachine
  rw [hEvaluation]
  exact settleStep_agree production reference obligations
    (Reference.stepReference reference machine) hReferenceMachine

structure OperationalStepSimulation
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference) where
  sourceEvidence : Generated.sourceEvidenceValid

/- These domain witnesses are intentionally separate from the finite-fuel
   evaluator equality.  They consume the exact generated route tables,
   admitted-state reachability, and the stage's stack-shape-only parent
   relation without silently upgrading any of them into a production claim. -/
theorem production_domain_obligations_explicit
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference) :
    obligations.exactOpcodeRoutes ∧
      obligations.exactPrecompileRoutes ∧
      obligations.admittedNonempty ∧
      obligations.admittedReferenceNonempty ∧
      (∀ machine, obligations.reachable machine → obligations.admitted machine) ∧
      (∀ machine next, obligations.reachable machine →
        Generated.settleStep production (Generated.evaluateStep production machine) =
          .continue next → obligations.reachable next) ∧
      (∀ left right, obligations.admitted left → obligations.admittedReference right →
        left.current = right.current ∧ left.parents = right.parents) ∧
      (∀ machine next, obligations.admitted machine →
        Generated.settleStep production (Generated.evaluateStep production machine) =
          .continue next → obligations.admittedReference next) := by
  exact ⟨obligations.exactOpcodeRoutes, obligations.exactPrecompileRoutes,
    obligations.admittedNonempty, obligations.admittedReferenceNonempty,
    obligations.reachableInitial, obligations.reachableContinue,
    obligations.frame, obligations.framePreserved⟩

theorem generated_runFuel_reference_runFuel
    (production reference : OperationalSemantics)
    (obligations : ProductionAdapterObligations production reference)
    (simulation : OperationalStepSimulation production reference obligations)
    (fuel : Nat) (machine : Machine)
    (admitted : obligations.admitted machine) :
    Generated.runFuel fuel production machine = Reference.runFuel fuel reference machine := by
  induction fuel generalizing machine with
  | zero => rfl
  | succ fuel inductionHypothesis =>
      rw [Generated.runFuel, Reference.runFuel]
      have stepAgreement := adapters_step_agree production reference obligations
        simulation.sourceEvidence machine admitted
      rw [stepAgreement]
      cases settled : Reference.settleStep reference (Reference.stepReference reference machine) with
      | continue next =>
          apply inductionHypothesis next
          exact obligations.admittedContinue machine next admitted (by
            calc
              Generated.settleStep production (Generated.evaluateStep production machine) =
                  Reference.settleStep reference (Reference.stepReference reference machine) := stepAgreement
              _ = .continue next := settled)
      | suspend beforePush parent child =>
          by_cases childValid : childEntryValid child
          · apply inductionHypothesis (pushChild beforePush parent child)
            exact obligations.admittedChild machine beforePush parent child admitted (by
              calc
                Generated.settleStep production (Generated.evaluateStep production machine) =
                    Reference.settleStep reference (Reference.stepReference reference machine) := stepAgreement
                _ = .suspend beforePush parent child := settled) childValid
          · simp [childValid]
      | halt current result =>
          simp [Generated.cleanupCompleted, Reference.cleanupCompletedReference,
            obligations.cleanup current]
      | topLevelHalt current result substate =>
          simp [Generated.cleanupCompletedTopLevel,
            Reference.cleanupCompletedTopLevelReference, obligations.cleanup current]
      | cancelled current reason =>
          simp [Generated.cleanupUnwind, obligations.cleanup current]
      | escaped current reason =>
          simp [Generated.cleanupUnwind, obligations.cleanup current]
      | incomplete current reason => rfl

def ConditionalFiniteFuelAgreementStatement
    (production reference : OperationalSemantics) : Prop :=
    ∃ obligations : ProductionAdapterObligations production reference,
    ∃ simulation : OperationalStepSimulation production reference obligations,
      ∀ fuel machine, (admitted : obligations.admitted machine) →
        generated_runFuel_reference_runFuel production reference obligations simulation fuel machine
          admitted

end Eip803x.Evm.FrameDriver.Operational.Refinement
