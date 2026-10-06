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
/// Rejects sensitive log markers that are not handled by a log interpolation handler.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SensitiveLogMarkerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH008";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use the sensitive log marker directly with a log handler",
        "Log marker ':{0}' is not handled here; use ':hide' in a direct ILogger interpolation",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Only an interpolation bound to a Nethermind logging handler can mask ':hide'. " +
                     "String materialization, case variants, and the retired ':sensitive' marker are rejected.");

    private static readonly string[] HandlerTypeNames =
    [
        "InfoInterpolatedStringHandler",
        "DebugInterpolatedStringHandler",
        "TraceInterpolatedStringHandler",
        "WarnInterpolatedStringHandler",
        "ErrorInterpolatedStringHandler"
    ];

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

        if (format == "hide" && IsHandledByLogger(context, interpolation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, clause.FormatStringToken.GetLocation(), format));
    }

    private static bool IsHandledByLogger(SyntaxNodeAnalysisContext context, InterpolationSyntax interpolation)
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

        IArgumentOperation? operation = context.SemanticModel.GetOperation(argument, context.CancellationToken) as IArgumentOperation;
        ITypeSymbol? argumentType = operation?.Parameter?.Type;
        if (argumentType is null) return false;

        foreach (string handlerTypeName in HandlerTypeNames)
        {
            INamedTypeSymbol? handlerType = context.Compilation.GetTypeByMetadataName($"Nethermind.Logging.{handlerTypeName}");
            if (SymbolEqualityComparer.Default.Equals(argumentType, handlerType)) return true;
        }

        return false;
    }
}
