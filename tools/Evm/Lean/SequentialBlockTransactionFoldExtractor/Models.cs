// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int AnchorCount,
    int ControlFlowCount);

internal sealed record SourceIdentity(
    string Path,
    string Role,
    string Sha256,
    string SyntaxSha256);

/// <summary>Roslyn identity for a source declaration or invocation admitted by the fold.</summary>
/// <remarks>
/// The operation and CFG fields are evidence attached to the exact source node. They are not a
/// claim that Roslyn, the CLR, or the production dependency graph has been executed.
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
    string ContainingMember,
    int Position,
    int StartLine,
    int StartColumn,
    int ControlFlowBlock,
    bool IsReachable,
    bool HasCandidateSymbols,
    string CandidateReason,
    bool IsErrorSymbol,
    bool DataFlowSucceeded,
    string[] ReadInside,
    string[] WrittenInside,
    string ReceiverSymbol,
    string[] ArgumentBindings);

internal sealed record MemberIdentity(
    string Id,
    string Path,
    string Owner,
    string Name,
    int ParameterCount,
    string Signature,
    TypedBinding Binding);

internal sealed record AnchorIdentity(
    string Id,
    string Path,
    string Owner,
    string CanonicalSyntax,
    string Relation,
    TypedBinding Binding);

internal sealed record ControlFlowIdentity(
    string Id,
    string Path,
    string Owner,
    string Member,
    int[] ReachableBlocks,
    string[] Edges,
    int[] NormalExitBlocks,
    int[] ExceptionalExitBlocks,
    string[] BackEdges,
    TypedBinding Binding);

internal sealed record ConditionalSignalIdentity(
    TypedBinding Evaluation,
    int WhenNullBlock,
    int WhenNotNullBlock);

internal sealed record TransactionProjectionIdentity(
    string LocalSymbol,
    string TransactionType,
    string TransactionAssembly,
    string InitializerOperation,
    string TransactionsProperty,
    string BlockParameter,
    string IndexLocal,
    string HelperParameter,
    string HelperArgumentLocal,
    bool InitializerIdentityConversion,
    bool HelperArgumentIdentityConversion,
    string InitializerOperatorMethod,
    string HelperArgumentOperatorMethod);

internal sealed record SynchronousCallableIdentity(
    string Id,
    string Symbol,
    string MethodKind,
    string BodyKind,
    bool ReturnsVoid,
    bool IsAsync,
    bool IsIterator,
    bool IsPartial,
    bool IsExtern,
    bool IsAbstract,
    bool HasConditionalAttribute,
    CallableAttributeIdentity[] Attributes,
    CallableLineageIdentity Lineage);

internal sealed record CallableAttributeIdentity(string Type, string Assembly, bool HasArguments);

internal sealed record CallableTypeIdentity(string Symbol, string Assembly);

internal sealed record CallableLineageIdentity(
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    string[] OverriddenMethods,
    bool HasInheritedConditionalAttribute,
    CallableAttributeIdentity[] InheritedAttributes,
    CallableTypeIdentity DeclaringType,
    CallableTypeIdentity[] BaseTypes,
    CallableTypeIdentity[] Interfaces,
    string[] ImplementedInterfaceMethods);

internal sealed record CompositionIdentity(
    string GeneratedReceiptKernel,
    string GeneratedReceiptKernelSha256,
    string ReceiptRefinement,
    string ReceiptRefinementSha256,
    string Relation,
    string[] RequiredTerminalFields,
    ReceiptTerminalSourceClosureIdentity ReceiptTerminalSourceClosure);

internal sealed record ReceiptTerminalSourceClosureIdentity(
    string SourcePinsPath,
    string SourcePinsSha256,
    string SourceManifestPath,
    string SourceManifestSha256,
    string CompilerReferenceInventoryPath,
    string CompilerReferenceInventorySha256,
    SourcePin[] Sources,
    SourcePin[] BindingSources,
    int SourcePinsSchemaVersion,
    int ManifestSchemaVersion,
    string ReceiptExtractorVersion,
    DependencyArtifact[] Artifacts);

internal sealed record DependencyArtifact(string Path, string Sha256);

internal sealed record CompilerReferenceClosureIdentity(
    string InventoryPath,
    string InventorySha256,
    int Count,
    string AggregateSha256);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string[] Included,
    string[] Excluded,
    SourceIdentity[] Sources,
    AuxiliarySourceIdentity[] AuxiliarySources,
    AuxiliarySourceIdentity[] SupportSources,
    CompilerReferenceClosureIdentity CompilerReferences,
    MemberIdentity[] Members,
    AnchorIdentity[] Anchors,
    AuxiliaryAnchorIdentity[] AuxiliaryAnchors,
    ControlFlowIdentity[] ControlFlows,
    ConditionalSignalIdentity TransactionsExecuted,
    TransactionProjectionIdentity TransactionProjection,
    SynchronousCallableIdentity[] SynchronousCallables,
    CompositionIdentity Composition,
    SourceRouteIdentity SourceRoute,
    string[] OpenObligations);

internal sealed record SourcePin(string Path, string Role, string Sha256);

internal sealed record SourcePinDocument(int SchemaVersion, SourcePin[] Sources, SourcePin[] AuxiliarySources,
    SourcePin[] SupportSources);

internal sealed record AuxiliarySourceIdentity(string Path, string Role, string Sha256);

/// <summary>Compact typed operation evidence for a source node outside the generated Lean kernel.</summary>
/// <remarks>
/// The extractor fills these fields from Roslyn <see cref="Microsoft.CodeAnalysis.IOperation"/> and
/// refuses invalid operations, candidate symbols, and error symbols. The compact form ties the
/// checked-in artifact to the admitted source's CFG block and reachability while retaining the
/// exact source token identity and operation shape. The containing member is recorded separately so
/// a local function or lambda cannot be mistaken for the declared method owner.
/// </remarks>
internal sealed record AuxiliaryAnchorIdentity(
    string Id,
    string Path,
    string Owner,
    string Member,
    string CanonicalSyntax,
    string Relation,
    string NodeKind,
    string OperationKind,
    string OperationType,
    string TargetSymbol,
    string SyntaxSha256,
    bool HasCandidateSymbols,
    bool IsErrorSymbol,
    int ControlFlowBlock,
    bool IsReachable,
    string ContainingMember,
    string ReceiverType,
    string ReceiverSymbol,
    string[] ArgumentBindings,
    AssignmentValueBinding[] RightHandSideBindings);

internal sealed record AssignmentValueBinding(
    string OperationKind,
    string Symbol,
    string Type,
    string ReceiverType,
    string ReceiverSymbol,
    int ParameterOrdinal);

internal sealed record SourceRouteIdentity(
    string ExecutorPath,
    string ExecutorOwner,
    string ExecutorMember,
    int ExecutorParameterCount,
    string ExecutorSignature,
    bool ExecutorIsNonVirtual,
    string BalManagerPath,
    string BalEnabledMember,
    string ParallelDecoratorPath,
    string BaseRegistration,
    string DecoratorRegistration);

internal sealed record CompilerReferenceIdentity(string Path, string AssemblyName, string Sha256,
    string Mvid, bool Selected);

internal sealed record CompilerReferenceInventory(
    int SchemaVersion,
    int Count,
    string AggregateSha256,
    CompilerReferenceIdentity[] References);

internal sealed record SourceFile(
    string RelativePath,
    string Role,
    string FullPath,
    byte[] Bytes,
    string Sha256,
    string SyntaxSha256,
    Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax Root,
    Microsoft.CodeAnalysis.SyntaxTree Tree);

internal sealed record CompilerContext(
    Microsoft.CodeAnalysis.Compilation Compilation,
    CompilerReferenceIdentity[] References,
    IReadOnlyDictionary<string, Microsoft.CodeAnalysis.SemanticModel> Models);
