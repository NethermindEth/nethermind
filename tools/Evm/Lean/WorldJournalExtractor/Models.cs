// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.WorldJournalExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount);

internal sealed record OperationDescriptor(
    string Name,
    string Surface,
    string Observation,
    bool Journaled,
    string SourcePath,
    string ContainingType,
    string Member,
    string Signature,
    string[] OrderedEffects);

internal sealed record SnapshotDescriptor(
    string[] RestoredSurfaces,
    string[] TransactionWideSurfaces,
    string PositionConvention,
    string LeanPositionNormalization,
    string RestoreOrder);

internal sealed record ScopeDescriptor(
    string WorldState,
    string ExecutionMode,
    string FrameScope,
    string CommitBoundary,
    string[] Exclusions,
    string[] UnextractedPremises);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    ScopeDescriptor Scope,
    SnapshotDescriptor Snapshot,
    OperationDescriptor[] Operations);

internal sealed record SourceIdentity(string Path, string Sha256);

internal sealed record MemberIdentity(
    string SourcePath,
    string ContainingType,
    string Member,
    string Signature,
    string CanonicalSha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string CompilerVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string[] SemanticBindings);
