// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports <c>async void</c> methods and local functions. Event handlers with the
/// <c>(object sender, EventArgs e)</c> shape are exempt because the delegate type fixes the return type.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncVoidAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH009";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Do not use async void",
        "'{0}' is async void; return Task instead so callers can await it and observe its exceptions",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An exception thrown from an async void method cannot be caught by the caller and is " +
                     "rethrown on the thread pool, where it terminates the process. Callers also cannot await " +
                     "completion. Return Task or ValueTask. Event handlers with an (object, EventArgs) signature are exempt.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            INamedTypeSymbol? eventArgs = startContext.Compilation.GetTypeByMetadataName("System.EventArgs");
            startContext.RegisterSyntaxNodeAction(
                ctx => Analyze(ctx, eventArgs),
                SyntaxKind.MethodDeclaration,
                SyntaxKind.LocalFunctionStatement);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol? eventArgs)
    {
        (SyntaxTokenList modifiers, SyntaxToken identifier) = context.Node switch
        {
            MethodDeclarationSyntax method => (method.Modifiers, method.Identifier),
            LocalFunctionStatementSyntax local => (local.Modifiers, local.Identifier),
            _ => default
        };
        if (!modifiers.Any(SyntaxKind.AsyncKeyword)) return;

        if (context.SemanticModel.GetDeclaredSymbol(context.Node, context.CancellationToken) is not IMethodSymbol symbol) return;
        if (!symbol.ReturnsVoid || IsEventHandler(symbol, eventArgs)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), symbol.Name));
    }

    private static bool IsEventHandler(IMethodSymbol method, INamedTypeSymbol? eventArgs)
    {
        if (eventArgs is null || method.Parameters.Length != 2) return false;
        if (method.Parameters[0].Type.SpecialType != SpecialType.System_Object) return false;

        for (ITypeSymbol? type = method.Parameters[1].Type; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, eventArgs)) return true;
        }

        return false;
    }
}
