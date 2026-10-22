using Microsoft.CodeAnalysis;

class SszType
{
    private const string SelectorPropertyName = "Selector";

    public static List<SszType> CreateKnownTypes(IEnumerable<SszTypeConverterInfo> converters)
    {
        List<SszType> types =
        [
            new() { Namespace = "System.Collections", Name = "BitArray", Kind = Kind.Basic },
        ];

        foreach (SszTypeConverterInfo converter in converters)
        {
            types.Add(new()
            {
                Namespace = converter.TargetNamespace,
                Name = converter.TargetName,
                TypeReferenceName = converter.TargetTypeReferenceName,
                Kind = Kind.Basic,
                StaticLength = converter.Length,
                ConverterKind = converter.Kind,
                CustomConverterType = converter.ConverterStaticMemberAccess,
                CustomEncodeMethod = $"{converter.ConverterStaticMemberAccess}.ToSpan",
                CustomDecodeMethod = $"{converter.ConverterStaticMemberAccess}.FromSpan",
                CustomEncodeTemplate = $"{converter.ConverterStaticMemberAccess}.ToSpan({{0}}, {{1}});",
                CustomDecodeTemplate = $"{{1}} = {converter.ConverterStaticMemberAccess}.FromSpan({{0}});",
                CustomFeedMethod = $"{converter.ConverterStaticMemberAccess}.Feed",
                CustomFeedTemplate = $"{converter.ConverterStaticMemberAccess}.Feed(ref {{0}}, {{1}});",
            });
        }

        return types;
    }

    public required string Name { get; init; }
    public required string? Namespace { get; init; }
    public required Kind Kind { get; init; }

    private string? _typeReferenceName;
    /// <summary>
    /// How this type is spelled at a use site — closed generics like <c>NewPayloadRequest&lt;TExecutionPayload&gt;</c>
    /// or primitive keywords like <c>int</c>. Defaults to <see cref="Name"/> for hand-registered basic types.
    /// </summary>
    public string TypeReferenceName
    {
        get => _typeReferenceName ?? Name;
        init => _typeReferenceName = value;
    }

    private string? _staticMemberAccess;
    /// <summary>
    /// Fully-qualified spelling used as the receiver for static-member access in generated code
    /// (e.g. <c>global::Ns.Foo.Encode(...)</c>). Must be used at every <c>{Type}.{StaticMethod}(...)</c>
    /// emission site so the call cannot be shadowed by an instance property of the same simple name
    /// declared on the enclosing partial.
    /// </summary>
    /// <remarks>
    /// Defaults to a <c>global::</c>-prefixed namespace concatenation for hand-registered basic types
    /// and falls back to the bare <see cref="TypeReferenceName"/> when no containing namespace is known
    /// (e.g. open type parameters).
    /// </remarks>
    public string StaticMemberAccess
    {
        get => _staticMemberAccess ?? (string.IsNullOrEmpty(Namespace) ? TypeReferenceName : $"global::{Namespace}.{TypeReferenceName}");
        init => _staticMemberAccess = value;
    }

    /// <summary>Generic constraint clauses (e.g. <c>where T : ...</c>) for the root type declaration. Empty when there are none.</summary>
    public string TypeParameterConstraints { get; set; } = string.Empty;

    /// <summary>
    /// Namespaces of types referenced from <see cref="TypeParameterConstraints"/>. The generator emits a
    /// <c>using</c> for each so the constraint clause resolves in the generated file.
    /// </summary>
    public List<string> TypeParameterConstraintNamespaces { get; } = [];

    /// <summary>
    /// Extra namespaces referenced by generated code for this type, such as the namespace that contains its vector converter.
    /// </summary>
    public List<string> AdditionalNamespaces { get; } = [];

    /// <summary><c>true</c> when this <see cref="SszType"/> represents an open type parameter (forces variable-size encoding).</summary>
    public bool IsTypeParameter { get; init; }

    /// <summary>Filename-safe form of <see cref="TypeReferenceName"/> used as the <c>AddSource</c> hint.</summary>
    public string HintName => TypeReferenceName
        .Replace('<', '_')
        .Replace('>', '_')
        .Replace(',', '_')
        .Replace(' ', '_')
        .Replace('.', '_');

    public SszProperty[]? Members { get; set; }
    public byte[]? ActiveFieldsBytes { get; set; }
    public int ActiveFieldsBitLength { get; set; }

    public bool IsStruct { get; set; }
    public SszTypeConverterKind? ConverterKind { get; set; }
    public bool IsSszBasicType => ConverterKind == SszTypeConverterKind.BasicType;
    public SszType? EnumType { get; set; }

    public string? CustomConverterType { get; init; }
    public string? CustomEncodeMethod { get; init; }
    public string? CustomDecodeMethod { get; init; }
    public string? CustomEncodeTemplate { get; init; }
    public string? CustomDecodeTemplate { get; init; }
    public string? CustomFeedMethod { get; init; }
    public string? CustomFeedTemplate { get; init; }
    public bool HasCustomInlineCodec => CustomEncodeTemplate is not null;
    public IEnumerable<SszProperty>? CompatibleUnionMembers => Kind == Kind.CompatibleUnion ? Members?.Where(x => x.Name != SelectorPropertyName) : null;
    public SszProperty? Selector => Members?.FirstOrDefault(x => x.Name == SelectorPropertyName);

    private int? _length;

    public int StaticLength
    {
        get => _length ?? Members?.Sum(x => x.StaticLength) ?? 0;
        private set => _length = value;
    }

    public bool IsVariable => IsTypeParameter || (Members is not null && Members.Any(x => x.IsVariable)) || Kind is Kind.CompatibleUnion;

    public bool IsSszListItself { get; private set; }

    public const int PointerLength = 4;

    internal static SszType From(SemanticModel semanticModel, List<SszType> types, ITypeSymbol type)
    {
        string? @namespace = GetNamespace(type);
        string name = GetTypeName(type);
        string typeReferenceName = GetTypeReferenceName(type);
        string staticMemberAccess = GetStaticMemberAccess(type, typeReferenceName);

        // Hand-registered basic types (e.g. byte, Int32) use BCL casing in Name while a
        // user-site reference comes back in keyword form ("byte", "int") via MinimallyQualifiedFormat —
        // so reconcile to the basic-type entry by Name in that case.
        SszType? existingType = types.FirstOrDefault(t => t.Namespace == @namespace
            && (t.TypeReferenceName == typeReferenceName || (t.Kind == Kind.Basic && t.Name == name)));
        if (existingType is not null)
        {
            return existingType;
        }

        INamedTypeSymbol? enumType = (type as INamedTypeSymbol)?.EnumUnderlyingType;
        Kind kind = GetKind(type, enumType);

        SszType result = new()
        {
            Namespace = @namespace,
            Name = name,
            TypeReferenceName = typeReferenceName,
            StaticMemberAccess = staticMemberAccess,
            Kind = kind,
            IsTypeParameter = type is ITypeParameterSymbol,
        };
        types.Add(result);

        if (enumType is not null)
        {
            string? enumNamespace = GetNamespace(enumType);
            result.EnumType = types.First(x => x.Namespace == enumNamespace && x.Name == enumType.Name);
            result.StaticLength = result.EnumType.StaticLength;
            result.ConverterKind = result.EnumType.ConverterKind;
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

        if (type is ITypeParameterSymbol)
        {
            return Kind.Container;
        }

        bool isProgressiveContainer = HasAnyFieldIndex(type);
        bool isCompatibleUnion = HasAttribute(type, "SszCompatibleUnionAttribute");
        bool isContainer = HasAttribute(type, "SszContainerAttribute");
        if (isProgressiveContainer && isCompatibleUnion)
        {
            throw new InvalidOperationException($"Type {GetTypeName(type)} cannot be both a progressive container and a compatible union.");
        }

        if (isCompatibleUnion)
        {
            return Kind.CompatibleUnion;
        }

        if (!isContainer)
        {
            throw new InvalidOperationException($"Type {type.ToDisplayString()} is not SSZ serializable. Mark it with SszContainer or SszCompatibleUnion, or provide an SszBasicTypeConverter<{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}> or SszVectorTypeConverter<{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>.");
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

    private static string? GetNamespace(ITypeSymbol syntaxNode) =>
        syntaxNode.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToString() : null;

    private static string GetTypeName(ITypeSymbol syntaxNode) =>
        string.IsNullOrEmpty(syntaxNode.ContainingNamespace?.ToString()) ? syntaxNode.ToString() : syntaxNode.Name;

    // Strip the nullable reference-type annotation so the resulting name is usable both as a
    // bare type identifier in static-member access (e.g. `Foo.Encode(...)`) and as a parameter
    // type where the generator decides nullability contextually (see <see cref="IsStruct"/>).
    // Leaving `?` baked in would emit invalid syntax like `Foo?.Encode(...)` at static call sites.
    private static string GetTypeReferenceName(ITypeSymbol typeSymbol) =>
        typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    // Globally-rooted form (e.g. `global::Ns.Foo<global::Other.Bar>`) used wherever the generator
    // emits a static-member access on the type. Disambiguates against instance members of the
    // same simple name on the enclosing partial and qualifies any closed-generic type arguments
    // embedded in the reference. Type parameters resolve at the use site and degrade to their
    // declared identifier (e.g. `T`).
    private static string GetStaticMemberAccess(ITypeSymbol typeSymbol, string typeReferenceName) =>
        typeSymbol is ITypeParameterSymbol
            ? typeReferenceName
            : typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public override string ToString() => $"type({Kind},{Name},{(IsVariable ? "v" : "f")})";

    private static bool GetIsCollectionItselfValue(ITypeSymbol typeSymbol)
    {
        object? attrValue = typeSymbol
            .GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SszContainerAttribute")?
            .ConstructorArguments.FirstOrDefault().Value;

        return attrValue is not null && (bool)attrValue;
    }
}
