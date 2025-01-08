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

        if (itemType is not null || prop.Type.Name == "BitArray")
        {
            var vectorAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SszVectorAttribute");
            if (vectorAttr is not null)
            {
                result.Length = vectorAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
            }
            var listAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SszListAttribute");
            if (listAttr is not null)
            {
                result.Limit = listAttr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
            }
        }

        return result;
    }

    private static ITypeSymbol? GetCollectionType(ITypeSymbol typeSymbol, Compilation compilation)
    {
        if (typeSymbol is IArrayTypeSymbol array)
        {
            return array.ElementType!;
        }

        INamedTypeSymbol? ienumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1");
        INamedTypeSymbol? enumerable = typeSymbol.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, ienumerableOfT));
        if (ienumerableOfT != null && enumerable is not null)
        {
            return enumerable.TypeArguments.First();
        }

        return null;
    }


    public required string Name { get; init; }
    public required SszType Type { get; init; }
    public Kind Kind
    {
        get
        {
            if (Limit is not null)
            {
                return Type.Name == "BitArray" ? Kind.BitList : Kind.List;
            }
            if (Length is not null)
            {
                return Type.Name == "BitArray" ? Kind.BitVector : Kind.Vector;
            }
            return Type.Kind;
        }
    }

    public bool HandledByStd => ((Kind & (Kind.Basic | Kind.BitVector | Kind.BitList)) != Kind.None) || (((Kind & (Kind.Vector | Kind.List)) != Kind.None) && Type.Kind == Kind.Basic);
    public bool IsCollection => (Kind & Kind.Collection) != Kind.None;

    public bool IsVariable => (Kind & (Kind.List | Kind.BitList)) != Kind.None || Type.IsVariable;

    public int StaticLength
    {
        get
        {
            if (IsVariable)
            {
                return 4;
            }
            try
            {
                return Kind switch
                {
                    Kind.Vector => Length!.Value * Type.StaticLength,
                    Kind.BitVector => (Length!.Value + 7) / 8,
                    _ => Type.StaticLength,
                };
            }
            catch { throw; }
        }
    }

    public int? Length { get; set; }
    public int? Limit { get; set; }
}
