// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EnvironmentOpcodeExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int OpcodeCount,
    int SpecializationCount,
    int SourceCount);

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    string HandlerRoute,
    string ActivationGate,
    string ValueSelector,
    ulong FixedGas,
    int DispatchStackInputs,
    int DispatchStackGrowth,
    int SemanticPops,
    int SemanticPushes,
    bool ChecksAvailabilityBeforeGas);

internal sealed record OpcodeSpecialization(
    string Opcode,
    string DispatchTable,
    string TracingFlag,
    string CancelableFlag,
    string ClosedRoot);

internal sealed record ReachabilityDescriptor(
    string Service,
    string ConcreteVm,
    string ClosedVm,
    string GasPolicy,
    string Fork,
    string DispatchRoot,
    string[] EnabledForkFlags);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string AncestorBaselineCommit,
    string Kernel,
    ReachabilityDescriptor Reachability,
    OpcodeDescriptor[] Opcodes,
    OpcodeSpecialization[] Specializations,
    string[] ExternalObligations);

internal sealed record NodeFingerprint(string Identity, string Path, string Kind, string Sha256);

internal sealed record SourceIdentity(string Path, string Sha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record Manifest(
    int SchemaVersion,
    string ExtractorVersion,
    string CompilerVersion,
    string LanguageVersion,
    string AncestorBaselineCommit,
    string Kernel,
    SourceIdentity[] Sources,
    NodeFingerprint[] AdmittedNodes,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string[] SemanticBindings);
