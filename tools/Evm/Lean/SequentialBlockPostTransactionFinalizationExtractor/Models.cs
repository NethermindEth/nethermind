// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int AnchorCount,
    int ControlFlowCount);

internal sealed record SourceIdentity(string Path, string Role, string Sha256, string SyntaxSha256);

/// <summary>Typed Roslyn identity attached to one admitted source node.</summary>
/// <remarks>The record is evidence from the pinned source closure; it is not production execution.</remarks>
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
    string[] WrittenInside);

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
    int[] ExceptionHandlerEntries,
    string[] BackEdges,
    ControlFlowBlockMembership[] BlockMemberships,
    ControlFlowTransferIdentity[] Transfers,
    string ShapeSha256,
    TypedBinding Binding);

internal sealed record ControlFlowBlockMembership(int Ordinal, int[] SyntaxStarts);

internal sealed record ControlFlowTransferIdentity(int Source, int Destination, string Semantics,
    int[] FinallyEntries, int[][] FinallyExits);

internal sealed record ConditionalSignalIdentity(TypedBinding Evaluation, int WhenNullBlock, int WhenNotNullBlock);

internal sealed record CompilerClosureIdentity(
    string InventoryPath,
    int Count,
    string AggregateSha256,
    string InventorySha256,
    PublicationDependencyIdentity[] SupportSources);

internal sealed record CompilerReferenceIdentity(string Path, string AssemblyName, string Sha256,
    string Mvid, bool Selected);

internal sealed record CommitIdentity(
    string Id,
    string InvocationAnchorId,
    string Event,
    string MethodId,
    bool CommitRoots,
    string StateProviderCall,
    TypedBinding Binding,
    TypedBinding InvocationBinding);

internal sealed record GuardIdentity(
    string Id,
    string Condition,
    string ExpectedPolarity,
    string SelectedArm,
    string ExcludedArm,
    TypedBinding Binding);

internal sealed record StepIdentity(
    int Ordinal,
    string Id,
    string AnchorId,
    string Branch,
    string Observable,
    TypedBinding Binding);

/// <summary>Typed header assignment and selected-arm evidence for one source write.</summary>
/// <remarks>
/// Excluded write canonicals retain the complete parent assignment while bindings identify the
/// typed target component on an excluded branch, so a normal-tail assignment cannot silently be
/// replaced by a competing write.
/// </remarks>
internal sealed record HeaderAssignmentIdentity(
    string Id,
    string AnchorId,
    string GuardAnchorId,
    string SelectedArm,
    string TargetCanonical,
    string ValueCanonical,
    string[] ExcludedWriteCanonicals,
    TypedBinding AssignmentBinding,
    TypedBinding ValueBinding,
    TypedBinding GuardBinding,
    TypedBinding[] ExcludedBindings);

internal sealed record OpaqueDelegateIdentity(
    string Id,
    string AnchorId,
    bool RequiresNormalReturn,
    string ThrowScope);

/// <summary>Typed source-entry premises carried by the finalization adapter.</summary>
/// <remarks>
/// The adapter closes the pinned static layout and typed identities. It does not prove DI
/// selection or runtime dispatch; those remain explicit premises, together with normal return and
/// no additional modeled effect for named external or unpinned boundaries; source-owned opaque
/// hooks are closed by the pinned-tree effect and callable audits.
/// </remarks>
internal sealed record SourceEntryAdapterIdentity(
    string Id,
    string Claim,
    string EntryMemberId,
    string ExactBaseReceiver,
    string SelectedStandardExecutor,
    string BalPremise,
    string BackgroundPremise,
    string MainThreadPremise,
    string StateRootPremise,
    string SpecPremise,
    TypedBinding EntryBinding,
    TypedBinding ExecutorBinding,
    int ReceiptsReferenceCount,
    int ReceiptsWriteCount,
    TypedBinding ReceiptsBinding,
    TypedBinding BalBinding,
    TypedBinding BackgroundBinding,
    TypedBinding MainThreadBinding,
    TypedBinding StateRootBinding,
    TypedBinding SpecBinding,
    TypedBinding StateRootWriteBinding,
    TypedBinding StateRootValueBinding,
    TypedBinding AccountChangesWriteBinding,
    TypedBinding AccountChangesValueBinding,
    string TransactionsExecutedNormalReturnPremise,
    string PostTransactionCommitNormalReturnPremise,
    string TransactionsExecutedEventSymbol,
    TypedBinding TransactionsExecutedBinding,
    TypedBinding PostTransactionCommitBinding,
    ConditionalSignalIdentity ConditionalSignal,
    TypedBinding HeaderBinding,
    TypedBinding HeaderValueBinding,
    PreservedValueIdentity[] PreservedValues);

internal sealed record PreservedValueIdentity(string Name, string SymbolId, string Type,
    string DefinitionCanonical, string[] ReadContexts, int[] ReadPositions);

/// <summary>Source data-flow evidence for the synchronous null-task arm.</summary>
/// <remarks>The counts are derived from an ILocalSymbol reference/write audit; the booleans are admitted only after CFG reaching-definition checks.</remarks>
internal sealed record TaskFlowIdentity(
    string Variable,
    string LocalSymbol,
    string Initializer,
    string SynchronousArm,
    string EndTracePredicate,
    string BackgroundResultPredicate,
    string FinallyPredicate,
    int BackgroundAssignments,
    int SynchronousAssignments,
    bool NullPreservedInSynchronousArm,
    bool BackgroundResultExcluded,
    bool FinallyObservationExcluded,
    TypedBinding InitializerBinding,
    TypedBinding EndTraceBinding,
    TypedBinding BackgroundResultBinding,
    TypedBinding FinallyBinding);

internal sealed record BridgeIdentity(
    string FoldPackage,
    string FoldArtifact,
    string FoldArtifactSha256,
    string FoldRefinement,
    string FoldRefinementSha256,
    string Relation,
    string[] RequiredTerminalFields,
    string[] RequiredFoldPremises);

internal sealed record HelperPreservationIdentity(
    string Path,
    string Member,
    string SymbolId,
    string CanonicalSyntax,
    string SyntaxSha256);

internal sealed record InstrumentationSinkIdentity(
    string Name,
    string Path,
    string TypeSymbol,
    string CanonicalSyntax,
    string SyntaxSha256,
    string EnabledPropertySymbol,
    string EnabledValueSymbol,
    string AddTicksSymbol,
    string[] AddTicksCalls,
    string OwnerSymbol,
    string ResourceCanonical,
    string WrapperType,
    string ConstructorSymbol,
    string DisposeSymbol);

internal sealed record InstrumentationActivationIdentity(
    string Path,
    string OwnerSymbol,
    string OperationKind,
    string ResourceCanonical,
    string WrapperType,
    string SinkTypeSymbol,
    string TargetSymbol);

internal sealed record InstrumentationBoundaryIdentity(SourceIdentity Wrapper, InstrumentationSinkIdentity[] Sinks,
    InstrumentationActivationIdentity[] Activations);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string[] Included,
    string[] Excluded,
    SourceIdentity[] Sources,
    CompilerClosureIdentity CompilerClosure,
    MemberIdentity[] Members,
    AnchorIdentity[] Anchors,
    ControlFlowIdentity[] ControlFlows,
    CommitIdentity[] Commits,
    GuardIdentity[] Guards,
    StepIdentity[] Steps,
    HeaderAssignmentIdentity[] HeaderAssignments,
    OpaqueDelegateIdentity[] OpaqueDelegates,
    BridgeIdentity Bridge,
    SourceEntryAdapterIdentity[] SourceEntryAdapters,
    TaskFlowIdentity TaskFlow,
    HelperPreservationIdentity EntryPreservation,
    HelperPreservationIdentity[] HelperPreservation,
    InstrumentationBoundaryIdentity Instrumentation,
    string[] OpenObligations);

internal sealed record SourcePin(string Path, string Role, string Sha256);

internal sealed record SourcePinDocument(int SchemaVersion, SourcePin[] Sources);

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
    CompilerClosureIdentity Closure);
