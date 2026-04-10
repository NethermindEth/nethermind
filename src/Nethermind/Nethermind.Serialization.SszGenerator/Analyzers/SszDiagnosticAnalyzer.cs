// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections;
using Nethermind.Serialization.Ssz;

public abstract class SszDiagnosticAnalyzer : DiagnosticAnalyzer
{
    protected static readonly HashSet<string> SszRootAttributeNames = CreateAttributeNameSet(
        nameof(SszContainerAttribute),
        nameof(SszCompatibleUnionAttribute));

    protected static readonly HashSet<string> SszCollectionAttributeNames = CreateAttributeNameSet(
        nameof(SszVectorAttribute),
        nameof(SszListAttribute),
        nameof(SszProgressiveListAttribute),
        nameof(SszProgressiveBitlistAttribute));

    protected static bool IsSszRootType(TypeDeclarationSyntax type) =>
        type.AttributeLists.SelectMany(attrList => attrList.Attributes).Any(attr => MatchesAttributeName(attr, SszRootAttributeNames));

    protected static bool IsCollectionType(ITypeSymbol typeSymbol) =>
            typeSymbol is IArrayTypeSymbol || (typeSymbol is INamedTypeSymbol namedTypeSymbol
                && (namedTypeSymbol.Name == nameof(BitArray) || namedTypeSymbol.AllInterfaces.Any(i => i.Name == nameof(IEnumerable))));

    protected static bool IsPropertyMarkedWithCollectionAttribute(PropertyDeclarationSyntax property) =>
        property.AttributeLists.SelectMany(attrList => attrList.Attributes).Any(attr => MatchesAttributeName(attr, SszCollectionAttributeNames));

    protected static bool MatchesAttributeName(AttributeSyntax attribute, HashSet<string> attributeNames) =>
        attributeNames.Contains(GetAttributeName(attribute));

    public static bool IsPublicGetSetProperty(PropertyDeclarationSyntax property) =>
        property.Modifiers.Any(SyntaxKind.PublicKeyword) &&
        property.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.GetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true &&
        property.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true;

    private static HashSet<string> CreateAttributeNameSet(params string[] attributeTypeNames)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string attributeTypeName in attributeTypeNames)
        {
            names.Add(attributeTypeName);
            names.Add(TrimAttributeSuffix(attributeTypeName));
        }

        return names;
    }

    private static string GetAttributeName(AttributeSyntax attribute) => attribute.Name switch
    {
        IdentifierNameSyntax identifierName => identifierName.Identifier.ValueText,
        GenericNameSyntax genericName => genericName.Identifier.ValueText,
        QualifiedNameSyntax qualifiedName => qualifiedName.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliasQualifiedName => aliasQualifiedName.Name.Identifier.ValueText,
        _ => attribute.Name.ToString(),
    };

    private static string TrimAttributeSuffix(string attributeTypeName) =>
        attributeTypeName.EndsWith(nameof(Attribute), StringComparison.Ordinal)
            ? attributeTypeName.Substring(0, attributeTypeName.Length - nameof(Attribute).Length)
            : attributeTypeName;
}
