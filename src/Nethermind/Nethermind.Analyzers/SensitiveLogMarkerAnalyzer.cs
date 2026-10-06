// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Rejects sensitive log markers that are not handled by the informational log interpolation handler.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SensitiveLogMarkerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH008";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use the sensitive log marker directly with the Info handler",
        "Log marker ':{0}' is not handled here; use ':hide' in a direct ILogger.Info interpolation",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Only an interpolation bound to InfoInterpolatedStringHandler can mask ':hide'. " +
                     "String materialization, other log levels, case variants, and the retired ':sensitive' marker are rejected.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.Interpolation);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        InterpolationSyntax interpolation = (InterpolationSyntax)context.Node;
        InterpolationFormatClauseSyntax? clause = interpolation.FormatClause;
        if (clause is null) return;

        string format = clause.FormatStringToken.ValueText;
        string marker = format.Trim();
        if (!string.Equals(marker, "hide", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(marker, "sensitive", StringComparison.OrdinalIgnoreCase))
            return;

        if (format == "hide" && IsHandledByInfo(context, interpolation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, clause.FormatStringToken.GetLocation(), format));
    }

    private static bool IsHandledByInfo(SyntaxNodeAnalysisContext context, InterpolationSyntax interpolation)
    {
        if (interpolation.Parent is not InterpolatedStringExpressionSyntax expression) return false;

        ExpressionSyntax value = expression;
        while (true)
        {
            if (value.Parent is ParenthesizedExpressionSyntax parenthesized)
                value = parenthesized;
            else if (value.Parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
                value = binary;
            else
                break;
        }

        if (value.Parent is not ArgumentSyntax argument) return false;

        INamedTypeSymbol? handlerType = context.Compilation.GetTypeByMetadataName(
            "Nethermind.Logging.InfoInterpolatedStringHandler");
        if (handlerType is null) return false;

        IArgumentOperation? operation = context.SemanticModel.GetOperation(argument, context.CancellationToken) as IArgumentOperation;
        return SymbolEqualityComparer.Default.Equals(operation?.Parameter?.Type, handlerType);
    }
}
