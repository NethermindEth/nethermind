using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nethermind.Serialization.Ssz;
using System.Data.Common;
using System.Runtime;

class SszType
{
    static SszType()
    {
        BasicTypes.Add("Byte", new SszType
        {
            Namespace = "System",
            Name = "byte",
            IsVariable = false,
            IsNullable = true,
            IsStruct = true,
            IsUnion = false,
            Members = [],
            StaticLength = sizeof(int),
            DecodeMethod = "DecodeByte"
        });


        BasicTypes.Add("UInt16", new SszType
        {
            Namespace = "System",
            Name = "ushort",
            IsVariable = false,
            IsNullable = true,
            IsStruct = true,
            IsUnion = false,
            Members = [],
            StaticLength = sizeof(int),
            DecodeMethod = "DecodeUShort"
        });

        BasicTypes.Add("Int32", new SszType
        {
            Namespace = "System",
            Name = "int",
            IsVariable = false,
            IsNullable = true,
            IsStruct = true,
            IsUnion = false,
            Members = [],
            StaticLength = sizeof(int),
            DecodeMethod = "DecodeInt"
        });
        
        BasicTypes.Add("UInt32", new SszType
        {
            Namespace = "System",
            Name = "uint",
            IsVariable = false,
            IsNullable = true,
            IsStruct = true,
            IsUnion = false,
            Members = [],
            StaticLength = sizeof(uint),
            DecodeMethod = "DecodeUInt"
        });

        BasicTypes.Add("Int64", new SszType
        {
            Namespace = "System",
            Name = "long",
            IsVariable = false,
            IsNullable = true,
            IsStruct = true,
            IsUnion = false,
            Members = [],
            StaticLength = sizeof(long),
            DecodeMethod = "DecodeLong"
        });

        BasicTypes.Add("UInt64", new SszType
        {
            Namespace = "System",
            Name = "ulong",
            IsVariable = false,
            IsNullable = true,
            IsStruct = true,
            IsUnion = false,
            Members = [],
            StaticLength = sizeof(ulong),
            DecodeMethod = "DecodeULong"
        });

        BasicTypes.Add("UInt256", new SszType
        {
            Namespace = "Nethermind.Int256",
            Name = "UInt256",
            IsVariable = false,
            IsNullable = true,
            IsStruct = true,
            IsUnion = false,
            Members = [],
            StaticLength = 32,
            DecodeMethod = "DecodeUInt256"
        });
    }

    public static Dictionary<string, SszType> BasicTypes { get; set; } = [];

    public static SszType From(SemanticModel semanticModel, Dictionary<string, SszType> types, ITypeSymbol type)
    {
        string typeName = type.Name;

        //if(type.Name == "Nullable" && type is INamedTypeSymbol named)
        //{
        //    SszType underlayingType = From(semanticModel, types, named.TypeArguments[0]);
        //    typeName = named.TypeArguments[0].Name.ToString() + "?";
        //    if (types.ContainsKey(typeName))
        //    {
        //        return types[typeName];
        //    }

        //    ITypeSymbol? itemType = GetCollectionType(type, semanticModel.Compilation);

        //    SszType result = types[typeName] = new SszType
        //    {
        //        Namespace = underlayingType.Namespace,
        //        Name = underlayingType.Name+"?",
        //        IsStruct = underlayingType.IsStruct,

        //        IsNullable = true,
        //        Members = underlayingType.Members,
        //        IsVariable = true,
        //        StaticLength = PointerLength,
        //        IsUnion = underlayingType.IsUnion,

        //        IsCollection = itemType is not null,
        //    };

        //    return result;
        //}

        if (types.ContainsKey(typeName))
        {
            return types[typeName];
        }

        {
            ITypeSymbol? itemType = GetCollectionType(type, semanticModel.Compilation);

            SszType result = types[typeName] = new SszType
            {
                Namespace = GetNamespace(type),
                Name = GetTypeName(type),
                IsStruct = type.TypeKind == TypeKind.Struct,

                IsNullable = false,

                IsUnion = type.GetMembers().OfType<IPropertySymbol>().Any(x => x.Name == "Selector" && x.Type.TypeKind == TypeKind.Enum),

                IsCollection = itemType is not null,
                ElementType = itemType is not null ? From(semanticModel, types, itemType) : null,
            };

            result.IsVariable = type.NullableAnnotation == NullableAnnotation.Annotated || type.TypeKind != TypeKind.Structure;
            result.Members = type.GetMembers().OfType<IPropertySymbol>().Where(p => p.GetMethod is not null && p.SetMethod is not null).Select(prop => SszProperty.From(semanticModel, types, prop)).ToArray() ?? [];
            result.IsVariable |= result.Members.Any(m => m.Type.IsVariable) || (result.ElementType?.IsVariable ?? false);
            result.StaticLength = result.IsVariable ? PointerLength : result.Members.Sum(m => m.Type.StaticLength);
            return result;
        }
    }
   
    private static string? GetNamespace(ITypeSymbol syntaxNode)
    {
        return syntaxNode.ContainingNamespace?.ToString();
    }

    private static string GetTypeName(ITypeSymbol syntaxNode)
    {
        return string.IsNullOrEmpty(syntaxNode.ContainingNamespace?.ToString())? syntaxNode.ToString() : syntaxNode.ToString().Replace(syntaxNode.ContainingNamespace!.ToString() + ".", "");
    }

    public static int PointerLength = 4;

    public required bool IsUnion { get; init; }



    public required string? Namespace { get; init; }
    public required string Name { get; init; }


    public required bool IsNullable { get; init; }
    public bool IsVariable { get; set; }


    public int StaticLength { get; set; }



    public SszProperty[] Members { get; set; } = [];
    public required bool IsStruct { get; init; }
    public string DecodeMethod { get; init; } = "Decode";
    public bool IsProcessed { get; set; }
    public bool IsBasic => BasicTypes.Values.Contains(this);

    public bool IsCollection { get; set; }
    public SszType? ElementType { get; set; }

    public override string ToString()
    {
        return $"type({Namespace}::{Name})";
    }

    //private static string GetDecode(ITypeSymbol type)
    //{
    //    return type.ToString() switch
    //    {
    //        "int" => "DecodeInt",
    //        "int?" => "DecodeInt",
    //        "long" => "DecodeLong",
    //        "ulong" => "DecodeUlong",
    //        "byte[]" or "byte[]?" => "DecodeBytes",
    //        _ => "Decode",
    //    };
    //}

    //private static bool IsCollectionType(ITypeSymbol typeSymbol, Compilation compilation)
    //{
    //    // Check if the type is an array
    //    if (typeSymbol is IArrayTypeSymbol)
    //    {
    //        return true;
    //    }

    //    // Check if the type implements IEnumerable<T>
    //    INamedTypeSymbol? ienumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
    //    if (ienumerableOfT != null && typeSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, ienumerableOfT)))
    //    {
    //        return true;// enumerable.TypeArguments.First();
    //    }

    //    // Check if the type implements non-generic IEnumerable
    //    INamedTypeSymbol? ienumerable = compilation.GetTypeByMetadataName("System.Collections.IEnumerable");
    //    if (ienumerable != null && typeSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, ienumerable)))
    //    {
    //        return true;
    //    }

    //    // Not a collection type
    //    return false;
    //}

    private static ITypeSymbol? GetCollectionType(ITypeSymbol typeSymbol, Compilation compilation)
    {
        // Check if the type is an array
        if (typeSymbol is IArrayTypeSymbol array)
        {
            return array.ElementType!;
        }

        // Check if the type implements IEnumerable<T>
        INamedTypeSymbol? ienumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        INamedTypeSymbol? enumerable = typeSymbol.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, ienumerableOfT));
        if (ienumerableOfT != null && enumerable is not null)
        {
            return enumerable.TypeArguments.First();
        }

        // Check if the type implements non-generic IEnumerable
        INamedTypeSymbol? ienumerable = compilation.GetTypeByMetadataName("System.Collections.IEnumerable");
        enumerable = typeSymbol.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i, ienumerable));
        if (ienumerable != null && enumerable is not null)
        {
            return enumerable.TypeArguments.First();
        }

        // Not a collection type
        return null;
    }
}


class SszProperty
{
    static readonly MetadataReference SystemRuntimeReference =
            MetadataReference.CreateFromFile(typeof(GCSettings).Assembly.Location);
    public override string ToString()
    {
        return $"prop({Type} {Name})";
    }
    public static SszProperty From(SemanticModel semanticModel, Dictionary<string, SszType> types, IPropertySymbol prop)
    {
        var valueGetter = GetValueGetter(prop);
        var valueSetter = GetValueSetter(prop);
        var type = SszType.From(semanticModel, types, prop.Type);

        return new SszProperty
        {
            Type = type,
            Name = prop.Name,

            ValueAccessor = valueGetter,

            StaticEncode = $"SszLib.Encode(buf.Slice({{offset}}, {type.StaticLength}), {(type.IsVariable ? $"{{dynOffset}}" : $"{valueGetter}")})",
            DynamicEncode = type.IsVariable ? $"if (container.{prop.Name} is not null) {(type.IsCollection && !(type.ElementType?.IsBasic ?? false) ? "Encode" : "SszLib.Encode")}(buf.Slice({{dynOffset}}, {{length}}), {valueGetter})" : null,

            StaticDecode = type.IsVariable ? $"int {{dynOffset}} = SszLib.DecodeInt(data.Slice({{offset}}, {type.StaticLength}))" :
                           type.IsBasic ? $"{valueSetter} = SszLib.{type.DecodeMethod}(data.Slice({{offset}}, {type.StaticLength}))" :
                                            $"Decode(data.Slice({{offset}}, {type.StaticLength}), out {type.Name} {LowerStart(prop.Name)}); {valueSetter} = {LowerStart(prop.Name)}",
            DynamicDecode = !type.IsVariable ? null :
                            type.IsBasic ? $"if ({{dynOffsetNext}} - {{dynOffset}} > 0) {valueSetter} = SszLib.{type.DecodeMethod}(data.Slice({{dynOffset}}, {{dynOffsetNext}} - {{dynOffset}}))" :
                                           $"if ({{dynOffsetNext}} - {{dynOffset}} > 0) {{ {type.DecodeMethod}(data.Slice({{dynOffset}}, {{dynOffsetNext}} - {{dynOffset}}), out {type.Name} value); {valueSetter} = value; }}",
            DynamicLength = type.IsVariable ? GetDynamicLength(prop, type) : null,

            IsVector = prop.GetAttributes().Any(a=>a.AttributeClass?.Name == "SszVectorAttribute"),
            Limit = prop.GetAttributes().FirstOrDefault()?.ConstructorArguments.FirstOrDefault().Value as int? ?? 0,
        };
    }

    private static string LowerStart(string name) => string.IsNullOrEmpty(name) ? name : (name.Substring(0, 1).ToLower() + name.Substring(1));

    private static string GetValueGetter(IPropertySymbol prop)
    {
        if (prop.Type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            if (prop.Type.TypeKind == TypeKind.Structure)
            {
                return $"container.{prop.Name}.Value";
            }
        }

        return $"container.{prop.Name}";
    }
    private static string GetValueSetter(IPropertySymbol prop)
    {
        return $"container.{prop.Name}";
    }
   

    private static string? GetDynamicLength(IPropertySymbol prop, SszType type)
    {
        if (prop.Type.NullableAnnotation == NullableAnnotation.Annotated && prop.Type.TypeKind == TypeKind.Structure)
        {
            return $"(container.{prop.Name}.HasValue ? {type.StaticLength} : 0)";
        }

        return prop.Type.ToString() switch
        {
            "byte[]" or "byte[]?" => $"(container.{prop.Name}?.Length ?? 0)",
            _ => $"GetLength(container.{prop.Name})",
        };
    }

    public string FullLength => DynamicLength is null ? Type.StaticLength.ToString() : ($"{Type.StaticLength} + {DynamicLength}");

    public required SszType Type { get; init; }
    public required string Name { get; init; }
    public int Limit { get; set; }
    public bool IsVector { get; set; }

    public required string ValueAccessor { get; init; }


    public required string StaticEncode { get; init; }
    public required string? DynamicEncode { get; init; }
    public required string StaticDecode { get; init; }
    public required string? DynamicDecode { get; init; }
    public required string? DynamicLength { get; init; }
}
