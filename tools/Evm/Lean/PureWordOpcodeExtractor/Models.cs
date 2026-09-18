// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PureWordOpcodeExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int OpcodeCount,
    int SpecializationCount);

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    string BodyType,
    string InstructionFamily,
    string OperationType,
    string WordSemantics,
    string FixedGasFamily,
    ulong FixedGas,
    int StackInputs,
    int StackGrowth,
    string ActivationEvidence);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ContinuableFlag,
    string ClosedRoot);

internal sealed record DispatchTableBinding(
    string Name,
    string TracingFlag,
    string CancelableFlag);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Root,
    string GasPolicy,
    string Fork,
    ulong ExpByteGas,
    IReadOnlyList<OpcodeDescriptor> Opcodes,
    IReadOnlyList<OpcodeSpecialization> Specializations,
    IReadOnlyList<string> OpenExtractionObligations);

internal sealed record SourceEntry(string Path, string Sha256, string RoslynSyntaxSha256);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string Root,
    string CombinedSha256,
    IReadOnlyList<SourceEntry> Sources);
