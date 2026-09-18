-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameControlSettlementExtractor.Refinement.StageCFullPrecompileBridge
import EvmFrameControlSettlementExtractor.Specification.AdmissionWitnesses

/-!
Constructive Stage C component vectors and harness fail-closed checks. These
instantiate admitted inputs, execute the generated component and invoke its
refinement theorem. The existing Stage D synthetic admission checks remain
separate; no vector claims to solve OpenFullPrecompileCompositionObligation.
-/

namespace EvmFrameControlSettlementExtractor.Refinement.StageCFullPrecompileBridge

open Eip803x.Evm.FrameMachineState
open EvmFrameControlSettlementExtractor

def oracleBoundedFullFrameAddresses : List Nat :=
  [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 256]

theorem oracle_bounded_full_frame_routes_match_stage_c :
    oracleBoundedFullFrameAddresses = Eip803x.PrecompileFullFrame.Generated.admittedOracleAddresses := rfl

inductive FullPrecompileSemanticVector where
  | success
  | returnedFailure
  | managedFailure
  | pricingOverflow
  | pricingOutOfGas
  | missingNativeDependency
  deriving DecidableEq, Repr

def fullPrecompileSemanticVectors : List FullPrecompileSemanticVector :=
  [.success, .returnedFailure, .managedFailure, .pricingOverflow, .pricingOutOfGas, .missingNativeDependency]

theorem full_precompile_semantic_vector_count : fullPrecompileSemanticVectors.length * 2 = 12 := rfl

private def vectorMachine (nested : Bool) : Machine :=
  let seed := Specification.AdmissionWitnesses.fullPrecompile
  { seed with
    current := { seed.current with
      isTopLevel := !nested
      outputDestination := 9
      outputLength := 5
      gas := { seed.current.gas with gas := { seed.current.gas.gas with gasLeft := 40 } } }
    parents := if nested then Specification.AdmissionWitnesses.bytecodeContinuation.parents else [] }

private def vectorInput (nested : Bool) (vector : FullPrecompileSemanticVector) :
    Eip803x.PrecompileFullFrame.Input :=
  { machine := vectorMachine nested
    codeSource := some 1
    executingAccount := 1
    precompileName := "vector"
    baseCost := match vector with
      | .pricingOverflow => 2 ^ 64 - 1
      | .pricingOutOfGas => 41
      | _ => 7
    dataCost := match vector with
      | .pricingOverflow => 1
      | .pricingOutOfGas => 0
      | _ => 2
    wasCreated := false
    transferValueZero := true
    ripemdDead := false }

private def vectorFront : Eip803x.PrecompileFullFrame.FrontOperations :=
  { traceActionStart := fun _ machine => machine
    addTransferLog := id
    touchAccount := fun _ machine => machine
    traceActionEnd := fun machine _ => machine
    prepareTopLevelSubstate := fun _ result => { result with exit := .success, controlRoute := .ordinary } }

private def vectorOracle (vector : FullPrecompileSemanticVector) : Eip803x.PrecompileFullFrame.LeafOracle :=
  fun _ _ => match vector with
  | .returnedFailure => .returnedFailure (some "leaf-error")
  | .managedFailure => .managedException
  | .missingNativeDependency => .missingNativeDependency
  | _ => .success [Eip803x.Evm.MemoryStackControl.zeroByte]

private def vectorBridge (semantics : Semantics) (nested : Bool) (vector : FullPrecompileSemanticVector) : BridgeInput :=
  { stageDInput := { machine := vectorMachine nested }
    input := vectorInput nested vector
    oracleRoute := { address := 1, admitted := by decide }
    front := vectorFront
    referenceFront := vectorFront
    semantics
    oracle := vectorOracle vector
    referenceOracle := vectorOracle vector }

private theorem vector_input_admitted (nested : Bool) (vector : FullPrecompileSemanticVector) :
    (vectorInput nested vector).Admitted := by
  cases nested <;> cases vector <;>
    unfold Eip803x.PrecompileFullFrame.Input.Admitted <;>
    (repeat' apply And.intro) <;> first | rfl | exact True.intro | decide

private theorem vector_admission (semantics : Semantics) (nested : Bool) (vector : FullPrecompileSemanticVector) :
    StageCAdmission (vectorBridge semantics nested vector) :=
  { entryAdmitted := vector_input_admitted nested vector
    entryUsesOracleRoute := rfl
    frontAgrees := ⟨rfl, rfl, rfl, rfl, rfl⟩
    oracleAgrees := fun _ _ => rfl
    frontPreservesParents := by
      intro input
      simp [vectorBridge, Eip803x.PrecompileFullFrame.Reference.enter, vectorFront]
    topAdaptersValid := by
      intro raw parents _
      cases tracing : raw.machine.isTracingActions <;>
        simp [vectorBridge, vectorFront, parents] }

theorem vector_stage_c_refinement (semantics : Semantics) (nested : Bool) (vector : FullPrecompileSemanticVector) :
    stageCGeneratedFullFrame (vectorBridge semantics nested vector) =
      stageCReferenceFullFrame (vectorBridge semantics nested vector) :=
  stage_c_generated_full_frame_agrees_reference _ (vector_admission semantics nested vector)

private def expectedRawKind (nested : Bool) : FullPrecompileSemanticVector → Option Eip803x.PrecompileFullFrame.RawKind
  | .success => some .success
  | .returnedFailure => some .returnedFailure
  | .managedFailure => some (if nested then .managedNestedFailure else .managedTopFailure)
  | .pricingOverflow => some .pricingOverflow
  | .pricingOutOfGas => some .pricingOutOfGas
  | .missingNativeDependency => none

theorem vector_execution_has_expected_kind (semantics : Semantics) (nested : Bool) (vector : FullPrecompileSemanticVector) :
    (stageCExecution (vectorBridge semantics nested vector)).map (fun raw => raw.kind) = expectedRawKind nested vector := by
  cases nested <;> cases vector <;> rfl

private def expectedRoute (nested : Bool) : FullPrecompileSemanticVector → Option SettlementRoute
  | .success => some (if nested then .regularSuccessNested else .topLevelSuccess)
  | .returnedFailure => some (if nested then .fullPrecompileReturnedFailureNested else .fullPrecompileReturnedFailureTop)
  | .managedFailure => some (if nested then .fullPrecompileManagedExceptionNested else .fullPrecompileManagedExceptionTop)
  | .pricingOverflow | .pricingOutOfGas => some (if nested then .fullPrecompileOutOfGasNested else .fullPrecompileOutOfGasTop)
  | .missingNativeDependency => none

/- This checks classification of computed Stage C results only. Settlement
effects of the selected Stage D leaf remain the open composition obligation. -/
theorem vector_execution_has_expected_route (semantics : Semantics) (nested : Bool) (vector : FullPrecompileSemanticVector) :
    (stageCExecution (vectorBridge semantics nested vector)).map
      (fun raw => Generated.classify (vectorMachine nested) (invocationOfRaw raw)) = expectedRoute nested vector := by
  cases nested <;> cases vector <;> rfl

theorem vector_returned_failure_preserves_priced_gas_and_error (semantics : Semantics) (nested : Bool) :
    (stageCExecution (vectorBridge semantics nested .returnedFailure)).map
      (fun raw => (raw.machine.current.gas.gas.gasLeft, raw.result.substateError)) =
      some (31, some "Precompile vector failed with error: leaf-error") := rfl

theorem vector_success_preserves_output (semantics : Semantics) (nested : Bool) :
    (stageCExecution (vectorBridge semantics nested .success)).map
      (fun raw => (raw.result.output, raw.result.precompileSuccess)) =
      some ([Eip803x.Evm.MemoryStackControl.zeroByte], some true) := rfl

private def vectorRun (semantics : Semantics) (nested : Bool) (vector : FullPrecompileSemanticVector) :
    FullPrecompileRun (vectorBridge semantics nested vector) :=
  match execution : stageCExecution (vectorBridge semantics nested vector) with
  | none => .nativeDependencyUnavailable execution
  | some raw => .executed raw execution

theorem vector_executed_raw_is_admitted (semantics : Semantics) (nested : Bool)
    (vector : FullPrecompileSemanticVector) (raw : Eip803x.PrecompileFullFrame.RawResult)
    (execution : stageCExecution (vectorBridge semantics nested vector) = some raw) :
    Eip803x.PrecompileFullFrame.Refinement.RawAdmitted raw :=
  Eip803x.PrecompileFullFrame.Refinement.generated_execute_is_raw_admitted _ _ _
    (vector_admission semantics nested vector).entryAdmitted
    (vector_admission semantics nested vector).frontPreservesParents raw execution

theorem vector_missing_native_fails_closed (leaves : CanonicalLeaves) (semantics : Semantics) (nested : Bool) :
    (checkedRunResult leaves (vectorBridge semantics nested .missingNativeDependency)
      (vectorRun semantics nested .missingNativeDependency)).termination = .incomplete ∧
    (checkedRunResult leaves (vectorBridge semantics nested .missingNativeDependency)
      (vectorRun semantics nested .missingNativeDependency)).route = .noSettlement := by
  exact missing_native_is_incomplete leaves (vectorBridge semantics nested .missingNativeDependency) rfl

theorem vector_process_adapter_fails_closed (leaves : CanonicalLeaves) (semantics : Semantics) (nested : Bool) :
    (checkedRunResult leaves (vectorBridge semantics nested .success)
      (.processAdapterUnavailable "adapter unavailable" (by decide))).termination = .incomplete ∧
    (checkedRunResult leaves (vectorBridge semantics nested .success)
      (.processAdapterUnavailable "adapter unavailable" (by decide))).route = .noSettlement :=
  process_adapter_is_incomplete leaves (vectorBridge semantics nested .success) "adapter unavailable" (by decide)

private def statusVector : ShellResult :=
  { failClosedShell (vectorMachine false) .processAdapterUnavailable with
    status := some "driver-status"
    result := some
      { exit := .exception .outOfGas
        controlRoute := .handleFailure
        frame := (vectorMachine false).current
        output := []
        createdAddress := none
        precompileSuccess := some false
        substateError := some "leaf-error"
        shouldRestoreRipemdTouch := false
        controlTrace := [] } }

theorem vector_status_and_substate_error_are_distinct :
    (observeFullPrecompile statusVector).driver.status = some "driver-status" ∧
    (observeFullPrecompile statusVector).substateError = some "leaf-error" := ⟨rfl, rfl⟩

theorem vector_stage_d_admission_is_separately_constructible :
    ∃ admitted : Machine → Prop,
      admitted Specification.AdmissionWitnesses.bytecodeTerminal ∧
      admitted Specification.AdmissionWitnesses.bytecodeContinuation ∧
      admitted Specification.AdmissionWitnesses.fullPrecompile ∧ FixedWidthFacts admitted :=
  Specification.AdmissionWitnesses.representative_admission_is_inhabited

end EvmFrameControlSettlementExtractor.Refinement.StageCFullPrecompileBridge
