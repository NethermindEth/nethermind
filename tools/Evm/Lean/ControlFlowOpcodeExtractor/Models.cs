// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.ControlFlowOpcodeExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int OpcodeCount,
    int SpecializationCount,
    int AdmissionCount);

internal sealed record DispatchTableBinding(string Name, string TracingFlag, string CancelableFlag);

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    string HandlerBody,
    string HandlerKind,
    string HandlerTarget,
    int StackInputs,
    int StackOutputs,
    string HasCheckedBody,
    bool EndsInstructionTrace,
    string Activation,
    string GasClass,
    int FixedGas,
    int TracePushWidth,
    string[] EffectOrder);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ContinuableFlag,
    string EnabledRoot,
    string DisabledRoot);

internal sealed record GasScheduleDescriptor(
    ulong Zero,
    ulong Base,
    ulong Mid,
    ulong High,
    ulong JumpDest,
    ulong MemoryLinear,
    ulong MemoryQuadraticDivisor,
    ulong MaxMemorySize,
    int StackLimit,
    int CancellationCheckMask);

internal sealed record JumpValidationDescriptor(
    int StopOpcode,
    int JumpDestinationOpcode,
    int PushFirstOpcode,
    int PushLastOpcode,
    int MaximumDestination);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string ClosedVm,
    string GasPolicy,
    string Fork,
    string StandardBuildSelector,
    GasScheduleDescriptor Schedule,
    JumpValidationDescriptor JumpValidation,
    DispatchTableBinding[] DispatchTables,
    OpcodeDescriptor[] Opcodes,
    OpcodeSpecialization[] Specializations,
    string[] ForkLineage,
    string[] SemanticBindings,
    string[] OpenExtractionObligations);

internal sealed record SourceIdentity(string Path, string Sha256, string RoslynSyntaxSha256);
internal sealed record RawSourceIdentity(string Path, string Sha256);

internal sealed record AdmissionIdentity(
    string SourcePath,
    string Namespace,
    string OwnerName,
    int OwnerGenericArity,
    string MemberKind,
    string MemberName,
    int MemberGenericArity,
    int ParameterCount,
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

internal sealed record MemberSelector(
    string Kind,
    string Name,
    int GenericArity = 0,
    int ParameterCount = 0,
    string ParameterTypes = "");

internal sealed record AdmissionSpec(
    string SourcePath,
    string Namespace,
    string OwnerName,
    int OwnerGenericArity,
    MemberSelector[] Members);

internal sealed record SourceFile(
    string RelativePath,
    string FullPath,
    byte[] Bytes,
    string Sha256,
    Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax Root);
