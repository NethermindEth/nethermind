using Microsoft.CodeAnalysis;
using System.Collections;
using System.Runtime.InteropServices;

class SszType
{
    private const string UnionSelectorPropertyName = "Selector";
    private const string UnionNoneMemberName = "None";

    static SszType()
    {
        Type[] intTypes = [typeof(byte), typeof(ushort), typeof(short), typeof(uint), typeof(int), typeof(ulong), typeof(long)];

        foreach (Type type in intTypes)
        {
            BasicTypes.Add(new SszType
            {
                Namespace = type.Namespace,
                Name = type.Name,
                Kind = Kind.Basic,
                StaticLength = Marshal.SizeOf(type),
            });
        }

        BasicTypes.Add(new SszType
        {
            Namespace = "System",
            Name = "UInt128",
            Kind = Kind.Basic,
            StaticLength = 16,
        });

        BasicTypes.Add(new SszType
        {
            Namespace = "Nethermind.Int256",
            Name = "UInt256",
            Kind = Kind.Basic,
            StaticLength = 32,
        });

        BasicTypes.Add(new SszType
        {
            Namespace = typeof(BitArray).Namespace,
            Name = nameof(BitArray),
            Kind = Kind.Basic,
        });
    }

    public static List<SszType> BasicTypes { get; set; } = [];
    public required string Name { get; init; }
    public required string? Namespace { get; init; }
    public required Kind Kind { get; init; }
    public SszProperty[]? Members { get; set; } = null;

    public bool IsStruct { get; set; }

    public bool HasNone { get; set; }
    public IEnumerable<SszProperty>? UnionMembers { get => Kind == Kind.Union ? Members.Where(x => x.Name != UnionSelectorPropertyName) : null; }
    public SszProperty? Selector { get => Members.FirstOrDefault(x => x.Name == UnionSelectorPropertyName); }

    public Dictionary<string, object>? UnionValues { get; set; }

    private int? length = null;

    public SszType? EnumType { get; set; }
    public int StaticLength { get => length ?? Members.Sum(x => x.StaticLength); set => length = value; }

    public bool IsVariable => Members is not null && Members.Any(x => x.IsVariable);

    public const int PointerLength = 4;

    internal static SszType From(SemanticModel semanticModel, List<SszType> types, ITypeSymbol type)
    {

        string? @namespace = GetNamespace(type);
        string name = GetTypeName(type);

        SszType? existingType = types.FirstOrDefault(t => t.Name == name && t.Namespace == @namespace);
        if (existingType is not null)
        {
            return existingType;
        }

        INamedTypeSymbol? enumType = (type as INamedTypeSymbol)?.EnumUnderlyingType;

        SszType result = new SszType
        {
            Namespace = @namespace,
            Name = name,
            Kind = type.GetMembers().OfType<IPropertySymbol>().Any(x => x.Name == UnionSelectorPropertyName && x.Type.TypeKind == TypeKind.Enum) ? Kind.Union : enumType is not null ? Kind.Basic : Kind.Container,
        };
        types.Add(result);

        if (enumType is not null)
        {
            result.EnumType = BasicTypes.First(x => x.Name == enumType.Name);
            result.StaticLength = result.EnumType.StaticLength;
            result.HasNone = (type as INamedTypeSymbol)?.MemberNames.Contains(UnionNoneMemberName) == true;
        }

        result.Members = result.Kind switch
        {
            Kind.Container or Kind.Union => type.GetMembers().OfType<IPropertySymbol>()
                .Where(p => p.GetMethod is not null && p.SetMethod is not null && p.GetMethod.DeclaredAccessibility == Accessibility.Public && p.SetMethod.DeclaredAccessibility == Accessibility.Public)
                .Select(prop => SszProperty.From(semanticModel, types, prop)).ToArray() ?? [],
            _ => null,
        };

        if (result.Kind == Kind.Union)
        {
            result.HasNone = result.Members.Any(x => x.Name == UnionSelectorPropertyName && x.Type.HasNone);
        }

        if ((result.Kind & (Kind.Container | Kind.Union)) != Kind.None && type.TypeKind == TypeKind.Struct)
        {
            result.IsStruct = true;
        }
        return result;
    }

    private static string? GetNamespace(ITypeSymbol syntaxNode)
    {
        return syntaxNode.ContainingNamespace?.ToString();
    }

    private static string GetTypeName(ITypeSymbol syntaxNode)
    {
        return string.IsNullOrEmpty(syntaxNode.ContainingNamespace?.ToString()) ? syntaxNode.ToString() : syntaxNode.Name.Replace(syntaxNode.ContainingNamespace!.ToString() + ".", "");
    }
    public override string ToString()
    {
        return $"type({Kind},{Name},{(IsVariable ? "v" : "f")})";
    }
}
