// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.FrameJournalExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int DependencyCount);

internal sealed record OperationDescriptor(
    string Name,
    string Effect,
    string AdmissionBoundary,
    string BindingKind,
    string SourcePath,
    string ContainingType,
    string Member,
    string Signature,
    string[] OrderedEffects);

internal sealed record ScopeDescriptor(
    string Fork,
    string ExecutionMode,
    string FrameBoundary,
    string[] RestoredSurfaces,
    string[] TransactionWideSurfaces,
    string[] Exclusions,
    string[] Premises);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    ScopeDescriptor Scope,
    OperationDescriptor[] Operations,
    string[] SourceChains,
    string[] DependencyTheorems,
    ImportedKernelDescriptor[] ImportedKernels);

internal sealed record ImportedKernelDescriptor(
    string Name,
    string ManifestPath,
    string ArtifactPath);

internal sealed record SourceIdentity(string Path, string Sha256);

internal sealed record MemberIdentity(
    string SourcePath,
    string ContainingType,
    string Member,
    string Signature,
    string CanonicalSha256);

internal sealed record TheoremIdentity(string FullyQualifiedName, string SignatureSha256);

internal sealed record DependencyIdentity(
    string Name,
    string AdmissionState,
    string IdentityPath,
    string IdentitySha256,
    string ProofModule,
    string ProofPath,
    string ProofSha256,
    TheoremIdentity[] RequiredTheorems);

internal sealed record ImportedKernelIdentity(
    string Name,
    string ManifestPath,
    string ManifestSha256,
    string ArtifactPath,
    string ArtifactSha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string CompilerVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    DependencyIdentity[] Dependencies,
    ImportedKernelIdentity[] ImportedKernels,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string CombinedDependencySha256,
    string CombinedImportedKernelSha256,
    string[] SemanticBindings);
