// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.OrdinaryPostNonceDispatchExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int BranchCount,
    int EffectCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);

internal sealed record SourceIdentity(string Path, string Role, string Sha256);

internal sealed record DependencyIdentity(string Path, string Role, string Sha256);

internal sealed record CompilerReferenceIdentity(
    string Path,
    string AssemblyName,
    string Sha256,
    string Mvid,
    bool Selected,
    string[] Dependencies);

internal sealed record CompilerReferencePin(
    string Path,
    string AssemblyName,
    string Sha256,
    string Mvid,
    bool Selected);

internal sealed record CompilerReferenceInventory(
    int SchemaVersion,
    int Count,
    string AggregateSha256,
    CompilerReferencePin[] References);

/// <summary>A source location and semantic binding for one lowered operation.</summary>
internal sealed record SourceBinding(
    string Path,
    string Owner,
    string Member,
    string Signature,
    string NodeKind,
    string CanonicalSyntax,
    string TokenSha256,
    string CanonicalSyntaxSha256,
    string ContainingMember,
    string Receiver,
    string TargetSymbol,
    string TargetSymbolKind,
    string CandidateReason,
    bool IsErrorSymbol,
    bool HasCandidateSymbols,
    int StatementOrdinal,
    string ControlFlowPath,
    int ControlFlowBlock,
    string SourceSyntax,
    string OperationKind,
    string OperationType,
    bool OperationIsImplicit,
    bool DataFlowSucceeded,
    string[] ReadInside,
    string[] WrittenInside,
    string[] ReadOutside,
    string[] WrittenOutside,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>A recursively complete source expression accepted by the bounded emitter.</summary>
/// <remarks>
/// The tree is attached to the exact bound source node. Its grammar identity and type category
/// are independently recorded so an IR edit cannot change the emitted executable term by changing
/// only a descriptive spelling. Roslyn symbol and operation identities remain on
/// <see cref="SourceBinding"/>; this tree is the finite syntax vocabulary owned by the emitter.
/// </remarks>
internal enum SourceExpressionKind
{
    Projection,
    Block,
    LocalDeclaration,
    VariableDeclarator,
    If,
    Return,
    ExpressionStatement,
    Identifier,
    Literal,
    Default,
    MemberAccess,
    Invocation,
    Argument,
    Unary,
    Binary,
    Conditional,
    IsPattern,
    PatternConstant,
    PatternUnary,
    PatternBinary,
    Assignment,
    Parenthesized,
}

internal sealed record SourceExpression(
    SourceExpressionKind Kind,
    string Normalized,
    string Symbol,
    string SymbolId,
    string TypeName,
    SourceExpression[] Children);

internal enum SemanticFormula
{
    OptionHasFlag,
    EffectiveCommit,
    PrepareFastPath,
    CandidateGuard,
    CodeLookup,
    SimpleDecision,
    CommitBeforeExecution,
    PrecommitRequest,
    CalculateAvailableGas,
    GasFailurePolicy,
    SimpleHandoff,
    EvmHandoff,
}

internal enum SemanticEffect
{
    InitializePreloadedOutputs,
    LookupCodeInfo,
    AssignSimpleRecipient,
    PreserveDelegationDesignator,
    RequestCommit,
    PreserveFiveZeroGasPolicy,
    AssignGasAvailable,
    HandoffSimple,
    HandoffEvm,
}

internal enum TerminalKind
{
    NoTerminal,
    EscapedLookup,
    EscapedPrecommit,
    GasRejected,
    SimpleHandoff,
    EvmHandoff,
}

internal enum PreloadKind
{
    None,
    Loaded,
}

internal enum NumericWidth
{
    UInt64,
    UInt256,
    Int32,
}

internal sealed record StageShape(
    string Id,
    int Ordinal,
    string Kind,
    SourceBinding Binding);

internal sealed record BranchShape(
    string Id,
    int Ordinal,
    string Condition,
    string Terminal,
    TerminalKind TerminalKind,
    string[] Effects,
    SourceBinding Binding,
    SourceExpression ConditionAst);

internal sealed record EffectShape(
    string Id,
    int Ordinal,
    string Kind,
    string[] Reads,
    string[] Writes,
    string FailureVisibility,
    SourceBinding Binding,
    SemanticEffect Effect);

internal sealed record SemanticOperation(
    string Id,
    int Ordinal,
    SemanticFormula Formula,
    NumericWidth[] InputWidths,
    NumericWidth OutputWidth,
    string Expression,
    string[] SourceOperands,
    SourceBinding Binding,
    SourceExpression ExpressionAst);

internal sealed record WidthShape(
    string Id,
    NumericWidth Width,
    int Bits,
    string SourceDefinition,
    SourceBinding Binding);

/// <summary>
/// A source-bound adapter obligation. The extractor binds the projection and records the
/// remaining implementation correspondence instead of silently treating an oracle as a proof.
/// </summary>
internal sealed record AdapterPremise(
    string Id,
    string SourcePath,
    string SourceMember,
    string Projection,
    string Assumption,
    string DomainPredicate,
    SourceBinding[] Bindings,
    SourceExpression[] ProjectionAsts,
    SourceExpression DomainAst,
    SourceBinding DomainBinding);

/// <summary>Source-bound result classification returned by CalculateAvailableGas.</summary>
internal sealed record GasClassification(
    string Condition,
    string SuccessResult,
    string FailureResult,
    SourceBinding ConditionBinding,
    SourceBinding SuccessBinding,
    SourceBinding FailureBinding,
    SourceExpression ConditionAst,
    SourceExpression SuccessAst,
    SourceExpression FailureAst);

internal sealed record SemanticLowering(
    WidthShape[] Widths,
    SemanticOperation[] Operations,
    AdapterPremise[] AdapterPremises,
    GasClassification GasClassification);

internal sealed record DispatchShape(
    string RestoreFormula,
    string CommitFormula,
    string CommitBeforeFormula,
    string CandidateFormula,
    string LookupFormula,
    string SimpleFormula,
    string AvailableGasFailureFormula,
    StageShape[] Stages,
    BranchShape[] Branches,
    EffectShape[] Effects,
    SourceBinding[] Handoffs,
    SourceBinding[] MethodBindings,
    SourceBinding[] RouteBindings,
    PreloadKind[] PreloadStates,
    SemanticLowering Semantics,
    PostNonceControlFlow PostNonceBoundary);

internal sealed record PostNonceControlFlow(
    string SuccessPredicate,
    int PredicateBlock,
    int SuccessEdgeBlock,
    int GasBoundaryBlock,
    int DispatchBoundaryBlock,
    int[] ClosedNormalExitBlocks,
    SourceBinding SuccessPredicateBinding);

internal sealed record RouteShape(
    string Registration,
    string ProcessorInheritance,
    string WorldStateRegistration,
    string CodeRepositoryRegistration,
    string BlobCalculatorRegistration,
    string CommitInterface,
    string CommitImplementation,
    string[] ExcludedRoutes,
    SourceBinding[] Bindings);

internal sealed record OptionShape(
    int None,
    int Commit,
    int Restore,
    int SkipValidation,
    int Warmup,
    int BuildUp,
    string RestoreFormula,
    string CommitFormula,
    string CommitBeforeFormula,
    SourceBinding Binding);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string SourceClosureSha256,
    SourceIdentity[] Sources,
    DependencyIdentity[] Dependencies,
    CompilerReferenceIdentity[] CompilerReferences,
    OptionShape Options,
    RouteShape Route,
    DispatchShape Dispatch,
    string[] ArithmeticRules,
    string[] ExternalCorrespondence);

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
    CompilerReferenceIdentity[] CompilerReferences,
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
