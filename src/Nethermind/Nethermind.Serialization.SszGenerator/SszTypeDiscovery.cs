// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;

internal sealed class SszTypeDiscovery(Compilation compilation)
{
    private const string AttributeNamespace = "Nethermind.Serialization.Ssz.";
    private readonly Compilation _compilation = compilation;

    public CollectionInfo? GetCollection(ITypeSymbol type, bool validate = true)
    {
        if (type is IArrayTypeSymbol array)
        {
            if (validate && array.Rank != 1)
            {
                throw new InvalidOperationException($"SSZ collection {type.ToDisplayString()} must be a one-dimensional array.");
            }
            return new(array.ElementType, CollectionStorage.Array, CollectionDisposal.None);
        }
        if (type is not INamedTypeSymbol named)
        {
            return null;
        }
        CollectionStorage? storage = IsType(named.OriginalDefinition, "System.Span`1") ? CollectionStorage.Span
            : IsType(named.OriginalDefinition, "System.ReadOnlySpan`1") ? CollectionStorage.ReadOnlySpan
            : IsType(named.OriginalDefinition, "System.Memory`1") ? CollectionStorage.Memory
            : IsType(named.OriginalDefinition, "System.ReadOnlyMemory`1") ? CollectionStorage.ReadOnlyMemory
            : IsType(named.OriginalDefinition, "System.Collections.Generic.List`1") ? CollectionStorage.List : null;
        if (storage is not null)
        {
            return new(named.TypeArguments[0], storage.Value, CollectionDisposal.None);
        }

        ITypeSymbol? item = null;
        foreach (INamedTypeSymbol implemented in named.AllInterfaces)
        {
            if (IsType(implemented.OriginalDefinition, "System.Collections.Generic.IList`1"))
            {
                item = implemented.TypeArguments[0];
                break;
            }
        }
        IMethodSymbol? asSpan = null;
        foreach (ISymbol symbol in named.GetMembers("AsSpan"))
        {
            if (symbol is IMethodSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false, Arity: 0, Parameters.Length: 0 } method
                && method.ReturnType is INamedTypeSymbol returned
                && (IsType(returned.OriginalDefinition, "System.Span`1") || IsType(returned.OriginalDefinition, "System.ReadOnlySpan`1"))
                && (item is null || SymbolEqualityComparer.Default.Equals(item, returned.TypeArguments[0])))
            {
                item = returned.TypeArguments[0];
                asSpan = method;
                break;
            }
        }
        if (item is null)
        {
            return null;
        }
        bool constructorFound = false;
        foreach (IMethodSymbol constructor in named.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 1
                && constructor.Parameters[0].RefKind == RefKind.None && constructor.Parameters[0].Type is INamedTypeSymbol parameter
                && IsType(parameter.OriginalDefinition, "System.ReadOnlySpan`1")
                && SymbolEqualityComparer.Default.Equals(parameter.TypeArguments[0], item))
            {
                constructorFound = true;
                break;
            }
        }
        if (validate && (asSpan is null || !constructorFound))
        {
            throw new InvalidOperationException($"Collection type {type.ToDisplayString()} must be an array, span, memory, List<T>, or expose public AsSpan() plus a public constructor(ReadOnlySpan<T>).");
        }
        CollectionDisposal disposal = CollectionDisposal.None;
        foreach (INamedTypeSymbol implemented in named.AllInterfaces)
        {
            if (IsType(implemented, "System.IDisposable")) disposal = CollectionDisposal.DisposableInterface;
        }
        foreach (ISymbol member in named.GetMembers("Dispose"))
        {
            if (member is IMethodSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false, Arity: 0, Parameters.Length: 0, ReturnsVoid: true }
                && (named.IsRefLikeType || disposal != CollectionDisposal.None))
            {
                disposal = CollectionDisposal.DisposeMethod;
                break;
            }
        }
        return new(item, CollectionStorage.CustomSpan, disposal);
    }

    public static List<IPropertySymbol> GetProperties(INamedTypeSymbol type)
    {
        List<INamedTypeSymbol> chain = [];
        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType == SpecialType.None; current = current.BaseType)
        {
            chain.Add(current);
        }
        chain.Reverse();
        Dictionary<string, IPropertySymbol> canonical = new(StringComparer.Ordinal);
        List<string> order = [];
        foreach (INamedTypeSymbol current in chain)
        {
            foreach (ISymbol symbol in current.GetMembers())
            {
                if (symbol is not IPropertySymbol { GetMethod.DeclaredAccessibility: Accessibility.Public, SetMethod.DeclaredAccessibility: Accessibility.Public } property)
                {
                    continue;
                }
                if (!canonical.ContainsKey(property.Name))
                {
                    order.Add(property.Name);
                }
                canonical[property.Name] = property;
            }
        }
        List<IPropertySymbol> result = new(order.Count);
        foreach (string name in order)
        {
            IPropertySymbol property = canonical[name];
            if (GetAttribute(property, "SszIgnoreAttribute") is null)
            {
                result.Add(property);
            }
        }
        return result;
    }

    public static AttributeData? GetAttribute(ISymbol symbol, string name)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == AttributeNamespace + name)
            {
                return attribute;
            }
        }
        return null;
    }

    public bool IsType(ITypeSymbol type, string metadataName) =>
        SymbolEqualityComparer.Default.Equals(type, _compilation.GetTypeByMetadataName(metadataName));

    public static string TypeName(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public static bool IsNullable(ITypeSymbol type) => type.NullableAnnotation == NullableAnnotation.Annotated;

    internal sealed record CollectionInfo(ITypeSymbol Element, CollectionStorage Storage, CollectionDisposal Disposal);
}
