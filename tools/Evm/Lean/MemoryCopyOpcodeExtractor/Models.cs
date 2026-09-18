// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.MemoryCopyOpcodeExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int OpcodeCount,
    int SpecializationCount,
    int AdmissionCount);

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    string HandlerBody,
    string HandlerTarget,
    int StackInputs,
    int StackOutputs,
    string HasCheckedBody,
    string Activation,
    OpcodeSemantics Semantics,
    string[] EffectOrder);

internal sealed record OpcodeSemantics(
    string Operation,
    string GasClass,
    int AccessWidth,
    string CopySource,
    string TraceRule);

internal sealed record DispatchTableBinding(string Name, string TracingFlag, string CancelableFlag);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ClosedRoot);

internal sealed record GasScheduleDescriptor(
    ulong VeryLow,
    ulong Base,
    ulong CopyWord,
    ulong MemoryLinear,
    ulong MemoryQuadraticDivisor,
    ulong MaxMemorySize,
    ulong MaxUInt64,
    ulong MaxUInt32,
    string UInt256Modulus);

internal sealed record ActivationDescriptor(bool Eip211, bool Eip5656);

internal sealed record OuterFailureDescriptor(
    bool ClearGasOnOutOfGas,
    bool FinishBeforeError,
    bool DecrementFaultPc);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string ClosedVm,
    string GasPolicy,
    string Fork,
    string StandardBuildSelector,
    GasScheduleDescriptor Schedule,
    ActivationDescriptor Activation,
    OuterFailureDescriptor OuterFailure,
    DispatchTableBinding[] DispatchTables,
    OpcodeDescriptor[] Opcodes,
    OpcodeSpecialization[] Specializations,
    string[] ForkLineage,
    string[] OpenExtractionObligations);

internal sealed record SourceIdentity(string Path, string Sha256, string RoslynSyntaxSha256);

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
