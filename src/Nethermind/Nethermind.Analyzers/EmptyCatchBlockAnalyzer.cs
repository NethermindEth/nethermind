// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports <c>catch</c> clauses whose block has no statements. Catching <see cref="System.OperationCanceledException"/>
/// or <c>System.Threading.Channels.ChannelClosedException</c>, or a derived type, is exempt because cancellation and a
/// completed channel are the expected ways to stop the guarded work, not failures. Catching
/// <see cref="System.ObjectDisposedException"/> around a single statement is exempt as a dispose race. A
/// <c>catch (Exception e) when (e is A or B)</c> filter is exempt when every listed type is.
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
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A failure swallowed by an empty catch block leaves no trace, which makes it the hardest kind to " +
                     "diagnose on a running node. Log the exception, handle it, or avoid the throw. Catching " +
                     "OperationCanceledException or ChannelClosedException or a derived type, ObjectDisposedException " +
                     "around a single statement, or an exception filter that lists only those types, is exempt.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            ExpectedExceptions expected = new(
                startContext.Compilation.GetTypeByMetadataName("System.OperationCanceledException"),
                startContext.Compilation.GetTypeByMetadataName("System.Threading.Channels.ChannelClosedException"),
                startContext.Compilation.GetTypeByMetadataName("System.ObjectDisposedException"));
            startContext.RegisterSyntaxNodeAction(ctx => Analyze(ctx, expected), SyntaxKind.CatchClause);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, ExpectedExceptions expected)
    {
        CatchClauseSyntax catchClause = (CatchClauseSyntax)context.Node;
        if (catchClause.Block.Statements.Count != 0) return;

        string caught = AllExceptions;
        if (catchClause.Declaration is { } declaration)
        {
            ITypeSymbol? type = context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type;
            if (type is null || type.TypeKind == TypeKind.Error) return;
            if (expected.IsExpected(type, catchClause)) return;
            if (FilterListsOnlyExpected(context, catchClause, expected)) return;
            caught = type.Name;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, catchClause.CatchKeyword.GetLocation(), caught));
    }

    private static bool FilterListsOnlyExpected(SyntaxNodeAnalysisContext context, CatchClauseSyntax catchClause, ExpectedExceptions expected)
    {
        if (catchClause.Filter is not { } filter) return false;
        if (context.SemanticModel.GetDeclaredSymbol(catchClause.Declaration!, context.CancellationToken) is not { } local) return false;

        return context.SemanticModel.GetOperation(filter.FilterExpression, context.CancellationToken) switch
        {
            IIsTypeOperation { ValueOperand: ILocalReferenceOperation value } isType =>
                SymbolEqualityComparer.Default.Equals(value.Local, local) && expected.IsExpected(isType.TypeOperand, catchClause),
            IIsPatternOperation { Value: ILocalReferenceOperation value } isPattern =>
                SymbolEqualityComparer.Default.Equals(value.Local, local) && PatternListsOnlyExpected(isPattern.Pattern, catchClause, expected),
            _ => false,
        };
    }

    private static bool PatternListsOnlyExpected(IPatternOperation pattern, CatchClauseSyntax catchClause, ExpectedExceptions expected) =>
        pattern switch
        {
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or =>
                PatternListsOnlyExpected(or.LeftPattern, catchClause, expected) && PatternListsOnlyExpected(or.RightPattern, catchClause, expected),
            ITypePatternOperation typePattern => expected.IsExpected(typePattern.MatchedType, catchClause),
            IDeclarationPatternOperation { MatchedType: { } matched } => expected.IsExpected(matched, catchClause),
            _ => false,
        };

    private sealed class ExpectedExceptions(INamedTypeSymbol? operationCanceled, INamedTypeSymbol? channelClosed, INamedTypeSymbol? objectDisposed)
    {
        public bool IsExpected(ITypeSymbol type, CatchClauseSyntax catchClause) =>
            DerivesFrom(type, operationCanceled)
            || DerivesFrom(type, channelClosed)
            || IsDisposeRaceOnSingleStatement(catchClause, type);

        /// <summary>
        /// A lone expression statement guarded by <c>catch (ObjectDisposedException)</c> is a call that races a concurrent
        /// <c>Dispose</c> of the same object (cancelling a token source, releasing a semaphore); the object being gone
        /// already means the call has nothing left to do.
        /// </summary>
        private bool IsDisposeRaceOnSingleStatement(CatchClauseSyntax catchClause, ITypeSymbol type) =>
            objectDisposed is not null
            && SymbolEqualityComparer.Default.Equals(type, objectDisposed)
            && catchClause.Parent is TryStatementSyntax { Block.Statements: { Count: 1 } statements }
            && statements[0] is ExpressionStatementSyntax;

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
}
