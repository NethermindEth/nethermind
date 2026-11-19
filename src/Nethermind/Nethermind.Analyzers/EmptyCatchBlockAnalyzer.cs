// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports <c>catch</c> clauses whose block has no statements. Catching <see cref="System.OperationCanceledException"/>
/// or a derived type is exempt because cancellation is the expected way to stop the guarded work, not a failure.
/// Catching <see cref="System.ObjectDisposedException"/> around a single statement is exempt as a dispose race.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyCatchBlockAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH010";

    private const string AllExceptions = "all exceptions";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Do not swallow exceptions in an empty catch block",
        "Empty catch block swallows {0}; log the exception or handle it",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A failure swallowed by an empty catch block leaves no trace, which makes it the hardest kind to " +
                     "diagnose on a running node. Log the exception, handle it, or avoid the throw. Catching " +
                     "OperationCanceledException or a derived type, or ObjectDisposedException around a single " +
                     "statement, is exempt.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            INamedTypeSymbol? operationCanceled = startContext.Compilation.GetTypeByMetadataName("System.OperationCanceledException");
            INamedTypeSymbol? objectDisposed = startContext.Compilation.GetTypeByMetadataName("System.ObjectDisposedException");
            startContext.RegisterSyntaxNodeAction(ctx => Analyze(ctx, operationCanceled, objectDisposed), SyntaxKind.CatchClause);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol? operationCanceled, INamedTypeSymbol? objectDisposed)
    {
        CatchClauseSyntax catchClause = (CatchClauseSyntax)context.Node;
        if (catchClause.Block.Statements.Count != 0) return;

        string caught = AllExceptions;
        if (catchClause.Declaration is { } declaration)
        {
            ITypeSymbol? type = context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type;
            if (type is null || type.TypeKind == TypeKind.Error) return;
            if (DerivesFrom(type, operationCanceled)) return;
            if (IsDisposeRaceOnSingleStatement(catchClause, type, objectDisposed)) return;
            caught = type.Name;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, catchClause.CatchKeyword.GetLocation(), caught));
    }

    /// <summary>
    /// A lone statement guarded by <c>catch (ObjectDisposedException)</c> is a call that races a concurrent
    /// <c>Dispose</c> of the same object (cancelling a token source, releasing a semaphore); the object being gone
    /// already means the call has nothing left to do.
    /// </summary>
    private static bool IsDisposeRaceOnSingleStatement(CatchClauseSyntax catchClause, ITypeSymbol type, INamedTypeSymbol? objectDisposed) =>
        objectDisposed is not null
        && SymbolEqualityComparer.Default.Equals(type, objectDisposed)
        && catchClause.Parent is TryStatementSyntax { Block.Statements.Count: 1 };

    private static bool DerivesFrom(ITypeSymbol type, INamedTypeSymbol? baseType)
    {
        if (baseType is null) return false;

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
        }

        return false;
    }
}
