using Microsoft.CodeAnalysis;

internal sealed class SszVectorConverterInfo
{
    private const string LengthMemberName = "Length";

    public required string TargetName { get; init; }
    public required string TargetNamespace { get; init; }
    public required string TargetTypeReferenceName { get; init; }
    public required string ConverterStaticMemberAccess { get; init; }
    public required int Length { get; init; }
    public required bool IsSszPrimitive { get; init; }

    public static IEnumerable<SszVectorConverterInfo> Find(Compilation compilation)
    {
        INamedTypeSymbol? converterInterface = compilation.GetTypeByMetadataName("Nethermind.Serialization.Ssz.SszVectorConverter`1");
        if (converterInterface is null)
        {
            return [];
        }

        Dictionary<string, SszVectorConverterInfo> result = new(StringComparer.Ordinal);
        foreach (INamedTypeSymbol converterType in EnumerateAvailableTypes(compilation))
        {
            SszVectorConverterInfo? converter = TryCreate(converterInterface, converterType);
            if (converter is not null)
            {
                string key = converter.TargetNamespace + "." + converter.TargetTypeReferenceName;
                if (result.TryGetValue(key, out SszVectorConverterInfo? existingConverter))
                {
                    throw new InvalidOperationException($"Multiple SSZ converters found for {key}: {existingConverter.ConverterStaticMemberAccess} and {converter.ConverterStaticMemberAccess}.");
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

    private static SszVectorConverterInfo? TryCreate(INamedTypeSymbol converterInterface, INamedTypeSymbol converterType)
    {
        INamedTypeSymbol? implementedInterface = converterType.AllInterfaces.FirstOrDefault(
            i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, converterInterface));
        if (implementedInterface is null || converterType.DeclaredAccessibility != Accessibility.Public)
        {
            return null;
        }

        ITypeSymbol targetType = implementedInterface.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (!TryGetLength(converterType, out int length, out string? lengthError))
        {
            throw new InvalidOperationException(lengthError!);
        }

        if (!HasFromSpanMethod(converterType, targetType))
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must declare public static {targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} FromSpan(ReadOnlySpan<byte> span).");
        }

        if (!HasToSpanMethod(converterType, targetType))
        {
            throw new InvalidOperationException($"SSZ converter {converterType.ToDisplayString()} must declare public static void ToSpan(Span<byte> span, {targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} value).");
        }

        return new()
        {
            TargetName = GetTypeName(targetType),
            TargetNamespace = GetNamespace(targetType),
            TargetTypeReferenceName = targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ConverterStaticMemberAccess = converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Length = length,
            IsSszPrimitive = IsPackedSszPrimitive(targetType),
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
            .Any(m => m is { IsStatic: true, Parameters.Length: 1 }
                && SymbolEqualityComparer.Default.Equals(m.ReturnType, targetType)
                && IsSpanOfByte(m.Parameters[0].Type, nameof(ReadOnlySpan<byte>)));

    private static bool HasToSpanMethod(INamedTypeSymbol converterType, ITypeSymbol targetType) =>
        converterType.GetMembers("ToSpan")
            .OfType<IMethodSymbol>()
            .Any(m => m is { IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 }
                && IsSpanOfByte(m.Parameters[0].Type, nameof(Span<byte>))
                && SymbolEqualityComparer.Default.Equals(m.Parameters[1].Type, targetType));

    private static bool IsSpanOfByte(ITypeSymbol type, string name) =>
        type is INamedTypeSymbol { IsGenericType: true, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true }, TypeArguments.Length: 1 } named
        && named.Name == name
        && named.TypeArguments[0].SpecialType == SpecialType.System_Byte;

    private static string GetNamespace(ITypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToString() : string.Empty;

    private static string GetTypeName(ITypeSymbol type) =>
        string.IsNullOrEmpty(type.ContainingNamespace?.ToString()) ? type.ToString() : type.Name;

    private static bool IsPackedSszPrimitive(ITypeSymbol type) =>
        type.SpecialType
            is SpecialType.System_Byte
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Boolean
        || type is { Name: "UInt256", ContainingNamespace.Name: "Int256", ContainingNamespace.ContainingNamespace.Name: "Nethermind" };
}
