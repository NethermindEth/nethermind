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
            StaticLength = sizeof(byte),
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
            StaticLength = sizeof(ushort),
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

            result.Members = type.GetMembers().OfType<IPropertySymbol>().Where(p => p.GetMethod is not null && p.SetMethod is not null).Select(prop => SszProperty.From(semanticModel, types, prop)).ToArray() ?? [];
            result.IsVariable = result.Members.Any(m => m.Type.IsVariable) || (result.ElementType?.IsVariable ?? false);
            result.StaticLength = result.IsVariable ? PointerLength : result.Members.Sum(m => m.StaticLength);
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
        var isVector = prop.GetAttributes().Any(a => a.AttributeClass?.Name == "SszVectorAttribute");
        var limit = prop.GetAttributes().FirstOrDefault()?.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
        var staticLength = type.IsVariable ? SszType.PointerLength : type.IsCollection ? (isVector ? type.ElementType!.StaticLength * limit : SszType.PointerLength) : type.StaticLength;

        var isVariable = type.IsCollection ? (isVector ? type.IsVariable : true) : type.IsVariable;

        return new SszProperty
        {
            Type = type,
            Name = prop.Name,

            ValueAccessor = valueGetter,

            StaticEncode = $"SszLib.Encode(buf.Slice({{offset}}, {staticLength}), {(isVariable ? $"{{dynOffset}}" : $"{valueGetter}")})",
            DynamicEncode = isVariable ? $"if (container.{prop.Name} is not null) {(type.IsCollection && !(type.ElementType?.IsBasic ?? false) ? "Encode" : "SszLib.Encode")}(buf.Slice({{dynOffset}}, {{length}}), {valueGetter})" : null,

            StaticDecode = isVariable ? $"int {{dynOffset}} = SszLib.DecodeInt(data.Slice({{offset}}, {type.StaticLength}))" :
                           type.IsBasic ? $"{valueSetter} = SszLib.{type.DecodeMethod}(data.Slice({{offset}}, {type.StaticLength}))" :
                                            $"Decode(data.Slice({{offset}}, {type.StaticLength}), out {type.Name} {LowerStart(prop.Name)}); {valueSetter} = {LowerStart(prop.Name)}",
            DynamicDecode = !isVariable ? null :
                            type.IsBasic ? $"if ({{dynOffsetNext}} - {{dynOffset}} > 0) {valueSetter} = SszLib.{type.DecodeMethod}(data.Slice({{dynOffset}}, {{dynOffsetNext}} - {{dynOffset}}))" :
                                           $"if ({{dynOffsetNext}} - {{dynOffset}} > 0) {{ {type.DecodeMethod}(data.Slice({{dynOffset}}, {{dynOffsetNext}} - {{dynOffset}}), out {type.Name} value); {valueSetter} = value; }}",
            DynamicLength = isVariable ? GetDynamicLength(prop, type) : null,

            IsVector = isVector,
            Limit = limit,

            StaticLength = staticLength,
            IsVariable = isVariable,
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
    public required int StaticLength { get; init; }
    public required bool IsVariable { get; init; }
}


//public enum PropertyType
//{
//    Basic,
//    Container,
//    Vector,
//    List,
//    BitVector,
//    BitList,
//    Union,
//}
//public enum CollectionType
//{
//    Array,
//    List,
//}

//public class SszType2
//{
//    static Dictionary<string, SszType> Cache = [];

//    internal static SszType2? From(SemanticModel semanticModel, INamedTypeSymbol type)
//    {
//        var collectionAttributes = GetElementType(type, semanticModel.Compilation);
//        var members = type.GetMembers().OfType<IPropertySymbol>().Where(p => p.GetMethod is not null && p.SetMethod is not null).Select(prop => SszProperty.From(semanticModel, types, prop)).ToArray() ?? [];

//        return new SszType2
//        {
//            CollectionType = collectionAttributes?.Item1,
//            ElementType = collectionAttributes?.Item2,
//        };
//    }


//    private static (CollectionType, ITypeSymbol)? GetElementType(ITypeSymbol typeSymbol, Compilation compilation)
//    {
//        // Check if the type is an array
//        if (typeSymbol is IArrayTypeSymbol array)
//        {
//            return (CollectionType.Array, array.ElementType!);
//        }

//        // Check if the type implements IEnumerable<T>
//        INamedTypeSymbol? ienumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1");
//        INamedTypeSymbol? enumerable = typeSymbol.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, ienumerableOfT));
//        if (ienumerableOfT != null && enumerable is not null)
//        {
//            return (CollectionType.List, enumerable.TypeArguments.First());
//        }

//        // Not a collection type
//        return null;
//    }
//}

//public class Container
//{
//    public SszProperty2[] Props { get; set; } = [];
//}

//public class Collection
//{
//    public PropertyType ElementType { get; set; }
//    public int? Limit { get; set; }
//    public int? Length { get; set; }
//}


//public class SszProperty2
//{
//    public PropertyType Type { get; set; }

//    public int Limit { get; set; }
//}
