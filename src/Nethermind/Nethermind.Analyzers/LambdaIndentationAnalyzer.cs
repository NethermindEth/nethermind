// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports multi-line lambda expressions whose body is indented so far to the right
/// (due to Roslyn aligning it to the opening parenthesis of the enclosing call) that
/// readability suffers. The fix is to extract the body to a local function or a
/// separately-declared variable.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LambdaIndentationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH002";

    /// <summary>Maximum allowed column offset of the lambda body relative to its containing statement.</summary>
    public const int MaxIndentOffset = 24;

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Lambda body is excessively deep",
        messageFormat: "Lambda body starts at column {0}, which is {1} columns past the containing statement (max {2}). Reindent the lambda body to use normal block indentation.",
        category: "Style",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A multi-line lambda body is indented so far right that it hurts readability. " +
                     "This usually happens when Roslyn aligns the body to the opening parenthesis of " +
                     "the enclosing method call. Reindent the lambda body manually to use normal block indentation.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        LambdaExpressionSyntax lambda = (LambdaExpressionSyntax)context.Node;

        SyntaxToken bodyFirstToken = lambda.Body.GetFirstToken();

        // Only flag multi-line lambdas: body must start on a different line than the arrow
        if (lambda.ArrowToken.GetLocation().GetLineSpan().EndLinePosition.Line
            == bodyFirstToken.GetLocation().GetLineSpan().StartLinePosition.Line)
            return;

        int bodyColumn = bodyFirstToken.GetLocation().GetLineSpan().StartLinePosition.Character;

        // Walk up to the nearest statement or member declaration to get the baseline column
        SyntaxNode? container = lambda.Parent;
        while (container is not null
               && container is not StatementSyntax
               && container is not MemberDeclarationSyntax
               && container is not AccessorDeclarationSyntax)
        {
            container = container.Parent;
        }

        if (container is null)
            return;

        int containerColumn = container.GetFirstToken().GetLocation().GetLineSpan().StartLinePosition.Character;
        int offset = bodyColumn - containerColumn;

        if (offset > MaxIndentOffset)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Rule, bodyFirstToken.GetLocation(), bodyColumn, offset, MaxIndentOffset));
        }
    }
}
