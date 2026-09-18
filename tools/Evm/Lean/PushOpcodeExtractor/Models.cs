// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PushOpcodeExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int OpcodeCount,
    int SpecializationCount);

internal sealed record ReachabilityDescriptor(
    string Service,
    string ConcreteVm,
    string ClosedVm,
    string GasPolicy,
    string Fork,
    string Push0Activation,
    string BuildSelection,
    string DispatchRoot);

internal sealed record OpcodeDescriptor(
    string Name,
    string Instruction,
    int OpcodeByte,
    int ImmediateBytes,
    string Handler,
    string Wrapper,
    int FixedGas,
    int StackInputs,
    int StackGrowth,
    bool HasCheckedBodyWhenUntraced,
    bool TerminalPushElisionWhenUntraced,
    string[] OrderedEffects);

internal sealed record DispatchSpecialization(
    string Opcode,
    string Table,
    string TracingFlag,
    string CancellationFlag,
    string ClosedRoot);

internal sealed record Push2FusionDescriptor(
    int JumpOpcodeByte,
    int JumpiOpcodeByte,
    int JumpdestOpcodeByte,
    int JumpGas,
    int JumpiGas,
    int JumpdestGas,
    string Eligibility,
    string StackLimitPrecheck,
    string TerminalRule,
    string JumpRule,
    string JumpiRule,
    string DestinationRule,
    string[] OrderedEffects);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    ReachabilityDescriptor Reachability,
    OpcodeDescriptor[] Opcodes,
    DispatchSpecialization[] Specializations,
    int StackLimit,
    Push2FusionDescriptor Push2Fusion,
    string[] ForkLineage,
    string[] OpenExtractionObligations);

internal sealed record SourceIdentity(string Path, string Sha256);

internal sealed record AdmissionIdentity(string Key, string SourceSha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string CompilerVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    AdmissionIdentity[] Admissions,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSha256,
    string[] SemanticBindings);
