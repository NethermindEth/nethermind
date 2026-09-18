// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EvmFrameControlSettlementExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal enum AdmissionKind
{
    Source,
    Member,
    Dependency,
}

internal sealed record SourceAdmission(string Role, string Path, string Sha256);

internal sealed record MemberAdmission(string Path, string Owner, string Name, string SyntaxKind);

internal sealed record DependencyAdmission(string Kind, string Name, string Path, string Sha256, string Binding,
    string[] Theorems);

internal sealed record SourceIdentity(string Role, string Path, string Sha256, string SyntaxSha256);

internal sealed record MemberIdentity(string SourcePath, string Owner, string Member, string SyntaxKind, string Signature);

internal sealed record DependencyIdentity(string Kind, string Name, string Path, string Sha256, string Binding,
    string[] Theorems);

internal sealed record ExternalBoundary(
    string[] Adapters,
    string[] Oracles,
    string[] FixedWidth,
    string[] Cancellation,
    string[] Fuel);

internal sealed record BranchDescriptor(string Name, string Invocation, string[] Effects, string Settlement);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string ClosedRoot,
    string ClosedFork,
    string ClosedGasPolicy,
    ExternalBoundary Boundary,
    string[] Phases,
    string[] FrameKinds,
    string[] ExitKinds,
    string[] ControlRoutes,
    string[] SettlementVariants,
    string[] DispatchOrder,
    BranchDescriptor[] Branches,
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
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string[] ReviewedOperationalBindings);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int DependencyCount,
    int BranchCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);
