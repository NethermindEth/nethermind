-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Specification.FrameMachineExecution

/-!
Concrete relation vocabulary for a future generated frame machine. No generated
artifact is imported and no theorem is asserted. Acceptance requires the future
proof statements to instantiate these relations with named extracted definitions,
not with a caller-populated record of functions or unconstrained propositions.
-/

namespace Eip803x.Evm.FrameMachineRefinement

open FrameMachineState

structure DependencyIdentity where
  kind : String
  name : String
  path : String
  sha256 : String
  proofModule : Option String
  proofModulePath : Option String
  proofModuleSha256 : Option String
  requiredTheorems : List ProofTheoremIdentity
  deriving DecidableEq, Repr

def dependencyKey (dependency : DependencyIdentity) : String × String × String :=
  (dependency.kind, dependency.name, dependency.path)

def expectedDependencyKeys (packages : List OpcodePackageBinding)
    (precompiles : List PrecompileRoute) : List (String × String × String) :=
  packages.map (fun package => ("opcodePackage", package.name, package.manifestPath)) ++
  precompiles.map (fun route => ("precompileWrapper", route.name, route.wrapperManifestPath))

def DependencyPathsUnique (dependencies : List DependencyIdentity) : Prop :=
  (dependencies.flatMap (fun dependency =>
    dependency.path :: dependency.proofModulePath.toList)).Nodup

def DependencyBindingsExact (packages : List OpcodePackageBinding)
    (precompiles : List PrecompileRoute) (dependencies : List DependencyIdentity) : Prop :=
  packages.length = 14 ∧
  precompiles.length = 18 ∧
  PackageBindingsUnique packages ∧
  PrecompileAddressesUnique precompiles ∧
  dependencies.length = 32 ∧
  DependencyPathsUnique dependencies ∧
  dependencies.map dependencyKey = expectedDependencyKeys packages precompiles ∧
  (∀ package ∈ packages, ∃ dependency ∈ dependencies,
    dependency.kind = "opcodePackage" ∧ dependency.name = package.name ∧
    dependency.path = package.manifestPath ∧ dependency.sha256 = package.manifestSha256 ∧
    dependency.proofModule = some package.leanModule ∧
    dependency.proofModulePath = some package.proofModulePath ∧
    dependency.proofModuleSha256 = some package.proofModuleSha256 ∧
    dependency.requiredTheorems = package.requiredTheorems) ∧
  (∀ route ∈ precompiles, ∃ dependency ∈ dependencies,
    dependency.kind = "precompileWrapper" ∧ dependency.name = route.name ∧
    dependency.path = route.wrapperManifestPath ∧
    dependency.sha256 = route.wrapperManifestSha256 ∧
    dependency.proofModule = none ∧ dependency.proofModulePath = none ∧
    dependency.proofModuleSha256 = none ∧ dependency.requiredTheorems = [])

def ChildBaselineMatches (suspended : SuspendedFrame) (child : Frame) : Prop :=
  suspended.childBaseline.executionType = child.executionType ∧
  suspended.childBaseline.snapshot = child.snapshot ∧
  suspended.childBaseline.initialStateGasUsed = child.initialStateGasUsed ∧
  suspended.childBaseline.stateGasRefundAdvancedAtEntry = child.stateGasRefundAdvanced ∧
  suspended.childBaseline.isCreateOnPreExistingAccount = child.isCreateOnPreExistingAccount ∧
  suspended.childBaseline.isCreateStateGasCharged = child.isCreateStateGasCharged ∧
  suspended.childBaseline.newAccountCharged = child.newAccountCharged

def SettlementPrimitivesRefine (extracted reference : SettlementPrimitives) : Prop :=
  (∀ parent child,
    extracted.incorporateAdvancedRefund parent child =
      reference.incorporateAdvancedRefund parent child) ∧
  (∀ entry child,
    extracted.refundChildGas entry child = reference.refundChildGas entry child) ∧
  (∀ parent child,
    extracted.returnChildExecutionGas parent child =
      reference.returnChildExecutionGas parent child) ∧
  (∀ parent child,
    extracted.removeAdvancedRefund parent child = reference.removeAdvancedRefund parent child) ∧
  (∀ parent child,
    extracted.restoreChildStateGas parent child = reference.restoreChildStateGas parent child) ∧
  (∀ parent child,
    extracted.restoreChildStateGasOnHalt parent child =
      reference.restoreChildStateGasOnHalt parent child) ∧
  (∀ parent child,
    extracted.revertRefundToHalt parent child = reference.revertRefundToHalt parent child) ∧
  (∀ frame,
    extracted.refundRevertedTopLevelStateGas frame =
      reference.refundRevertedTopLevelStateGas frame) ∧
  (∀ frame,
    extracted.clearExecutionGas frame = reference.clearExecutionGas frame) ∧
  (∀ frame,
    extracted.repayStateGasSpill frame = reference.repayStateGasSpill frame) ∧
  (∀ frame amount,
    extracted.creditStateGasRefund frame amount = reference.creditStateGasRefund frame amount) ∧
  extracted.stateGasCreateCost = reference.stateGasCreateCost ∧
  extracted.stateGasNewAccountCost = reference.stateGasNewAccountCost ∧
  (∀ parent child,
    extracted.commitChild parent child = reference.commitChild parent child) ∧
  (∀ snapshot frame,
    extracted.restoreSnapshot snapshot frame = reference.restoreSnapshot snapshot frame) ∧
  (∀ restore frame,
    extracted.restoreRipemdTouch restore frame = reference.restoreRipemdTouch restore frame) ∧
  (∀ machine,
    extracted.traceOperationRemainingGasZero machine =
      reference.traceOperationRemainingGasZero machine) ∧
  (∀ kind machine,
    extracted.traceOperationFailure kind machine =
      reference.traceOperationFailure kind machine) ∧
  (∀ kind machine,
    extracted.traceActionFailure kind machine = reference.traceActionFailure kind machine) ∧
  (∀ result frame,
    extracted.calculateCodeDeposit result frame =
      reference.calculateCodeDeposit result frame) ∧
  (∀ plan frame,
    extracted.chargeCodeDeposit plan frame = reference.chargeCodeDeposit plan frame) ∧
  (∀ frame amount,
    extracted.consumeReturnedExecutionGas frame amount =
      reference.consumeReturnedExecutionGas frame amount) ∧
  (∀ result frame,
    extracted.insertCode result frame = reference.insertCode result frame) ∧
  (∀ frame,
    extracted.deleteCreatedAccount frame = reference.deleteCreatedAccount frame)

def SemanticsRefine (extracted reference : Semantics) : Prop :=
  (∀ frame, extracted.enterFreshFrame frame = reference.enterFreshFrame frame) ∧
  (∀ machine,
    extracted.enterContinuation machine = reference.enterContinuation machine) ∧
  (∀ route machine,
    extracted.runOpcode route machine = reference.runOpcode route machine) ∧
  (∀ route,
    extracted.precompileEnabled route = reference.precompileEnabled route) ∧
  (∀ route machine,
    extracted.runPrecompile route machine = reference.runPrecompile route machine) ∧
  (∀ address machine,
    extracted.missingPrecompile address machine = reference.missingPrecompile address machine) ∧
  (∀ machine, extracted.stopAtEnd machine = reference.stopAtEnd machine) ∧
  (∀ machine byte,
    extracted.badInstruction machine byte = reference.badInstruction machine byte) ∧
  SettlementPrimitivesRefine extracted.settlement reference.settlement

def RouteLookupRefines (routes : List OpcodeRoute)
    (extractedRouteAt : DispatchTable -> Byte -> Option OpcodeRoute) : Prop :=
  TableByteRoutesComplete routes ∧
  ∀ table byte,
    extractedRouteAt table byte = FrameMachineExecution.routeAt routes table byte

def PrecompileLookupRefines (routes : List PrecompileRoute)
    (extractedPrecompileAt : Nat -> Option PrecompileRoute) : Prop :=
  PrecompileAddressesUnique routes ∧
  ∀ address,
    extractedPrecompileAt address = FrameMachineExecution.precompileAt routes address

def FrameDriverRefines (routes : List OpcodeRoute) (precompiles : List PrecompileRoute)
    (extractedSemantics referenceSemantics : Semantics)
    (extractedPrepare : Machine -> Machine)
    (extractedSettle : Machine -> FrameResult -> Settlement)
    (extractedStep : Machine -> MachineStep)
    (extractedDrive : Nat -> Machine -> RunResult)
    (extractedExecuteAmsterdam : Nat -> Machine -> RunResult) : Prop :=
  SemanticsRefine extractedSemantics referenceSemantics ∧
  (∀ machine,
    extractedPrepare machine = FrameMachineExecution.prepareMachine referenceSemantics machine) ∧
  (∀ machine result,
    extractedSettle machine result =
      FrameMachineExecution.settleChild referenceSemantics machine result) ∧
  (∀ machine,
    extractedStep machine =
      FrameMachineExecution.stepFrame referenceSemantics routes precompiles machine) ∧
  (∀ fuel machine,
    extractedDrive fuel machine =
      FrameMachineExecution.drive referenceSemantics routes precompiles fuel machine) ∧
  (∀ fuel initial,
    extractedExecuteAmsterdam fuel initial =
      FrameMachineExecution.executeAmsterdam referenceSemantics routes precompiles fuel initial)

end Eip803x.Evm.FrameMachineRefinement
