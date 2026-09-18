-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameControlSettlementExtractor.Refinement.FrameControlSettlement

/-!
Typed OPEN composition obligations for an already-created full-frame precompile.
The checked results below are separate Stage C refinement and Stage D control
agreement. There is deliberately no Stage C-to-Stage D refinement theorem.

Stage D's settlement leaves have no concrete definitions here. Its failure
invocation tags also omit the mutated machine and frame result carried by Stage
C. Stage C's top-level `Settlement.complete` in turn omits the full terminal
machine. Consequently neither component theorem supplies the missing adapter.
In particular, a caller-provided equality or arbitrary settlement projection
cannot discharge this open composition work.
-/

namespace EvmFrameControlSettlementExtractor.Refinement.StageCFullPrecompileBridge

open Eip803x.Evm.FrameMachineState
open EvmFrameControlSettlementExtractor

/- Preserve Stage D's complete observation, including status, gas, output,
journal, traces and disposal, and retain substate error independently of status. -/
structure FullPrecompileObservation where
  driver : Observation
  substateError : Option String
  deriving DecidableEq, Repr

def observeFullPrecompile (result : ShellResult) : FullPrecompileObservation :=
  { driver := observe result
    substateError := result.result.bind (fun frameResult => frameResult.substateError) }

def entryMachine : Eip803x.PrecompileFullFrame.Entry → Machine
  | .fullFrame input => input.machine
  | .outerEvmException machine _ => machine
  | .outerOverflow machine => machine

structure OracleBoundedFullFrameRoute where
  address : Nat
  admitted : address ∈ Eip803x.PrecompileFullFrame.Generated.admittedOracleAddresses

structure BridgeInput where
  stageDInput : EvmFrameControlSettlementExtractor.Input
  input : Eip803x.PrecompileFullFrame.Input
  oracleRoute : OracleBoundedFullFrameRoute
  front : Eip803x.PrecompileFullFrame.FrontOperations
  referenceFront : Eip803x.PrecompileFullFrame.FrontOperations
  semantics : Semantics
  oracle : Eip803x.PrecompileFullFrame.LeafOracle
  referenceOracle : Eip803x.PrecompileFullFrame.LeafOracle

def BridgeInput.entry (bridge : BridgeInput) : Eip803x.PrecompileFullFrame.Entry :=
  .fullFrame bridge.input

structure StageCAdmission (bridge : BridgeInput) : Prop where
  entryAdmitted : bridge.entry.Admitted
  entryUsesOracleRoute : Eip803x.PrecompileFullFrame.actionAddress bridge.input = bridge.oracleRoute.address
  frontAgrees : Eip803x.PrecompileFullFrame.FrontAgrees bridge.front bridge.referenceFront
  oracleAgrees : Eip803x.PrecompileFullFrame.OracleAgrees bridge.oracle bridge.referenceOracle
  frontPreservesParents : Eip803x.PrecompileFullFrame.Refinement.FrontPreservesParents bridge.front
  topAdaptersValid : Eip803x.PrecompileFullFrame.Refinement.TopAdaptersValid bridge.front

def preparedMachine (leaves : CanonicalLeaves) (bridge : BridgeInput) : Machine :=
  Generated.prepare leaves bridge.stageDInput.machine

/- This bundles the existing component premises, not an equality between their
results. Entry preparation correspondence itself remains an open obligation. -/
structure BridgeAdmission (leaves : CanonicalLeaves) (bridge : BridgeInput) where
  stageC : StageCAdmission bridge
  stageD : StepAdmission leaves bridge.stageDInput
  oracles : ExternalOracles
  bindings : SourceAdapterBindings leaves stageD.admits oracles
  fixedWidth : FixedWidthFacts stageD.admits
  preparedFullPrecompile : subjectOf (preparedMachine leaves bridge) = .fullPrecompile

def stageCExecution (bridge : BridgeInput) : Option Eip803x.PrecompileFullFrame.RawResult :=
  Eip803x.PrecompileFullFrame.Generated.execute bridge.front bridge.oracle bridge.entry

def stageCGeneratedFullFrame (bridge : BridgeInput) : Option Settlement :=
  Eip803x.PrecompileFullFrame.Generated.fullFrame bridge.front bridge.semantics.settlement
    bridge.oracle bridge.entry

def stageCReferenceFullFrame (bridge : BridgeInput) : Option Settlement :=
  Eip803x.PrecompileFullFrame.Reference.fullFrame bridge.referenceFront bridge.semantics
    bridge.referenceOracle bridge.entry

theorem stage_c_generated_full_frame_agrees_reference
    (bridge : BridgeInput) (admission : StageCAdmission bridge) :
    stageCGeneratedFullFrame bridge = stageCReferenceFullFrame bridge :=
  stage_c_full_theorem_is_constructible bridge.front bridge.referenceFront bridge.semantics
    bridge.oracle bridge.referenceOracle bridge.entry admission.frontAgrees admission.oracleAgrees
    admission.entryAdmitted admission.frontPreservesParents admission.topAdaptersValid

/- The source-adapter and driver-boundary witnesses are used to derive the
dispatch domain and binding. Output bounds come from Stage D output admission. -/
theorem stage_d_component_evidence
    (leaves : CanonicalLeaves) (bridge : BridgeInput) (admission : BridgeAdmission leaves bridge) :
    observe (Generated.driveIteration leaves bridge.stageDInput.machine) =
      observe (Reference.evaluateIteration leaves bridge.stageDInput.machine) ∧
    FrameFieldBounds (Generated.driveIteration leaves bridge.stageDInput.machine).machine ∧
    FrameFieldBounds (preparedMachine leaves bridge) ∧
    isFullPrecompileInvocation (preparedMachine leaves bridge)
      (leaves.dispatch.executeFullPrecompileFrame (preparedMachine leaves bridge)) ∧
    leaves.dispatch.executeFullPrecompileFrame (preparedMachine leaves bridge) =
      admission.oracles.fullPrecompileFrame (preparedMachine leaves bridge) ∧
    (Generated.dispatch leaves (preparedMachine leaves bridge)).directInlineStaticPrecompile = none := by
  have checked := hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement
    leaves bridge.stageDInput admission.stageD admission.oracles admission.bindings admission.fixedWidth
  have prepared := admission.stageD.preparedAdmitted _ admission.stageD.inputAdmitted
  have boundary : DriverBoundarySound leaves admission.stageD.admits admission.oracles :=
    checked.2.2.2.2.2.2.2
  refine ⟨checked.1, checked.2.1, checked.2.2.1.prepared _ admission.stageD.inputAdmitted,
    boundary.fullPrecompileOutcomeCompletesCurrentFrame _ prepared admission.preparedFullPrecompile,
    boundary.fullPrecompileFrameBound _ prepared admission.preparedFullPrecompile, ?_⟩
  simp [Generated.dispatch, admission.preparedFullPrecompile]

theorem separately_checked_components
    (leaves : CanonicalLeaves) (bridge : BridgeInput) (admission : BridgeAdmission leaves bridge) :
    stageCGeneratedFullFrame bridge = stageCReferenceFullFrame bridge ∧
    observe (Generated.driveIteration leaves bridge.stageDInput.machine) =
      observe (Reference.evaluateIteration leaves bridge.stageDInput.machine) :=
  ⟨stage_c_generated_full_frame_agrees_reference bridge admission.stageC,
    (stage_d_component_evidence leaves bridge admission).1⟩

def invocationOfRaw (raw : Eip803x.PrecompileFullFrame.RawResult) : InvocationResult :=
  match raw.kind with
  | .pricingOverflow | .pricingOutOfGas => .fullPrecompileOutOfGas
  | .returnedFailure => .fullPrecompileReturnedFailure raw.result.substateError
  | .managedTopFailure | .managedNestedFailure => .fullPrecompileManagedException raw.result.substateError
  | .success => .fullPrecompileSuccess
      { machine := raw.machine, controlRoute := raw.result.controlRoute, result := .halt raw.result }
  | .outerEvmException =>
      match raw.result.exit with
      | .exception kind => .thrownEvm kind
      | _ => .thrownEvm .other
  | .outerOverflow => .thrownOverflow

inductive FailClosedReason where
  | missingNativeDependency
  | processAdapterUnavailable
  deriving DecidableEq, Repr

def failClosedReasonText : FailClosedReason → String
  | .missingNativeDependency => "missingNativeDependency"
  | .processAdapterUnavailable => "processAdapterUnavailable"

/- This is a verification-harness result. The production missing-native branch
exits the process; it does not return a successful VM frame or this Lean shell. -/
def failClosedShell (machine : Machine) (reason : FailClosedReason) : ShellResult :=
  { termination := .incomplete
    route := .noSettlement
    machine
    result := none
    reason := some (failClosedReasonText reason)
    status := some "fullFramePrecompileFailClosed"
    directInlineStaticPrecompile := none
    disposal := DisposalObservation.notObserved }

inductive FullPrecompileRun (bridge : BridgeInput) where
  | nativeDependencyUnavailable (execution : stageCExecution bridge = none)
  | processAdapterUnavailable (reason : String) (nonempty : reason ≠ "")
  | executed (raw : Eip803x.PrecompileFullFrame.RawResult) (execution : stageCExecution bridge = some raw)

def checkedRunResult (leaves : CanonicalLeaves) (bridge : BridgeInput) : FullPrecompileRun bridge → ShellResult
  | .nativeDependencyUnavailable _ => failClosedShell bridge.stageDInput.machine .missingNativeDependency
  | .processAdapterUnavailable _ _ => failClosedShell bridge.stageDInput.machine .processAdapterUnavailable
  | .executed _ _ => Generated.driveIteration leaves bridge.stageDInput.machine

theorem missing_native_is_incomplete (leaves : CanonicalLeaves) (bridge : BridgeInput)
    (execution : stageCExecution bridge = none) :
    (observeFullPrecompile (checkedRunResult leaves bridge (.nativeDependencyUnavailable execution))).driver.termination =
      .incomplete ∧
    (observeFullPrecompile (checkedRunResult leaves bridge (.nativeDependencyUnavailable execution))).driver.route =
      .noSettlement := ⟨rfl, rfl⟩

theorem process_adapter_is_incomplete (leaves : CanonicalLeaves) (bridge : BridgeInput)
    (reason : String) (nonempty : reason ≠ "") :
    (checkedRunResult leaves bridge (.processAdapterUnavailable reason nonempty)).termination = .incomplete ∧
    (checkedRunResult leaves bridge (.processAdapterUnavailable reason nonempty)).route = .noSettlement := ⟨rfl, rfl⟩

/- Only fields actually represented by Stage C's settlement carrier are related.
For a top-level result the complete machine, transaction trace and disposal are
absent from that carrier. Proving this relation will therefore still fall short
of equality of FullPrecompileObservation. A concrete source adapter must first
supply those missing observations and trace/journal ownership. -/
def RepresentedSettlementPreserved (settlement : Settlement) (shell : ShellResult) : Prop :=
  match settlement with
  | .complete result =>
      shell.termination = .completed ∧ shell.result = some result ∧
      shell.machine.current = result.frame ∧
      shell.machine.shouldRestoreRipemdTouch = result.shouldRestoreRipemdTouch ∧
      shell.machine.controlTrace = result.controlTrace ∧
      (observe shell).status = result.substateError
  | .resume machine => shell.termination = .completed ∧ shell.machine = machine
  | .invalidControl machine => shell.termination = .incomplete ∧ shell.machine = machine

/- OPEN: these are goals for future concrete adapters, never premises of a
claimed refinement theorem. Route classification is checked independently of
the settlement result, and cannot be chosen by a caller-provided projection. -/
structure OpenFullPrecompileCompositionObligation (leaves : CanonicalLeaves) (bridge : BridgeInput) : Prop where
  preparation : entryMachine bridge.entry = preparedMachine leaves bridge
  dispatch : ∀ raw, stageCExecution bridge = some raw →
    leaves.dispatch.executeFullPrecompileFrame (preparedMachine leaves bridge) = invocationOfRaw raw
  route : ∀ raw, stageCExecution bridge = some raw →
    (Generated.driveIteration leaves bridge.stageDInput.machine).route =
      Generated.classify (preparedMachine leaves bridge) (invocationOfRaw raw)
  representedSettlement : ∀ settlement, stageCReferenceFullFrame bridge = some settlement →
    RepresentedSettlementPreserved settlement (Generated.driveIteration leaves bridge.stageDInput.machine)

end EvmFrameControlSettlementExtractor.Refinement.StageCFullPrecompileBridge
