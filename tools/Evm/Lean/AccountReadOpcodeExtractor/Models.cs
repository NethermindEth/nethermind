// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.AccountReadOpcodeExtractor;

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    string HandlerRoute,
    string ValueRule,
    int StackInputs,
    int StackOutputs,
    bool PopsBeforeGas,
    bool ChargesSecondRead,
    string EffectOrder);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ClosedRoot);

internal sealed record ReachabilityDescriptor(
    string Interface,
    string Implementation,
    string ClosedVm,
    string GasPolicy,
    string WorldState,
    string CodeRepository,
    string PrecompileProvider,
    string CodeCache,
    string Fork,
    string DispatchRoot,
    string BuildSelection);

internal sealed record ScheduleDescriptor(
    int AccountReadBase,
    int ColdAccountAccess,
    int WarmAccess,
    int CopyWord,
    int MemoryLinear,
    int MemoryQuadraticDivisor,
    int VeryLow);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    ReachabilityDescriptor Reachability,
    ScheduleDescriptor Schedule,
    OpcodeDescriptor[] Opcodes,
    OpcodeSpecialization[] Specializations,
    string[] OpenExtractionObligations);

internal sealed record NodeFingerprint(string Identity, string SourcePath, string SyntaxKind, string Sha256);
internal sealed record SourceIdentity(string Path, string Sha256);
internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    NodeFingerprint[] Admissions,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string[] SemanticBindings);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int OpcodeCount,
    int SpecializationCount,
    int SourceCount,
    int AdmissionCount);

internal sealed class ExtractionException(string message) : Exception(message);
