// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections;

public abstract class SszDiagnosticAnalyzer : DiagnosticAnalyzer
{
    protected static string[] SszSerializableAttributeNames = ["SszSerializable", "SszSerializableAttribute"];
    protected static string[] SszCollectionAttributeNames = ["SszVector", "SszList", "SszListAttribute", "SszVectorAttribute"];

    protected static bool IsSerializableType(TypeDeclarationSyntax type) =>
        type.AttributeLists.SelectMany(attrList => attrList.Attributes).Any(attr => SszSerializableAttributeNames.Contains(attr.ToString()));

    protected static bool IsCollectionType(ITypeSymbol typeSymbol) =>
            typeSymbol is IArrayTypeSymbol || (typeSymbol is INamedTypeSymbol namedTypeSymbol
                && (namedTypeSymbol.Name == nameof(BitArray) || namedTypeSymbol.AllInterfaces.Any(i => i.Name == nameof(IEnumerable))));

    protected static bool IsPropertyMarkedWithCollectionAttribute(PropertyDeclarationSyntax property) =>
        property.AttributeLists.SelectMany(attrList => attrList.Attributes).Any(attr => SszCollectionAttributeNames.Contains(attr.Name.ToString()));

    public static bool IsPublicGetSetProperty(PropertyDeclarationSyntax property) =>
        property.Modifiers.Any(SyntaxKind.PublicKeyword) &&
        property.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.GetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true &&
        property.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true;
}
