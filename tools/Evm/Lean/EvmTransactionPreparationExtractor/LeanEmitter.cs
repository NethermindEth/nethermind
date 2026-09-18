// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.EvmTransactionPreparationExtractor;

/// <summary>Emits a theorem-free, source-bound transition for the admitted pre-VM boundary.</summary>
internal static class LeanEmitter
{
    internal static byte[] Emit(IrDocument document, string irSha256)
    {
        if (document.Semantics is null || document.Semantics.Plan is null || document.Semantics.Operations is null ||
            document.Semantics.Branches is null || document.Semantics.AdapterPremises is null || document.Semantics.ControlFlows is null)
            throw new ExtractionException("Cannot emit Lean from an incomplete normalized semantic plan.");
        Extractor.ValidateForEmission(document);

        StringBuilder output = new();
        AppendHeader(output, document, irSha256);
        AppendMetadata(output, document);
        AppendTypes(output);
        AppendTransition(output, document);
        return new UTF8Encoding(false).GetBytes(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void AppendHeader(StringBuilder output, IrDocument document, string irSha256)
    {
        output.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        output.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        output.AppendLine();
        output.AppendLine("import Lean");
        output.AppendLine();
        output.AppendLine("namespace EvmTransactionPreparationExtractor.Generated");
        output.AppendLine();
        output.AppendLine("def schemaVersion : Nat := " + document.SchemaVersion);
        output.AppendLine("def extractorVersion : String := \"" + Escape(document.ExtractorVersion) + "\"");
        output.AppendLine("def acceptanceState : String := \"" + Escape(document.AcceptanceState) + "\"");
        output.AppendLine("def sourceBoundary : String := \"" + Escape(document.Semantics.SourceBoundary) + "\"");
        output.AppendLine("def targetGasPolicy : String := \"" + Escape(document.TargetGasPolicy) + "\"");
        output.AppendLine("def admissionSha256 : String := \"" + Escape(document.AdmissionSha256) + "\"");
        output.AppendLine("def sourceClosureSha256 : String := \"" + Escape(document.SourceClosureSha256) + "\"");
        output.AppendLine("def semanticIrSha256 : String := \"" + Escape(irSha256) + "\"");
        output.AppendLine("def semanticPlanSha256 : String := \"" + Escape(document.Semantics.Plan.Sha256) + "\"");
        output.AppendLine();
    }

    private static void AppendMetadata(StringBuilder output, IrDocument document)
    {
        output.AppendLine("def sourceIdentities : List (String × String × String × String) := [");
        foreach (SourceIdentity source in document.Sources)
            output.AppendLine("  (\"" + Escape(source.Path) + "\", \"" + Escape(source.Role) + "\", \"" + Escape(source.Sha256) + "\", \"" + Escape(source.SyntaxSha256) + "\"),");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def compilerReferenceIdentities : List (String × String × String × String × Bool × List String) := [");
        foreach (CompilerReferenceIdentity reference in document.CompilerReferences)
            output.AppendLine("  (\"" + Escape(reference.Path) + "\", \"" + Escape(reference.AssemblyName) + "\", \"" + Escape(reference.Sha256) + "\", \"" + Escape(reference.Mvid) + "\", " + (reference.Selected ? "true" : "false") + ", " + LeanStringList(reference.Dependencies) + "),");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("structure DependencyEvidence where");
        output.AppendLine("  kind : String");
        output.AppendLine("  name : String");
        output.AppendLine("  path : String");
        output.AppendLine("  sha256 : String");
        output.AppendLine("  binding : String");
        output.AppendLine("  theorems : List String");
        output.AppendLine("deriving BEq");
        output.AppendLine("def dependencyIdentities : List DependencyEvidence := [");
        foreach (DependencyIdentity dependency in document.Dependencies)
            output.AppendLine("  { kind := " + LeanString(dependency.Kind) + ", name := " + LeanString(dependency.Name) + ", path := " + LeanString(dependency.Path) + ", sha256 := " + LeanString(dependency.Sha256) + ", binding := " + LeanString(dependency.Binding) + ", theorems := " + LeanStringList(dependency.Theorems) + " },");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("structure StageEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  ordinal : Int");
        output.AppendLine("  owner : String");
        output.AppendLine("  kind : String");
        output.AppendLine("  operationIds : List String");
        output.AppendLine("deriving BEq");
        output.AppendLine("def admittedStages : List StageEvidence := [");
        foreach (StageIdentity stage in document.Semantics.Stages)
            output.AppendLine("  { id := " + LeanString(stage.Id) + ", ordinal := " + stage.Ordinal + ", owner := " + LeanString(stage.Owner) + ", kind := " + LeanString(stage.Kind) + ", operationIds := " + LeanStringList(stage.OperationIds) + " },");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("structure OperationEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  ordinal : Int");
        output.AppendLine("  formula : String");
        output.AppendLine("  expression : String");
        output.AppendLine("  expected : String");
        output.AppendLine("  syntaxHash : String");
        output.AppendLine("  owner : String");
        output.AppendLine("  cfgBlock : Int");
        output.AppendLine("  receiver : String");
        output.AppendLine("  target : String");
        output.AppendLine("  sourceOperands : List String");
        output.AppendLine("  reads : List String");
        output.AppendLine("  writes : List String");
        output.AppendLine("  failureVisibility : String");
        output.AppendLine("  effects : List String");
        output.AppendLine("  binding : String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure BranchEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  ordinal : Int");
        output.AppendLine("  owner : String");
        output.AppendLine("  condition : String");
        output.AppendLine("  terminalKind : String");
        output.AppendLine("  effects : List String");
        output.AppendLine("  syntaxHash : String");
        output.AppendLine("  terminal : String");
        output.AppendLine("  successors : List Int");
        output.AppendLine("  exits : List Int");
        output.AppendLine("  binding : String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure AdapterEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  domain : String");
        output.AppendLine("  projection : String");
        output.AppendLine("  assumption : String");
        output.AppendLine("  requiredFields : List String");
        output.AppendLine("  bindings : List String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure CompositionEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  kind : String");
        output.AppendLine("  artifactPath : String");
        output.AppendLine("  artifactSha256 : String");
        output.AppendLine("  theoremName : String");
        output.AppendLine("  theoremShape : String");
        output.AppendLine("  requiredFields : List String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure ControlFlowEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  owner : String");
        output.AppendLine("  member : String");
        output.AppendLine("  reachable : List Int");
        output.AppendLine("  edges : List (Int × Int)");
        output.AppendLine("  normalExits : List Int");
        output.AppendLine("  exceptionalExits : List Int");
        output.AppendLine("  backEdges : List (Int × Int)");
        output.AppendLine("  loopHeaders : List Int");
        output.AppendLine("  binding : String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure BindingEvidence where");
        output.AppendLine("  path : String");
        output.AppendLine("  owner : String");
        output.AppendLine("  member : String");
        output.AppendLine("  signature : String");
        output.AppendLine("  nodeKind : String");
        output.AppendLine("  operation : String");
        output.AppendLine("  canonicalSyntax : String");
        output.AppendLine("  tokenSha256 : String");
        output.AppendLine("  syntaxHash : String");
        output.AppendLine("  containingMember : String");
        output.AppendLine("  target : String");
        output.AppendLine("  targetKind : String");
        output.AppendLine("  targetAssembly : String");
        output.AppendLine("  receiver : String");
        output.AppendLine("  arguments : List String");
        output.AppendLine("  refKinds : List String");
        output.AppendLine("  candidateReason : String");
        output.AppendLine("  isErrorSymbol : Bool");
        output.AppendLine("  hasCandidateSymbols : Bool");
        output.AppendLine("  statementOrdinal : Int");
        output.AppendLine("  controlFlowPath : String");
        output.AppendLine("  cfgBlock : Int");
        output.AppendLine("  reachable : Bool");
        output.AppendLine("  startLine : Int");
        output.AppendLine("  startColumn : Int");
        output.AppendLine("  endLine : Int");
        output.AppendLine("  endColumn : Int");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure GasFieldEvidence where");
        output.AppendLine("  name : String");
        output.AppendLine("  owner : String");
        output.AppendLine("  sourceType : String");
        output.AppendLine("  width : Int");
        output.AppendLine("  representation : String");
        output.AppendLine("  fixedWidthOperations : List String");
        output.AppendLine("  binding : String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure SnapshotEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  owner : String");
        output.AppendLine("  kind : String");
        output.AppendLine("  fields : List String");
        output.AppendLine("  binding : String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure EnvironmentEvidence where");
        output.AppendLine("  fields : List String");
        output.AppendLine("  codeFields : List String");
        output.AppendLine("  inputFields : List String");
        output.AppendLine("  binding : String");
        output.AppendLine("deriving BEq");
        output.AppendLine();
        output.AppendLine("structure HandoffEvidence where");
        output.AppendLine("  id : String");
        output.AppendLine("  fields : List String");
        output.AppendLine("  boundaryCall : String");
        output.AppendLine("  rentOwner : String");
        output.AppendLine("  rentOverload : String");
        output.AppendLine("  rentReceiver : String");
        output.AppendLine("  rentArguments : List String");
        output.AppendLine("  rentRefKinds : List String");
        output.AppendLine("  vmOwner : String");
        output.AppendLine("  vmOverloads : String");
        output.AppendLine("  vmReceiver : String");
        output.AppendLine("  vmArguments : List String");
        output.AppendLine("  vmRefKinds : List String");
        output.AppendLine("  boundaryBindings : List String");
        output.AppendLine("  accessLineage : List String");
        output.AppendLine("  accessBindings : List String");
        output.AppendLine("  binding : String");
        output.AppendLine("deriving BEq");
        output.AppendLine();

        output.AppendLine("def admittedOperations : List OperationEvidence := [");
        foreach (OperationIdentity operation in document.Semantics.Operations)
            output.AppendLine("  " + LeanOperationRecord(operation) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedBindings : List BindingEvidence := [");
        foreach (SourceBinding binding in document.Semantics.Bindings)
            output.AppendLine("  " + LeanBindingRecord(binding) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedBranches : List BranchEvidence := [");
        foreach (BranchIdentity branch in document.Semantics.Branches)
            output.AppendLine("  " + LeanBranchRecord(branch) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedAdapters : List AdapterEvidence := [");
        foreach (AdapterPremise adapter in document.Semantics.AdapterPremises)
            output.AppendLine("  " + LeanAdapterRecord(adapter) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedCompositions : List CompositionEvidence := [");
        foreach (CompositionDependency dependency in document.Composition)
            output.AppendLine("  " + LeanCompositionRecord(dependency) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedControlFlows : List ControlFlowEvidence := [");
        foreach (ControlFlowIdentity flow in document.Semantics.ControlFlows)
            output.AppendLine("  " + LeanControlFlowRecord(flow) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedGasFields : List GasFieldEvidence := [");
        foreach (GasFieldIdentity field in document.Semantics.GasFields)
            output.AppendLine("  " + LeanGasFieldRecord(field) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedSnapshots : List SnapshotEvidence := [");
        foreach (SnapshotIdentity snapshot in document.Semantics.Snapshots)
            output.AppendLine("  " + LeanSnapshotRecord(snapshot) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def admittedEnvironment : EnvironmentEvidence := " + LeanEnvironmentRecord(document.Semantics.Environment));
        output.AppendLine("def admittedHandoff : HandoffEvidence := " + LeanHandoffRecord(document.Semantics.Handoff));
        output.AppendLine();

        output.AppendLine("def orderedEffects : List String := [");
        foreach (string effect in document.Semantics.OrderedEffects)
            output.AppendLine("  \"" + Escape(effect) + "\",");
        output.AppendLine("]");
        output.AppendLine();
        output.AppendLine("def expectedSemanticPlanStageIds : List String := " + LeanStringList(document.Semantics.Plan.StageIds));
        output.AppendLine("def expectedSemanticPlanOperationIds : List String := " + LeanStringList(document.Semantics.Plan.OperationIds));
        output.AppendLine("def expectedSemanticPlanBranchIds : List String := " + LeanStringList(document.Semantics.Plan.BranchIds));
        output.AppendLine("def expectedSemanticPlanGasFieldIds : List String := " + LeanStringList(document.Semantics.Plan.GasFieldIds));
        output.AppendLine("def expectedSemanticPlanSnapshotIds : List String := " + LeanStringList(document.Semantics.Plan.SnapshotIds));
        output.AppendLine("def expectedSemanticPlanAdapterIds : List String := " + LeanStringList(document.Semantics.Plan.AdapterIds));
        output.AppendLine("def expectedSemanticPlanControlFlowIds : List String := " + LeanStringList(document.Semantics.Plan.ControlFlowIds));
        output.AppendLine("def expectedSemanticPlanEffectIds : List String := " + LeanStringList(document.Semantics.Plan.EffectIds));
        output.AppendLine("def expectedSemanticPlanBoundaryOperationId : String := " + LeanString(document.Semantics.Plan.BoundaryOperationId));
        output.AppendLine("def semanticPlanStageIds : List String := admittedStages.map StageEvidence.id");
        output.AppendLine("def semanticPlanOperationIds : List String := admittedOperations.map OperationEvidence.id");
        output.AppendLine("def semanticPlanBranchIds : List String := admittedBranches.map BranchEvidence.id");
        output.AppendLine("def semanticPlanGasFieldIds : List String := admittedGasFields.map GasFieldEvidence.name");
        output.AppendLine("def semanticPlanSnapshotIds : List String := admittedSnapshots.map SnapshotEvidence.id");
        output.AppendLine("def semanticPlanAdapterIds : List String := admittedAdapters.map AdapterEvidence.id");
        output.AppendLine("def semanticPlanControlFlowIds : List String := admittedControlFlows.map ControlFlowEvidence.id");
        output.AppendLine("def semanticPlanEffectIds : List String := expectedSemanticPlanEffectIds");
        output.AppendLine("def semanticPlanBoundaryOperationId : String := match admittedOperations.reverse with | operation :: _ => operation.id | [] => \"\"");
        output.AppendLine();

        AppendSemanticEvidence(output, document);
        foreach (OperationIdentity operation in document.Semantics.Operations)
            output.AppendLine("def op_" + LeanIdentifier(operation.Id) + " : Bool := operationReady " + LeanOperationRecord(operation));
        foreach (BranchIdentity branch in document.Semantics.Branches)
            output.AppendLine("def branch_" + LeanIdentifier(branch.Id) + " : Bool := branchReady " + LeanBranchRecord(branch));
        foreach (AdapterPremise adapter in document.Semantics.AdapterPremises)
            output.AppendLine("def adapter_" + LeanIdentifier(adapter.Id) + " : Bool := adapterReady " + LeanAdapterRecord(adapter));
        foreach (ControlFlowIdentity flow in document.Semantics.ControlFlows)
            output.AppendLine("def cfg_" + LeanIdentifier(flow.Id) + " : Bool := controlFlowReady " + LeanControlFlowRecord(flow));
        foreach (SourceBinding binding in document.Semantics.Bindings)
            output.AppendLine("def binding_" + LeanIdentifier(binding.Member) + "_" + binding.CanonicalSyntaxSha256[..8] + " : Bool := bindingReady " + LeanBindingRecord(binding));
        output.AppendLine();
        string[] gates = document.Semantics.Operations.Select(operation => "op_" + LeanIdentifier(operation.Id))
            .Concat(document.Semantics.Branches.Select(branch => "branch_" + LeanIdentifier(branch.Id)))
            .Concat(document.Semantics.AdapterPremises.Select(adapter => "adapter_" + LeanIdentifier(adapter.Id)))
            .Concat(document.Semantics.ControlFlows.Select(flow => "cfg_" + LeanIdentifier(flow.Id)))
            .Concat(document.Semantics.Bindings.Select(binding => "binding_" + LeanIdentifier(binding.Member) + "_" + binding.CanonicalSyntaxSha256[..8]))
            .ToArray();
        output.AppendLine("def semanticAdmission : Bool := semanticEvidenceReady && " + string.Join(" && ", gates) + " && semanticPlanSha256 == \"" + Escape(document.Semantics.Plan.Sha256) + "\"");
        output.AppendLine();
    }

    private static void AppendSemanticEvidence(StringBuilder output, IrDocument document)
    {
        SemanticProfile profile = document.Semantics;
        output.AppendLine("def expectedDependencyIdentities : List DependencyEvidence := [");
        foreach (DependencyIdentity dependency in document.Dependencies)
            output.AppendLine("  " + LeanDependencyRecord(dependency) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedStages : List StageEvidence := [");
        foreach (StageIdentity stage in profile.Stages)
            output.AppendLine("  " + LeanStageRecord(stage) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedOperations : List OperationEvidence := [");
        foreach (OperationIdentity operation in profile.Operations)
            output.AppendLine("  " + LeanOperationRecord(operation) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedBranches : List BranchEvidence := [");
        foreach (BranchIdentity branch in profile.Branches)
            output.AppendLine("  " + LeanBranchRecord(branch) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedAdapters : List AdapterEvidence := [");
        foreach (AdapterPremise adapter in profile.AdapterPremises)
            output.AppendLine("  " + LeanAdapterRecord(adapter) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedCompositions : List CompositionEvidence := [");
        foreach (CompositionDependency dependency in document.Composition)
            output.AppendLine("  " + LeanCompositionRecord(dependency) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedControlFlows : List ControlFlowEvidence := [");
        foreach (ControlFlowIdentity flow in profile.ControlFlows)
            output.AppendLine("  " + LeanControlFlowRecord(flow) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedBindings : List BindingEvidence := [");
        foreach (SourceBinding binding in profile.Bindings)
            output.AppendLine("  " + LeanBindingRecord(binding) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedGasFields : List GasFieldEvidence := [");
        foreach (GasFieldIdentity field in profile.GasFields)
            output.AppendLine("  " + LeanGasFieldRecord(field) + ",");
        output.AppendLine("]");
        output.AppendLine();

        output.AppendLine("def expectedAdmittedSnapshots : List SnapshotEvidence := [");
        foreach (SnapshotIdentity snapshot in profile.Snapshots)
            output.AppendLine("  " + LeanSnapshotRecord(snapshot) + ",");
        output.AppendLine("]");
        output.AppendLine();
        output.AppendLine("def expectedAdmittedEnvironment : EnvironmentEvidence := " + LeanEnvironmentRecord(profile.Environment));
        output.AppendLine("def expectedAdmittedHandoff : HandoffEvidence := " + LeanHandoffRecord(profile.Handoff));
        output.AppendLine();

        output.AppendLine("def sourceIdentitiesExact : Bool := sourceIdentities == [");
        foreach (SourceIdentity source in document.Sources)
            output.AppendLine("  (" + LeanString(source.Path) + ", " + LeanString(source.Role) + ", " + LeanString(source.Sha256) + ", " + LeanString(source.SyntaxSha256) + "),");
        output.AppendLine("]");
        output.AppendLine("def compilerReferenceIdentitiesExact : Bool := compilerReferenceIdentities == [");
        foreach (CompilerReferenceIdentity reference in document.CompilerReferences)
            output.AppendLine("  (" + LeanString(reference.Path) + ", " + LeanString(reference.AssemblyName) + ", " + LeanString(reference.Sha256) + ", " + LeanString(reference.Mvid) + ", " + (reference.Selected ? "true" : "false") + ", " + LeanStringList(reference.Dependencies) + "),");
        output.AppendLine("]");
        output.AppendLine("def dependencyIdentitiesExact : Bool := dependencyIdentities == expectedDependencyIdentities");
        output.AppendLine("def stagesExact : Bool := admittedStages == expectedAdmittedStages");
        output.AppendLine("def operationsExact : Bool := admittedOperations == expectedAdmittedOperations");
        output.AppendLine("def branchesExact : Bool := admittedBranches == expectedAdmittedBranches");
        output.AppendLine("def adaptersExact : Bool := admittedAdapters == expectedAdmittedAdapters");
        output.AppendLine("def compositionsExact : Bool := admittedCompositions == expectedAdmittedCompositions");
        output.AppendLine("def controlFlowsExact : Bool := admittedControlFlows == expectedAdmittedControlFlows");
        output.AppendLine("def bindingsExact : Bool := admittedBindings == expectedAdmittedBindings");
        output.AppendLine("def gasFieldsExact : Bool := admittedGasFields == expectedAdmittedGasFields");
        output.AppendLine("def snapshotsExact : Bool := admittedSnapshots == expectedAdmittedSnapshots");
        output.AppendLine("def environmentExact : Bool := admittedEnvironment == expectedAdmittedEnvironment");
        output.AppendLine("def handoffExact : Bool := admittedHandoff == expectedAdmittedHandoff");
        output.AppendLine("def sourceAccessLineageAdmitted : Bool := handoffExact && admittedHandoff.accessLineage == expectedAdmittedHandoff.accessLineage && admittedHandoff.accessBindings == expectedAdmittedHandoff.accessBindings");
        output.AppendLine("def orderedEffectsExact : Bool := orderedEffects == " + LeanStringList(profile.OrderedEffects));
        output.AppendLine();

        output.AppendLine("def operationReady (expected : OperationEvidence) : Bool := admittedOperations.any (fun operation => operation == expected)");
        output.AppendLine("def branchReady (expected : BranchEvidence) : Bool := admittedBranches.any (fun branch => branch == expected)");
        output.AppendLine("def adapterReady (expected : AdapterEvidence) : Bool := admittedAdapters.any (fun adapter => adapter == expected)");
        output.AppendLine("def controlFlowReady (expected : ControlFlowEvidence) : Bool := admittedControlFlows.any (fun flow => flow == expected)");
        output.AppendLine("def bindingReady (expected : BindingEvidence) : Bool := admittedBindings.any (fun binding => binding == expected)");
        output.AppendLine("def operationEffectsCoverPlan : Bool :=");
        output.AppendLine("  expectedSemanticPlanEffectIds.all (fun effect => admittedOperations.any (fun operation => operation.effects.any (fun item => item == effect)))");
        output.AppendLine("def semanticPlanReady : Bool :=");
        output.AppendLine("  semanticPlanStageIds == expectedSemanticPlanStageIds &&");
        output.AppendLine("  semanticPlanOperationIds == expectedSemanticPlanOperationIds &&");
        output.AppendLine("  semanticPlanBranchIds == expectedSemanticPlanBranchIds &&");
        output.AppendLine("  semanticPlanGasFieldIds == expectedSemanticPlanGasFieldIds &&");
        output.AppendLine("  semanticPlanSnapshotIds == expectedSemanticPlanSnapshotIds &&");
        output.AppendLine("  semanticPlanAdapterIds == expectedSemanticPlanAdapterIds &&");
        output.AppendLine("  semanticPlanControlFlowIds == expectedSemanticPlanControlFlowIds &&");
        output.AppendLine("  semanticPlanEffectIds == expectedSemanticPlanEffectIds &&");
        output.AppendLine("  semanticPlanBoundaryOperationId == expectedSemanticPlanBoundaryOperationId && operationEffectsCoverPlan");
        output.AppendLine("def semanticEvidenceReady : Bool :=");
        output.AppendLine("  sourceIdentitiesExact && compilerReferenceIdentitiesExact && dependencyIdentitiesExact &&");
        output.AppendLine("  stagesExact && operationsExact && branchesExact && adaptersExact && compositionsExact &&");
        output.AppendLine("  controlFlowsExact && bindingsExact && gasFieldsExact && snapshotsExact && environmentExact &&");
        output.AppendLine("  handoffExact && orderedEffectsExact && semanticPlanReady");
        output.AppendLine();
    }

    private static void AppendTypes(StringBuilder output) => AppendBlock(output, """
structure UInt64Rep where
  raw : Int

structure Int64Rep where
  raw : Int

def uint64Min : Int := 0
def uint64Max : Int := 18446744073709551615
def int64Min : Int := -9223372036854775808
def int64Max : Int := 9223372036854775807
def fitsUInt64 (value : Int) : Bool := uint64Min ≤ value && value ≤ uint64Max
def fitsInt64 (value : Int) : Bool := int64Min ≤ value && value ≤ int64Max
def twosComplementDecode (raw : Int) : Int := if raw ≤ int64Max then raw else raw - 18446744073709551616
def twosComplementEncode (value : Int) : Int := if value < 0 then value + 18446744073709551616 else value

structure FixedWidthFacts where
  value : UInt64Rep
  stateReservoir : Int64Rep
  stateGasUsed : Int64Rep
  stateGasSpill : Int64Rep
  stateGasSpillRefunded : Int64Rep

def fixedWidthValid (facts : FixedWidthFacts) : Bool :=
  fitsUInt64 facts.value.raw && fitsInt64 facts.stateReservoir.raw && fitsInt64 facts.stateGasUsed.raw &&
    fitsInt64 facts.stateGasSpill.raw && fitsInt64 facts.stateGasSpillRefunded.raw &&
    twosComplementDecode (twosComplementEncode facts.stateReservoir.raw) = facts.stateReservoir.raw &&
    twosComplementDecode (twosComplementEncode facts.stateGasUsed.raw) = facts.stateGasUsed.raw &&
    twosComplementDecode (twosComplementEncode facts.stateGasSpill.raw) = facts.stateGasSpill.raw &&
    twosComplementDecode (twosComplementEncode facts.stateGasSpillRefunded.raw) = facts.stateGasSpillRefunded.raw

structure GasState where
  value : Int
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int

def gasToFixedWidth (gas : GasState) : FixedWidthFacts :=
  { value := ⟨gas.value⟩, stateReservoir := ⟨gas.stateReservoir⟩, stateGasUsed := ⟨gas.stateGasUsed⟩,
    stateGasSpill := ⟨gas.stateGasSpill⟩, stateGasSpillRefunded := ⟨gas.stateGasSpillRefunded⟩ }

structure SnapshotPart where
  persistent : Int
  transient : Int

structure Snapshot where
  storage : SnapshotPart
  state : Int
  blockAccessList : Int

structure AccountFacts where
  physicalExists : Bool
  logicalExists : Bool
  nonce : Nat
  code : String
  delegation : Option String
  balance : Nat
  storageNonEmpty : Bool

structure AuthorityStateFacts where
  authority : String
  original : AccountFacts
  current : AccountFacts
  delegatedBeforeTx : Option String

structure WorldFacts where
  physicalExists : Bool
  logicalExists : Bool
  originalPhysicalExists : Bool
  currentPhysicalExists : Bool
  originalLogicalExists : Bool
  currentLogicalExists : Bool
  originalNonce : Nat
  currentNonce : Nat
  originalCode : String
  currentCode : String
  originalDelegation : Option String
  currentDelegation : Option String
  originalBalance : Nat
  currentBalance : Nat
  recipientBalance : Nat
  delegationRefunds : Nat
  accountWarm : Bool
  storageWarm : Bool
  accountReads : List String
  accountWrites : List String
  storageReads : List String
  storageWrites : List String
  codeInsertRefunds : Nat
  traceAccess : List String
  authorityStates : List AuthorityStateFacts

structure TxExecutionContext where
  sender : String
  codeRepository : String
  blobHashes : List String
  opcodeGasPrice : Nat

structure AccessObservation where
  warmedAccounts : List String
  warmedStorage : List String
  reads : List String
  tracing : Bool

structure CodeIdentity where
  source : String
  target : Option String
  bytes : String
  isEmpty : Bool
  isNull : Bool

structure Environment where
  code : CodeIdentity
  executingAccount : String
  caller : String
  codeSource : Option String
  callDepth : Nat
  value : Nat
  input : String

structure AuthorizationFacts where
  authority : String
  codeAddress : String
  chainIdValid : Bool
  nonceValid : Bool
  signatureValid : Bool
  account : AccountFacts
  expectedNonce : Nat
  delegationBefore : Option String
  clearsDelegation : Bool
  newAccountStateCharge : Int
  accountWriteCharge : Nat
  perAuthStateCharge : Int

structure DelegatedTargetFacts where
  target : Option String
  isPrecompile : Bool
  account : AccountFacts
  accessCharge : Nat

structure DeadRecipientFacts where
  sender : String
  recipient : Option String
  physicalExists : Bool
  logicalExists : Bool
  accountEmpty : Bool
  stateCharge : Int

structure DeploymentFacts where
  physicalExists : Bool
  logicalExists : Bool
  accountEmpty : Bool
  storageNonEmpty : Bool
  storageCleared : Bool
  codeOrNonceCollision : Bool
  collision : Bool
  includeStorageCollision : Bool
  stateCharge : Int

structure ValueTransferFacts where
  sender : String
  recipient : Option String
  senderBalance : Nat
  recipientBalance : Nat
  amount : Nat

structure PreparationRelations where
  delegatedTarget : DelegatedTargetFacts
  deadRecipient : DeadRecipientFacts
  deployment : DeploymentFacts
  valueTransfer : ValueTransferFacts

structure SpecFacts where
  eip7702 : Bool
  eip8037 : Bool
  eip8038 : Bool
  useHotAndColdStorage : Bool
  useTxAccessLists : Bool
  addCoinbaseToTxAccessList : Bool

structure ExecutionOptionsFacts where
  warmup : Bool
  skipValidation : Bool

structure TxFacts where
  isCreate : Bool
  isSetCode : Bool
  sender : String
  recipient : Option String
  value : Nat
  input : String
  accessListAccounts : List String
  accessListStorage : List String
  coinbase : Option String

def hasValue (tx : TxFacts) : Bool := tx.value > 0
def hasAuthorization (tx : TxFacts) (authorizations : List AuthorizationFacts) : Bool := tx.isSetCode && !authorizations.isEmpty

  -- The enriched handoff is the package entry projection: its access is the
  -- fresh StackAccessTracker's modeled AccessObservation, not the
  -- post-preparation VM observation.
-- Result.access and VmInput.access carry that later mutated boundary value.
structure EvmHandoff where
  context : TxExecutionContext
  gas : GasState
  access : AccessObservation
  tx : TxFacts
  spec : SpecFacts
  options : ExecutionOptionsFacts
  executionIntrinsicGasStandard : GasState
  environment : Environment
  delegationRefunds : Nat
  exactBoundary : String

structure VmInput where
  gas : GasState
  executionType : String
  environment : Environment
  access : AccessObservation
  snapshot : Snapshot

inductive Status where
  | admissionMismatch
  | preparationOog
  | collision
  | nullCode
  | vmBoundary
  deriving DecidableEq, Repr

inductive Event where
  | txContext (context : TxExecutionContext)
  | stackTracker (tracing : Bool)
  | metrics (name : String)
  | authorizationWarm (authority : String)
  | authorizationRead (authority : String)
  | stateCharge (label : String) (amount : Int)
  | executionCharge (label : String) (amount : Nat)
  | accountWrite (authority : String)
  | delegationWrite (authority : String) (target : String)
  | recipientRead (recipient : String)
  | accessWarm (accounts : List String) (storage : List String)
  | delegatedTargetRead (target : String)
  | delegatedTargetWarm (target : String)
  | deploymentRead (recipient : String)
  | snapshot (which : String) (value : Snapshot)
  | restore (which : String) (value : Snapshot)
  | valueDebit (sender : String) (amount : Nat)
  | environmentRent (environment : Environment)
  | topFrameRent (input : VmInput)
  | topFrameHalt (gas : GasState) (environment : Environment)
  | accessReport (access : AccessObservation)
  | vmCallBoundary

structure Result where
  status : Status
  error : Option String
  gas : GasState
  executionIntrinsicGasStandard : GasState
  prePreparationGas : GasState
  preExecutionSnapshot : Snapshot
  topLevelSnapshot : Snapshot
  postIntrinsicStateReservoir : Int
  world : WorldFacts
  access : AccessObservation
  environment : Environment
  code : CodeIdentity
  input : String
  delegationRefunds : Nat
  vmInput : Option VmInput
  events : List Event

structure Input where
  handoff : EvmHandoff
  prePreparationGas : GasState
  preExecutionIntrinsicGasStandard : GasState
  hasPreExecutionSnapshot : Bool
  preExecutionSnapshot : Snapshot
  topLevelSnapshot : Snapshot
  postIntrinsicStateReservoir : Int
  preExecutionWorld : WorldFacts
  world : WorldFacts
  access : AccessObservation
  authorizations : List AuthorizationFacts
  relations : PreparationRelations
  environment : Environment
  fixedWidth : FixedWidthFacts
  preparationFixedWidth : FixedWidthFacts
  baselineFixedWidth : FixedWidthFacts
  preBaselineFixedWidth : FixedWidthFacts
  delegationRefunds : Nat
""");

    private static void AppendTransition(StringBuilder output, IrDocument document)
    {
        string OperationGates(params SemanticFormula[] formulas)
        {
            string[] gates = document.Semantics.Operations
                .Where(operation => formulas.Any(formula => formula == operation.Formula))
                .Select(operation => "op_" + LeanIdentifier(operation.Id))
                .ToArray();
            if (gates.Length == 0) throw new ExtractionException("The semantic plan has no operation gate for the requested phase.");
            return string.Join(" && ", gates.Distinct(StringComparer.Ordinal));
        }

        string BranchGates(string prefix)
        {
            string[] gates = document.Semantics.Branches
                .Where(branch => branch.Id.StartsWith(prefix, StringComparison.Ordinal))
                .Select(branch => "branch_" + LeanIdentifier(branch.Id))
                .ToArray();
            if (gates.Length == 0) throw new ExtractionException("The semantic plan has no branch gate for " + prefix + ".");
            return string.Join(" && ", gates);
        }

        string operationGate = OperationGates(SemanticFormula.ProcessDelegations, SemanticFormula.ValidateAuthorization,
            SemanticFormula.AuthorizationWarm, SemanticFormula.AuthorizationAccountRead,
            SemanticFormula.AuthorizationLogicalExistence, SemanticFormula.AuthorizationNewAccountStateCharge, SemanticFormula.AuthorizationAccountWriteCharge,
            SemanticFormula.AuthorizationPerAuthStateCharge, SemanticFormula.AuthorizationCreateAccount,
            SemanticFormula.AuthorizationIncrementNonce, SemanticFormula.AuthorizationSetDelegation,
            SemanticFormula.AuthorizationStateGasDelta, SemanticFormula.AuthorizationFoldTopFrameStateGas);
        string authorizationCreateExclusionGate = OperationGates(SemanticFormula.AuthorizationCreateExclusion) +
            " && " + BranchGates("staticValidation.");
        string contextGate = OperationGates(SemanticFormula.SetTransactionContext, SemanticFormula.IncrementCreateMetrics,
            SemanticFormula.AllocateAccessTracker, SemanticFormula.CapturePreparationGas,
            SemanticFormula.CaptureExecutionIntrinsicGasStandard, SemanticFormula.CaptureDelegationRefunds,
            SemanticFormula.InitializePreparationSnapshot, SemanticFormula.CapturePreparationSnapshot);
        string warmGate = OperationGates(SemanticFormula.WarmTransactionAccesses);
        string envGate = OperationGates(SemanticFormula.DeriveRecipient, SemanticFormula.WarmTransactionAccesses,
            SemanticFormula.ResolveCreationCode, SemanticFormula.ResolveRecipientCode, SemanticFormula.ResolveDelegationTarget,
            SemanticFormula.ResolveDelegationPrecompile, SemanticFormula.ChargeDelegatedTargetAccess,
            SemanticFormula.ReadDelegatedTarget, SemanticFormula.WarmLegacyDelegatedTarget, SemanticFormula.CreateVmInput);
        string targetGate = OperationGates(SemanticFormula.ResolveDelegationTarget);
        string precompileGate = OperationGates(SemanticFormula.ResolveDelegationPrecompile);
        string delegatedChargeGate = OperationGates(SemanticFormula.ChargeDelegatedTargetAccess);
        string delegatedReadGate = OperationGates(SemanticFormula.ReadDelegatedTarget);
        string legacyWarmGate = OperationGates(SemanticFormula.WarmLegacyDelegatedTarget);
        string reservoirGate = OperationGates(SemanticFormula.CapturePostIntrinsicReservoir, SemanticFormula.ChargeDeadRecipient,
            SemanticFormula.ChargeDeadRecipientState, SemanticFormula.RestorePreparationSnapshot,
            SemanticFormula.RestorePreparationGas, SemanticFormula.BuildExecutionIntrinsic);
        string deadGate = OperationGates(SemanticFormula.ChargeDeadRecipient);
        string deadChargeGate = OperationGates(SemanticFormula.ChargeDeadRecipientState);
        string createCollisionGate = OperationGates(SemanticFormula.CreateCollisionResult);
        string createCollisionClassificationGate = OperationGates(SemanticFormula.CreateCollisionClassification);
        string createStorageResetGate = OperationGates(SemanticFormula.CreateStorageReset);
        string valueGate = OperationGates(SemanticFormula.PayValue);
        string nullGate = OperationGates(SemanticFormula.NullCodeShortCircuit);
        string callGate = OperationGates(SemanticFormula.CaptureTopLevelSnapshot, SemanticFormula.HaltTopFrame,
            SemanticFormula.PrepareCreateDestination, SemanticFormula.CreateLogicalExistence,
            SemanticFormula.CreateCollisionResult, SemanticFormula.ChargeCreateState, SemanticFormula.RestoreTopLevelSnapshot,
            SemanticFormula.RejectCreateCollision, SemanticFormula.PayValue, SemanticFormula.NullCodeShortCircuit,
            SemanticFormula.RentTopLevel, SemanticFormula.ExecuteTransactionBoundary);
        string contextBranchGate = BranchGates("preparation.createMetrics");
        string authorizationBranchGate = BranchGates("authorization.");
        string environmentBranchGate = BranchGates("environment.");
        string preparationBranchGate = BranchGates("preparation.");
        string callBranchGate = BranchGates("call.");
        output.AppendLine("def admittedAuthorizationOperations : Bool := " + operationGate);
        output.AppendLine("def admittedAuthorizationCreateExclusion : Bool := " + authorizationCreateExclusionGate);
        output.AppendLine("def admittedContextOperations : Bool := " + contextGate);
        output.AppendLine("def admittedWarmTransactionOperation : Bool := " + warmGate);
        output.AppendLine("def admittedEnvironmentOperations : Bool := " + envGate);
        output.AppendLine("def admittedTargetOperation : Bool := " + targetGate);
        output.AppendLine("def admittedDelegatedPrecompileOperation : Bool := " + precompileGate);
        output.AppendLine("def admittedDelegatedChargeOperation : Bool := " + delegatedChargeGate);
        output.AppendLine("def admittedDelegatedReadOperation : Bool := " + delegatedReadGate);
        output.AppendLine("def admittedLegacyDelegatedWarmOperation : Bool := " + legacyWarmGate);
        output.AppendLine("def admittedReservoirOperations : Bool := " + reservoirGate);
        output.AppendLine("def admittedDeadOperation : Bool := " + deadGate);
        output.AppendLine("def admittedDeadChargeOperation : Bool := " + deadChargeGate);
        output.AppendLine("def admittedCreateCollisionOperation : Bool := " + createCollisionGate);
        output.AppendLine("def admittedCreateCollisionClassificationOperation : Bool := " + createCollisionClassificationGate);
        output.AppendLine("def admittedCreateStorageResetOperation : Bool := " + createStorageResetGate);
        output.AppendLine("def admittedPayValueOperation : Bool := " + valueGate);
        output.AppendLine("def admittedNullCodeOperation : Bool := " + nullGate);
        output.AppendLine("def admittedCallOperations : Bool := " + callGate);
        output.AppendLine("def admittedContextBranches : Bool := " + contextBranchGate);
        output.AppendLine("def admittedAuthorizationBranches : Bool := " + authorizationBranchGate);
        output.AppendLine("def admittedEnvironmentBranches : Bool := " + environmentBranchGate);
        output.AppendLine("def admittedPreparationBranches : Bool := " + preparationBranchGate);
        output.AppendLine("def admittedCallBranches : Bool := " + callBranchGate);
        output.AppendLine();
        AppendBlock(output, """
def hasValueIn (value : String) : List String → Bool
  | [] => false
  | head :: tail => if value == head then true else hasValueIn value tail

def messageInputData (input : Input) : String :=
  match input.handoff.tx.recipient with
  | some _ => input.handoff.tx.input
  | none => ""

def appendUnique (value : String) (values : List String) : List String :=
  if hasValueIn value values then values else values ++ [value]

def warmAccount (access : AccessObservation) (account : String) : AccessObservation :=
  { access with warmedAccounts := appendUnique account access.warmedAccounts }

def recordAccessRead (access : AccessObservation) (account : String) : AccessObservation :=
  { access with reads := access.reads ++ [account] }

def warmAccounts (access : AccessObservation) : List String → AccessObservation
  | [] => access
  | account :: rest => warmAccounts (warmAccount access account) rest

def warmStorage (access : AccessObservation) : List String → AccessObservation
  | [] => access
  | cell :: rest => warmStorage { access with warmedStorage := appendUnique cell access.warmedStorage } rest

def warmTransactionAccesses (input : Input) (access : AccessObservation) (recipient : Option String) (includeRecipient : Bool) : AccessObservation :=
  if !input.handoff.spec.useHotAndColdStorage then access else
    let access0 := if input.handoff.spec.useTxAccessLists then warmStorage (warmAccounts access input.handoff.tx.accessListAccounts) input.handoff.tx.accessListStorage else access
    let access1 := if input.handoff.spec.addCoinbaseToTxAccessList then (match input.handoff.tx.coinbase with | some coinbase => warmAccount access0 coinbase | none => access0) else access0
    let access2 := if includeRecipient then (match recipient with | some value => warmAccount access1 value | none => access1) else access1
    warmAccount access2 input.handoff.tx.sender

def recordAccountRead (world : WorldFacts) (account : String) : WorldFacts :=
  { world with accountReads := world.accountReads ++ [account], traceAccess := world.traceAccess ++ ["read:" ++ account] }

def recordAccountWrite (world : WorldFacts) (account : String) : WorldFacts :=
  { world with accountWrites := world.accountWrites ++ [account], traceAccess := world.traceAccess ++ ["write:" ++ account] }

def updateAuthorityBalance (world : WorldFacts) (authority : String) (balance : Nat) : WorldFacts :=
  let rec update : List AuthorityStateFacts → List AuthorityStateFacts
    | [] => []
    | state :: rest =>
      if state.authority == authority then
        { state with current := { state.current with balance := balance } } :: rest
      else state :: update rest
  { world with authorityStates := update world.authorityStates }

def recordStorageWrite (world : WorldFacts) (account : String) : WorldFacts :=
  { world with storageWrites := world.storageWrites ++ [account], traceAccess := world.traceAccess ++ ["storage-write:" ++ account] }

def stateSpill (gas : GasState) (amount : Int) : Int :=
  if amount ≤ gas.stateReservoir then 0 else amount - max 0 gas.stateReservoir

def gasFieldsValid (gas : GasState) : Bool :=
  fitsUInt64 gas.value && fitsInt64 gas.stateReservoir && fitsInt64 gas.stateGasUsed &&
    fitsInt64 gas.stateGasSpill && fitsInt64 gas.stateGasSpillRefunded

def chargeState (gas : GasState) (amount : Int) : Option GasState :=
  if !gasFieldsValid gas then none else if amount ≤ 0 then some gas else
    let spill := stateSpill gas amount
    if gas.value < spill then none
    else if gas.stateReservoir ≥ amount then
      let charged := { gas with stateReservoir := gas.stateReservoir - amount, stateGasUsed := gas.stateGasUsed + amount }
      if gasFieldsValid charged then some charged else none
    else
       let charged := { gas with value := gas.value - spill, stateReservoir := 0, stateGasUsed := gas.stateGasUsed + amount, stateGasSpill := gas.stateGasSpill + spill }
      if gasFieldsValid charged then some charged else none

def chargeExecution (gas : GasState) (amount : Int) : Option GasState :=
  if !gasFieldsValid gas then none else if amount ≤ 0 then some gas else if gas.value < amount then none else
    let charged := { gas with value := gas.value - amount }
    if gasFieldsValid charged then some charged else none

def findAuthorityStateIn : List AuthorityStateFacts → String → Option AuthorityStateFacts
  | [], _ => none
  | state :: rest, authority => if state.authority == authority then some state else findAuthorityStateIn rest authority

def findAuthorityState (world : WorldFacts) (authority : String) : Option AuthorityStateFacts :=
  findAuthorityStateIn world.authorityStates authority

def replaceAuthorityState (world : WorldFacts) (replacement : AuthorityStateFacts) : WorldFacts :=
  let rec replace : List AuthorityStateFacts → List AuthorityStateFacts
    | [] => []
    | state :: rest => if state.authority == replacement.authority then replacement :: rest else state :: replace rest
  { world with authorityStates := replace world.authorityStates }

def hasUniqueAuthorityStates : List AuthorityStateFacts → Bool
  | [] => true
  | state :: rest => !hasValueIn state.authority (rest.map AuthorityStateFacts.authority) && hasUniqueAuthorityStates rest

def accountFactsEqual (left right : AccountFacts) : Bool :=
  left.physicalExists == right.physicalExists && left.logicalExists == right.logicalExists &&
    left.nonce == right.nonce && left.code == right.code && left.delegation == right.delegation &&
    left.balance == right.balance && left.storageNonEmpty == right.storageNonEmpty

def accountLogicalExists (account : AccountFacts) : Bool :=
  account.balance > 0 || account.nonce > 0 || account.code != ""

def accountFactsCoherent (account : AccountFacts) : Bool :=
  account.nonce ≤ 18446744073709551615 && account.logicalExists == accountLogicalExists account &&
    (account.physicalExists || !account.storageNonEmpty) &&
    (account.physicalExists || (!account.logicalExists && account.balance == 0 && account.nonce == 0 &&
      account.code == "" && account.delegation.isNone)) &&
    (account.delegation.isNone || account.code != "")

def authorityStateFactsCoherent (state : AuthorityStateFacts) : Bool :=
  accountFactsCoherent state.original && accountFactsCoherent state.current &&
    state.delegatedBeforeTx == state.original.delegation

def authorityStatesCoherent : List AuthorityStateFacts → Bool
  | [] => true
  | state :: rest => authorityStateFactsCoherent state && authorityStatesCoherent rest

def authorizationCoherent (world : WorldFacts) (auth : AuthorizationFacts) : Bool :=
  match findAuthorityState world auth.authority with
  | none => false
  | some state => accountFactsEqual state.original auth.account && state.delegatedBeforeTx == auth.delegationBefore

def initialWrittenAccounts (input : Input) : List String :=
  if !input.handoff.spec.eip8037 then [] else
    let withSender := [input.handoff.tx.sender]
    if input.handoff.tx.value > 0 then match input.handoff.tx.recipient with
      | some recipient => appendUnique recipient withSender
      | none => withSender
    else withSender

def chargeDelegatedTarget (enabled alreadyWarm isPrecompile : Bool) (gas : GasState) (target : Option String)
    (amount : Nat) (access : AccessObservation) : Option (GasState × AccessObservation) :=
  if !enabled then some (gas, access) else
    match target with
    | none => some (gas, access)
     | some _address =>
      match chargeExecution gas (if alreadyWarm || isPrecompile then 0 else amount) with
      | none => none
      | some after => some (after, access)

def fixedWidthMatches (facts : FixedWidthFacts) (gas : GasState) : Bool :=
  facts.value.raw = gas.value && facts.stateReservoir.raw = gas.stateReservoir &&
    facts.stateGasUsed.raw = gas.stateGasUsed && facts.stateGasSpill.raw = gas.stateGasSpill &&
    facts.stateGasSpillRefunded.raw = gas.stateGasSpillRefunded

def gasFactsEqual (left right : GasState) : Bool :=
  left.value == right.value && left.stateReservoir == right.stateReservoir &&
    left.stateGasUsed == right.stateGasUsed && left.stateGasSpill == right.stateGasSpill &&
    left.stateGasSpillRefunded == right.stateGasSpillRefunded

def authorizationChargesValid : List AuthorizationFacts → Bool
  | [] => true
  | auth :: rest =>
    auth.newAccountStateCharge ≥ 0 && fitsInt64 auth.newAccountStateCharge &&
      fitsUInt64 (Int.ofNat auth.accountWriteCharge) &&
      auth.perAuthStateCharge ≥ 0 && fitsInt64 auth.perAuthStateCharge && authorizationChargesValid rest

def relationChargesValid (relations : PreparationRelations) : Bool :=
  relations.delegatedTarget.accessCharge ≤ 18446744073709551615 &&
    fitsUInt64 (Int.ofNat relations.delegatedTarget.accessCharge) &&
    relations.deadRecipient.stateCharge ≥ 0 && fitsInt64 relations.deadRecipient.stateCharge &&
    relations.deployment.stateCharge ≥ 0 && fitsInt64 relations.deployment.stateCharge

def delegatedTargetFactsCoherent (facts : DelegatedTargetFacts) : Bool :=
  facts.target.isNone || accountFactsCoherent facts.account

def foldAuthorizationStateGas (gas baseline : GasState) (delta : Int) : Option (GasState × GasState) :=
  if delta ≤ 0 then some (gas, baseline) else
    let foldedGas := { gas with stateGasSpill := 0, stateGasSpillRefunded := 0 }
    let foldedBaseline := { baseline with stateReservoir := baseline.stateReservoir + delta, stateGasUsed := baseline.stateGasUsed + delta }
    if gasFieldsValid foldedGas && gasFieldsValid foldedBaseline then some (foldedGas, foldedBaseline) else none

def fixedWidthValidForInput (input : Input) : Bool :=
  fixedWidthValid input.fixedWidth && fixedWidthMatches input.fixedWidth input.handoff.gas &&
    fixedWidthValid input.preparationFixedWidth && fixedWidthMatches input.preparationFixedWidth input.prePreparationGas &&
    fixedWidthValid input.baselineFixedWidth && fixedWidthMatches input.baselineFixedWidth input.handoff.executionIntrinsicGasStandard &&
    fixedWidthValid input.preBaselineFixedWidth && fixedWidthMatches input.preBaselineFixedWidth input.preExecutionIntrinsicGasStandard &&
    fitsInt64 input.postIntrinsicStateReservoir &&
    twosComplementDecode (twosComplementEncode input.postIntrinsicStateReservoir) = input.postIntrinsicStateReservoir &&
    authorizationChargesValid input.authorizations && relationChargesValid input.relations

def authorizationsCoherent (world : WorldFacts) : List AuthorizationFacts → Bool
  | [] => true
  | auth :: rest => authorizationCoherent world auth && auth.expectedNonce ≤ 18446744073709551615 &&
      (!auth.nonceValid || auth.expectedNonce < 18446744073709551615) && authorizationsCoherent world rest

def authorityStateMatchesTransaction (input : Input) (state : AuthorityStateFacts) : Bool :=
  let projectsSender := state.authority == input.handoff.tx.sender
  let projectsRecipient := match input.handoff.tx.recipient with | some recipient => state.authority == recipient | none => false
  (!projectsSender || (state.current.physicalExists == input.world.currentPhysicalExists &&
    state.current.logicalExists == input.world.currentLogicalExists && state.current.nonce == input.world.currentNonce &&
    state.current.code == input.world.currentCode && state.current.delegation == input.world.currentDelegation &&
    state.current.balance == input.world.currentBalance)) &&
    (!projectsRecipient || state.current.balance == input.world.recipientBalance)

def authorityStatesMatchTransaction (input : Input) : List AuthorityStateFacts → Bool
  | [] => true
  | state :: rest => authorityStateMatchesTransaction input state && authorityStatesMatchTransaction input rest

def codeIdentityEqual (left right : CodeIdentity) : Bool :=
  left.source == right.source && left.target == right.target && left.bytes == right.bytes &&
    left.isEmpty == right.isEmpty && left.isNull == right.isNull

def environmentFactsEqual (left right : Environment) : Bool :=
  codeIdentityEqual left.code right.code && left.executingAccount == right.executingAccount &&
    left.caller == right.caller && left.codeSource == right.codeSource && left.callDepth == right.callDepth &&
    left.value == right.value && left.input == right.input

def accessFactsEqual (left right : AccessObservation) : Bool :=
  left.warmedAccounts == right.warmedAccounts && left.warmedStorage == right.warmedStorage &&
    left.reads == right.reads && left.tracing == right.tracing

def freshAccess (access : AccessObservation) : Bool :=
  access.warmedAccounts.isEmpty && access.warmedStorage.isEmpty && access.reads.isEmpty

def authorityStateFactsEqual (left right : AuthorityStateFacts) : Bool :=
  left.authority == right.authority && accountFactsEqual left.original right.original &&
    accountFactsEqual left.current right.current && left.delegatedBeforeTx == right.delegatedBeforeTx

def authorityStateListEqual : List AuthorityStateFacts → List AuthorityStateFacts → Bool
  | [], [] => true
  | left :: leftRest, right :: rightRest => authorityStateFactsEqual left right && authorityStateListEqual leftRest rightRest
  | _, _ => false

def worldFactsEqual (left right : WorldFacts) : Bool :=
  left.physicalExists == right.physicalExists && left.logicalExists == right.logicalExists &&
    left.originalPhysicalExists == right.originalPhysicalExists && left.currentPhysicalExists == right.currentPhysicalExists &&
    left.originalLogicalExists == right.originalLogicalExists && left.currentLogicalExists == right.currentLogicalExists &&
    left.originalNonce == right.originalNonce && left.currentNonce == right.currentNonce &&
    left.originalCode == right.originalCode && left.currentCode == right.currentCode &&
    left.originalDelegation == right.originalDelegation && left.currentDelegation == right.currentDelegation &&
    left.originalBalance == right.originalBalance && left.currentBalance == right.currentBalance &&
    left.recipientBalance == right.recipientBalance && left.delegationRefunds == right.delegationRefunds &&
    left.accountWarm == right.accountWarm && left.storageWarm == right.storageWarm &&
    left.accountReads == right.accountReads && left.accountWrites == right.accountWrites &&
    left.storageReads == right.storageReads && left.storageWrites == right.storageWrites &&
    left.codeInsertRefunds == right.codeInsertRefunds && left.traceAccess == right.traceAccess &&
    authorityStateListEqual left.authorityStates right.authorityStates

def snapshotFactsEqual (left right : Snapshot) : Bool :=
  left.storage.persistent == right.storage.persistent && left.storage.transient == right.storage.transient &&
    left.state == right.state && left.blockAccessList == right.blockAccessList

def snapshotIsEmpty (snapshot : Snapshot) : Bool :=
  snapshotFactsEqual snapshot { storage := { persistent := -1, transient := -1 }, state := -1, blockAccessList := -1 }

def snapshotPresenceCoherent (input : Input) : Bool :=
  input.hasPreExecutionSnapshot == (input.handoff.spec.eip7702 && hasAuthorization input.handoff.tx input.authorizations && input.handoff.spec.eip8037)

def sourceEntryFactsCoherent (input : Input) : Bool :=
  accessFactsEqual input.access input.handoff.access && freshAccess input.access &&
    input.access.tracing == input.handoff.access.tracing &&
    input.delegationRefunds == 0 && input.handoff.delegationRefunds == 0 &&
    input.world.delegationRefunds == 0 && input.preExecutionWorld.delegationRefunds == 0 &&
    worldFactsEqual input.preExecutionWorld input.world &&
    gasFactsEqual input.prePreparationGas input.handoff.gas &&
    gasFactsEqual input.preExecutionIntrinsicGasStandard input.handoff.executionIntrinsicGasStandard &&
    environmentFactsEqual input.environment input.handoff.environment &&
    (input.hasPreExecutionSnapshot || snapshotIsEmpty input.preExecutionSnapshot) &&
    snapshotPresenceCoherent input &&
    sourceAccessLineageAdmitted &&
    input.handoff.context.codeRepository != "" &&
    targetGasPolicy == "EthereumGasPolicy" &&
    input.handoff.exactBoundary == "VirtualMachine.ExecuteTransaction"

def inputFactsCoherent (input : Input) : Bool :=
  sourceEntryFactsCoherent input && input.handoff.context.sender == input.handoff.tx.sender &&
    gasFactsEqual input.prePreparationGas input.handoff.gas &&
    gasFactsEqual input.preExecutionIntrinsicGasStandard input.handoff.executionIntrinsicGasStandard &&
    (!hasAuthorization input.handoff.tx input.authorizations || input.handoff.tx.recipient.isSome) &&
    (!input.handoff.tx.isSetCode || input.handoff.tx.recipient.isSome) &&
    (!input.handoff.tx.isSetCode || !input.authorizations.isEmpty) &&
    (input.authorizations.isEmpty || input.handoff.tx.isSetCode) &&
    (input.handoff.tx.recipient.isSome || !input.handoff.environment.code.isNull) &&
    environmentFactsEqual input.environment input.handoff.environment &&
    input.delegationRefunds == input.handoff.delegationRefunds &&
    input.handoff.environment.input == messageInputData input &&
    input.environment.input == messageInputData input &&
    input.handoff.tx.sender == input.relations.valueTransfer.sender &&
    input.relations.valueTransfer.recipient == input.handoff.tx.recipient &&
    input.handoff.tx.value == input.relations.valueTransfer.amount &&
    input.relations.valueTransfer.senderBalance == input.world.currentBalance &&
    input.relations.valueTransfer.recipientBalance == input.world.recipientBalance &&
    (!hasValue input.handoff.tx || input.handoff.options.warmup || input.relations.valueTransfer.senderBalance ≥ input.handoff.tx.value) &&
    input.relations.deadRecipient.sender == input.handoff.tx.sender &&
    input.relations.deadRecipient.recipient == input.handoff.tx.recipient &&
    input.relations.deadRecipient.logicalExists ==
      (input.relations.deadRecipient.physicalExists && !input.relations.deadRecipient.accountEmpty) &&
    input.relations.deadRecipient.accountEmpty == !input.relations.deadRecipient.logicalExists &&
    delegatedTargetFactsCoherent input.relations.delegatedTarget &&
    (input.handoff.tx.recipient.isSome || input.relations.delegatedTarget.target.isNone) &&
    input.relations.deployment.includeStorageCollision == input.handoff.spec.eip8037 &&
    input.relations.deployment.logicalExists ==
      (input.relations.deployment.physicalExists && !input.relations.deployment.accountEmpty) &&
    input.relations.deployment.accountEmpty == !input.relations.deployment.logicalExists &&
    input.relations.deployment.collision ==
      (input.relations.deployment.codeOrNonceCollision ||
        (input.relations.deployment.includeStorageCollision && input.relations.deployment.storageNonEmpty)) &&
    input.relations.deployment.storageCleared ==
      (input.handoff.tx.recipient.isNone && !input.handoff.spec.eip8037 && !input.relations.deployment.collision) &&
    input.handoff.tx.isCreate == input.handoff.tx.recipient.isNone &&
    input.hasPreExecutionSnapshot == (input.handoff.spec.eip7702 && hasAuthorization input.handoff.tx input.authorizations && input.handoff.spec.eip8037) &&
    hasUniqueAuthorityStates input.world.authorityStates && authorityStatesCoherent input.world.authorityStates &&
    authorizationsCoherent input.world input.authorizations &&
    authorityStatesMatchTransaction input input.world.authorityStates

def authPreliminaryValid (auth : AuthorizationFacts) : Bool :=
  auth.chainIdValid && auth.nonceValid && auth.signatureValid

def authHasCode (account : AccountFacts) : Bool := account.code != ""
def authHasDelegation (account : AccountFacts) : Bool := account.delegation.isSome
def authExecutionValid (world : WorldFacts) (auth : AuthorizationFacts) : Bool :=
  match findAuthorityState world auth.authority with
  | none => false
  | some state =>
    authPreliminaryValid auth && state.current.nonce == auth.expectedNonce &&
      (!authHasCode state.current || authHasDelegation state.current)

def applyAuthorizationWorld (input : Input) (world : WorldFacts) (auth : AuthorizationFacts) : WorldFacts :=
  match findAuthorityState world auth.authority with
  | none => world
  | some authorityState =>
    let current := authorityState.current
    let nextPhysical := true
    let nextLogical := true
    let nextNonce := if current.physicalExists then current.nonce + 1 else 1
    let delegatedCode := if auth.clearsDelegation then "" else "delegation:" ++ auth.codeAddress
    let delegatedAddress := if auth.clearsDelegation then none else some auth.codeAddress
    let next := { current with physicalExists := nextPhysical, logicalExists := nextLogical, nonce := nextNonce, code := delegatedCode, delegation := delegatedAddress }
    let replacement := { authorityState with current := next }
    let updated := replaceAuthorityState world replacement
    let projectsSender := auth.authority == input.handoff.tx.sender
    let projectsRecipient := match input.handoff.tx.recipient with | some recipient => auth.authority == recipient | none => false
    let projected := { updated with
      physicalExists := if projectsSender then nextPhysical else updated.physicalExists,
      logicalExists := if projectsSender then nextLogical else updated.logicalExists,
      currentPhysicalExists := if projectsSender then nextPhysical else updated.currentPhysicalExists,
      currentLogicalExists := if projectsSender then nextLogical else updated.currentLogicalExists,
      currentNonce := if projectsSender then nextNonce else updated.currentNonce,
      currentCode := if projectsSender then delegatedCode else updated.currentCode,
      currentDelegation := if projectsSender then delegatedAddress else updated.currentDelegation,
      currentBalance := if projectsSender then next.balance else updated.currentBalance,
      recipientBalance := if projectsRecipient then next.balance else updated.recipientBalance }
    recordAccountWrite projected auth.authority

structure AuthorizationState where
  gas : GasState
  baseline : GasState
  world : WorldFacts
  access : AccessObservation
  refunds : Nat
  writtenAuthorities : List String
  delegationSetFor : List String
  events : List Event
  failed : Bool

def authorizationCharge (state : AuthorizationState) (label : String) (amount : Int) : Option AuthorizationState :=
  match chargeState state.gas amount with
  | none => none
  | some gas => some { state with gas := gas, events := state.events ++ [Event.stateCharge label amount] }

def authorizationExecutionCharge (state : AuthorizationState) (label : String) (amount : Nat) : Option AuthorizationState :=
  match chargeExecution state.gas amount with
  | none => none
  | some gas => some { state with gas := gas, events := state.events ++ [Event.executionCharge label amount] }

def processAuthorizationTuple (input : Input) (state : AuthorizationState) (auth : AuthorizationFacts) : AuthorizationState :=
  if state.failed then state else
    let preliminary := authPreliminaryValid auth
    let warmed := if preliminary then warmAccount state.access auth.authority else state.access
    let readAccess := if preliminary then recordAccessRead warmed auth.authority else warmed
    let readWorld := if preliminary then recordAccountRead state.world auth.authority else state.world
    let observed := { state with access := readAccess, world := readWorld, events := if preliminary then state.events ++ [Event.authorizationWarm auth.authority, Event.authorizationRead auth.authority] else state.events }
    match findAuthorityState state.world auth.authority with
    | none => { observed with failed := true }
    | some authorityState =>
      let current := authorityState.current
      let codeValid := !authHasCode current || authHasDelegation current
      let nonceValid := current.nonce == auth.expectedNonce
      if !preliminary || !authorizationCoherent state.world auth || !codeValid || !nonceValid then observed
      else
        let firstWrite := !hasValueIn auth.authority observed.writtenAuthorities
        let firstDelegation := !auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone && !hasValueIn auth.authority observed.delegationSetFor
        let chargeNew := if input.handoff.spec.eip8037 && !current.logicalExists then authorizationCharge observed "new-account" auth.newAccountStateCharge else some observed
        match chargeNew with
        | none => { observed with failed := true }
        | some afterNew =>
          let chargeWrite := if input.handoff.spec.eip8037 && firstWrite then authorizationExecutionCharge afterNew "account-write" auth.accountWriteCharge else some afterNew
          match chargeWrite with
          | none => { afterNew with gas := { afterNew.gas with value := 0 }, failed := true }
          | some afterWrite =>
            let chargePerAuth := if input.handoff.spec.eip8037 && firstDelegation then authorizationCharge afterWrite "per-auth" auth.perAuthStateCharge else some afterWrite
            match chargePerAuth with
            | none => { afterWrite with failed := true }
            | some charged =>
              let nextWorld := applyAuthorizationWorld input charged.world auth
              let codeInsertRefund := if input.handoff.spec.eip8037 || !current.physicalExists then 0 else 1
              let refundedWorld := { nextWorld with codeInsertRefunds := nextWorld.codeInsertRefunds + codeInsertRefund }
              let nextWritten := if firstWrite then charged.writtenAuthorities ++ [auth.authority] else charged.writtenAuthorities
              let nextDelegations := if firstDelegation then charged.delegationSetFor ++ [auth.authority] else charged.delegationSetFor
              let nextRefunds := charged.refunds + codeInsertRefund
              { charged with world := refundedWorld, refunds := nextRefunds, writtenAuthorities := nextWritten, delegationSetFor := nextDelegations, events := charged.events ++ [Event.accountWrite auth.authority, Event.delegationWrite auth.authority auth.codeAddress] }

def processAuthorizationList (input : Input) (state : AuthorizationState) : List AuthorizationFacts → AuthorizationState
  | [] => state
  | auth :: rest => processAuthorizationList input (processAuthorizationTuple input state auth) rest

def emptyAuthorizationState (input : Input) : AuthorizationState :=
  { gas := input.handoff.gas, baseline := input.preExecutionIntrinsicGasStandard, world := input.world, access := input.access, refunds := input.delegationRefunds,
    writtenAuthorities := initialWrittenAccounts input, delegationSetFor := [],
     events := [Event.txContext input.handoff.context] ++ (if input.handoff.tx.recipient.isNone then [Event.metrics "creates"] else []) ++
       [Event.stackTracker input.access.tracing] ++ (if input.hasPreExecutionSnapshot then [Event.snapshot "preparation" input.preExecutionSnapshot] else []), failed := false }
""");

        output.AppendLine("def emptyResult (input : Input) (status : Status) (error : Option String) (gas baseline : GasState) (world : WorldFacts) (access : AccessObservation) (environment : Environment) (refunds : Nat) (events : List Event) (vmInput : Option VmInput) : Result :=");
        output.AppendLine("  { status := status, error := error, gas := gas, executionIntrinsicGasStandard := baseline, prePreparationGas := input.prePreparationGas, preExecutionSnapshot := input.preExecutionSnapshot,");
        output.AppendLine("    topLevelSnapshot := input.topLevelSnapshot, postIntrinsicStateReservoir := input.postIntrinsicStateReservoir, world := world, access := access,");
        output.AppendLine("    environment := environment, code := environment.code, input := environment.input,");
        output.AppendLine("    delegationRefunds := refunds, vmInput := vmInput, events := events }");
        output.AppendLine();
        output.AppendLine("def preparationOog (input : Input) (_gas _baseline : GasState) (world : WorldFacts) (access : AccessObservation) (environment : Environment) (refunds : Nat) (events : List Event) : Result :=");
        output.AppendLine("  let restoredWorld := if input.hasPreExecutionSnapshot then input.preExecutionWorld else world");
        output.AppendLine("  let restoredGas := input.prePreparationGas");
        output.AppendLine("  let restoredBaseline := input.preExecutionIntrinsicGasStandard");
        output.AppendLine("  let restoreEvents := if input.hasPreExecutionSnapshot then [Event.restore \"preparation\" input.preExecutionSnapshot] else []");
        output.AppendLine("  let accessEvents := if access.tracing then [Event.accessReport access] else []");
        output.AppendLine("  let result := emptyResult input .preparationOog (some \"preparation out of gas\") restoredGas restoredBaseline restoredWorld access environment refunds (events ++ restoreEvents ++ [Event.snapshot \"topLevel\" input.topLevelSnapshot, Event.topFrameHalt restoredGas environment] ++ accessEvents) none");
        output.AppendLine("  { result with postIntrinsicStateReservoir := restoredGas.stateReservoir }");
        output.AppendLine();
        output.AppendLine("def topLevelCreateOog (input : Input) (gas baseline : GasState) (postReservoir : Int) (world : WorldFacts) (access : AccessObservation) (environment : Environment) (refunds : Nat) (events : List Event) : Result :=");
        output.AppendLine("  let accessEvents := if access.tracing then [Event.accessReport access] else []");
        output.AppendLine("  let result := emptyResult input .preparationOog (some \"top-level create out of gas\") gas baseline world access environment refunds (events ++ [Event.snapshot \"topLevel\" input.topLevelSnapshot, Event.deploymentRead environment.executingAccount, Event.topFrameHalt gas environment, Event.restore \"topLevel\" input.topLevelSnapshot] ++ accessEvents) none");
        output.AppendLine("  { result with postIntrinsicStateReservoir := postReservoir }");
        output.AppendLine();

        output.AppendLine("def processAuthorization (input : Input) : Result × Bool :=");
        output.AppendLine("  if !admittedAuthorizationOperations || !admittedAuthorizationBranches then");
        output.AppendLine("    (emptyResult input .admissionMismatch (some \"admitted operation witness mismatch\") input.handoff.gas input.preExecutionIntrinsicGasStandard input.world input.access input.handoff.environment input.delegationRefunds [] none, true)");
        output.AppendLine("  else if !input.handoff.spec.eip7702 || !hasAuthorization input.handoff.tx input.authorizations then");
        output.AppendLine("    let initialized := emptyAuthorizationState input");
        output.AppendLine("    let result := emptyResult input .vmBoundary none initialized.gas initialized.baseline initialized.world initialized.access input.handoff.environment initialized.refunds initialized.events none");
        output.AppendLine("    ({ result with postIntrinsicStateReservoir := initialized.gas.stateReservoir }, false)");
        output.AppendLine("  else");
        output.AppendLine("    let processed := processAuthorizationList input (emptyAuthorizationState input) input.authorizations");
        output.AppendLine("    if processed.failed then");
        output.AppendLine("      let result := emptyResult input .vmBoundary none processed.gas processed.baseline processed.world processed.access input.handoff.environment processed.refunds processed.events none");
        output.AppendLine("      ({ result with postIntrinsicStateReservoir := processed.gas.stateReservoir }, true)");
        output.AppendLine("    else");
        output.AppendLine("      let delta := processed.gas.stateGasUsed - input.handoff.gas.stateGasUsed");
        output.AppendLine("      let folded := if input.handoff.spec.eip8037 then if fitsInt64 delta then foldAuthorizationStateGas processed.gas processed.baseline delta else none else some (processed.gas, processed.baseline)");
        output.AppendLine("      match folded with");
        output.AppendLine("      | none =>");
        output.AppendLine("        let result := emptyResult input .admissionMismatch (some \"authorization gas representation overflow\") processed.gas processed.baseline processed.world processed.access input.handoff.environment processed.refunds processed.events none");
        output.AppendLine("        ({ result with postIntrinsicStateReservoir := processed.gas.stateReservoir }, true)");
        output.AppendLine("      | some (authorizationGas, authorizationBaseline) =>");
        output.AppendLine("        let result := emptyResult input .vmBoundary none authorizationGas authorizationBaseline processed.world processed.access input.handoff.environment processed.refunds processed.events none");
        output.AppendLine("        ({ result with postIntrinsicStateReservoir := authorizationGas.stateReservoir }, false)");
        output.AppendLine();

        output.AppendLine("def isCreateTx (input : Input) : Bool := input.handoff.tx.recipient.isNone");
        output.AppendLine("def senderCurrentNonce (input : Input) (world : WorldFacts) : Nat := match findAuthorityState world input.handoff.tx.sender with | some state => state.current.nonce | none => world.currentNonce");
        output.AppendLine("def deriveRecipient (input : Input) (world : WorldFacts) : Option String :=");
        output.AppendLine("  if input.handoff.tx.recipient.isNone then some (\"create:\" ++ toString (senderCurrentNonce input world)) else input.handoff.tx.recipient");
        output.AppendLine();
        output.AppendLine("def creationCode (input : Input) : CodeIdentity :=");
        output.AppendLine("  { input.environment.code with source := \"initcode\", target := none, bytes := input.handoff.tx.input, isEmpty := input.handoff.tx.input == \"\", isNull := false }");
        output.AppendLine();
        output.AppendLine("def buildEnvironment (input : Input) (preparation : Result) (topFrameOutOfGas : Bool) : Result × Bool :=");
         output.AppendLine("  if !admittedEnvironmentOperations || !admittedEnvironmentBranches then");
         output.AppendLine("    (emptyResult input .admissionMismatch (some \"environment operation witness mismatch\") preparation.gas preparation.executionIntrinsicGasStandard preparation.world preparation.access input.handoff.environment preparation.delegationRefunds preparation.events none, true)");
        output.AppendLine("  else");
        output.AppendLine("    let recipient := deriveRecipient input preparation.world");
        output.AppendLine("    let access0 := if admittedWarmTransactionOperation then warmTransactionAccesses input preparation.access recipient !topFrameOutOfGas else preparation.access");
        output.AppendLine("    let isMessageRecipient := !isCreateTx input && !topFrameOutOfGas");
        output.AppendLine("    let access1 := if isMessageRecipient then (match recipient with | some value => recordAccessRead access0 value | none => access0) else access0");
        output.AppendLine("    let world0 := if isMessageRecipient then (match recipient with | some value => recordAccountRead preparation.world value | none => preparation.world) else preparation.world");
        output.AppendLine("    let recipientEvents := if isMessageRecipient then (match recipient with | some value => [Event.recipientRead value] | none => []) else []");
    output.AppendLine("    let delegated := input.relations.delegatedTarget");
        output.AppendLine("    let targetResolved := admittedTargetOperation && !isCreateTx input && delegated.target.isSome && !topFrameOutOfGas");
        output.AppendLine("    let targetPresent := targetResolved && input.handoff.spec.eip8037");
        output.AppendLine("    let targetWarmEnabled := targetResolved && ((!input.handoff.spec.eip8037 && admittedLegacyDelegatedWarmOperation) || input.handoff.spec.useHotAndColdStorage)");
        output.AppendLine("    let targetChargeEnabled := targetPresent && input.handoff.spec.useHotAndColdStorage");
        output.AppendLine("    let targetWasWarm := if targetWarmEnabled then (match delegated.target with | some value => hasValueIn value access1.warmedAccounts | none => false) else false");
        output.AppendLine("    let targetAccess0 := if targetWarmEnabled then (match delegated.target with | some value => warmAccount access1 value | none => access1) else access1");
        output.AppendLine("    let targetAccess := if admittedDelegatedChargeOperation then chargeDelegatedTarget targetChargeEnabled targetWasWarm delegated.isPrecompile preparation.gas delegated.target delegated.accessCharge targetAccess0 else some (preparation.gas, targetAccess0)");
        output.AppendLine("    match targetAccess with");
        output.AppendLine("    | none =>");
        output.AppendLine("      let targetWarmEvents := if targetWarmEnabled then (match delegated.target with | some value => [Event.delegatedTargetWarm value] | none => []) else []");
        output.AppendLine("      let environment := { input.environment with code := { input.environment.code with target := none, bytes := \"\", isEmpty := true, isNull := false }, executingAccount := match recipient with | some value => value | none => input.environment.executingAccount, caller := input.handoff.tx.sender, codeSource := recipient, callDepth := 0, value := input.handoff.tx.value, input := messageInputData input }");
         output.AppendLine("      let result := emptyResult input .vmBoundary none { preparation.gas with value := 0 } preparation.executionIntrinsicGasStandard world0 targetAccess0 environment preparation.delegationRefunds (preparation.events ++ [Event.accessWarm targetAccess0.warmedAccounts targetAccess0.warmedStorage] ++ recipientEvents ++ targetWarmEvents ++ [Event.environmentRent environment]) none");
         output.AppendLine("      ({ result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }, true)");
        output.AppendLine("    | some (gas, access2) =>");
        output.AppendLine("      let access := if targetPresent && admittedDelegatedReadOperation then (match delegated.target with | some value => recordAccessRead access2 value | none => access2) else access2");
        output.AppendLine("      let world := if targetPresent && admittedDelegatedReadOperation then (match delegated.target with | some value => recordAccountRead world0 value | none => world0) else world0");
        output.AppendLine("      let targetWarmEvents := if targetWarmEnabled then (match delegated.target with | some value => [Event.delegatedTargetWarm value] | none => []) else []");
        output.AppendLine("      let targetReadEvents := if targetPresent then (match delegated.target with | some value => [Event.delegatedTargetRead value] | none => []) else []");
        output.AppendLine("      let resolvedCode := if isCreateTx input then creationCode input else if targetPresent then (match delegated.target with | some value => if admittedDelegatedPrecompileOperation && delegated.isPrecompile then { input.environment.code with target := some value, bytes := \"\", isEmpty := true, isNull := false } else { input.environment.code with target := some value, bytes := delegated.account.code, isEmpty := delegated.account.code == \"\", isNull := false } | none => input.environment.code) else if topFrameOutOfGas then { input.environment.code with target := none, bytes := \"\", isEmpty := true, isNull := false } else input.environment.code");
          output.AppendLine("      let environment := { input.environment with code := resolvedCode, executingAccount := match recipient with | some value => value | none => input.environment.executingAccount, caller := input.handoff.tx.sender, codeSource := recipient, callDepth := 0, value := input.handoff.tx.value, input := messageInputData input }");
        output.AppendLine("      let targetEvents := targetWarmEvents ++ targetReadEvents");
         output.AppendLine("      let result := emptyResult input .vmBoundary none gas preparation.executionIntrinsicGasStandard world access environment preparation.delegationRefunds (preparation.events ++ [Event.accessWarm access.warmedAccounts access.warmedStorage] ++ recipientEvents ++ targetEvents ++ [Event.environmentRent environment]) none");
         output.AppendLine("      let result := { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }");
        output.AppendLine("      ({ result with environment := environment, code := environment.code, input := environment.input }, false)");
        output.AppendLine();

        output.AppendLine("def recipientAccountEmpty (input : Input) (world : WorldFacts) : Bool :=");
        output.AppendLine("  match input.handoff.tx.recipient with");
        output.AppendLine("  | some recipient => match findAuthorityState world recipient with | some state => !state.current.logicalExists | none => input.relations.deadRecipient.accountEmpty");
        output.AppendLine("  | none => input.relations.deadRecipient.accountEmpty");
        output.AppendLine();
        output.AppendLine("def deadRecipient (input : Input) (world : WorldFacts) : Bool :=");
        output.AppendLine("  admittedDeadOperation && recipientAccountEmpty input world && input.relations.deadRecipient.recipient.isSome &&");
        output.AppendLine("    input.handoff.tx.sender != (match input.relations.deadRecipient.recipient with | some value => value | none => input.handoff.tx.sender) &&");
        output.AppendLine("    !isCreateTx input && hasValue input.handoff.tx && input.handoff.spec.eip8037");
        output.AppendLine();
        output.AppendLine("def valueDebitAmount (input : Input) (world : WorldFacts) : Nat :=");
        output.AppendLine("  if !hasValue input.handoff.tx then 0 else if input.handoff.options.warmup then min input.handoff.tx.value world.currentBalance else if world.currentBalance ≥ input.handoff.tx.value then input.handoff.tx.value else 0");
        output.AppendLine();
        output.AppendLine("def payValue (input : Input) (world : WorldFacts) : WorldFacts :=");
        output.AppendLine("  let amount := valueDebitAmount input world");
        output.AppendLine("  if !admittedPayValueOperation || amount = 0 then world else");
        output.AppendLine("    let balance := world.currentBalance - amount");
        output.AppendLine("    let paid := recordAccountWrite world input.handoff.tx.sender");
         output.AppendLine("    updateAuthorityBalance { paid with currentBalance := balance }");
         output.AppendLine("      input.handoff.tx.sender balance");
        output.AppendLine();
        output.AppendLine("def deploymentCollision (input : Input) : Bool := admittedCreateCollisionOperation && admittedCreateCollisionClassificationOperation && isCreateTx input && input.relations.deployment.collision");
        output.AppendLine();
        output.AppendLine("def prepareCall (input : Input) (preparation : Result) (environment : Environment) (topFrameOutOfGas : Bool) : Result :=");
        output.AppendLine("  if !admittedCallOperations || !admittedCallBranches then let result := emptyResult input .admissionMismatch (some \"frame operation witness mismatch\") preparation.gas preparation.executionIntrinsicGasStandard preparation.world preparation.access environment preparation.delegationRefunds preparation.events none; { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }");
        output.AppendLine("  else if topFrameOutOfGas then preparationOog input preparation.gas preparation.executionIntrinsicGasStandard preparation.world preparation.access environment preparation.delegationRefunds preparation.events");
        output.AppendLine("  else");
        output.AppendLine("    let deploymentAccess := if isCreateTx input then recordAccessRead preparation.access environment.executingAccount else preparation.access");
        output.AppendLine("    let deploymentWorld := if isCreateTx input then recordAccountRead preparation.world environment.executingAccount else preparation.world");
        output.AppendLine("    let deploymentEvents := if isCreateTx input then [Event.deploymentRead environment.executingAccount] else []");
        output.AppendLine("    let storageWorld := if admittedCreateStorageResetOperation && isCreateTx input && !input.handoff.spec.eip8037 && input.relations.deployment.storageCleared then recordStorageWrite deploymentWorld environment.executingAccount else deploymentWorld");
        output.AppendLine("    let charged := if isCreateTx input && input.handoff.spec.eip8037 && !input.relations.deployment.logicalExists then chargeState preparation.gas input.relations.deployment.stateCharge else some preparation.gas");
        output.AppendLine("    match charged with");
        output.AppendLine("    | none => topLevelCreateOog input preparation.gas preparation.executionIntrinsicGasStandard preparation.postIntrinsicStateReservoir preparation.world deploymentAccess environment preparation.delegationRefunds preparation.events");
        output.AppendLine("    | some gas =>");
        output.AppendLine("      let chargeEvents := if isCreateTx input && input.handoff.spec.eip8037 && !input.relations.deployment.logicalExists then [Event.stateCharge \"create-state\" input.relations.deployment.stateCharge] else []");
        output.AppendLine("      if deploymentCollision input then");
        output.AppendLine("        let accessEvents := if deploymentAccess.tracing then [Event.accessReport deploymentAccess] else []");
        output.AppendLine("        let result := emptyResult input .collision (some \"transaction collision\") gas preparation.executionIntrinsicGasStandard preparation.world deploymentAccess environment preparation.delegationRefunds (preparation.events ++ [Event.snapshot \"topLevel\" input.topLevelSnapshot] ++ deploymentEvents ++ chargeEvents ++ [Event.restore \"topLevel\" input.topLevelSnapshot] ++ accessEvents) none");
        output.AppendLine("        { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }");
        output.AppendLine("      else");
        output.AppendLine("        let debit := valueDebitAmount input storageWorld");
        output.AppendLine("        let world := payValue input storageWorld");
        output.AppendLine("        let paymentEvents := if debit > 0 then [Event.valueDebit input.handoff.tx.sender debit] else []");
        output.AppendLine("        if admittedNullCodeOperation && environment.code.isNull then");
        output.AppendLine("          let accessEvents := if deploymentAccess.tracing then [Event.accessReport deploymentAccess] else []");
        output.AppendLine("          let result := emptyResult input .nullCode none gas preparation.executionIntrinsicGasStandard world deploymentAccess environment preparation.delegationRefunds (preparation.events ++ [Event.snapshot \"topLevel\" input.topLevelSnapshot] ++ deploymentEvents ++ chargeEvents ++ paymentEvents ++ accessEvents) none");
        output.AppendLine("          let result := { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }");
        output.AppendLine("          { result with environment := environment, code := environment.code, input := environment.input }");
        output.AppendLine("        else");
        output.AppendLine("          let vmInput := { gas := gas, executionType := if isCreateTx input then \"CREATE\" else \"TRANSACTION\", environment := environment, access := deploymentAccess, snapshot := input.topLevelSnapshot }");
        output.AppendLine("          let result := emptyResult input .vmBoundary none gas preparation.executionIntrinsicGasStandard world deploymentAccess environment preparation.delegationRefunds (preparation.events ++ [Event.snapshot \"topLevel\" input.topLevelSnapshot] ++ deploymentEvents ++ chargeEvents ++ paymentEvents ++ [Event.topFrameRent vmInput, Event.vmCallBoundary]) (some vmInput)");
        output.AppendLine("          let result := { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }");
        output.AppendLine("          { result with environment := environment, code := environment.code, input := environment.input }");
        output.AppendLine();

        output.AppendLine("def prepare (input : Input) : Result :=");
        output.AppendLine("  if !semanticAdmission || !admittedContextOperations || !admittedContextBranches || !admittedAuthorizationCreateExclusion || !admittedPreparationBranches || !admittedReservoirOperations || !fixedWidthValidForInput input || !inputFactsCoherent input then");
        output.AppendLine("    emptyResult input .admissionMismatch (some \"semantic, representation, or source-fact coherence mismatch\") input.handoff.gas input.preExecutionIntrinsicGasStandard input.world input.access input.handoff.environment input.delegationRefunds [] none");
        output.AppendLine("  else");
        output.AppendLine("    let canonical := { input with environment := input.handoff.environment, delegationRefunds := input.handoff.delegationRefunds }");
        output.AppendLine("    let authorization := processAuthorization canonical");
        output.AppendLine("    let environmentResult := buildEnvironment canonical authorization.1 authorization.2");
        output.AppendLine("    let environmentPreparation := environmentResult.1");
        output.AppendLine("    let environmentOog := environmentResult.2");
        output.AppendLine("    let withReservoir := { environmentPreparation with postIntrinsicStateReservoir := environmentPreparation.gas.stateReservoir }");
        output.AppendLine("    let deadApplicable := !authorization.2 && !environmentOog && deadRecipient canonical withReservoir.world");
        output.AppendLine("    let deadCharge := if deadApplicable && admittedDeadChargeOperation then chargeState withReservoir.gas canonical.relations.deadRecipient.stateCharge else some withReservoir.gas");
        output.AppendLine("    match deadCharge with");
        output.AppendLine("    | none => prepareCall canonical withReservoir withReservoir.environment true");
        output.AppendLine("    | some gas =>");
        output.AppendLine("      let deadCharged := if deadApplicable then { withReservoir with gas := gas, events := withReservoir.events ++ [Event.stateCharge \"dead-recipient\" canonical.relations.deadRecipient.stateCharge] } else withReservoir");
        output.AppendLine("      prepareCall canonical deadCharged deadCharged.environment (authorization.2 || environmentOog)");
        output.AppendLine();
        output.AppendLine("def boundary (input : Input) : Result := prepare input");
        output.AppendLine();
    }

    private static string ParseEdge(string edge)
    {
        string[] parts = edge.Split("->", StringSplitOptions.None);
        return parts.Length == 2 ? "(" + parts[0] + ", " + parts[1] + ")" : "(0, 0)";
    }

    private static string LeanString(string value) => "\"" + Escape(value) + "\"";

    private static string LeanStringList(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(LeanString)) + "]";

    private static string LeanBool(bool value) => value ? "true" : "false";

    private static string BindingToken(SourceBinding binding) => binding.Member + "#" + binding.CanonicalSyntaxSha256;

    private static string LeanDependencyRecord(DependencyIdentity dependency) =>
        "{ kind := " + LeanString(dependency.Kind) + ", name := " + LeanString(dependency.Name) + ", path := " + LeanString(dependency.Path) +
        ", sha256 := " + LeanString(dependency.Sha256) + ", binding := " + LeanString(dependency.Binding) +
        ", theorems := " + LeanStringList(dependency.Theorems) + " }";

    private static string LeanStageRecord(StageIdentity stage) =>
        "{ id := " + LeanString(stage.Id) + ", ordinal := " + stage.Ordinal + ", owner := " + LeanString(stage.Owner) +
        ", kind := " + LeanString(stage.Kind) + ", operationIds := " + LeanStringList(stage.OperationIds) + " }";

    private static string LeanOperationRecord(OperationIdentity operation) =>
        "{ id := " + LeanString(operation.Id) + ", ordinal := " + operation.Ordinal +
        ", formula := " + LeanString(operation.Formula.ToString()) + ", expression := " + LeanString(operation.Expression) +
        ", expected := " + LeanString(operation.Expected) +
        ", syntaxHash := " + LeanString(operation.Binding.CanonicalSyntaxSha256) + ", owner := " + LeanString(operation.Owner) +
        ", cfgBlock := " + operation.Binding.ControlFlowBlock + ", receiver := " + LeanString(operation.Binding.Receiver) +
        ", target := " + LeanString(operation.Binding.TargetSymbol) + ", sourceOperands := " + LeanStringList(operation.SourceOperands) +
        ", reads := " + LeanStringList(operation.Reads) + ", writes := " + LeanStringList(operation.Writes) +
        ", failureVisibility := " + LeanString(operation.FailureVisibility) + ", effects := " +
        LeanStringList(operation.Effects.Select(static effect => effect.ToString())) + ", binding := " + LeanString(BindingToken(operation.Binding)) + " }";

    private static string LeanBranchRecord(BranchIdentity branch) =>
        "{ id := " + LeanString(branch.Id) + ", ordinal := " + branch.Ordinal + ", owner := " + LeanString(branch.Owner) +
        ", condition := " + LeanString(branch.Condition) + ", terminalKind := " + LeanString(branch.TerminalKind.ToString()) +
        ", effects := " + LeanStringList(branch.Effects.Select(static effect => effect.ToString())) +
        ", syntaxHash := " + LeanString(branch.Binding.CanonicalSyntaxSha256) + ", terminal := " + LeanString(branch.Terminal) +
        ", successors := [" + string.Join(", ", branch.ControlFlowSuccessors) + "]" +
        ", exits := [" + string.Join(", ", branch.ExitBlocks) + "]" +
        ", binding := " + LeanString(BindingToken(branch.Binding)) + " }";

    private static string LeanAdapterRecord(AdapterPremise adapter) =>
        "{ id := " + LeanString(adapter.Id) + ", domain := " + LeanString(adapter.Domain) +
        ", projection := " + LeanString(adapter.Projection) + ", assumption := " + LeanString(adapter.Assumption) +
        ", requiredFields := " + LeanStringList(adapter.RequiredIdentities) + ", bindings := " +
        LeanStringList(adapter.Bindings.Select(BindingToken)) + " }";

    private static string LeanCompositionRecord(CompositionDependency dependency) =>
        "{ id := " + LeanString(dependency.Id) + ", kind := " + LeanString(dependency.Kind) +
        ", artifactPath := " + LeanString(dependency.ArtifactPath) + ", artifactSha256 := " + LeanString(dependency.ArtifactSha256) +
        ", theoremName := " + LeanString(dependency.Theorem) + ", theoremShape := " + LeanString(dependency.TheoremShape) +
        ", requiredFields := " + LeanStringList(dependency.RequiredFields) + " }";

    private static string LeanControlFlowRecord(ControlFlowIdentity flow) =>
        "{ id := " + LeanString(flow.Id) + ", owner := " + LeanString(flow.Owner) + ", member := " + LeanString(flow.Member) +
        ", reachable := [" + string.Join(", ", flow.ReachableBlocks) + "]" +
        ", edges := [" + string.Join(", ", flow.Edges.Select(ParseEdge)) + "]" +
        ", normalExits := [" + string.Join(", ", flow.NormalExitBlocks) + "]" +
        ", exceptionalExits := [" + string.Join(", ", flow.ExceptionalExitBlocks) + "]" +
        ", backEdges := [" + string.Join(", ", flow.BackEdges.Select(ParseEdge)) + "]" +
        ", loopHeaders := [" + string.Join(", ", flow.LoopHeaders) + "]" +
        ", binding := " + LeanString(BindingToken(flow.Binding)) + " }";

    private static string LeanBindingRecord(SourceBinding binding) =>
        "{ path := " + LeanString(binding.Path) + ", owner := " + LeanString(binding.Owner) +
        ", member := " + LeanString(binding.Member) + ", signature := " + LeanString(binding.Signature) +
        ", nodeKind := " + LeanString(binding.NodeKind) + ", operation := " + LeanString(binding.OperationKind) +
        ", canonicalSyntax := " + LeanString(binding.CanonicalSyntax) + ", tokenSha256 := " + LeanString(binding.TokenSha256) +
        ", syntaxHash := " + LeanString(binding.CanonicalSyntaxSha256) + ", containingMember := " + LeanString(binding.ContainingMember) +
        ", target := " + LeanString(binding.TargetSymbol) + ", targetKind := " + LeanString(binding.TargetSymbolKind) +
        ", targetAssembly := " + LeanString(binding.TargetAssembly) + ", receiver := " + LeanString(binding.Receiver) +
        ", arguments := " + LeanStringList(binding.ArgumentParameters) + ", refKinds := " + LeanStringList(binding.ArgumentRefKinds) +
        ", candidateReason := " + LeanString(binding.CandidateReason) + ", isErrorSymbol := " + LeanBool(binding.IsErrorSymbol) +
        ", hasCandidateSymbols := " + LeanBool(binding.HasCandidateSymbols) + ", statementOrdinal := " + binding.StatementOrdinal +
        ", controlFlowPath := " + LeanString(binding.ControlFlowPath) + ", cfgBlock := " + binding.ControlFlowBlock +
        ", reachable := " + LeanBool(binding.IsReachable) + ", startLine := " + binding.StartLine +
        ", startColumn := " + binding.StartColumn + ", endLine := " + binding.EndLine + ", endColumn := " + binding.EndColumn + " }";

    private static string LeanGasFieldRecord(GasFieldIdentity field) =>
        "{ name := " + LeanString(field.Name) + ", owner := " + LeanString(field.Owner) +
        ", sourceType := " + LeanString(field.SourceType) + ", width := " + field.Width +
        ", representation := " + LeanString(field.Representation) + ", fixedWidthOperations := " +
        LeanStringList(field.FixedWidthOperations) + ", binding := " + LeanString(BindingToken(field.Binding)) + " }";

    private static string LeanSnapshotRecord(SnapshotIdentity snapshot) =>
        "{ id := " + LeanString(snapshot.Id) + ", owner := " + LeanString(snapshot.Owner) +
        ", kind := " + LeanString(snapshot.Kind) + ", fields := " + LeanStringList(snapshot.Fields) +
        ", binding := " + LeanString(BindingToken(snapshot.Binding)) + " }";

    private static string LeanEnvironmentRecord(EnvironmentIdentity environment) =>
        "{ fields := " + LeanStringList(environment.Fields) + ", codeFields := " + LeanStringList(environment.CodeFields) +
        ", inputFields := " + LeanStringList(environment.InputFields) + ", binding := " +
        LeanString(BindingToken(environment.Binding)) + " }";

    private static string LeanHandoffRecord(HandoffIdentity handoff) =>
        "{ id := " + LeanString(handoff.Id) + ", fields := " + LeanStringList(handoff.Fields) +
        ", boundaryCall := " + LeanString(handoff.BoundaryCall) + ", rentOwner := " + LeanString(handoff.RentOwner) +
        ", rentOverload := " + LeanString(handoff.RentOverload) + ", rentReceiver := " + LeanString(handoff.RentReceiver) +
        ", rentArguments := " + LeanStringList(handoff.RentArguments) + ", rentRefKinds := " + LeanStringList(handoff.RentRefKinds) +
        ", vmOwner := " + LeanString(handoff.VmOwner) + ", vmOverloads := " + LeanString(handoff.VmOverloads) +
        ", vmReceiver := " + LeanString(handoff.VmReceiver) + ", vmArguments := " + LeanStringList(handoff.VmArguments) +
        ", vmRefKinds := " + LeanStringList(handoff.VmRefKinds) + ", boundaryBindings := " +
        LeanStringList(handoff.BoundaryBindings.Select(BindingToken)) + ", accessLineage := " + LeanStringList(handoff.AccessLineage) +
        ", accessBindings := " + LeanStringList(handoff.AccessBindings.Select(BindingToken)) + ", binding := " + LeanString(BindingToken(handoff.Binding)) + " }";

    private static string LeanIdentifier(string value)
    {
        StringBuilder builder = new();
        foreach (char character in value)
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        return builder.Length == 0 ? "anonymous" : builder.ToString();
    }

    private static void AppendBlock(StringBuilder output, string block)
    {
        foreach (string line in block.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').Split('\n')) output.AppendLine(line);
        output.AppendLine();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
