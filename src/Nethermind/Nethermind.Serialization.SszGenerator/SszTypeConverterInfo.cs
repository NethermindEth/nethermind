// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;

internal enum SszTypeConverterKind
{
    BasicType,
    VectorType,
}

internal sealed class SszTypeConverterInfo
{
    private const string LengthMemberName = "Length";

    public required string TargetName { get; init; }
    public required string TargetNamespace { get; init; }
    public required string TargetTypeReferenceName { get; init; }
    public required string ConverterNamespace { get; init; }
    public required string ConverterStaticMemberAccess { get; init; }
    public required string ConverterDisplayName { get; init; }
    public required int Length { get; init; }
    public required SszTypeConverterKind Kind { get; init; }

    public static IEnumerable<SszTypeConverterInfo> Find(Compilation compilation)
    {
        INamedTypeSymbol? basicTypeConverterAttribute = compilation.GetTypeByMetadataName("Nethermind.Serialization.Ssz.SszBasicTypeConverterAttribute`1");
        INamedTypeSymbol? vectorTypeConverterAttribute = compilation.GetTypeByMetadataName("Nethermind.Serialization.Ssz.SszVectorTypeConverterAttribute`1");
        if (basicTypeConverterAttribute is null && vectorTypeConverterAttribute is null)
        {
            return [];
        }

        Dictionary<string, SszTypeConverterInfo> result = new(StringComparer.Ordinal);
        foreach (INamedTypeSymbol converterType in EnumerateAvailableTypes(compilation))
        {
            SszTypeConverterInfo? basicTypeConverter = basicTypeConverterAttribute is null
                ? null
                : TryCreate(basicTypeConverterAttribute, SszTypeConverterKind.BasicType, converterType);
            SszTypeConverterInfo? vectorTypeConverter = vectorTypeConverterAttribute is null
                ? null
                : TryCreate(vectorTypeConverterAttribute, SszTypeConverterKind.VectorType, converterType);
            if (basicTypeConverter is not null && vectorTypeConverter is not null)
            {
                throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must use either SszBasicTypeConverter or SszVectorTypeConverter, not both.");
            }

            SszTypeConverterInfo? converter = basicTypeConverter ?? vectorTypeConverter;
            if (converter is not null)
            {
                string key = converter.TargetNamespace + "." + converter.TargetTypeReferenceName;
                if (result.TryGetValue(key, out SszTypeConverterInfo? existingConverter))
                {
                    throw new InvalidOperationException($"Multiple SSZ converters found for {key}: {existingConverter.ConverterDisplayName} and {converter.ConverterDisplayName}.");
                }

                result.Add(key, converter);
            }
        }

        return result.Values.OrderBy(x => x.TargetNamespace, StringComparer.Ordinal)
            .ThenBy(x => x.TargetTypeReferenceName, StringComparer.Ordinal);
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateAvailableTypes(Compilation compilation)
    {
        foreach (INamedTypeSymbol type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
        {
            yield return type;
        }

        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            foreach (INamedTypeSymbol type in EnumerateTypes(assembly.GlobalNamespace))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (ISymbol member in namespaceSymbol.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                foreach (INamedTypeSymbol type in EnumerateTypes(childNamespace))
                {
                    yield return type;
                }
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (INamedTypeSymbol nested in EnumerateNestedTypes(type))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        yield return type;
        foreach (INamedTypeSymbol nestedType in type.GetTypeMembers())
        {
            foreach (INamedTypeSymbol nested in EnumerateNestedTypes(nestedType))
            {
                yield return nested;
            }
        }
    }

    private static SszTypeConverterInfo? TryCreate(INamedTypeSymbol converterAttribute, SszTypeConverterKind converterKind, INamedTypeSymbol converterType)
    {
        AttributeData? attribute = converterType.GetAttributes().FirstOrDefault(
            a => SymbolEqualityComparer.Default.Equals(a.AttributeClass?.OriginalDefinition, converterAttribute));
        if (attribute is null)
        {
            return null;
        }

        if (converterType.DeclaredAccessibility != Accessibility.Public || !converterType.IsStatic)
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must be a public static class.");
        }

        if (attribute.AttributeClass is not INamedTypeSymbol attributeClass || attributeClass.TypeArguments.Length != 1)
        {
            return null;
        }

        ITypeSymbol targetType = attributeClass.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (!TryGetLength(converterType, out int length, out string? lengthError))
        {
            throw new InvalidOperationException(lengthError!);
        }

        if (!HasFromSpanMethod(converterType, targetType))
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must declare public static {targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} FromSpan(ReadOnlySpan<byte> span).");
        }

        if (!HasCollectionFromSpanMethod(converterType, targetType))
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must declare public static void FromSpan(ReadOnlySpan<byte> span, Span<{targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}> values).");
        }

        if (!HasToSpanMethod(converterType, targetType))
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must declare public static void ToSpan(Span<byte> span, {targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} value).");
        }

        if (!HasCollectionToSpanMethod(converterType, targetType))
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must declare public static void ToSpan(Span<byte> span, ReadOnlySpan<{targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}> values).");
        }

        if (!HasFeedMethod(converterType, targetType))
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must declare public static void Feed(ref Merkleizer merkleizer, {targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} value).");
        }

        return new()
        {
            TargetName = GetTypeName(targetType),
            TargetNamespace = GetNamespace(targetType),
            TargetTypeReferenceName = targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ConverterNamespace = GetNamespace(converterType),
            ConverterStaticMemberAccess = converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ConverterDisplayName = converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Length = length,
            Kind = converterKind,
        };
    }

    private static bool TryGetLength(INamedTypeSymbol converterType, out int length, out string? error)
    {
        IFieldSymbol? lengthField = converterType.GetMembers(LengthMemberName)
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f => f is { DeclaredAccessibility: Accessibility.Public, IsStatic: true, HasConstantValue: true }
                && f.Type.SpecialType == SpecialType.System_Int32);

        if (lengthField?.ConstantValue is int value && value > 0)
        {
            length = value;
            error = null;
            return true;
        }

        length = 0;
        error = $"SSZ converter {converterType.ToDisplayString()} must declare public const int {LengthMemberName} with a positive value.";
        return false;
    }

    private static bool HasFromSpanMethod(INamedTypeSymbol converterType, ITypeSymbol targetType) =>
        converterType.GetMembers("FromSpan")
            .OfType<IMethodSymbol>()
            .Any(m => m is { DeclaredAccessibility: Accessibility.Public, IsStatic: true, Parameters.Length: 1 }
                && SymbolEqualityComparer.Default.Equals(m.ReturnType, targetType)
                && IsSpanOfByte(m.Parameters[0].Type, nameof(ReadOnlySpan<byte>)));

    private static bool HasCollectionFromSpanMethod(INamedTypeSymbol converterType, ITypeSymbol targetType) =>
        converterType.GetMembers("FromSpan")
            .OfType<IMethodSymbol>()
            .Any(m => m is { DeclaredAccessibility: Accessibility.Public, IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 }
                && IsSpanOfByte(m.Parameters[0].Type, nameof(ReadOnlySpan<byte>))
                && IsSpanOfType(m.Parameters[1].Type, nameof(Span<byte>), targetType));

    private static bool HasToSpanMethod(INamedTypeSymbol converterType, ITypeSymbol targetType) =>
        converterType.GetMembers("ToSpan")
            .OfType<IMethodSymbol>()
            .Any(m => m is { DeclaredAccessibility: Accessibility.Public, IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 }
                && IsSpanOfByte(m.Parameters[0].Type, nameof(Span<byte>))
                && SymbolEqualityComparer.Default.Equals(m.Parameters[1].Type, targetType));

    private static bool HasCollectionToSpanMethod(INamedTypeSymbol converterType, ITypeSymbol targetType) =>
        converterType.GetMembers("ToSpan")
            .OfType<IMethodSymbol>()
            .Any(m => m is { DeclaredAccessibility: Accessibility.Public, IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 }
                && IsSpanOfByte(m.Parameters[0].Type, nameof(Span<byte>))
                && IsSpanOfType(m.Parameters[1].Type, nameof(ReadOnlySpan<byte>), targetType));

    private static bool HasFeedMethod(INamedTypeSymbol converterType, ITypeSymbol targetType) =>
        converterType.GetMembers("Feed")
            .OfType<IMethodSymbol>()
            .Any(m => m is { DeclaredAccessibility: Accessibility.Public, IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 }
                && m.Parameters[0].RefKind == RefKind.Ref
                && m.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Nethermind.Serialization.Ssz.Merkleization.Merkleizer"
                && SymbolEqualityComparer.Default.Equals(m.Parameters[1].Type, targetType));

    private static bool IsSpanOfByte(ITypeSymbol type, string name) =>
        type is INamedTypeSymbol { IsGenericType: true, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true }, TypeArguments.Length: 1 } named
        && named.Name == name
        && named.TypeArguments[0].SpecialType == SpecialType.System_Byte;

    private static bool IsSpanOfType(ITypeSymbol type, string name, ITypeSymbol elementType) =>
        type is INamedTypeSymbol { IsGenericType: true, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true }, TypeArguments.Length: 1 } named
        && named.Name == name
        && SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], elementType);

    private static string GetNamespace(ITypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToString() : string.Empty;

    private static string GetTypeName(ITypeSymbol type) =>
        string.IsNullOrEmpty(type.ContainingNamespace?.ToString()) ? type.ToString() : type.Name;
}
