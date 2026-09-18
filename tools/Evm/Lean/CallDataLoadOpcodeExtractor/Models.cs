// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.CallDataLoadOpcodeExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int OpcodeCount,
    int SpecializationCount,
    int AdmissionCount);

internal sealed record OpcodeSemantics(
    string Operation,
    string GasClass,
    int AccessWidth,
    string OffsetRule,
    string PaddingRule,
    string StackTraceOrder,
    string TraceRule);

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    string HandlerBody,
    string HandlerTarget,
    int StackInputs,
    int StackOutputs,
    int StackGrowth,
    int PushSize,
    bool HasCheckedBody,
    bool UsesVm,
    bool ReplacesTop,
    bool EndsInstructionTrace,
    string Activation,
    OpcodeSemantics Semantics,
    string[] EffectOrder);

internal sealed record DispatchTableBinding(string Name, string TracingFlag, string CancelableFlag);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ClosedRoot);

internal sealed record GasScheduleDescriptor(
    ulong VeryLow,
    int WordBytes,
    int MaxInt32,
    ulong MaxUInt64,
    string UInt256Modulus);

internal sealed record OuterFailureDescriptor(
    bool ClearGasOnOutOfGas,
    bool RetainChargedGasOnUnderflow,
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
