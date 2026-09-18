// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.StackRearrangementOpcodeExtractor;

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
    string HandlerRoot,
    string DispatchAssignment,
    string GasClass,
    int FixedGas,
    int StackInputs,
    int StackGrowth,
    int OperandDepth,
    string StackEffect,
    string PcOrder,
    string GasOrder,
    string FaultOrder,
    string Activation,
    string Fork,
    string GasPolicy,
    string VmRoot);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ContinuableFlag,
    string ClosedRoot,
    string HandlerRoot);

internal sealed record DispatchTableBinding(
    string Name,
    string TracingFlag,
    string CancelableFlag);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string Root,
    string GasPolicy,
    string Fork,
    string BuildFlavor,
    string DiClosure,
    string ForkClosure,
    string PcOrder,
    string GasOrder,
    string FaultOrder,
    int StackLimit,
    int PopGas,
    int RearrangementGas,
    IReadOnlyList<DispatchTableBinding> DispatchTables,
    IReadOnlyList<string> ForkLineage,
    IReadOnlyList<string> OpenExtractionObligations,
    IReadOnlyList<OpcodeDescriptor> Opcodes,
    IReadOnlyList<OpcodeSpecialization> Specializations);

internal sealed record SourceIdentity(
    string Path,
    string Sha256,
    string RoslynSyntaxSha256);

internal sealed record RawSourceIdentity(string Path, string Sha256);

internal sealed record AdmissionIdentity(
    string Key,
    string TemplateSha256,
    string SourceSyntaxSha256);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string IrSha256,
    string LeanSha256,
    string CombinedSourceSha256,
    IReadOnlyList<SourceIdentity> Sources,
    IReadOnlyList<RawSourceIdentity> RawSources,
    IReadOnlyList<AdmissionIdentity> Admissions);

internal sealed record MemberSelector(
    string Kind,
    string Name,
    int GenericArity = 0,
    int ParameterCount = 0,
    string ParameterTypes = "");

internal sealed record AdmissionSpec(
    string SourcePath,
    string ResourceStem,
    string Namespace,
    string? OwnerName,
    int OwnerGenericArity,
    IReadOnlyList<MemberSelector> Members);

internal sealed record SourceFile(
    string Path,
    string FullPath,
    byte[] Bytes,
    Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax Syntax);

internal sealed record OpcodeSeed(
    string Name,
    string Instruction,
    int ExpectedByte,
    string HandlerBody,
    string GasClass,
    int FixedGas,
    int StackInputs,
    int StackGrowth,
    int OperandDepth,
    string StackEffect);

internal sealed record ReviewedSource(
    string Path,
    string Sha256,
    string? RoslynSyntaxSha256);
