// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.OrdinaryTransactionMachineExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int BindingCount,
    int ControlFlowCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);

internal sealed record SourceSpec(string Path, string Role);

internal sealed record SourceIdentity(string Path, string Role, string Sha256, string SyntaxSha256);

internal sealed record DependencyIdentity(string Path, string Role, string Sha256);

internal sealed record SourcePin(string Path, string Role, string Sha256);

internal sealed record CompilerReferencePin(string Path, string AssemblyName, string Sha256);

internal sealed record CompilerReferenceInventory(
    int SchemaVersion,
    int Count,
    string AggregateSha256,
    CompilerReferencePin[] References);

internal sealed record CompilerReferenceIdentity(
    string Path,
    string AssemblyName,
    string Sha256,
    string Mvid,
    bool Selected,
    string[] Dependencies);

/// <summary>Typed identity and operation evidence for one admitted Roslyn node.</summary>
/// <remarks>
/// The CFG and data-flow fields are source evidence. They do not execute C# and do not make an
/// opaque adapter into a proof of its implementation.
/// </remarks>
internal sealed record TypedBinding(
    string Path,
    string Owner,
    string Member,
    string NodeKind,
    string CanonicalSyntax,
    string SyntaxSha256,
    string SymbolId,
    string SymbolKind,
    string OperationKind,
    string OperationType,
    string ReceiverType,
    string TargetSymbol,
    string ContainingMember,
    int Position,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int ControlFlowBlock,
    bool IsReachable,
    bool DataFlowSucceeded,
    bool IsErrorSymbol,
    bool HasCandidateSymbols,
    string CandidateReason,
    string[] ReadInside,
    string[] WrittenInside,
    string[] ReadOutside,
    string[] WrittenOutside);

internal sealed record Anchor(
    string Id,
    string Relation,
    TypedBinding Binding,
    string[] RequiredPredecessors,
    string[] RequiredSuccessors,
    string PathProof);

internal sealed record ControlFlowIdentity(
    string Id,
    string Path,
    string Owner,
    string Member,
    int[] ReachableBlocks,
    string[] NormalEdges,
    int[] NormalExitBlocks,
    int[] ExceptionalExitBlocks,
    string[] DominanceFacts);

/// <summary>Opaque normal-return premise attached to one source-bound effect boundary.</summary>
/// <remarks>
/// The generated model may use the observation only when this premise is supplied. It is not
/// an assertion that Roslyn executed the call or proved the callee's implementation.
/// </remarks>
internal sealed record NormalReturnPremise(
    string Id,
    string AnchorId,
    string Path,
    string Owner,
    string Member,
    string OutcomeType,
    string Condition);

/// <summary>One source-bound operation retained as semantic IR for deterministic lowering.</summary>
/// <remarks>
/// The operation is copied from a typed Roslyn anchor. Its source text, target, receiver, and
/// data-flow sets are evidence for the selected handoff; they are not an interpreter for C#.
/// </remarks>
internal sealed record SemanticOperation(
    string Id,
    string AnchorId,
    string Path,
    string Owner,
    string Member,
    string OperationKind,
    string OperationType,
    string ReceiverType,
    string TargetSymbol,
    string CanonicalSyntax,
    string[] ReadInside,
    string[] WrittenInside,
    string[] ReadOutside,
    string[] WrittenOutside);

/// <summary>Field-by-field source handoff used by the generated adapter boundary.</summary>
/// <remarks>
/// A seam names both source and model projections explicitly. It never relies on production
/// record equality or an opaque whole-object comparison.
/// </remarks>
internal sealed record FieldwiseHandoff(
    string Id,
    string AnchorId,
    string SourceMember,
    string SourceField,
    string ModelType,
    string ModelField,
    string Projection);

internal sealed record ReceiptTerminalSourceClosureIdentity(
    string SourcePinsPath,
    string SourcePinsSha256,
    string SourceManifestPath,
    string SourceManifestSha256,
    string CompilerReferenceInventoryPath,
    string CompilerReferenceInventorySha256,
    SourcePin[] Sources,
    SourcePin[] BindingSources);

internal sealed record RoutePremise(
    string ProcessorRegistration,
    string BaseType,
    string ExecutorRegistration,
    string DirectInnerGuard,
    string BalEnabledMember,
    string ParallelDecorator,
    string ForkIdentity,
    string BaseTracerType,
    string[] ExcludedRoutes,
    Anchor[] Bindings);

internal sealed record OptionShape(
    int None,
    int Commit,
    int Restore,
    int SkipValidation,
    int Warmup,
    int BuildUp,
    string ExactTarget,
    Anchor Binding);

internal sealed record MachineShape(
    string Entry,
    string SuccessDomain,
    string[] Stages,
    string[] Branches,
    string[] Effects,
    Anchor[] Anchors,
    ControlFlowIdentity[] ControlFlows,
    RoutePremise Route,
    OptionShape Options,
    string[] ExcludedBranches,
    NormalReturnPremise[] NormalReturnPremises,
    string[] FieldwiseHandoffs,
    SemanticOperation[] SemanticOperations,
    FieldwiseHandoff[] FieldwiseSeams,
    ReceiptTerminalSourceClosureIdentity ReceiptTerminalSourceClosure);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string SourceClosureSha256,
    string CompilerClosureSha256,
    SourceIdentity[] Sources,
    DependencyIdentity[] Dependencies,
    CompilerReferenceIdentity[] CompilerReferences,
    OptionShape Options,
    RoutePremise Route,
    MachineShape Machine,
    string[] ArithmeticRules,
    string[] ExternalCorrespondence,
    string[] OpenObligations);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record Manifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    string SourceClosureSha256,
    string CompilerClosureSha256,
    SourceIdentity[] Sources,
    DependencyIdentity[] Dependencies,
    CompilerReferenceIdentity[] CompilerReferences,
    ReceiptTerminalSourceClosureIdentity ReceiptTerminalSourceClosure,
    TypedBinding[] Bindings,
    ControlFlowIdentity[] ControlFlows,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string SemanticIrSha256,
    string[] OpenObligations);

internal sealed record SourceFile(
    string RelativePath,
    string Role,
    string FullPath,
    byte[] Bytes,
    string Sha256,
    string SyntaxSha256,
    SyntaxTree Tree,
    CompilationUnitSyntax Root);

internal sealed record CompilerClosure(
    MetadataReference[] References,
    CompilerReferenceIdentity[] Identities,
    string InventorySha256,
    string AggregateSha256);
