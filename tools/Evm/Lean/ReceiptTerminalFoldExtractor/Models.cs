// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount);

internal sealed class ExtractionException(string message) : Exception(message);

/// <summary>
/// A lossless, JSON-friendly syntax term captured from an admitted Roslyn node.
/// </summary>
/// <remarks>
/// The term retains Roslyn's node kind, canonical source text, resolved symbol identity,
/// and converted type. The Lean emitter lowers this small tree structurally; it does not
/// use a detached C# text template as the semantic input.
/// </remarks>
internal sealed record SemanticExpression(
    string Kind,
    string Text,
    SemanticExpression[] Children,
    string? SymbolId = null,
    string? TypeName = null,
    int Start = 0);

/// <summary>
/// One source-derived statement in an admitted operation.
/// </summary>
internal sealed record SemanticStep(
    string Kind,
    string Text,
    SemanticExpression? Expression,
    SemanticStep[] Children,
    int Start = 0,
    int End = 0,
    SemanticExpression? DeclaredTarget = null);

internal sealed record SemanticBranch(int Position, SemanticExpression Condition, bool WhenTrue);

/// <summary>
/// A source-resolved invocation retained by the semantic operation IR, including
/// the complete outer-to-inner source branch path, including each arm's polarity.
/// </summary>
internal sealed record SemanticInvocation(
    string Receiver,
    string Method,
    SemanticExpression[] Arguments,
    SemanticExpression Expression,
    int Position,
    SemanticBranch[] Branches,
    string? SymbolId = null,
    string? ReturnType = null,
    string? ReceiverSymbolId = null,
    string? ReceiverTypeName = null)
{
    [JsonIgnore]
    internal SemanticExpression? Guard => Branches.LastOrDefault()?.Condition;
}

/// <summary>
/// A source-resolved local initializer or assignment with its complete branch path.
/// </summary>
internal sealed record SemanticAssignment(
    string Target,
    SemanticExpression Value,
    SemanticBranch[] Branches,
    string Form,
    int Position,
    SemanticExpression? TargetExpression = null)
{
    [JsonIgnore]
    internal SemanticExpression? Guard => Branches.LastOrDefault()?.Condition;
}

/// <summary>
/// The typed source facts needed by one lowered operation.
/// </summary>
internal sealed record SemanticOperation(
    SemanticInvocation[] Invocations,
    SemanticAssignment[] Assignments,
    SemanticStep[] Guards,
    SemanticExpression? ReturnExpression);
