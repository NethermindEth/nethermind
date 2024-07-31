using Microsoft.CodeAnalysis;

public partial class SszGenerator
{
    class SszType
    {
        public static SszType From(IPropertySymbol prop)
        {
            bool? isNullable = prop.Type.NullableAnnotation switch { NullableAnnotation.NotAnnotated => false, NullableAnnotation.Annotated => true, _ => null };

            var isDynamic = prop.Type.NullableAnnotation == NullableAnnotation.Annotated || prop.Type.TypeKind != TypeKind.Structure;
            var valueAccessor = GetValueAcccesor(prop);
            var staticLength = isDynamic ? PointerLength : GetStaticLength(prop.Type);

            return new SszType
            {
                Namespace = prop.ContainingNamespace.ToString(),
                Name = prop.Name,
                ValueAccessor = valueAccessor,

                IsNullable = isDynamic,
                IsDynamic = isDynamic,

                StaticLength = staticLength,
                DynamicLength = isDynamic ? GetDynamicLength(prop) : null,

                StaticEncode = $"Ssz.Encode(buf.Slice({{offset}}, {staticLength}), {(isDynamic ? $"{{dynOffset}}" : $"{valueAccessor}")})",
                DynamicEncode = isDynamic ? $"if (container.{prop.Name} is not null) Ssz.Encode(buf.Slice({{dynOffset}}, {{length}}), {GetValueAcccesor(prop)})" : null,
            };
        }

        private static int PointerLength = 4;

        private static int GetStaticLength(ITypeSymbol type)
        {
            return type.Name switch
            {
                "int" => 4,
                "long" => 8,
                "ulong" => 8,
                _ => 4,
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

        private static string GetValueAcccesor(IPropertySymbol prop)
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
    }
}


