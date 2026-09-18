// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.CallCreateOpcodeExtractor;

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
    string Family,
    string HandlerBody,
    string HandlerTarget,
    int StackInputs,
    int StackOutputs,
    bool Terminates,
    bool OwnsPreChildTraceEnd,
    OpcodeSemantics Semantics,
    string[] EffectOrder);

internal sealed record OpcodeSemantics(
    string Operation,
    string ValueRule,
    string TargetRule,
    string ResultRule);

internal sealed record DispatchTableBinding(string Name, string TracingFlag, string CancelableFlag);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ClosedRoot);

internal sealed record GasScheduleDescriptor(
    ulong CallBase,
    ulong CallValue,
    ulong CallStipend,
    ulong WarmAccess,
    ulong ColdAccess,
    ulong CreateAccess,
    ulong InitCodeWord,
    ulong Create2HashWord,
    ulong AccountWrite,
    ulong SelfDestructBase,
    long NewAccountState,
    long CreateState,
    ulong CodeDepositExecutionPerWord,
    long CodeDepositStatePerByte,
    ulong MemoryLinear,
    ulong MemoryQuadraticDivisor,
    ulong MaxMemorySize,
    ulong MaxInitCodeSize,
    ulong MaxCodeSize,
    int MaxCallDepth,
    int StackLimit,
    string UInt256Modulus);

internal sealed record ActivationDescriptor(
    bool Eip150,
    bool Eip158,
    bool Eip2929,
    bool Eip3860,
    bool Eip6780,
    bool Eip7702,
    bool Eip7708,
    bool Eip8037,
    bool Eip8038,
    bool Eip8246);

internal sealed record OuterFailureDescriptor(
    bool ClearExecutionGasOnOutOfGas,
    bool RestoreJournalsOnChildFailure,
    bool BurnChildExecutionOnException,
    bool ReturnChildExecutionOnRevert,
    bool PushResultBeforeResume,
    bool ReportResultAfterPush);

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
