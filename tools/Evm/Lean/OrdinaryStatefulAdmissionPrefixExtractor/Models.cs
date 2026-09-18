// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.OrdinaryStatefulAdmissionPrefixExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int BranchCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256);

internal sealed record SourceIdentity(string Path, string Role, string Sha256);

internal sealed record SourceBinding(
    string Path,
    string Owner,
    string Member,
    string Signature,
    string TokenSha256,
    string CanonicalSyntaxSha256,
    string CanonicalSyntax,
    string SourceSyntax,
    string ContainingMember,
    string Receiver,
    int StatementOrdinal,
    string ControlFlowPath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

internal sealed record StageAnchor(
    string Id,
    int Ordinal,
    SourceBinding Binding);

internal sealed record BranchShape(
    string Id,
    int Ordinal,
    string Member,
    string Condition,
    string Terminal,
    string[] PartialEffects,
    SourceBinding Binding,
    BranchTerminalKind TerminalKind,
    SemanticEffect[] Effects,
    SourceExpression ConditionAst);

internal sealed record EffectShape(
    string Id,
    string Member,
    string[] Reads,
    string[] Writes,
    string FailureVisibility,
    SourceBinding Binding,
    SemanticEffect[] Effects);

internal sealed record RouteShape(
    string Registration,
    string BlobBaseFeeCalculatorRegistration,
    string WorldStateRegistration,
    string ConcreteProcessor,
    string ClosedGenericBase,
    string[] ExcludedOverrides,
    string[] Reachability,
    SourceBinding[] Bindings);

internal sealed record OptionsShape(
    int None,
    int Commit,
    int Restore,
    int SkipValidation,
    int Warmup,
    int BuildUp,
    string MetricsEqualityGate,
    string RecoveryCommitFormula,
    string ShouldValidateGasFormula);

/// <summary>Source-width domains which remain explicit in the generated Lean transition.</summary>
internal enum NumericWidth
{
    UInt64,
    UInt256,
    Int32,
}

/// <summary>One source-derived operation family used to choose generated Lean syntax.</summary>
internal enum SemanticFormula
{
    NormalizeUnsigned,
    CheckedAdd,
    CheckedMultiply,
    EffectiveGasPrice,
    PremiumPerGas,
    RecoveryDecision,
    RecoveryApplication,
    FeeReservation,
    NonceAdvance,
    CombinedFailureReset,
    AdmissionPrefix,
    MetricsCommitOrNone,
    MetricsCommitOnly,
    SourceConstant,
    BlobGas,
}

/// <summary>A visible state effect retained by the bounded observation.</summary>
internal enum SemanticEffect
{
    NormalizeInput,
    PreserveWrappedOutValue,
    AppendMetric,
    AppendRecoveryLog,
    ReplaceTransactionSender,
    CreateZeroAccount,
    PreserveThrowState,
    AppendValidationLog,
    DebitEffectiveSender,
    SetEffectiveSenderNonce,
    ResetJournalAfterCombinedFailure,
    ContinueAfterPrefix,
}

/// <summary>Terminal form extracted from the exact branch node.</summary>
internal enum BranchTerminalKind
{
    Continue,
    Return,
    Throw,
    Reset,
}

/// <summary>
/// The deliberately small Roslyn expression grammar accepted by this extractor.
/// Every node keeps its normalized source spelling as well as its typed children so that emission
/// consumes the admitted source tree rather than an independently selected formula label.
/// </summary>
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
    MemberAccess,
    Invocation,
    Argument,
    Unary,
    PostfixUnary,
    Binary,
    Conditional,
    Cast,
    IsPattern,
    PatternConstant,
    PatternUnary,
    PatternBinary,
    Assignment,
    Parenthesized,
}

/// <summary>
/// A complete normalized expression tree from one exact Roslyn node.
/// <paramref name="Symbol"/> is the identifier, member, operator, or literal token appropriate
/// to <paramref name="Kind"/>. <paramref name="SymbolId"/> is a grammar-assigned identity tag
/// for executable identifiers, members, invocations, and operators; <paramref name="TypeName"/>
/// is a grammar-assigned declaration/result category. They are not Roslyn <c>ISymbol</c> or
/// <c>ITypeSymbol</c> results. The production-facing theorem therefore carries the separate
/// compiler-resolution audit premise that those finite tags agree with the reviewed source closure.
/// No untyped syntax fragment is used to choose Lean semantics.
/// </summary>
internal sealed record SourceExpression(
    SourceExpressionKind Kind,
    string Normalized,
    string Symbol,
    string SymbolId,
    string TypeName,
    SourceExpression[] Children);

/// <summary>Trusted adapter class whose source projection is pinned in the admission closure.</summary>
internal enum AdapterKind
{
    TransactionProjection,
    TransactionTypeProjection,
    BlobProjection,
    WorldStateProjection,
    HeaderAndSpecProjection,
    StaticAdmissionProjection,
    RouteProjection,
    SourceGrammarSemanticsAssumption,
}

internal sealed record WidthShape(
    string Id,
    NumericWidth Width,
    int Bits,
    string SourceDefinition,
    SourceBinding Binding);

internal sealed record SemanticOperation(
    string Id,
    int Ordinal,
    string SourceMember,
    SemanticFormula Formula,
    NumericWidth[] InputWidths,
    NumericWidth OutputWidth,
    SemanticEffect[] Effects,
    SourceBinding Binding,
    string Expression,
    SourceExpression ExpressionAst);

/// <summary>
/// An explicitly trusted input projection. It does not claim to extract the referenced component's
/// implementation, but binds its current source identity and records exactly what the pure input supplies.
/// </summary>
internal sealed record AdapterPremise(
    string Id,
    string SourcePath,
    string SourceMember,
    string Projection,
    string Assumption,
    AdapterKind Kind,
    SourceBinding[] Bindings,
    SourceExpression[] ProjectionAsts,
    string DomainPredicate);

internal sealed record SemanticLowering(
    WidthShape[] Widths,
    SemanticOperation[] Operations,
    AdapterPremise[] AdapterPremises);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string AdmissionClosureSha256,
    RouteShape Route,
    OptionsShape Options,
    SourceIdentity[] Sources,
    StageAnchor[] Stages,
    BranchShape[] Branches,
    EffectShape[] Effects,
    SemanticLowering Semantics,
    string[] ArithmeticRules,
    string[] ExternalObligations);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record Manifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    string AdmissionClosureSha256,
    SourceIdentity[] Sources,
    SourceBinding[] Bindings,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string SemanticIrSha256);
