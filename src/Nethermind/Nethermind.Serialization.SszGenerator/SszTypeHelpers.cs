// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;

internal static class SszTypeHelpers
{
    public static bool IsSpanType(ITypeSymbol typeSymbol) =>
        typeSymbol is INamedTypeSymbol { IsGenericType: true, ContainingNamespace.Name: "System", TypeArguments.Length: 1 } named
            && (named.Name == "ReadOnlySpan" || named.Name == "Span");

    public static bool IsMemoryType(ITypeSymbol typeSymbol) =>
        typeSymbol is INamedTypeSymbol { IsGenericType: true, ContainingNamespace.Name: "System", TypeArguments.Length: 1 } named
            && (named.Name == "ReadOnlyMemory" || named.Name == "Memory");

    public static bool IsReadOnlySpanType(ITypeSymbol typeSymbol) =>
        IsSpanType(typeSymbol) && typeSymbol.Name == "ReadOnlySpan";

    public static bool IsReadOnlyMemoryType(ITypeSymbol typeSymbol) =>
        IsMemoryType(typeSymbol) && typeSymbol.Name == "ReadOnlyMemory";
}
