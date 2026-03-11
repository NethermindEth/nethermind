// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nethermind.Test.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class UseCustomSubstituteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NMT001";
    public const string FactoryInvocationPropertyName = "FactoryInvocation";
    public const string FactoryNamespacePropertyName = "FactoryNamespace";

    private static readonly LocalizableString Title = "You might prefer to use an existing configured substitute factory";
    private static readonly LocalizableString MessageFormat = "Replace Substitute.For<{0}>() with {1}";
    private static readonly LocalizableString Description = "Directly substituting some spec interfaces misses required setup. Use the corresponding substitute factory instead.";

    private readonly struct SubstituteReplacement(string substitutedTypeFullName, string factoryTypeFullName, string factoryNamespace, string factoryInvocation)
    {
        public string SubstitutedTypeFullName { get; } = substitutedTypeFullName;
        public string FactoryTypeFullName { get; } = factoryTypeFullName;
        public string FactoryNamespace { get; } = factoryNamespace;
        public string FactoryInvocation { get; } = factoryInvocation;
    }

    private static readonly ImmutableArray<SubstituteReplacement> Replacements =
    [
        new("global::Nethermind.Core.Specs.IReleaseSpec", "global::Nethermind.Core.Test.ReleaseSpecSubstitute", "Nethermind.Core.Test", "ReleaseSpecSubstitute.Create()"),
        new("global::Nethermind.Core.Specs.ISpecProvider", "global::Nethermind.Core.Test.SpecProviderSubstitute", "Nethermind.Core.Test", "SpecProviderSubstitute.Create()")
    ];

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.Text: "For" } genericName
            })
        {
            return;
        }

        if (genericName.TypeArgumentList.Arguments.Count != 1)
        {
            return;
        }

        INamedTypeSymbol? substitutedType = context.SemanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0], context.CancellationToken).Type as INamedTypeSymbol;
        if (substitutedType is null)
        {
            return;
        }

        string substitutedTypeFullName = substitutedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        SubstituteReplacement? replacement = GetReplacement(substitutedTypeFullName);
        if (replacement is null)
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol methodSymbol)
        {
            return;
        }

        if (methodSymbol.Name != "For" || methodSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::NSubstitute.Substitute")
        {
            return;
        }

        INamedTypeSymbol? containingType = context.ContainingSymbol?.ContainingType;
        if (containingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == replacement.Value.FactoryTypeFullName)
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            properties: ImmutableDictionary<string, string?>.Empty
                .Add(FactoryInvocationPropertyName, replacement.Value.FactoryInvocation)
                .Add(FactoryNamespacePropertyName, replacement.Value.FactoryNamespace),
            messageArgs: [substitutedType.Name, replacement.Value.FactoryInvocation]);

        context.ReportDiagnostic(diagnostic);
    }

    private static SubstituteReplacement? GetReplacement(string substitutedTypeFullName)
    {
        foreach (SubstituteReplacement replacement in Replacements)
        {
            if (replacement.SubstitutedTypeFullName == substitutedTypeFullName)
            {
                return replacement;
            }
        }

        return null;
    }
}
