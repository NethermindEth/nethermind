// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int OperationCount,
    int EffectCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);

internal sealed record SourceSpec(string Path, string Role, string ExpectedSha256);

internal sealed record DependencySpec(string Path, string Role, string ExpectedSha256);

internal sealed record SourceIdentity(string Path, string Role, string Sha256);

internal sealed record DependencyIdentity(string Path, string Role, string Sha256);

/// <summary>One Roslyn-bound source admission node and its resolved operation/symbol identity.</summary>
/// <remarks>The typed AST is retained as executable-lowering evidence, not only as a source hash.</remarks>
internal sealed record SourceBinding(
    string Path,
    string Owner,
    string Member,
    string Role,
    string NodeKind,
    string OperationKind,
    string CanonicalSyntax,
    string TokenSha256,
    string CanonicalSyntaxSha256,
    string ContainingMember,
    string Receiver,
    string TargetSymbol,
    string TargetSymbolIdentity,
    bool SymbolResolved,
    int StatementOrdinal,
    string ControlFlowPath,
    int ControlFlowBlock,
    bool IsReachable,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    TypedAstNode TypedAst);

/// <summary>Closed, symbol-resolved Roslyn operation tree used by the bounded model admission.</summary>
/// <remarks>Method bindings contain the lowered body operation; invocation/condition bindings contain the selected operation itself. The tree proves identity/order admission, not production body execution.</remarks>
internal sealed record TypedAstNode(
    string Kind,
    string Name,
    string Symbol,
    string Constant,
    TypedAstNode[] Children,
    string Type = "",
    string ParameterName = "",
    string ArgumentKind = "",
    string RefKind = "",
    /// <summary>Fully qualified Roslyn identity of the node's resolved type.</summary>
    string TypeIdentity = "");

internal enum CompletionStageKind
{
    Metrics,
    StateCharge,
    ValueAndRecipient,
    ActionStart,
    OogForfeit,
    TransferLog,
    Substate,
    ActionEnd,
    RefundSettlement,
    AccessReport,
    HeaderAndFees,
    Finalize,
    ReceiptContinuation,
}

internal sealed record StageShape(
    string Id,
    int Ordinal,
    CompletionStageKind Kind,
    string Contract,
    SourceBinding Binding);

internal sealed record BranchShape(
    string Id,
    int Ordinal,
    string Condition,
    string Meaning,
    PredicateKind Predicate,
    bool UsesNonSelf,
    bool UsesNotOutOfGas,
    SourceBinding Binding);

internal enum PredicateKind
{
    NewAccountCharge,
    RecipientWrites,
    Actions,
    TransferLogs,
    NormalCounters,
    StateCounters,
    Restore,
    Commit,
    BuildUp,
    Receipt,
}

internal sealed record EffectShape(
    string Id,
    int Ordinal,
    string Kind,
    string[] Reads,
    string[] Writes,
    string FailureVisibility,
    SourceBinding Binding);

internal sealed record AdapterPremise(
    string Id,
    string Kind,
    string SourcePath,
    string SourceMember,
    string Projection,
    string CoherencePredicate,
    SourceBinding[] Bindings);

internal sealed record OperationShape(
    string Id,
    int Ordinal,
    string Owner,
    string Member,
    string Formula,
    string[] Inputs,
    string[] Outputs,
    SourceBinding Binding,
    OperationLowering Lowering);

/// <summary>Exact typed invocation grammar consumed by the generated model operation.</summary>
/// <remarks>
/// The grammar retains the resolved receiver, return type, parameter names/types, and argument
/// kinds. It is deliberately separate from the human-readable formula name: a same-named overload,
/// receiver, or reordered argument is a different lowering and is rejected by the extractor.
/// </remarks>
internal sealed record OperationLowering(
    string Formula,
    string Grammar,
    string TargetSymbolIdentity,
    string Receiver,
    string ReturnType,
    string[] ArgumentNames,
    string[] ArgumentTypes,
    string[] ArgumentKinds,
    /// <summary>Closed Lean expression selected from the resolved invocation grammar.</summary>
    /// <remarks>
    /// This is executable lowering for the bounded model, not a production-body proof. The emitter
    /// consumes it when building the generated operation definitions; source metadata is never
    /// consulted by the transition.
    /// </remarks>
    string ExecutionTerm);

internal sealed record RouteShape(
    string StandardProcessorRegistration,
    string StandardWorldRegistration,
    string StandardCodeRegistration,
    string ProcessorInheritance,
    string SettlementIdentity,
    string[] ExcludedRoutes,
    SourceBinding[] Bindings);

internal sealed record CompletionShape(
    string Handoff,
    string ReceiptMode,
    StageShape[] Stages,
    BranchShape[] Branches,
    EffectShape[] Effects,
    OperationShape[] Operations,
    AdapterPremise[] Adapters,
    CompletionLowering Lowering,
    SourceBinding[] MethodBindings,
    SourceBinding[] RouteBindings);

internal sealed record CompletionLowering(
    bool CombineBlockGasUsesMax,
    bool EffectiveBlockGasUsesFallback,
    bool FinalizeBuildUpUsesExactEquality,
    bool FastToStringUsesEnumName,
    bool AccessRequiresHotStorage,
    bool AccessUsesTxAccessList,
    bool AccessUsesCoinbase,
    bool AccessUsesRecipient,
    bool AccessUsesSender,
    bool AccessSetDeduplicates,
    string TransferLogPayloadGrammar,
    string TransferLogAddress,
    string TransferSignature,
    long NewAccountStateCost,
    int NoFrameGotoCount,
    bool NoFrameCfgProven,
    bool FailContractCreateBypassesNoFrame,
    string[] NoFrameCfgPaths);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    string SourceClosureSha256,
    SourceIdentity[] Sources,
    DependencyIdentity[] Dependencies,
    RouteShape Route,
    CompletionShape Completion,
    string[] ArithmeticRules,
    string[] ExternalCorrespondence,
    string[] Exclusions);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record Manifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    string SourceClosureSha256,
    SourceIdentity[] Sources,
    DependencyIdentity[] Dependencies,
    SourceBinding[] Bindings,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string SemanticIrSha256);

internal sealed record SourceFile(
    string RelativePath,
    string Role,
    string Sha256,
    byte[] Bytes,
    SyntaxTree Tree,
    CompilationUnitSyntax Root);
