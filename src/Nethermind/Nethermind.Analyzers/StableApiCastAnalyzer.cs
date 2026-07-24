// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports core code that force-casts (<c>(T)x</c>), <c>as</c>-casts (<c>x as T</c>), or type-checks
/// (<c>x is T</c>, <c>x is T v</c>, <c>case T:</c>) a value against an interface marked
/// <c>[StableApi]</c>. A plugin may supply a decorator or an alternative implementation of a stable
/// interface, so branching on the concrete runtime type is fragile.
/// </summary>
/// <remarks>
/// Reflection and discovery patterns — <c>typeof(T)</c>, <c>typeof(T).IsAssignableFrom(...)</c>, and
/// <c>OfType&lt;T&gt;()</c> — are intentionally not flagged; the plugin/step loaders rely on them.
/// Test assemblies (name ending in <c>.Test</c> / <c>.Tests</c>) are exempt.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StableApiCastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH008";

    private const string StableApiAttributeName = "StableApiAttribute";
    private const string AttributesNamespace = "Nethermind.Core.Attributes";

    private static readonly LocalizableString Title =
        "Do not cast or type-check a [StableApi] interface";

    private static readonly LocalizableString MessageFormat =
        "'{0}' is marked [StableApi]; do not cast to or type-check against it. A plugin may decorate or override it, so runtime type-branching is fragile.";

    private static readonly LocalizableString Description =
        "Interfaces marked [StableApi] are a plugin-facing contract that plugins may decorate or " +
        "override. Force-casting ((T)x), as-casting (x as T), or type-checking (x is T) a value against " +
        "such an interface breaks when the real object is a plugin-supplied wrapper. Depend on the " +
        "interface directly instead of branching on the concrete type.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            // Honor "core code only": test assemblies may legitimately cast/type-check for setup and assertions.
            string? assemblyName = startContext.Compilation.AssemblyName;
            if (assemblyName is not null
                && (assemblyName.EndsWith(".Test", System.StringComparison.Ordinal)
                    || assemblyName.EndsWith(".Tests", System.StringComparison.Ordinal)))
                return;

            startContext.RegisterSyntaxNodeAction(
                AnalyzeNode,
                SyntaxKind.CastExpression,
                SyntaxKind.AsExpression,
                SyntaxKind.IsExpression,
                SyntaxKind.DeclarationPattern,
                SyntaxKind.TypePattern,
                SyntaxKind.ConstantPattern,
                SyntaxKind.CaseSwitchLabel);
        });
    }

    private static void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        // A bare identifier in a pattern position (e.g. `is not IStable`, `case IStable:`) parses as a
        // ConstantPatternSyntax and is reclassified to a type by the semantic model; hence ExpressionSyntax.
        ExpressionSyntax? typeSyntax = context.Node switch
        {
            CastExpressionSyntax cast => cast.Type,
            BinaryExpressionSyntax binary => binary.Right,
            DeclarationPatternSyntax declaration => declaration.Type,
            TypePatternSyntax typePattern => typePattern.Type,
            ConstantPatternSyntax constant => constant.Expression,
            CaseSwitchLabelSyntax caseLabel => caseLabel.Value,
            _ => null,
        };

        if (typeSyntax is null)
            return;

        if (context.SemanticModel.GetSymbolInfo(typeSyntax, context.CancellationToken).Symbol is not INamedTypeSymbol type)
            return;

        if (!IsStableApi(type))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, typeSyntax.GetLocation(), type.Name));
    }

    private static bool IsStableApi(INamedTypeSymbol type)
    {
        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == StableApiAttributeName
                && attribute.AttributeClass.ContainingNamespace?.ToDisplayString() == AttributesNamespace)
                return true;
        }
        return false;
    }
}
