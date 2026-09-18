// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class ProcessOneValidatedPublicationExtractor
{
    private static PublicationSynchronousCallable BindPublicationSynchronous(
        PublicationSemanticContext context, string id, MethodDeclarationSyntax declaration)
    {
        IMethodSymbol symbol = context.Model(declaration).GetDeclaredSymbol(declaration)
            ?? throw new ExtractionException($"publication.synchronous.{id}: missing callable symbol.");
        static string[] Attributes(IMethodSymbol method) => method.GetAttributes().Select(attribute =>
            ArtifactSafety.TypeIdentity(attribute.AttributeClass) + "|" + attribute.AttributeClass?.ContainingAssembly.Name + "|" +
            (attribute.ConstructorArguments.Length != 0 || attribute.NamedArguments.Length != 0)).ToArray();
        string[] attributes = Attributes(symbol);
        List<string> overriddenMethods = [];
        List<string> inheritedAttributes = [];
        for (IMethodSymbol? overridden = symbol.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
        {
            overriddenMethods.Add(MethodKey(overridden));
            inheritedAttributes.AddRange(Attributes(overridden));
        }
        List<string> baseTypes = [];
        for (INamedTypeSymbol? parent = symbol.ContainingType.BaseType; parent is not null; parent = parent.BaseType)
            baseTypes.Add(parent.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "|" + parent.ContainingAssembly.Name);
        string[] interfaces = symbol.ContainingType.AllInterfaces.Select(type =>
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "|" + type.ContainingAssembly.Name).ToArray();
        string[] implementedMethods = symbol.ContainingType.AllInterfaces.SelectMany(static type => type.GetMembers())
            .OfType<IMethodSymbol>().Where(method => SymbolEqualityComparer.Default.Equals(
                symbol.ContainingType.FindImplementationForInterfaceMember(method), symbol)).Select(MethodKey).ToArray();
        PublicationSynchronousCallable identity = new(id, MethodKey(symbol), symbol.MethodKind.ToString(),
            declaration.Body is not null ? "block" : declaration.ExpressionBody is not null ? "expression" : "none",
            symbol.ReturnsVoid, symbol.IsAsync, declaration.DescendantNodes().OfType<YieldStatementSyntax>().Any(),
            symbol.IsPartialDefinition || symbol.PartialDefinitionPart is not null || symbol.PartialImplementationPart is not null,
            symbol.IsExtern, symbol.IsAbstract, attributes.Any(IsConditionalAttribute), attributes,
            symbol.IsStatic, symbol.IsVirtual, symbol.IsOverride, symbol.DeclaredAccessibility.ToString(),
            symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), symbol.ContainingAssembly.Name,
            baseTypes.ToArray(), interfaces, implementedMethods, overriddenMethods.ToArray(), inheritedAttributes.ToArray(),
            inheritedAttributes.Any(IsConditionalAttribute));
        return identity;
    }

    private static bool IsConditionalAttribute(string identity) =>
        identity.StartsWith("System.Diagnostics.ConditionalAttribute|", StringComparison.Ordinal);

    private static void ValidatePublicationSynchronous(PublicationSynchronousCallable callable)
    {
        if (callable.HasConditionalAttribute)
            throw new ExtractionException($"publication.synchronous.{callable.Id}.attributes: ConditionalAttribute is not admitted.");
        if (callable.HasInheritedConditionalAttribute)
            throw new ExtractionException($"publication.synchronous.{callable.Id}.lineage: inherited ConditionalAttribute is not admitted.");
        string expectedSymbol = callable.Id switch
        {
            "validateProcessedBlock" => ValidateProcessedBlockFqn,
            "postValidation" => PostValidationFqn,
            "storeTxReceipts" => StoreReceiptsFqn,
            _ => throw new ExtractionException("publication.synchronous.inventory: callable identity set changed."),
        };
        if (callable.Symbol != expectedSymbol || callable.MethodKind != "Ordinary" ||
            callable.BodyKind != (callable.Id == "storeTxReceipts" ? "expression" : "block") ||
            !callable.ReturnsVoid || callable.IsAsync || callable.IsIterator || callable.IsPartial ||
            callable.IsExtern || callable.IsAbstract)
            throw new ExtractionException($"publication.synchronous.{callable.Id}: callable must retain its synchronous concrete void body.");
        if (callable.Attributes is not { Length: 0 })
            throw new ExtractionException($"publication.synchronous.{callable.Id}.attributes: exact attribute inventory changed.");
        if (callable.IsStatic || callable.IsVirtual != (callable.Id == "postValidation") || callable.IsOverride ||
            callable.Accessibility != (callable.Id == "postValidation" ? "Protected" : "Private") ||
            callable.DeclaringType != "global::Nethermind.Consensus.Processing.BlockProcessor" ||
            callable.DeclaringAssembly != "Nethermind.Consensus" ||
            callable.BaseTypes is null || !callable.BaseTypes.SequenceEqual(["object|System.Private.CoreLib"]) ||
            callable.Interfaces is null || !callable.Interfaces.SequenceEqual(["global::Nethermind.Consensus.Processing.IBlockProcessor|Nethermind.Consensus"]) ||
            callable.ImplementedInterfaceMethods is not { Length: 0 } ||
            callable.OverriddenMethods is not { Length: 0 } || callable.InheritedAttributes is not { Length: 0 })
            throw new ExtractionException($"publication.synchronous.{callable.Id}.lineage: exact callable lineage changed.");
    }

    private static void ValidatePublicationSynchronousInventory(PublicationSynchronousCallable[] callables)
    {
        if (callables is null || callables.Any(static callable => callable is null) ||
            !callables.Select(static callable => callable.Id).SequenceEqual(
                ["validateProcessedBlock", "postValidation", "storeTxReceipts"], StringComparer.Ordinal))
            throw new ExtractionException("publication.synchronous.inventory: callable identity set changed.");
        foreach (PublicationSynchronousCallable callable in callables.Where(static callable =>
                     callable.HasConditionalAttribute || callable.HasInheritedConditionalAttribute))
            ValidatePublicationSynchronous(callable);
        foreach (PublicationSynchronousCallable callable in callables) ValidatePublicationSynchronous(callable);
    }
}
