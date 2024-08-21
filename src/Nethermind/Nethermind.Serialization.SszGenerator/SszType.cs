using Microsoft.CodeAnalysis;

public partial class SszGenerator
{
    class SszType
    {
        public static SszType From(IPropertySymbol prop)
        {
            bool? isNullable = prop.Type.NullableAnnotation switch { NullableAnnotation.NotAnnotated => false, NullableAnnotation.Annotated => true, _ => null };

            var isDynamic = prop.Type.NullableAnnotation == NullableAnnotation.Annotated || prop.Type.TypeKind != TypeKind.Structure;
            var valueGetter = GetValueGetter(prop);
            var valueSetter = GetValueSetter(prop);
            var staticLength = isDynamic ? PointerLength : GetStaticLength(prop.Type);

            return new SszType
            {
                Namespace = prop.ContainingNamespace.ToString(),
                Name = prop.Name,
                ValueAccessor = valueGetter,

                IsNullable = isDynamic,
                IsDynamic = isDynamic,

                StaticLength = staticLength,
                DynamicLength = isDynamic ? GetDynamicLength(prop) : null,

                StaticEncode = $"Ssz.Ssz.Encode(buf.Slice({{offset}}, {staticLength}), {(isDynamic ? $"{{dynOffset}}" : $"{valueGetter}")})",
                DynamicEncode = isDynamic ? $"if (container.{prop.Name} is not null) Ssz.Ssz.Encode(buf.Slice({{dynOffset}}, {{length}}), {valueGetter})" : null,

                StaticDecode = $"{(isDynamic ? $"int {{dynOffset}}" : $"{valueSetter}")} = Ssz.Ssz.{(isDynamic ? "DecodeInt" : GetDecode(prop.Type))}(data.Slice({{offset}}, {staticLength}))",
                DynamicDecode = isDynamic ? $"if ({{dynOffsetNext}} - {{dynOffset}} > 0) {valueSetter} = Ssz.Ssz.{GetDecode(prop.Type)}(data.Slice({{dynOffset}}, {{dynOffsetNext}} - {{dynOffset}}))" : null,
            };
        }

        private static int PointerLength = 4;

        private static int GetStaticLength(ITypeSymbol type)
        {
            return type.ToString() switch
            {
                "int" => 4,
                "long" => 8,
                "ulong" => 8,
                _ => 4,
            };
        }
        private static string GetDecode(ITypeSymbol type)
        {
            return type.ToString() switch
            {
                "int" => "DecodeInt",
                "int?" => "DecodeInt",
                "long" => "DecodeLong",
                "ulong" => "DecodeUlong",
                "byte[]" or "byte[]?" => "DecodeBytes",
                _ => "Decode",
            };
        }

        private static string? GetDynamicLength(IPropertySymbol prop)
        {
            if (prop.Type.NullableAnnotation == NullableAnnotation.Annotated && prop.Type.TypeKind == TypeKind.Structure)
            {
                return $"(container.{prop.Name}.HasValue ? {GetStaticLength((prop.Type as INamedTypeSymbol)!.TypeArguments.First())} : 0)";
            }

            return prop.Type.ToString() switch
            {
                "byte[]" or "byte[]?" => $"(container.{prop.Name}?.Length ?? 0)",
                _ => null,
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

        public required string Namespace { get; init; }
        public required string Name { get; init; }
        public required string ValueAccessor { get; init; }


        public required bool IsNullable { get; init; }
        public required bool IsDynamic { get; init; }


        public required int StaticLength { get; init; }
        public required string? DynamicLength { get; init; }
        public string FullLength => DynamicLength is null ? StaticLength.ToString() : ($"{StaticLength} + {DynamicLength}");


        public required string StaticEncode { get; init; }
        public required string? DynamicEncode { get; init; }
        public required string StaticDecode { get; init; }
        public required string? DynamicDecode { get; init; }
    }
}


