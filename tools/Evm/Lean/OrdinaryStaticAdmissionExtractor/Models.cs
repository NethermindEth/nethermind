// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.OrdinaryStaticAdmissionExtractor;

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

internal sealed record SourceFile(string Path, string Role, byte[] Bytes, string Sha256);

internal sealed record SourceBinding(
    string Path,
    string Role,
    string Owner,
    string Member,
    string Signature,
    string Sha256,
    string TokenSha256);

internal sealed record IrField(string Name, string Type, string SourceDefinition);

internal enum IrConditionKind
{
    Predicate,
    NegatedPredicate,
    Comparison,
    Conjunction,
    Disjunction,
    Else,
}

internal enum IrComparisonOperator
{
    None,
    IsNull,
    IsNotNull,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

internal enum IrOperandKind
{
    Field,
    Constant,
    Call,
    Derived,
}

internal enum IrReductionKind
{
    None,
    Addition,
    Subtraction,
    Minimum,
    Maximum,
}

internal sealed record IrOperand(string Name, IrOperandKind Kind, string Source);

internal enum IrOperationKind
{
    Assignment,
    Guard,
    Call,
    Return,
}

internal sealed record IrOperation(
    IrOperationKind Kind,
    string Source,
    IrOperand[] Operands,
    IrComparisonOperator ComparisonOperator,
    IrReductionKind Reduction);

internal sealed record IrShape(
    string Id,
    string SourceMember,
    string SourceExpression,
    IrConditionKind ConditionKind,
    IrComparisonOperator ComparisonOperator,
    IrReductionKind Reduction,
    IrOperand[] Operands);

internal sealed record IrCallMapping(
    string Id,
    string SourceMember,
    string[] ArgumentExpressions,
    string[] OutputFields,
    string[] OutputExpressions,
    string FailureDefaultExpression);

internal sealed record IrGuard(
    string Source,
    IrConditionKind ConditionKind,
    IrComparisonOperator ComparisonOperator,
    IrOperand[] Operands,
    bool IsSyntheticFallthrough);

internal sealed record IrBranch(
    string Id,
    int Ordinal,
    string SourceMethod,
    string Condition,
    string SourceExpression,
    string SourceReturnExpression,
    IrGuard[] GuardPath,
    IrConditionKind ConditionKind,
    IrOperand[] Operands,
    IrComparisonOperator ComparisonOperator,
    string Error,
    string EvmExceptionType,
    string ReturnSite);

internal sealed record IrFunction(
    string Name,
    string Owner,
    string ReturnType,
    IrField[] Parameters,
    IrOperation[] Operations,
    string SourceTokenSha256);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string[] SourceWidthRules,
    IrField[] StaticInputFields,
    IrField[] InitializationInputFields,
    IrField[] ResultFields,
    IrBranch[] Branches,
    IrFunction[] Functions,
    IrShape[] Shapes,
    IrCallMapping[] CallMappings,
    SourceBinding[] Sources,
    string[] ExternalObligations,
    string IrSha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record Manifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    string AcceptanceState,
    SourceBinding[] Sources,
    ArtifactIdentity[] Dependencies,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string SemanticIrSha256);
