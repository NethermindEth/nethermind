using Microsoft.CodeAnalysis;

class SszProperty
{
    public override string ToString()
    {
        return $"prop({Kind},{Type},{Name},{(IsVariable ? "v" : "f")})";
    }

    public static SszProperty From(SemanticModel semanticModel, List<SszType> types, IPropertySymbol prop)
    {
        ITypeSymbol? itemType = GetCollectionType(prop.Type, semanticModel.Compilation);

        SszType type = SszType.From(semanticModel, types, itemType ?? prop.Type);

        SszProperty result = new SszProperty { Name = prop.Name, Type = type };

        AttributeData? fieldAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SszFieldAttribute");
        if (fieldAttr is not null)
        {
            result.FieldIndex = fieldAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
        }

        if (itemType is not null || prop.Type.Name == "BitArray")
        {
            AttributeData? vectorAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SszVectorAttribute");
            if (vectorAttr is not null)
            {
                result.Length = vectorAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
            }

            AttributeData? listAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SszListAttribute");
            if (listAttr is not null)
            {
                result.Limit = listAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
            }

            result.IsProgressiveList = prop.GetAttributes().Any(a => a.AttributeClass?.Name == "SszProgressiveListAttribute");
            result.IsProgressiveBitList = prop.GetAttributes().Any(a => a.AttributeClass?.Name == "SszProgressiveBitlistAttribute");
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


    public required string Name { get; init; }
    public required SszType Type { get; init; }
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
                return Type.Name == "BitArray" ? Kind.BitList : Kind.List;
            }

            if (Length is not null)
            {
                return Type.Name == "BitArray" ? Kind.BitVector : Kind.Vector;
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
