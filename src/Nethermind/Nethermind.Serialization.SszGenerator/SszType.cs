using Microsoft.CodeAnalysis;
using System.Data.Common;

class SszType
{
    static SszType()
    {
        InbuiltTypes.Add("Int32", new SszType
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

        InbuiltTypes.Add("UInt32", new SszType
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

        InbuiltTypes.Add("Int64", new SszType
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

        InbuiltTypes.Add("UInt64", new SszType
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

        InbuiltTypes.Add("Byte", new SszType
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

        InbuiltTypes.Add("UInt256", new SszType
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

    public static Dictionary<string, SszType> InbuiltTypes { get; set; } = [];

    public static SszType From(SemanticModel semanticModel, Dictionary<string, SszType> types, ITypeSymbol type)
    {
        string typeName = type.Name;

        if(type.Name == "Nullable" && type is INamedTypeSymbol named)
        {
            var underlayingType = From(semanticModel, types, named.TypeArguments[0]);
            typeName = named.TypeArguments[0].Name.ToString() + "?";
            if (types.ContainsKey(typeName))
            {
                return types[typeName];
            }
            var result = types[typeName] = new SszType
            {
                Namespace = underlayingType.Namespace,
                Name = underlayingType.Name+"?",
                IsStruct = underlayingType.IsStruct,

                IsNullable = true,
                Members = underlayingType.Members,
                IsVariable = true,
                StaticLength = PointerLength,
                IsUnion = underlayingType.IsUnion,
                
            };

            return result;
        }
        if (types.ContainsKey(typeName))
        {
            return types[typeName];
        }
        {
            var result = types[typeName] = new SszType
            {
                Namespace = GetNamespace(type),
                Name = GetTypeName(type),
                IsStruct = type.TypeKind == TypeKind.Struct,

                IsNullable = false,

                IsUnion = type.GetMembers().OfType<IPropertySymbol>().Any(x => x.Name == "Selector" && x.Type.TypeKind == TypeKind.Enum),
            };

            result.IsVariable = type.NullableAnnotation == NullableAnnotation.Annotated || type.TypeKind != TypeKind.Structure || result.Members.Any(m => m.Type.IsVariable);
            result.Members = type.GetMembers().OfType<IPropertySymbol>().Where(p => p.GetMethod is not null && p.SetMethod is not null).Select(prop => SszProperty.From(semanticModel, types, prop)).ToArray() ?? [];
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
    public bool IsSimple => !Members.Any();

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
}


class SszProperty
{
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

            StaticEncode = $"Ssz.Ssz.Encode(buf.Slice({{offset}}, {type.StaticLength}), {(type.IsVariable ? $"{{dynOffset}}" : $"{valueGetter}")})",
            DynamicEncode = type.IsVariable ? $"if (container.{prop.Name} is not null) Ssz.Ssz.Encode(buf.Slice({{dynOffset}}, {{length}}), {valueGetter})" : null,

            StaticDecode = type.IsVariable ? $"int {{dynOffset}} = Ssz.Ssz.DecodeInt(data.Slice({{offset}}, {type.StaticLength}))" :
                           type.IsSimple ? $"{valueSetter} = Ssz.Ssz.{type.DecodeMethod}(data.Slice({{offset}}, {type.StaticLength}))" :
                                            $"Deserialize(data.Slice({{offset}}, {type.StaticLength}), out {valueSetter})",
            DynamicDecode = !type.IsVariable ? null :
                            type.IsSimple ? $"if ({{dynOffsetNext}} - {{dynOffset}} > 0) {valueSetter} = Ssz.Ssz.{type.DecodeMethod}(data.Slice({{dynOffset}}, {{dynOffsetNext}} - {{dynOffset}}))" :
                                            $"if ({{dynOffsetNext}} - {{dynOffset}} > 0) Deserialize{type.DecodeMethod}(data.Slice({{dynOffset}}, {{dynOffsetNext}} - {{dynOffset}}), out {valueSetter})",
            DynamicLength = type.IsVariable ? GetDynamicLength(prop, type) : null,
        };
    }

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

    public required string ValueAccessor { get; init; }


    public required string StaticEncode { get; init; }
    public required string? DynamicEncode { get; init; }
    public required string StaticDecode { get; init; }
    public required string? DynamicDecode { get; init; }
    public required string? DynamicLength { get; init; }
}
