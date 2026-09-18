// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.Keccak256OpcodeExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int AdmissionCount,
    int SpecializationCount);

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    string HandlerBody,
    string HandlerTarget,
    int ProgramCounterDelta,
    int OpcodeCountDelta,
    int StackInputs,
    int StackOutputs,
    bool HasCheckedBody,
    string PushDepthFlag,
    string[] EffectOrder);

internal sealed record GasScheduleDescriptor(
    ulong Base,
    ulong Word,
    ulong MemoryLinear,
    ulong MemoryQuadraticDivisor,
    ulong MaxMemorySize,
    ulong MaxUInt64,
    ulong MaxUInt32);

internal sealed record DispatchTableBinding(
    string Name,
    string TracingFlag,
    string CancellationFlag);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancellationFlag,
    string ClosedRoot);

internal sealed record ReachabilityDescriptor(
    string Service,
    string Implementation,
    string ClosedVm,
    string GasPolicy,
    string Fork,
    string DispatchRoot,
    string BuildSelection,
    string HashBoundary);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    ReachabilityDescriptor Reachability,
    OpcodeDescriptor Opcode,
    GasScheduleDescriptor Schedule,
    DispatchTableBinding[] DispatchTables,
    OpcodeSpecialization[] Specializations,
    string[] ForkLineage,
    string[] OpenExtractionObligations);

internal sealed record SourceIdentity(string Path, string Sha256);

internal sealed record RawSourceIdentity(string Path, string Sha256);

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

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    RawSourceIdentity[] RawSources,
    AdmissionIdentity[] Admissions,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string[] SemanticBindings);

internal sealed record AdmissionSpec(
    string SourcePath,
    string Namespace,
    string OwnerPath,
    string MemberKind,
    string MemberName,
    int MemberGenericArity = 0,
    string ParameterTypes = "");

internal sealed record SourceFile(
    string RelativePath,
    string FullPath,
    byte[] Bytes,
    string Sha256,
    Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax Root);

