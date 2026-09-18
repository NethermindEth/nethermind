// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.EvmTransactionPreparationExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int OperationCount,
    int BranchCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);

internal sealed record SourceAdmission(string Role, string Path, string Sha256);

internal sealed record DependencyAdmission(
    string Kind,
    string Name,
    string Path,
    string Sha256,
    string Binding,
    string[] Theorems);

internal sealed record AdmissionClosure(
    int SchemaVersion,
    SourceAdmission[] Sources,
    DependencyAdmission[] Dependencies,
    string Sha256);

internal sealed record SourceIdentity(string Role, string Path, string Sha256, string SyntaxSha256);

internal sealed record DependencyIdentity(
    string Kind,
    string Name,
    string Path,
    string Sha256,
    string Binding,
    string[] Theorems);

internal sealed record CompilerReferenceIdentity(
    string Path,
    string AssemblyName,
    string Sha256,
    string Mvid,
    bool Selected,
    string[] Dependencies);

/// <summary>Complete Roslyn evidence for one admitted source node.</summary>
internal sealed record SourceBinding(
    string Path,
    string Owner,
    string Member,
    string Signature,
    string NodeKind,
    string OperationKind,
    string CanonicalSyntax,
    string TokenSha256,
    string CanonicalSyntaxSha256,
    string ContainingMember,
    string Receiver,
    string TargetSymbol,
    string TargetSymbolKind,
    string TargetAssembly,
    string[] ArgumentParameters,
    string[] ArgumentRefKinds,
    string CandidateReason,
    bool IsErrorSymbol,
    bool HasCandidateSymbols,
    int StatementOrdinal,
    string ControlFlowPath,
    int ControlFlowBlock,
    bool IsReachable,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

internal enum SemanticFormula
{
    SetTransactionContext,
    IncrementCreateMetrics,
    AllocateAccessTracker,
    CapturePreparationGas,
    CaptureExecutionIntrinsicGasStandard,
    CaptureDelegationRefunds,
    InitializePreparationSnapshot,
    CapturePreparationSnapshot,
    ProcessDelegations,
    AuthorizationCreateExclusion,
    ValidateAuthorization,
    AuthorizationWarm,
    AuthorizationAccountRead,
    AuthorizationLogicalExistence,
    AuthorizationNewAccountStateCharge,
    AuthorizationAccountWriteCharge,
    AuthorizationPerAuthStateCharge,
    AuthorizationCreateAccount,
    AuthorizationIncrementNonce,
    AuthorizationSetDelegation,
    AuthorizationStateGasDelta,
    AuthorizationFoldTopFrameStateGas,
    DeriveRecipient,
    WarmTransactionAccesses,
    ResolveCreationCode,
    ResolveRecipientCode,
    ResolveDelegationTarget,
    ResolveDelegationPrecompile,
    ChargeDelegatedTargetAccess,
    ReadDelegatedTarget,
    WarmLegacyDelegatedTarget,
    CapturePostIntrinsicReservoir,
    ChargeDeadRecipient,
    ChargeDeadRecipientState,
    RestorePreparationSnapshot,
    RestorePreparationGas,
    BuildExecutionIntrinsic,
    CaptureTopLevelSnapshot,
    HaltTopFrame,
    PrepareCreateDestination,
    CreateLogicalExistence,
    CreateCollisionResult,
    CreateCollisionClassification,
    CreateStorageReset,
    ChargeCreateState,
    RestoreTopLevelSnapshot,
    RejectCreateCollision,
    PayValue,
    NullCodeShortCircuit,
    CreateVmInput,
    RentTopLevel,
    ExecuteTransactionBoundary,
}

internal enum SemanticEffect
{
    OwnsTxExecutionContext,
    OwnsStackAccessTracker,
    RecordsMetrics,
    PreservesGas,
    CapturesSnapshot,
    ReadsWorld,
    WritesWorld,
    WarmsAccount,
    WarmsStorage,
    ChargesStateGas,
    ChargesExecutionGas,
    ResetsGas,
    RestoresWorld,
    PreservesPhysicalExistence,
    PreservesLogicalExistence,
    PreservesOriginalFacts,
    PreservesCurrentFacts,
    PreservesRefundCounter,
    PreservesCodeIdentity,
    PreservesDelegationIdentity,
    PreservesInputIdentity,
    PreservesTraceObservations,
    PreservesStatus,
    PreservesError,
    StopsBeforeVmExecution,
}

internal enum TerminalKind
{
    None,
    PreparationReturn,
    PreparationOutOfGas,
    Collision,
    NullCode,
    VmHandoff,
}

internal sealed record StageIdentity(
    string Id,
    int Ordinal,
    string Owner,
    string Kind,
    SourceBinding Binding,
    string[] OperationIds);

internal sealed record BranchIdentity(
    string Id,
    int Ordinal,
    string Owner,
    string Condition,
    string Terminal,
    TerminalKind TerminalKind,
    string[] Effects,
    SourceBinding Binding,
    string[] ControlFlowSuccessors,
    int[] ExitBlocks);

internal sealed record OperationIdentity(
    string Id,
    int Ordinal,
    string Owner,
    SemanticFormula Formula,
    string Expression,
    string Expected,
    string[] SourceOperands,
    string[] Reads,
    string[] Writes,
    string FailureVisibility,
    SemanticEffect[] Effects,
    SourceBinding Binding);

internal sealed record MemberIdentity(
    string Path,
    string Namespace,
    string OwnerPath,
    string MemberKind,
    string Name,
    int GenericArity,
    string ParameterTypes,
    string SyntaxKind,
    string TokenSha256,
    SourceBinding Binding);

internal sealed record AdapterPremise(
    string Id,
    string Domain,
    string Projection,
    string Assumption,
    string[] RequiredIdentities,
    SourceBinding[] Bindings);

internal sealed record GasFieldIdentity(
    string Name,
    string Owner,
    string SourceType,
    int Width,
    string Representation,
    string[] FixedWidthOperations,
    SourceBinding Binding);

internal sealed record SnapshotIdentity(
    string Id,
    string Owner,
    string Kind,
    string[] Fields,
    SourceBinding Binding);

internal sealed record EnvironmentIdentity(
    string[] Fields,
    string[] CodeFields,
    string[] InputFields,
    SourceBinding Binding);

internal sealed record HandoffIdentity(
    string Id,
    string[] Fields,
    string BoundaryCall,
    string RentOwner,
    string RentOverload,
    string RentReceiver,
    string[] RentArguments,
    string[] RentRefKinds,
    string VmOwner,
    string VmOverloads,
    string VmReceiver,
    string[] VmArguments,
    string[] VmRefKinds,
    SourceBinding[] BoundaryBindings,
    string[] AccessLineage,
    SourceBinding[] AccessBindings,
    string[] DataflowLineage,
    SourceBinding[] DataflowBindings,
    SourceBinding Binding);

/// <summary>Exact reachable control-flow evidence for one admitted production member.</summary>
internal sealed record ControlFlowIdentity(
    string Id,
    string Owner,
    string Member,
    int[] ReachableBlocks,
    string[] Edges,
    int[] NormalExitBlocks,
    int[] ExceptionalExitBlocks,
    string[] BackEdges,
    int[] LoopHeaders,
    SourceBinding Binding);

/// <summary>Canonical lowering plan consumed by the generated theorem-free transition.</summary>
internal sealed record SemanticPlan(
    string[] StageIds,
    string[] OperationIds,
    string[] BranchIds,
    string[] GasFieldIds,
    string[] SnapshotIds,
    string[] AdapterIds,
    string[] ControlFlowIds,
    string[] EffectIds,
    string BoundaryOperationId,
    string Sha256);

internal sealed record CompositionDependency(
    string Id,
    string Kind,
    string ArtifactPath,
    string ArtifactSha256,
    string Theorem,
    string TheoremShape,
    string[] RequiredFields);

internal sealed record SemanticProfile(
    StageIdentity[] Stages,
    BranchIdentity[] Branches,
    OperationIdentity[] Operations,
    GasFieldIdentity[] GasFields,
    SnapshotIdentity[] Snapshots,
    EnvironmentIdentity Environment,
    HandoffIdentity Handoff,
    ControlFlowIdentity[] ControlFlows,
    SemanticPlan Plan,
    AdapterPremise[] AdapterPremises,
    string[] OrderedEffects,
    string[] ExcludedOperations,
    string SourceBoundary,
    string[] CompilerUnits,
    SourceBinding[] Bindings);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string TargetFork,
    string TargetGasPolicy,
    string TargetRoot,
    string AdmissionSha256,
    string SourceClosureSha256,
    SourceIdentity[] Sources,
    DependencyIdentity[] Dependencies,
    CompilerReferenceIdentity[] CompilerReferences,
    CompositionDependency[] Composition,
    SemanticProfile Semantics,
    string[] ArithmeticRules,
    string[] ExternalCorrespondence,
    string[] Exclusions);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record Manifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    string AdmissionSha256,
    string SourceClosureSha256,
    SourceIdentity[] Sources,
    DependencyIdentity[] Dependencies,
    CompilerReferenceIdentity[] CompilerReferences,
    CompositionDependency[] Composition,
    MemberIdentity[] Members,
    SourceBinding[] Bindings,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string CombinedBindingSha256,
    string SemanticIrSha256);

internal sealed record SourceFile(
    string RelativePath,
    string Role,
    string Sha256,
    string SyntaxSha256,
    byte[] Bytes,
    SyntaxTree Tree,
    CompilationUnitSyntax Root);

internal sealed record MemberAdmission(
    string Path,
    string Namespace,
    string OwnerPath,
    string MemberKind,
    string Name,
    int GenericArity,
    string ParameterTypes);

internal sealed record CompilerReferenceClosure(
    MetadataReference[] References,
    CompilerReferenceIdentity[] Identities,
    Dictionary<string, string> ResolvedPaths);
