// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EvmFrameMachineExtractor;

internal class ExtractionException(string message) : Exception(message);

internal sealed class IncompleteProfileException(string message) : ExtractionException(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int OpcodeRouteCount,
    int SourceCount,
    int AdmissionCount);

internal sealed record ProductionSourceDescriptor(
    string Path,
    string Role,
    bool IsRaw,
    string? ExpectedSha256,
    string? ExpectedRoslynSyntaxSha256);

internal sealed record MemberAdmissionDescriptor(
    string SourcePath,
    string Namespace,
    string OwnerPath,
    string MemberKind,
    string MemberName,
    int MemberGenericArity,
    string ParameterTypes,
    string? ExpectedSyntaxKind,
    string? ExpectedSha256);

internal sealed record OpcodePackageDescriptor(
    string Name,
    string ManifestPath,
    string LeanModule,
    string ProofModulePath,
    ProofTheoremIdentity[] RequiredTheorems,
    string? ProofModuleSha256,
    int DeclaredOpcodeCount,
    bool Admitted,
    string? ManifestSha256);

internal sealed record ProofTheoremIdentity(
    string FullyQualifiedName,
    string? SignatureSha256);

internal sealed record OpcodeRouteDescriptor(
    string DispatchTable,
    int Byte,
    string Instruction,
    string RouteKind,
    string ActivationRule,
    string Package,
    string ClosedHandlerRoot,
    bool Admitted);

internal sealed record PrecompileRouteDescriptor(
    string Name,
    int Address,
    string ActivationRule,
    string ProviderRoot,
    string WrapperManifestPath,
    string LeanModule,
    string? FullyQualifiedTheorem,
    bool Admitted,
    string? WrapperManifestSha256);

internal sealed record FrameBoundaryDescriptor(
    string TransactionRoot,
    string ClosedGasPolicy,
    string Fork,
    string[] DispatchTables,
    string[] FramePhases,
    string[] ChildExitKinds,
    string[] TopLevelExitKinds);

internal sealed record FrameExitBindingDescriptor(string ExitKind, string MergeOperation);

internal sealed record FrameDriverDescriptor(
    string[] DispatchOrder,
    string[] FreshFrameEntryOrder,
    string ParentDiscipline,
    FrameExitBindingDescriptor[] ChildExitBindings,
    string[] TopLevelCloseOrder,
    string[] SettlementDiscriminants,
    string[] TransactionWideFields,
    string[] FailureOrigins,
    string[] SuccessSettlementOrder,
    string[] RevertSettlementOrder,
    string[] HandleExceptionOrder,
    string[] HandleFailureOrder,
    string[] NestedPrecompileFailureOrder,
    string[] DirectPrecompileFailureOrder,
    string[] CodeDepositFailureOrder);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    FrameBoundaryDescriptor Boundary,
    FrameDriverDescriptor FrameDriver,
    ProductionSourceDescriptor[] Sources,
    MemberAdmissionDescriptor[] Admissions,
    OpcodePackageDescriptor[] OpcodePackages,
    OpcodeRouteDescriptor[] OpcodeRoutes,
    PrecompileRouteDescriptor[] PrecompileRoutes,
    string[] SemanticBindings,
    string[] OpenExtractionObligations,
    string[] IncompleteGates);

internal sealed record SourceIdentity(string Path, string Sha256, string? RoslynSyntaxSha256);

internal sealed record AdmissionIdentity(
    string SourcePath,
    string Namespace,
    string OwnerPath,
    string MemberKind,
    string MemberName,
    int MemberGenericArity,
    string ParameterTypes,
    string SyntaxKind,
    string Sha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record DependencyIdentity(
    string Kind,
    string Name,
    string Path,
    string Sha256,
    string? ProofModule,
    string? ProofModulePath,
    string? ProofModuleSha256,
    ProofTheoremIdentity[] RequiredTheorems,
    bool Admitted);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    AdmissionIdentity[] Admissions,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    DependencyIdentity[] Dependencies,
    string[] SemanticBindings);
