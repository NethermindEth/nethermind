using Microsoft.CodeAnalysis;

class SszType
{
    private const string SelectorPropertyName = "Selector";

    // Types annotated with [SszBasicType] are discovered at generation time via
    // DiscoverKnownTypes(Compilation) — see below. The list here covers BCL primitives
    // and Nethermind core types that aren't (or can't be) decorated with that attribute.
    public static List<SszType> BasicTypes { get; set; } =
    [
        Primitive<byte>(),
        Primitive<ushort>(),
        Primitive<int>(),
        Primitive<uint>(),
        Primitive<long>(),
        Primitive<ulong>(),
        Primitive<bool>(),
        new()
        {
            Namespace = "Nethermind.Int256", Name = "UInt256",
            Kind = Kind.Basic, StaticLength = 32,
            CustomEncodeTemplate = "{1}.ToLittleEndian({0});",
            CustomDecodeTemplate = "{1} = new UInt256({0}, isBigEndian: false);",
        },
        new() { Namespace = "System.Collections", Name = "BitArray", Kind = Kind.Basic },
        new() { Namespace = "Nethermind.Serialization.Ssz", Name = "SszBytes32", Kind = Kind.Basic, StaticLength = 32 },
        BytesRef("Nethermind.Core.Crypto", "Hash256", 32),
        BytesRef("Nethermind.Core", "Address", 20),
        BytesRef("Nethermind.Core", "Bloom", 256),
    ];

    /// <summary>
    /// Factory for BCL primitive numerics (and bool). Pulls Name/Namespace from
    /// <c>typeof(T)</c>; size is the managed size from
    /// <see cref="System.Runtime.CompilerServices.Unsafe.SizeOf{T}"/>, which returns
    /// 1 for <c>bool</c> (matching SSZ's 1-byte encoding) and avoids the
    /// <c>Marshal.SizeOf&lt;bool&gt;()</c> = 4 native-marshaling quirk.
    /// </summary>
    private static SszType Primitive<T>() where T : unmanaged =>
        new()
        {
            Namespace = typeof(T).Namespace!,
            Name = typeof(T).Name,
            Kind = Kind.Basic,
            StaticLength = System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
        };

    /// <summary>
    /// Factory for Nethermind reference types whose SSZ codec is the same shape:
    /// encode by <c>{value}.Bytes.CopyTo({dest})</c>, decode by <c>new T({src})</c>.
    /// Covers Hash256, Address, Bloom, and any future fixed-length-byte ref type.
    /// </summary>
    private static SszType BytesRef(string @namespace, string name, int byteLength) =>
        new()
        {
            Namespace = @namespace,
            Name = name,
            Kind = Kind.Basic,
            StaticLength = byteLength,
            IsRefType = true,
            CustomEncodeTemplate = "{1}.Bytes.CopyTo({0});",
            CustomDecodeTemplate = $"{{1}} = new {name}({{0}});",
        };

    public static List<SszType> KnownTypes { get; set; } = [];

    // Identity gate: avoids re-walking the namespace tree once per [SszContainer] in a
    // single compilation, and resets KnownTypes when Roslyn hands us a fresh Compilation
    // (incremental rebuilds, multi-project solutions) so the list doesn't grow monotonically.
    private static WeakReference<Compilation>? _lastDiscoveredCompilation;

    public required string Name { get; init; }
    public required string? Namespace { get; init; }
    public required Kind Kind { get; init; }

    public SszProperty[]? Members { get; set; }
    public byte[]? ActiveFieldsBytes { get; set; }
    public int ActiveFieldsBitLength { get; set; }

    public bool IsStruct { get; set; }
    public bool IsRefType { get; init; }
    public SszType? EnumType { get; set; }

    public string? CustomEncodeTemplate { get; init; }
    public string? CustomDecodeTemplate { get; init; }
    public bool HasCustomInlineCodec => CustomEncodeTemplate is not null;
    public IEnumerable<SszProperty>? CompatibleUnionMembers => Kind == Kind.CompatibleUnion ? Members?.Where(x => x.Name != SelectorPropertyName) : null;
    public SszProperty? Selector => Members?.FirstOrDefault(x => x.Name == SelectorPropertyName);

    private int? _length;

    public int StaticLength
    {
        get => _length ?? Members?.Sum(x => x.StaticLength) ?? 0;
        private set => _length = value;
    }

    public bool IsVariable => (Members is not null && Members.Any(x => x.IsVariable)) || Kind is Kind.CompatibleUnion;

    public bool IsSszListItself { get; private set; }

    public const int PointerLength = 4;

    internal static void DiscoverKnownTypes(Compilation compilation)
    {
        if (_lastDiscoveredCompilation?.TryGetTarget(out Compilation? prev) == true
            && ReferenceEquals(prev, compilation))
            return;

        INamedTypeSymbol? attrSymbol = compilation.GetTypeByMetadataName(
            "Nethermind.Serialization.Ssz.SszBasicTypeAttribute");
        if (attrSymbol is null)
        {
            KnownTypes.Clear();
            _lastDiscoveredCompilation = new WeakReference<Compilation>(compilation);
            return;
        }

        List<SszType> discovered = [];
        foreach (INamedTypeSymbol type in GetAllTypes(compilation.GlobalNamespace))
        {
            AttributeData? attr = type.GetAttributes()
                .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attrSymbol));
            if (attr is null) continue;

            int staticLength = attr.ConstructorArguments.Length > 0
                ? (int)attr.ConstructorArguments[0].Value! : 0;

            bool isRefType = attr.NamedArguments
                .FirstOrDefault(kv => kv.Key == "IsRefType").Value.Value is true;
            string? encodeTemplate = attr.NamedArguments
                .FirstOrDefault(kv => kv.Key == "EncodeTemplate").Value.Value as string;
            string? decodeTemplate = attr.NamedArguments
                .FirstOrDefault(kv => kv.Key == "DecodeTemplate").Value.Value as string;

            discovered.Add(new SszType
            {
                Namespace = type.ContainingNamespace?.ToString(),
                Name = type.Name,
                Kind = Kind.Basic,
                StaticLength = staticLength,
                IsRefType = isRefType,
                CustomEncodeTemplate = encodeTemplate,
                CustomDecodeTemplate = decodeTemplate,
            });
        }

        KnownTypes = discovered;
        _lastDiscoveredCompilation = new WeakReference<Compilation>(compilation);
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
            yield return type;
        foreach (INamespaceSymbol nested in ns.GetNamespaceMembers())
            foreach (INamedTypeSymbol type in GetAllTypes(nested))
                yield return type;
    }

    internal static SszType From(SemanticModel semanticModel, List<SszType> types, ITypeSymbol type)
    {
        string? @namespace = GetNamespace(type);
        string name = GetTypeName(type);

        SszType? existingType = types.FirstOrDefault(t => t.Name == name && t.Namespace == @namespace);
        if (existingType is not null)
        {
            return existingType;
        }

        SszType? knownType = KnownTypes.FirstOrDefault(t => t.Name == name && t.Namespace == @namespace);
        if (knownType is not null)
        {
            types.Add(knownType);
            return knownType;
        }

        INamedTypeSymbol? enumType = (type as INamedTypeSymbol)?.EnumUnderlyingType;
        Kind kind = GetKind(type, enumType);

        SszType result = new()
        {
            Namespace = @namespace,
            Name = name,
            Kind = kind,
        };
        types.Add(result);

        if (enumType is not null)
        {
            result.EnumType = BasicTypes.First(x => x.Name == enumType.Name);
            result.StaticLength = result.EnumType.StaticLength;
        }

        result.Members = kind switch
        {
            Kind.Container or Kind.ProgressiveContainer or Kind.CompatibleUnion => GetPublicProperties(type)
                .Select(prop => SszProperty.From(semanticModel, types, prop))
                .ToArray(),
            _ => null,
        };

        switch (result.Kind)
        {
            case Kind.ProgressiveContainer:
                result.Members = BuildProgressiveContainerMembers(result.Name, result.Members ?? []);
                (result.ActiveFieldsBytes, result.ActiveFieldsBitLength) = BuildActiveFields(result.Members);
                break;
            case Kind.CompatibleUnion:
                InitializeCompatibleUnion(type, result);
                break;
        }

        if ((result.Kind & (Kind.Container | Kind.ProgressiveContainer | Kind.CompatibleUnion)) != Kind.None && type.TypeKind == TypeKind.Struct)
        {
            result.IsStruct = true;
        }

        if (result.Kind is Kind.Container && result.Members is { Length: 1 } && result.Members[0] is { Kind: Kind.List or Kind.ProgressiveList })
        {
            result.IsSszListItself = GetIsCollectionItselfValue(type);
        }

        return result;
    }

    public bool IsCompatibleWith(SszType other, HashSet<(SszType, SszType)>? visited = null)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Kind != other.Kind)
        {
            return false;
        }

        visited ??= [];
        if (!visited.Add((this, other)))
        {
            return true;
        }

        return Kind switch
        {
            Kind.Basic => HaveCompatibleBasicEncoding(other),
            Kind.Container => HaveCompatibleContainerLayout(other, visited),
            Kind.ProgressiveContainer => HaveCompatibleProgressiveContainerLayout(other, visited),
            Kind.CompatibleUnion => HaveCompatibleUnionOptions(other, visited),
            _ => Namespace == other.Namespace && Name == other.Name,
        };
    }

    private bool HaveCompatibleBasicEncoding(SszType other)
    {
        if (Namespace == other.Namespace && Name == other.Name)
        {
            return true;
        }

        return EnumType is not null
            && other.EnumType is not null
            && EnumType.Namespace == other.EnumType.Namespace
            && EnumType.Name == other.EnumType.Name;
    }

    private bool HaveCompatibleContainerLayout(SszType other, HashSet<(SszType, SszType)> visited)
    {
        SszProperty[] leftMembers = Members ?? [];
        SszProperty[] rightMembers = other.Members ?? [];
        if (leftMembers.Length != rightMembers.Length)
        {
            return false;
        }

        for (int index = 0; index < leftMembers.Length; index++)
        {
            if (leftMembers[index].Name != rightMembers[index].Name || !leftMembers[index].IsCompatibleWith(rightMembers[index], visited))
            {
                return false;
            }
        }

        return true;
    }

    private bool HaveCompatibleProgressiveContainerLayout(SszType other, HashSet<(SszType, SszType)> visited)
    {
        SszProperty[] leftMembers = Members ?? [];
        SszProperty[] rightMembers = other.Members ?? [];

        Dictionary<int, SszProperty> rightByIndex = rightMembers.ToDictionary(m => m.FieldIndex!.Value);
        Dictionary<string, SszProperty> rightByName = rightMembers.ToDictionary(m => m.Name);

        foreach (SszProperty left in leftMembers)
        {
            if (rightByIndex.TryGetValue(left.FieldIndex!.Value, out SszProperty? byIndex)
                && (left.Name != byIndex.Name || !left.IsCompatibleWith(byIndex, visited)))
            {
                return false;
            }

            if (rightByName.TryGetValue(left.Name, out SszProperty? byName)
                && (left.FieldIndex != byName.FieldIndex || !left.IsCompatibleWith(byName, visited)))
            {
                return false;
            }
        }

        return true;
    }

    private bool HaveCompatibleUnionOptions(SszType other, HashSet<(SszType, SszType)> visited)
    {
        Dictionary<byte, SszProperty> leftBySelector = (CompatibleUnionMembers ?? [])
            .ToDictionary(member => member.SelectorValue!.Value);
        Dictionary<byte, SszProperty> rightBySelector = (other.CompatibleUnionMembers ?? [])
            .ToDictionary(member => member.SelectorValue!.Value);
        if (leftBySelector.Count != rightBySelector.Count)
        {
            return false;
        }

        foreach (KeyValuePair<byte, SszProperty> entry in leftBySelector)
        {
            if (!rightBySelector.TryGetValue(entry.Key, out SszProperty? rightMember) || !entry.Value.IsCompatibleWith(rightMember, visited))
            {
                return false;
            }
        }

        return true;
    }

    private static Kind GetKind(ITypeSymbol type, INamedTypeSymbol? enumType)
    {
        if (enumType is not null)
        {
            return Kind.Basic;
        }

        bool isProgressiveContainer = HasAnyFieldIndex(type);
        bool isCompatibleUnion = HasAttribute(type, "SszCompatibleUnionAttribute");
        if (isProgressiveContainer && isCompatibleUnion)
        {
            throw new InvalidOperationException($"Type {GetTypeName(type)} cannot be both a progressive container and a compatible union.");
        }

        if (isCompatibleUnion)
        {
            return Kind.CompatibleUnion;
        }

        return isProgressiveContainer ? Kind.ProgressiveContainer : Kind.Container;
    }

    private static SszProperty[] BuildProgressiveContainerMembers(string typeName, SszProperty[] members)
    {
        if (members.Length == 0)
        {
            throw new InvalidOperationException($"Progressive container {typeName} must have at least one public property.");
        }

        HashSet<int> usedFieldIndices = [];
        for (int index = 0; index < members.Length; index++)
        {
            SszProperty member = members[index];
            if (member.FieldIndex is null)
            {
                throw new InvalidOperationException($"Progressive container {typeName}.{member.Name} must be marked with SszField.");
            }

            int fieldIndex = member.FieldIndex.Value;
            if (fieldIndex is < 0 or > 255)
            {
                throw new InvalidOperationException($"Progressive container {typeName}.{member.Name} must use a field index in the range [0, 255].");
            }

            if (!usedFieldIndices.Add(fieldIndex))
            {
                throw new InvalidOperationException($"Progressive container {typeName} uses the field index {fieldIndex} more than once.");
            }
        }

        Array.Sort(members, static (left, right) => left.FieldIndex!.Value.CompareTo(right.FieldIndex!.Value));
        return members;
    }

    private static (byte[] Bytes, int BitLength) BuildActiveFields(SszProperty[] members)
    {
        int bitLength = members.Max(x => x.FieldIndex!.Value) + 1;
        byte[] activeFields = new byte[(bitLength + 7) / 8];
        foreach (SszProperty member in members)
        {
            int fieldIndex = member.FieldIndex!.Value;
            activeFields[fieldIndex / 8] |= (byte)(1 << (fieldIndex % 8));
        }

        return (activeFields, bitLength);
    }

    private static void InitializeCompatibleUnion(ITypeSymbol type, SszType result)
    {
        SszProperty selector = result.Selector ?? throw new InvalidOperationException($"Compatible union {result.Name} must declare a public {SelectorPropertyName} property.");
        ITypeSymbol selectorSymbol = GetPublicProperties(type).First(x => x.Name == selector.Name).Type;
        if (selectorSymbol is not INamedTypeSymbol selectorType || selectorType.TypeKind != TypeKind.Enum || selectorType.EnumUnderlyingType?.Name != nameof(Byte))
        {
            throw new InvalidOperationException($"Compatible union {result.Name}.{SelectorPropertyName} must use a byte-backed enum.");
        }

        SszProperty[] members = result.CompatibleUnionMembers?.ToArray() ?? [];
        if (members.Length == 0)
        {
            throw new InvalidOperationException($"Compatible union {result.Name} must declare at least one member property.");
        }

        Dictionary<string, (byte Value, string Name)> selectorsByName = selectorType
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(x => x.HasConstantValue && !x.IsImplicitlyDeclared)
            .ToDictionary(x => x.Name, x => ((byte)Convert.ToInt32(x.ConstantValue), x.Name));

        if (selectorsByName.Count != members.Length)
        {
            throw new InvalidOperationException($"Compatible union {result.Name} must define one selector enum member for each union property.");
        }

        HashSet<byte> usedSelectors = [];
        foreach (SszProperty member in members)
        {
            if (!selectorsByName.TryGetValue(member.Name, out (byte Value, string Name) selectorValue))
            {
                throw new InvalidOperationException($"Compatible union {result.Name}.{member.Name} must have a matching selector enum member.");
            }

            if (selectorValue.Value is < 1 or > 127)
            {
                throw new InvalidOperationException($"Compatible union {result.Name}.{member.Name} must use a selector in the range [1, 127].");
            }

            if (!usedSelectors.Add(selectorValue.Value))
            {
                throw new InvalidOperationException($"Compatible union {result.Name} uses the selector {selectorValue.Value} more than once.");
            }

            member.SelectorValue = selectorValue.Value;
        }

        for (int leftIndex = 0; leftIndex < members.Length; leftIndex++)
        {
            for (int rightIndex = leftIndex + 1; rightIndex < members.Length; rightIndex++)
            {
                if (!members[leftIndex].IsCompatibleWith(members[rightIndex], []))
                {
                    throw new InvalidOperationException($"Compatible union {result.Name} must contain pairwise-compatible member types.");
                }
            }
        }
    }

    private static IEnumerable<IPropertySymbol> GetPublicProperties(ITypeSymbol type)
    {
        List<ITypeSymbol> typeChain = [];
        ITypeSymbol? current = type;
        while (current is not null && current.SpecialType == SpecialType.None)
        {
            typeChain.Add(current);
            current = current.BaseType;
        }
        typeChain.Reverse();

        Dictionary<string, IPropertySymbol> mostDerived = new(StringComparer.Ordinal);
        foreach (ITypeSymbol t in typeChain)
        {
            foreach (IPropertySymbol p in GetPublicReadWriteProperties(t))
            {
                mostDerived[p.Name] = p;
            }
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ITypeSymbol t in typeChain)
        {
            foreach (IPropertySymbol p in GetPublicReadWriteProperties(t))
            {
                if (!seen.Add(p.Name))
                    continue;

                if (!mostDerived.TryGetValue(p.Name, out IPropertySymbol? canonical))
                    continue;

                if (HasPropertyAttribute(canonical, "SszIgnoreAttribute"))
                    continue;

                yield return canonical;
            }
        }
    }

    private static IEnumerable<IPropertySymbol> GetPublicReadWriteProperties(ITypeSymbol type) =>
        type.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.GetMethod?.DeclaredAccessibility == Accessibility.Public
                     && p.SetMethod?.DeclaredAccessibility == Accessibility.Public);

    private static bool HasAttribute(ITypeSymbol typeSymbol, string attributeName) =>
        typeSymbol.GetAttributes().Any(a => a.AttributeClass?.Name == attributeName);

    private static bool HasPropertyAttribute(IPropertySymbol property, string attributeName) =>
        property.GetAttributes().Any(a => a.AttributeClass?.Name == attributeName);

    private static bool HasAnyFieldIndex(ITypeSymbol typeSymbol) =>
        GetPublicProperties(typeSymbol).Any(property => property.GetAttributes().Any(a => a.AttributeClass?.Name == "SszFieldAttribute"));

    private static string? GetNamespace(ITypeSymbol syntaxNode) => syntaxNode.ContainingNamespace?.ToString();

    private static string GetTypeName(ITypeSymbol syntaxNode) =>
        string.IsNullOrEmpty(syntaxNode.ContainingNamespace?.ToString()) ? syntaxNode.ToString() : syntaxNode.Name.Replace(syntaxNode.ContainingNamespace! + ".", "");

    public override string ToString() => $"type({Kind},{Name},{(IsVariable ? "v" : "f")})";

    private static bool GetIsCollectionItselfValue(ITypeSymbol typeSymbol)
    {
        object? attrValue = typeSymbol
            .GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SszContainerAttribute")?
            .ConstructorArguments.FirstOrDefault().Value;

        return attrValue is not null && (bool)attrValue;
    }
}
