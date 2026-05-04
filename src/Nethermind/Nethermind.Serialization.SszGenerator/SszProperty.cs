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
        ITypeSymbol? itemType = GetCollectionType(prop.Type, semanticModel.Compilation);
        ImmutableArray<AttributeData> attributes = prop.GetAttributes();

        SszType type = SszType.From(semanticModel, types, itemType ?? prop.Type);

        SszProperty result = new()
        {
            Name = prop.Name,
            Type = type,
            IsArrayProperty = prop.Type is IArrayTypeSymbol,
        };

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
                result.Limit = listAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
            }

            result.IsProgressiveList = HasAttribute(attributes, nameof(SszProgressiveListAttribute));
            result.IsProgressiveBitList = HasAttribute(attributes, nameof(SszProgressiveBitlistAttribute));
        }

        return result;
    }

    private static ITypeSymbol? GetCollectionType(ITypeSymbol typeSymbol, Compilation compilation)
    {
        if (typeSymbol is IArrayTypeSymbol array)
        {
            return array.ElementType!;
        }

        INamedTypeSymbol? iListOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1");
        INamedTypeSymbol? enumerable = typeSymbol.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, iListOfT));
        if (iListOfT != null && enumerable is not null)
        {
            return enumerable.TypeArguments.First();
        }

        return null;
    }

    private static AttributeData? GetAttribute(ImmutableArray<AttributeData> attributes, string attributeName) =>
        attributes.FirstOrDefault(attribute => attribute.AttributeClass?.Name == attributeName);

    private static bool HasAttribute(ImmutableArray<AttributeData> attributes, string attributeName) =>
        attributes.Any(attribute => attribute.AttributeClass?.Name == attributeName);


    public required string Name { get; init; }
    public required SszType Type { get; init; }
    public bool IsArrayProperty { get; init; }
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

    public bool HandledByStd => ((Kind & (Kind.Basic | Kind.BitVector | Kind.BitList | Kind.ProgressiveBitList)) != Kind.None) || (((Kind & (Kind.Vector | Kind.List | Kind.ProgressiveList)) != Kind.None) && Type.Kind == Kind.Basic);
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
    public int? Limit { get; set; }

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
