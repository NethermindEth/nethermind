// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PrecompileFrameExtractor;

internal enum StageBStatus
{
    NotStarted,
    Declined,
    InputMemoryOutOfGas,
    PricingOverflow,
    PricingOutOfGas,
    ReturnedFailure,
    ManagedException,
    OutputCopyOutOfGas,
    Success,
}

internal enum StageBResult
{
    Cancelled,
    Declined,
    OutOfGas,
    StackFailure,
    StackSuccess,
}

internal enum StageBEffect
{
    PreDispatchCancellationPoll,
    InputLoad,
    ChildCreate,
    Pricing,
    RunOracle,
    ExecutionGasClear,
    StateGasRestore,
    AccountTouch,
    ChildRefund,
    ReturnDataClear,
    ReturnDataSet,
    OutputCopy,
    ReturnOutOfGas,
    StackFailure,
    StackSuccess,
    BoundaryCancellationPoll,
}

internal sealed record StageBBranchDescriptor(
    string Name,
    StageBStatus Status,
    StageBResult Result,
    StageBEffect[] Effects,
    bool InvokesOracle,
    bool RefundsChildGas,
    bool WritesOutput,
    bool PushesStack);

internal sealed record StageBOracleBinding(
    string Name,
    int Address,
    string DependencyPath,
    string DependencySha256,
    string Namespace,
    string EntrySymbol);

internal sealed record StageBArtifactBinding(string Path, string Sha256);

internal sealed record StageBProofBinding(
    string Kind,
    string Path,
    string Sha256,
    string Namespace,
    string[] Types,
    string Definition,
    string? Theorem);

internal sealed record StageBStageAIdentity(
    StageBArtifactBinding Ir,
    StageBArtifactBinding Manifest,
    StageBArtifactBinding Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    int SourceCount,
    int MemberCount,
    int DependencyCount,
    int RouteCount);

internal sealed record StageBScope(
    string Claim,
    string[] Included,
    string[] Excluded,
    string[] Assumptions);

internal sealed record StageBOperationalDescriptor(
    string EntryMember,
    string DirectEligibility,
    string CancellationBoundary,
    string PricingRule,
    string OracleRule,
    string OutputCopyRule,
    StageBBranchDescriptor[] Branches);

internal sealed record StageBIrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    StageBStageAIdentity StageA,
    StageBScope Scope,
    StageBOperationalDescriptor Operational,
    StageBOracleBinding[] OracleBindings,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    StageBProofBinding Reference,
    StageBProofBinding Refinement,
    string[] MutationChecks,
    string[] SemanticBindings);

internal sealed record StageBManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    StageBStageAIdentity StageA,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    StageBOracleBinding[] OracleBindings,
    StageBProofBinding Reference,
    StageBProofBinding Refinement,
    StageBArtifactBinding Ir,
    StageBArtifactBinding Lean,
    string[] SemanticBindings);

internal sealed record StageBExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256,
    int BranchCount,
    int OracleCount);
