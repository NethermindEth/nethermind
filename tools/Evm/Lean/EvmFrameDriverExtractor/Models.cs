// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record SourceAdmission(string Role, string Path, string Sha256);

internal sealed record MemberAdmission(
    string Path,
    string Namespace,
    string OwnerPath,
    string MemberKind,
    string Name,
    int GenericArity,
    string ParameterTypes,
    string SyntaxKind,
    string Sha256);

internal sealed record DependencyAdmission(
    string Kind,
    string Name,
    string Path,
    string Sha256,
    string Binding,
    string[] Theorems);

internal sealed record SourceIdentity(string Role, string Path, string Sha256, string SyntaxSha256);

internal sealed record MemberIdentity(
    string SourcePath,
    string Namespace,
    string OwnerPath,
    string MemberKind,
    string Member,
    int GenericArity,
    string ParameterTypes,
    string SyntaxKind,
    string Sha256);

internal sealed record DependencyIdentity(
    string Kind,
    string Name,
    string Path,
    string Sha256,
    string Binding,
    string[] Theorems);

internal sealed record SourceControlIdentity(
    string SourcePath,
    string OwnerPath,
    string Member,
    string Sha256,
    string[] Operations,
    ControlNodeIdentity[] Topology,
    string TopologySha256);

internal enum ControlConditionKind
{
    Unknown,
    Property,
    Invocation,
    Local,
}

/// <summary>Exact syntax/semantic shape used to select an admitted condition.</summary>
internal sealed record ControlConditionIdentity(
    ControlConditionKind Kind,
    string Receiver,
    string Member,
    bool Negated)
{
    internal static ControlConditionIdentity Unknown { get; } =
        new(ControlConditionKind.Unknown, string.Empty, string.Empty, false);
}

/// <summary>Canonical syntax evidence for one control-flow node in an admitted production member.</summary>
internal sealed record ControlNodeIdentity(
    string Id,
    string Kind,
    string ParentId,
    string Arm,
    string Condition,
    string Sha256,
    string[] Operations)
{
    // This shape is intentionally not serialized: accepted Stage E artifacts
    // retain their exact JSON topology while selectors use typed syntax data.
    [JsonIgnore]
    public ControlConditionIdentity ConditionIdentity { get; init; } = ControlConditionIdentity.Unknown;
}

/// <summary>One source-attached shell stage bound to a concrete topology node.</summary>
internal sealed record ControlPlanStepIdentity(
    string Stage,
    string Member,
    string NodeId,
    string Kind,
    string SourceArm,
    string SourceSha256);

internal sealed record ExternalBoundary(
    string[] LeafBindings,
    string[] Cancellation,
    string[] FixedWidth,
    string[] Fuel);

internal sealed record BranchDescriptor(
    string Name,
    string Invocation,
    string[] Effects,
    string Settlement,
    string[] SourceBindings);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string TargetRoot,
    string TargetFork,
    string TargetGasPolicy,
    ExternalBoundary Boundary,
    string[] Phases,
    string[] Subjects,
    string[] Routes,
    string[] DispatchOrder,
    ControlPlanStepIdentity[] ControlPlan,
    BranchDescriptor[] Branches,
    SourceControlIdentity[] SourceControl,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    DependencyIdentity[] Dependencies,
    string[] ReviewedOperationalBindings,
    string[] Exclusions);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    DependencyIdentity[] Dependencies,
    SourceControlIdentity[] SourceControl,
    ControlPlanStepIdentity[] ControlPlan,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string CombinedControlSha256,
    string[] ReviewedOperationalBindings);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int DependencyCount,
    int ControlAnchorCount,
    int BranchCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);
