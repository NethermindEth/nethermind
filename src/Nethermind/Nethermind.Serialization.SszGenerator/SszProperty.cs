using System.Collections;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Nethermind.Serialization.Ssz;

class SszProperty
{
    public override string ToString() =>
        $"prop({Kind},{Type},{Name},{(IsVariable ? "v" : "f")})";

    public static SszProperty From(SemanticModel semanticModel, List<SszType> types, IPropertySymbol prop)
    {
        CollectionInfo? collection = GetCollectionInfo(prop.Type, semanticModel.Compilation);
        ITypeSymbol? itemType = collection?.ItemType;
        ImmutableArray<AttributeData> attributes = prop.GetAttributes();

        SszType type = SszType.From(semanticModel, types, itemType ?? prop.Type);

        SszProperty result = new()
        {
            Name = prop.Name,
            Type = type,
            IsArrayProperty = prop.Type is IArrayTypeSymbol,
            IsSpanLikeProperty = SszTypeHelpers.IsSpanType(prop.Type),
            IsReadOnlySpanProperty = SszTypeHelpers.IsReadOnlySpanType(prop.Type),
            IsMemoryLikeProperty = SszTypeHelpers.IsMemoryType(prop.Type),
            IsReadOnlyMemoryProperty = SszTypeHelpers.IsReadOnlyMemoryType(prop.Type),
            IsNullable = prop.Type.NullableAnnotation == NullableAnnotation.Annotated,
            IsListProperty = collection?.IsList ?? false,
            HasCollectionAsSpan = collection?.HasAsSpan ?? false,
            CanConstructCollectionFromReadOnlySpan = collection?.CanConstructFromReadOnlySpan ?? false,
            CollectionTypeReferenceName = collection?.TypeReferenceName,
        };

        if (itemType is not null
            && !result.IsArrayProperty
            && !result.IsSpanLikeProperty
            && !result.IsMemoryLikeProperty
            && !result.IsListProperty
            && (!result.HasCollectionAsSpan || !result.CanConstructCollectionFromReadOnlySpan))
        {
            throw new InvalidOperationException(
                $"Collection property {prop.ContainingType.Name}.{prop.Name} must be an array, span, memory, List<T>, or expose public AsSpan() plus a public constructor(ReadOnlySpan<T>).");
        }

        AttributeData? fieldAttr = GetAttribute(attributes, nameof(SszFieldAttribute));
        if (fieldAttr is not null)
        {
            result.FieldIndex = fieldAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
        }

        if (itemType is not null || prop.Type.Name == nameof(BitArray))
        {
            AttributeData? vectorAttr = GetAttribute(attributes, nameof(SszVectorAttribute));
            if (vectorAttr is not null)
            {
                result.Length = vectorAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
            }

            AttributeData? listAttr = GetAttribute(attributes, nameof(SszListAttribute));
            if (listAttr is not null)
            {
                ulong limit = listAttr.ConstructorArguments.FirstOrDefault().Value as ulong? ?? 0UL;
                if (prop.Type.Name == nameof(BitArray) && limit > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Bitlist property {prop.ContainingType.Name}.{prop.Name} declares limit {limit}, but a BitArray cannot exceed int.MaxValue bits.");
                }

                result.Limit = limit;
            }

            result.IsProgressiveList = HasAttribute(attributes, nameof(SszProgressiveListAttribute));
            result.IsProgressiveBitList = HasAttribute(attributes, nameof(SszProgressiveBitlistAttribute));
        }

        return result;
    }

    private static CollectionInfo? GetCollectionInfo(ITypeSymbol typeSymbol, Compilation compilation)
    {
        if (typeSymbol is IArrayTypeSymbol array)
        {
            return new CollectionInfo(array.ElementType!);
        }

        if (SszTypeHelpers.IsSpanType(typeSymbol))
        {
            return new CollectionInfo(((INamedTypeSymbol)typeSymbol).TypeArguments[0]);
        }

        if (SszTypeHelpers.IsMemoryType(typeSymbol))
        {
            return new CollectionInfo(((INamedTypeSymbol)typeSymbol).TypeArguments[0]);
        }

        INamedTypeSymbol? iListOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1");
        INamedTypeSymbol? listOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        INamedTypeSymbol? iList = typeSymbol.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, iListOfT));
        if (iListOfT != null && iList is not null)
        {
            ITypeSymbol itemType = iList.TypeArguments.First();
            return new CollectionInfo(
                itemType,
                IsListType(typeSymbol, listOfT),
                HasAsSpan(typeSymbol, compilation, itemType),
                CanConstructFromReadOnlySpan(typeSymbol, compilation, itemType),
                typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        IMethodSymbol? asSpan = GetAsSpanMethod(typeSymbol, compilation);
        if (asSpan is not null)
        {
            ITypeSymbol itemType = ((INamedTypeSymbol)asSpan.ReturnType).TypeArguments[0];
            return new CollectionInfo(
                itemType,
                IsList: false,
                HasAsSpan: true,
                CanConstructFromReadOnlySpan(typeSymbol, compilation, itemType),
                typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        return null;
    }

    private static bool IsListType(ITypeSymbol typeSymbol, INamedTypeSymbol? listOfT) =>
        typeSymbol is INamedTypeSymbol named
        && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, listOfT);

    private static bool HasAsSpan(ITypeSymbol typeSymbol, Compilation compilation, ITypeSymbol itemType) =>
        GetAsSpanMethod(typeSymbol, compilation, itemType) is not null;

    private static IMethodSymbol? GetAsSpanMethod(ITypeSymbol typeSymbol, Compilation compilation, ITypeSymbol? itemType = null)
    {
        INamedTypeSymbol? spanOfT = compilation.GetTypeByMetadataName("System.Span`1");
        INamedTypeSymbol? readOnlySpanOfT = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
        return typeSymbol.GetMembers("AsSpan")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method => method is
            {
                DeclaredAccessibility: Accessibility.Public,
                IsStatic: false,
                Parameters.Length: 0,
            } && IsSpanOf(method.ReturnType, spanOfT, readOnlySpanOfT, itemType));
    }

    private static bool CanConstructFromReadOnlySpan(ITypeSymbol typeSymbol, Compilation compilation, ITypeSymbol itemType)
    {
        INamedTypeSymbol? readOnlySpanOfT = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
        return typeSymbol is INamedTypeSymbol named
            && named.InstanceConstructors.Any(constructor => constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length == 1
                && IsReadOnlySpanOf(constructor.Parameters[0].Type, readOnlySpanOfT, itemType));
    }

    private static bool IsSpanOf(ITypeSymbol typeSymbol, INamedTypeSymbol? spanOfT, INamedTypeSymbol? readOnlySpanOfT, ITypeSymbol? itemType) =>
        IsConstructedFrom(typeSymbol, spanOfT, itemType)
        || IsConstructedFrom(typeSymbol, readOnlySpanOfT, itemType);

    private static bool IsReadOnlySpanOf(ITypeSymbol typeSymbol, INamedTypeSymbol? readOnlySpanOfT, ITypeSymbol itemType) =>
        IsConstructedFrom(typeSymbol, readOnlySpanOfT, itemType);

    private static bool IsConstructedFrom(ITypeSymbol typeSymbol, INamedTypeSymbol? genericType, ITypeSymbol? itemType)
    {
        if (genericType is null || typeSymbol is not INamedTypeSymbol named
            || !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, genericType))
        {
            return false;
        }

        return itemType is null || SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], itemType);
    }

    private sealed record CollectionInfo(
        ITypeSymbol ItemType,
        bool IsList = false,
        bool HasAsSpan = false,
        bool CanConstructFromReadOnlySpan = false,
        string? TypeReferenceName = null);

    private static AttributeData? GetAttribute(ImmutableArray<AttributeData> attributes, string attributeName) =>
        attributes.FirstOrDefault(attribute => attribute.AttributeClass?.Name == attributeName);

    private static bool HasAttribute(ImmutableArray<AttributeData> attributes, string attributeName) =>
        attributes.Any(attribute => attribute.AttributeClass?.Name == attributeName);


    public required string Name { get; init; }
    public required SszType Type { get; init; }
    public bool IsArrayProperty { get; init; }
    public bool IsSpanLikeProperty { get; init; }
    public bool IsReadOnlySpanProperty { get; init; }
    public bool IsMemoryLikeProperty { get; init; }
    public bool IsReadOnlyMemoryProperty { get; init; }
    public bool IsNullable { get; init; }
    public bool IsListProperty { get; init; }
    public bool HasCollectionAsSpan { get; init; }
    public bool CanConstructCollectionFromReadOnlySpan { get; init; }
    public string? CollectionTypeReferenceName { get; init; }
    public int? FieldIndex { get; set; }
    public byte? SelectorValue { get; set; }
    public bool IsProgressiveList { get; set; }
    public bool IsProgressiveBitList { get; set; }
    public Kind Kind
    {
        get
        {
            if (IsProgressiveBitList)
            {
                return Kind.ProgressiveBitList;
            }

            if (Limit is not null)
            {
                return Type.Name == nameof(BitArray) ? Kind.BitList : Kind.List;
            }

            if (Length is not null)
            {
                return Type.Name == nameof(BitArray) ? Kind.BitVector : Kind.Vector;
            }

            if (IsProgressiveList)
            {
                return Kind.ProgressiveList;
            }

            return Type.Kind;
        }
    }

    public bool HandledByStd => ((Kind & (Kind.BitVector | Kind.BitList | Kind.ProgressiveBitList)) != Kind.None)
        || (((Kind & (Kind.Vector | Kind.List | Kind.ProgressiveList)) != Kind.None) && Type.Kind == Kind.Basic && Type.IsSszBasicType);
    public bool IsCollection => (Kind & Kind.Collection) != Kind.None;

    public bool IsVariable => (Kind & (Kind.List | Kind.BitList | Kind.ProgressiveList | Kind.ProgressiveBitList)) != Kind.None || Type.IsVariable;

    public int StaticLength
    {
        get
        {
            if (IsVariable)
            {
                return 4;
            }

            return Kind switch
            {
                Kind.Vector => Length!.Value * Type.StaticLength,
                Kind.BitVector => (Length!.Value + 7) / 8,
                _ => Type.StaticLength,
            };
        }
    }

    public int? Length { get; set; }
    public ulong? Limit { get; set; }

    public bool IsCompatibleWith(SszProperty other, HashSet<(SszType, SszType)> visited)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            Kind.Basic => Type.IsCompatibleWith(other.Type, visited),
            Kind.Container or Kind.ProgressiveContainer or Kind.CompatibleUnion => Type.IsCompatibleWith(other.Type, visited),
            Kind.Vector => Length == other.Length && Type.IsCompatibleWith(other.Type, visited),
            Kind.List => Limit == other.Limit && Type.IsCompatibleWith(other.Type, visited),
            Kind.ProgressiveList => Type.IsCompatibleWith(other.Type, visited),
            Kind.BitVector => Length == other.Length,
            Kind.BitList => Limit == other.Limit,
            Kind.ProgressiveBitList => true,
            _ => false,
        };
    }
}
