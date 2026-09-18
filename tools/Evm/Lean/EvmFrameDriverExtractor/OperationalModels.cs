// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor;

internal sealed record OperationalDependencyIdentity(
    string Stage,
    string Name,
    string ManifestPath,
    string ManifestSha256,
    string ProofPath,
    string ProofSha256,
    string Theorem,
    string SignatureSha256,
    string Binding);

internal sealed record OperationalAdapterIdentity(
    string Name,
    string SourceBoundary,
    string SemanticSurface,
    string Status,
    string FailureMode,
    string[] AcceptedTheorems,
    string Obligation);

/// <summary>A typed action in the executable control AST.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
internal enum OperationalControlActionIdentity
{
    PrepareFresh,
    PrepareContinuation,
    DispatchBytecode,
    DispatchFullPrecompile,
    PrecompileFailure,
    ClassifyContinue,
    ClassifySuspend,
    ClassifyHalt,
    SettleNestedRegularSuccess,
    SettleNestedCreateSuccess,
    SettleNestedCreateInvalidCode,
    SettleNestedCreateOutOfGas,
    SettleNestedRevert,
    SettleNestedException,
    SettleResume,
    SettleTopLevelSuccess,
    SettleTopLevelRevert,
    SettleTopLevelException,
    CleanupCancelled,
    CleanupEscaped,
    CleanupInvalidControl,
    CleanupCompleted,
}

/// <summary>A typed stage in the source-lowered operational control AST.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
internal enum OperationalControlStageIdentity
{
    Prepare,
    Dispatch,
    Classify,
    Settle,
    Cleanup,
}

/// <summary>A typed predicate in the source-lowered operational control AST.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
internal enum OperationalControlPredicateIdentity
{
    FreshFrame,
    ContinuationFrame,
    BytecodeFrame,
    FullPrecompileFrame,
    BytecodeContinue,
    BytecodeSuspend,
    NestedRegularSuccess,
    NestedCreateSuccess,
    CreateDepositInvalidCode,
    CreateDepositOutOfGas,
    NestedRevert,
    NestedException,
    ResumeParent,
    TopLevelSuccess,
    TopLevelRevert,
    TopLevelException,
    PrecompileOutOfGasNested,
    PrecompileOutOfGasTop,
    PrecompileReturnedFailure,
    PrecompileManagedException,
    Cancelled,
    Escaped,
    InvalidControl,
    Completed,
}

/// <summary>A typed effect in the source-lowered operational control AST.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
internal enum OperationalControlEffectIdentity
{
    ClearReturnData,
    PrepareFresh,
    RetainReturnData,
    PrepareContinuation,
    RunBytecode,
    RunDispatchLoop,
    RunFullPrecompile,
    ExecutePrecompile,
    RetainCurrentFrame,
    PrepareChildFrame,
    RetainParent,
    PopParent,
    MergeChild,
    RepayStateGasSpill,
    PrepareCreateData,
    HandleCreate,
    RestoreWorld,
    CreditParent,
    BurnDepositGas,
    RestoreSnapshot,
    RestoreChildGas,
    HandleRevert,
    RestoreFailureControl,
    ResumeParent,
    PrepareTopLevelSubstate,
    RefundRevertedStateGas,
    HandleExceptionOrFailure,
    FailureSettlement,
    Dispose,
    FailClosed,
    DisposeActiveFrames,
}

/// <summary>A typed settlement tag in the source-lowered operational control AST.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
internal enum OperationalControlSettlementIdentity
{
    Preparation,
    Invocation,
    Continued,
    Suspended,
    ChildSuccess,
    ChildCreateSuccess,
    ChildCreateInvalidCode,
    ChildCreateOutOfGas,
    ChildRevert,
    ChildException,
    TopLevelSuccess,
    TopLevelRevert,
    TopLevelException,
    FullPrecompileOutOfGasNested,
    FullPrecompileOutOfGasTop,
    FullPrecompileReturnedFailure,
    FullPrecompileManagedException,
    Cancelled,
    Escaped,
    InvalidControl,
    Completed,
}

/// <summary>A source-attached control node consumed by the operational emitter.</summary>
internal sealed record OperationalTopologyNodeIdentity(
    string Member,
    string Id,
    string Kind,
    string ParentId,
    string Arm,
    string Condition,
    string Sha256,
    string[] Operations);

/// <summary>A source-attached control-plan stage and its complete typed action set.</summary>
internal sealed record OperationalControlPlanStepIdentity(
    string Stage,
    string Member,
    string NodeId,
    string Kind,
    string SourceArm,
    string SourceSha256,
    OperationalControlActionIdentity[] Actions);

/// <summary>A branch binding whose source arm, stage set, and topology digest are emitted as typed data.</summary>
internal sealed record OperationalBranchBindingIdentity(
    string Branch,
    string Member,
    string NodeId,
    string Kind,
    string SourceArm,
    string SourceSha256,
    string BindingArm,
    string Invocation,
    OperationalControlPredicateIdentity Predicate,
    string[] Effects,
    OperationalControlEffectIdentity[] EffectKinds,
    string Settlement,
    OperationalControlSettlementIdentity SettlementKind,
    OperationalControlActionIdentity[] Actions,
    string[] Stages);

/// <summary>The exact source dispatch-table route imported from the accepted Stage A IR.</summary>
internal sealed record OperationalOpcodeRouteIdentity(
    string DispatchTable,
    int Byte,
    string Instruction,
    string RouteKind,
    string ActivationRule,
    string Package,
    string ClosedHandlerRoot,
    bool Admitted);

/// <summary>The exact source precompile route imported from the accepted Stage A IR.</summary>
internal sealed record OperationalPrecompileRouteIdentity(
    string Name,
    int Address,
    string ActivationRule,
    string ProviderRoot,
    string WrapperManifestPath,
    string WrapperManifestSha256,
    string LeanModule,
    string? FullyQualifiedTheorem,
    bool Admitted);

internal sealed record OperationalLoopIdentity(
    int BatchLimit,
    string[] DispatchModes,
    string[] EntryTransitions,
    string[] BytecodeOutcomes,
    string[] SettlementRoutes,
    string[] StateEffects,
    string[] CleanupRoutes,
    string[] FuelRules,
    string Measure,
    string AdequacyStatus);

internal sealed record CompilerReferenceIdentity(
    string Path,
    string Sha256,
    string ModuleMvid);

internal sealed record OperationalIrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string CompilerMvid,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    string TargetRoot,
    string TargetFork,
    string TargetGasPolicy,
    string StageEBoundary,
    string StageEIrSha256,
    CompilerReferenceIdentity[] CompilerReferences,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    SourceControlIdentity[] SourceControl,
    OperationalControlPlanStepIdentity[] ControlPlan,
    OperationalTopologyNodeIdentity[] Topology,
    OperationalBranchBindingIdentity[] BranchBindings,
    OperationalOpcodeRouteIdentity[] OpcodeRoutes,
    OperationalPrecompileRouteIdentity[] PrecompileRoutes,
    OperationalLoopIdentity Loop,
    OperationalAdapterIdentity[] Adapters,
    OperationalDependencyIdentity[] AcceptedDependencies,
    string[] ReviewedOperationalBindings,
    string[] MutationVectors,
    string[] ExplicitOpenObligations);

internal sealed record OperationalArtifactIdentity(string Path, string Sha256);

internal sealed record OperationalSourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string CompilerMvid,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    string StageEIrSha256,
    CompilerReferenceIdentity[] CompilerReferences,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    SourceControlIdentity[] SourceControl,
    OperationalControlPlanStepIdentity[] ControlPlan,
    OperationalTopologyNodeIdentity[] Topology,
    OperationalBranchBindingIdentity[] BranchBindings,
    OperationalOpcodeRouteIdentity[] OpcodeRoutes,
    OperationalPrecompileRouteIdentity[] PrecompileRoutes,
    OperationalLoopIdentity Loop,
    OperationalDependencyIdentity[] AcceptedDependencies,
    OperationalArtifactIdentity Ir,
    OperationalArtifactIdentity Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string CombinedControlSha256,
    string CombinedDependencySha256,
    string[] ReviewedOperationalBindings,
    string[] ExplicitOpenObligations);

internal sealed record OperationalExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int ControlAnchorCount,
    int DependencyCount,
    int AdapterCount,
    int MutationVectorCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);
