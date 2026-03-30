// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Test.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseReleaseSpecSubstituteCodeFixProvider)), Shared]
public sealed class UseReleaseSpecSubstituteCodeFixProvider : CodeFixProvider
{
    private const string Title = "Use configured substitute factory";
    private const string DefaultFactoryNamespace = "Nethermind.Core.Test";

    public override ImmutableArray<string> FixableDiagnosticIds => [UseCustomSubstituteAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue(UseCustomSubstituteAnalyzer.FactoryInvocationPropertyName, out string? factoryInvocation)
            || string.IsNullOrWhiteSpace(factoryInvocation))
        {
            return;
        }

        string? factoryNamespace = null;
        if (!diagnostic.Properties.TryGetValue(UseCustomSubstituteAnalyzer.FactoryNamespacePropertyName, out factoryNamespace)
            || string.IsNullOrWhiteSpace(factoryNamespace))
        {
            factoryNamespace = DefaultFactoryNamespace;
        }

        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        SyntaxNode? node = root.FindNode(context.Span, getInnermostNodeForTie: true);
        if (node is null)
        {
            return;
        }

        InvocationExpressionSyntax? invocation = node as InvocationExpressionSyntax ?? node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation is null)
        {
            return;
        }

        string replacement = factoryInvocation!;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Title,
                createChangedDocument: cancellationToken => ReplaceInvocationAsync(context.Document, root, invocation, replacement, factoryNamespace, cancellationToken),
                equivalenceKey: Title),
            diagnostic);
    }

    private static Task<Document> ReplaceInvocationAsync(
        Document document,
        SyntaxNode root,
        InvocationExpressionSyntax invocation,
        string factoryInvocation,
        string? factoryNamespace,
        CancellationToken cancellationToken)
    {
        string invocationToUse = factoryInvocation;
        ExpressionSyntax replacementExpression = SyntaxFactory.ParseExpression(invocationToUse)
            .WithTriviaFrom(invocation);

        SyntaxNode updatedRoot = root.ReplaceNode(invocation, replacementExpression);

        if (!string.IsNullOrWhiteSpace(factoryNamespace) && updatedRoot is CompilationUnitSyntax compilationUnit)
        {
            bool hasUsing = compilationUnit.Usings.Any(u =>
                !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                && string.Equals(u.Name?.ToString(), factoryNamespace, StringComparison.Ordinal));

            if (!hasUsing)
            {
                UsingDirectiveSyntax usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(factoryNamespace!));
                SyntaxList<UsingDirectiveSyntax> usings = compilationUnit.Usings;
                int insertionIndex = GetInsertionIndex(usings, factoryNamespace!);
                SyntaxList<UsingDirectiveSyntax> updatedUsings = usings.Insert(insertionIndex, usingDirective);
                updatedRoot = compilationUnit.WithUsings(updatedUsings);
            }
        }
        else if (!string.IsNullOrWhiteSpace(factoryNamespace))
        {
            string fullyQualifiedInvocation = $"{factoryNamespace}.{factoryInvocation}";
            ExpressionSyntax fullyQualifiedReplacement = SyntaxFactory.ParseExpression(fullyQualifiedInvocation)
                .WithTriviaFrom(invocation);
            updatedRoot = root.ReplaceNode(invocation, fullyQualifiedReplacement);
        }

        return Task.FromResult(document.WithSyntaxRoot(updatedRoot));
    }

    private static int GetInsertionIndex(SyntaxList<UsingDirectiveSyntax> usings, string factoryNamespace)
    {
        List<(int Index, string Name)> normalUsings = [];
        for (int i = 0; i < usings.Count; i++)
        {
            UsingDirectiveSyntax usingDirective = usings[i];
            if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) || usingDirective.Alias is not null)
            {
                continue;
            }

            string? namespaceName = usingDirective.Name?.ToString();
            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                normalUsings.Add((i, namespaceName!));
            }
        }

        List<(int Index, string Name)> nethermindUsings = normalUsings
            .Where(u => u.Name.StartsWith("Nethermind.", StringComparison.Ordinal))
            .ToList();

        if (nethermindUsings.Count > 0)
        {
            List<(int Index, string Name)> beforeOrEqual = nethermindUsings
                .Where(u => string.CompareOrdinal(u.Name, factoryNamespace) <= 0)
                .ToList();

            if (beforeOrEqual.Count > 0)
            {
                return beforeOrEqual[beforeOrEqual.Count - 1].Index + 1;
            }

            List<(int Index, string Name)> after = nethermindUsings
                .Where(u => string.CompareOrdinal(u.Name, factoryNamespace) > 0)
                .ToList();

            if (after.Count > 0)
            {
                return after[0].Index;
            }
        }

        List<(int Index, string Name)> alphabeticallyAfter = normalUsings
            .Where(u => string.CompareOrdinal(u.Name, factoryNamespace) > 0)
            .ToList();

        if (alphabeticallyAfter.Count > 0)
        {
            return alphabeticallyAfter[0].Index;
        }

        return usings.Count;
    }
}
