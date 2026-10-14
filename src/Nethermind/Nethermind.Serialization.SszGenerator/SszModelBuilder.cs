// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using static SszTypeDiscovery;

internal sealed class SszModelBuilder
{
    private readonly SszTypeDiscovery _discovery;
    private readonly string _byteIdentity;
    private readonly Dictionary<ITypeSymbol, SszTypeConverterInfo> _converters = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<INamedTypeSymbol, SszCodecDefinition> _completed = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _active = new(SymbolEqualityComparer.Default);
    private readonly List<string> _path = [];

    public SszModelBuilder(Compilation compilation, IEnumerable<SszTypeConverterInfo> converters)
    {
        _discovery = new(compilation);
        _byteIdentity = TypeName(compilation.GetSpecialType(SpecialType.System_Byte));
        foreach (SszTypeConverterInfo converter in converters)
        {
            _converters.Add(converter.TargetType, converter);
        }
    }

    public SszCodecDefinition Build(INamedTypeSymbol type)
    {
        if (_completed.TryGetValue(type, out SszCodecDefinition? completed))
        {
            return completed;
        }

        if (!_active.Add(type.OriginalDefinition))
        {
            throw new InvalidOperationException($"A recursive SSZ schema is not supported: {string.Join(" -> ", _path)} -> {type.ToDisplayString()}.");
        }

        _path.Add(type.ToDisplayString());
        try
        {
            SszCodecDefinition definition = BuildDefinition(type);
            _completed.Add(type, definition);
            return definition;
        }
        finally
        {
            _path.RemoveAt(_path.Count - 1);
            _active.Remove(type.OriginalDefinition);
        }
    }

    private SszCodecDefinition BuildDefinition(INamedTypeSymbol type)
    {
        bool union = GetAttribute(type, "SszCompatibleUnionAttribute") is not null;
        AttributeData? container = GetAttribute(type, "SszContainerAttribute");
        if (!union && container is null)
        {
            throw new InvalidOperationException($"Type {type.ToDisplayString()} is not SSZ serializable. Mark it with SszContainer or SszCompatibleUnion, or provide an SszBasicTypeConverter<{type.ToDisplayString()}> or SszVectorTypeConverter<{type.ToDisplayString()}>.");
        }
        if (union && container is not null)
        {
            throw new InvalidOperationException($"Type {type.Name} cannot be both a container and a compatible union.");
        }

        List<IPropertySymbol> properties = GetProperties(type);
        if (properties.Count == 0)
        {
            throw new InvalidOperationException($"SSZ type {type.Name} must declare at least one serializable property.");
        }
        Dictionary<string, ITypeSymbol> declaredProperties = new(StringComparer.Ordinal);
        foreach (IPropertySymbol property in GetProperties(type.OriginalDefinition))
        {
            declaredProperties.Add(property.Name, property.Type);
        }
        List<SszMember> members = new(properties.Count);
        IPropertySymbol? selectorProperty = null;
        SszMember? selector = null;
        bool progressive = false;
        bool hasOwnedValues = false;
        foreach (IPropertySymbol property in properties)
        {
            SszMember member = BuildMember(property, declaredProperties[property.Name]);
            progressive |= member.FieldIndex is not null;
            if (union && property.Name == "Selector")
            {
                selectorProperty = property;
                selector = member;
            }
            else
            {
                members.Add(member);
                hasOwnedValues |= member.Value.Binding.HasOwnedValues;
            }
        }

        if (progressive && union)
        {
            throw new InvalidOperationException($"Type {type.Name} cannot be both a progressive container and a compatible union.");
        }

        SszSchema schema;
        SszMember? wrapped = null;
        if (union)
        {
            schema = BuildUnion(type, selectorProperty, members);
        }
        else
        {
            if (progressive)
            {
                ValidateFieldIndices(type.Name, members);
                members.Sort(static (left, right) => left.FieldIndex!.Value.CompareTo(right.FieldIndex!.Value));
            }

            bool isWrapper = !progressive && members.Count == 1
                && members[0].Value.Schema is SequenceSchema { Kind: SequenceKind.List or SequenceKind.ProgressiveList }
                && container!.ConstructorArguments.Length > 0 && container.ConstructorArguments[0].Value is true;
            if (isWrapper)
            {
                wrapped = members[0];
                schema = wrapped.Value.Schema;
            }
            else
            {
                schema = BuildContainer(members, progressive);
            }
        }

        string ns = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString();
        SszDeclaration declaration = new(ns, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            (ns.Length == 0 ? string.Empty : ns + ".") + type.MetadataName,
            GetConstraints(type), type.IsValueType, type.IsRecord);
        return new(declaration, schema, members.ToImmutableArray(), wrapped, selector, hasOwnedValues);
    }

    private SszMember BuildMember(IPropertySymbol property, ITypeSymbol declaredType)
    {
        if (property.IsStatic || property.IsIndexer)
        {
            throw new InvalidOperationException($"SSZ property {property.ContainingType.Name}.{property.Name} must be an instance property without index parameters.");
        }
        AttributeData? field = GetAttribute(property, "SszFieldAttribute");
        int? index = field is null ? null : (int)field.ConstructorArguments[0].Value!;
        CollectionInfo? collection = _discovery.GetCollection(property.Type);
        AttributeData? ownership = GetAttribute(property, "SszCollectionOwnsElementsAttribute");
        bool ownsElements = ownership?.ConstructorArguments[0].Value is true;
        if (ownsElements && (collection is null || collection.Disposal == CollectionDisposal.None))
        {
            throw new InvalidOperationException($"Property {property.ContainingType.Name}.{property.Name} cannot declare SszCollectionOwnsElements(true) because its collection has no disposer.");
        }
        bool bits = _discovery.IsType(property.Type, "System.Collections.BitArray");
        SszValue value;
        if (collection is null && !bits)
        {
            value = BuildScalar(property.Type, declaredType);
        }
        else
        {
            (SequenceKind kind, ulong? bound) = GetSequence(property, bits);
            if (bits)
            {
                int? size = kind == SequenceKind.Vector ? checked((int)(bound!.Value / 8 + (bound.Value % 8 == 0 ? 0UL : 1UL))) : null;
                value = new(new BitSequenceSchema(kind, bound, size), new BitArrayBinding(TypeName(property.Type), IsNullable(property.Type)));
            }
            else
            {
                CollectionInfo info = collection!;
                ITypeSymbol declaredElement = _discovery.GetCollection(declaredType)?.Element ?? info.Element;
                SszValue element = BuildScalar(info.Element, declaredElement);
                if (info.Storage == CollectionStorage.CustomSpan && info.Disposal != CollectionDisposal.None
                    && element.Binding.HasOwnedValues && ownership is null)
                {
                    throw new InvalidOperationException($"Collection property {property.ContainingType.Name}.{property.Name} must declare SszCollectionOwnsElements(true or false) because both its storage and its elements own resources.");
                }
                int? size = kind == SequenceKind.Vector && element.Schema.FixedSize is int itemSize
                    ? checked((int)bound!.Value * itemSize) : null;
                CollectionBinding binding = new(TypeName(property.Type), property.Type.IsValueType, IsNullable(property.Type),
                    info.Storage, element, info.Disposal, ownsElements, info.Disposal != CollectionDisposal.None || element.Binding.HasOwnedValues);
                value = new(new SequenceSchema(kind, element.Schema, bound, size), binding);
            }
        }

        NullMapping nullMapping = (value.Schema, value.Binding) switch
        {
            (SequenceSchema { Kind: SequenceKind.Vector }, CollectionBinding binding) => binding.IsNullable
                && binding.Element.Binding is ScalarBinding { CodecKind: CodecKind.Converter } ? NullMapping.ZeroEncoded : NullMapping.Reject,
            (BitSequenceSchema { Kind: SequenceKind.Vector }, BitArrayBinding binding) => binding.IsNullable ? NullMapping.ZeroEncoded : NullMapping.Reject,
            (_, CollectionBinding or BitArrayBinding) => NullMapping.EmptyCollection,
            (_, ScalarBinding { Definition.WrappedMember: not null }) => NullMapping.EmptyCollection,
            _ when value.Schema.FixedSize is not null => NullMapping.ZeroEncoded,
            _ => NullMapping.Reject,
        };
        return new(property.Name, value, nullMapping, index);
    }

    private SszValue BuildScalar(ITypeSymbol type, ITypeSymbol declaredType)
    {
        string typeName = TypeName(type);
        if (declaredType is ITypeParameterSymbol parameter)
        {
            if (!HasCodecConstraint(parameter))
            {
                throw new InvalidOperationException($"Type parameter {parameter.Name} must be constrained to ISszCodec<{parameter.Name}>.");
            }
            return new(new CodecParameterSchema(parameter.Name),
                new ScalarBinding(typeName, type.IsValueType, IsNullable(type), CodecKind.Parameter, typeName, null, null, true));
        }

        if (_converters.TryGetValue(type, out SszTypeConverterInfo? converter))
        {
            SszSchema schema = converter.Kind == SszTypeConverterKind.BasicType
                ? new BasicSchema(typeName, converter.Length)
                : new SequenceSchema(SequenceKind.Vector, new BasicSchema(_byteIdentity, 1), (ulong)converter.Length, converter.Length);
            return new(schema, new ScalarBinding(typeName, type.IsValueType, IsNullable(type), CodecKind.Converter,
                converter.ConverterStaticMemberAccess, null, null, false));
        }

        if (type is INamedTypeSymbol { EnumUnderlyingType: not null } enumType)
        {
            ITypeSymbol underlying = enumType.EnumUnderlyingType;
            SszValue value = BuildScalar(underlying, underlying);
            ScalarBinding binding = (ScalarBinding)value.Binding;
            BasicSchema schema = (BasicSchema)value.Schema;
            return new(new BasicSchema("enum:" + schema.Identity, schema.Size), binding with
            {
                TypeName = typeName,
                IsValueType = true,
                IsNullable = false,
                EnumUnderlyingType = TypeName(underlying),
            });
        }

        if (type is not INamedTypeSymbol named)
        {
            throw new InvalidOperationException($"Type {type.ToDisplayString()} is not SSZ serializable.");
        }
        SszCodecDefinition definition = Build(named);
        return new(definition.Schema, new ScalarBinding(typeName, type.IsValueType, IsNullable(type), CodecKind.Generated,
            typeName, null, definition, definition.HasOwnedValues));
    }

    private (SequenceKind Kind, ulong? Bound) GetSequence(IPropertySymbol property, bool bits)
    {
        AttributeData? vector = GetAttribute(property, "SszVectorAttribute");
        AttributeData? list = GetAttribute(property, "SszListAttribute");
        AttributeData? progressive = GetAttribute(property, "SszProgressiveListAttribute");
        AttributeData? progressiveBits = GetAttribute(property, "SszProgressiveBitlistAttribute");
        int count = (vector is null ? 0 : 1) + (list is null ? 0 : 1) + (progressive is null ? 0 : 1) + (progressiveBits is null ? 0 : 1);
        if (count != 1)
        {
            throw new InvalidOperationException($"Collection property {property.ContainingType.Name}.{property.Name} must declare exactly one SSZ collection attribute.");
        }
        if ((bits && progressive is not null) || (!bits && progressiveBits is not null))
        {
            throw new InvalidOperationException($"Collection property {property.ContainingType.Name}.{property.Name} uses an SSZ collection attribute for the wrong element type.");
        }
        if (vector is not null)
        {
            int length = (int)vector.ConstructorArguments[0].Value!;
            if (length <= 0)
            {
                throw new InvalidOperationException($"Vector property {property.ContainingType.Name}.{property.Name} must have a positive length.");
            }
            return (SequenceKind.Vector, (ulong)length);
        }
        if (list is not null)
        {
            ulong limit = (ulong)list.ConstructorArguments[0].Value!;
            if (bits && limit > int.MaxValue)
            {
                throw new InvalidOperationException($"Bitlist property {property.ContainingType.Name}.{property.Name} declares limit {limit}, but a BitArray cannot exceed int.MaxValue bits.");
            }
            return (SequenceKind.List, limit);
        }
        return (SequenceKind.ProgressiveList, null);
    }

    private static ContainerSchema BuildContainer(List<SszMember> members, bool progressive)
    {
        ImmutableArray<SszField>.Builder fields = ImmutableArray.CreateBuilder<SszField>(members.Count);
        int fixedSectionSize = 0;
        bool variable = false;
        int activeFieldCount = progressive ? members[members.Count - 1].FieldIndex!.Value + 1 : 0;
        byte[] activeFields = new byte[(activeFieldCount + 7) / 8];
        foreach (SszMember member in members)
        {
            fields.Add(new(member.Name, member.Value.Schema, member.FieldIndex, fixedSectionSize));
            fixedSectionSize = checked(fixedSectionSize + (member.Value.Schema.FixedSize ?? 4));
            variable |= member.Value.Schema.FixedSize is null;
            if (progressive)
            {
                int index = member.FieldIndex!.Value;
                activeFields[index / 8] |= (byte)(1 << (index % 8));
            }
        }
        return new(progressive ? ContainerKind.ProgressiveContainer : ContainerKind.Container, fields.MoveToImmutable(),
            fixedSectionSize, variable ? null : fixedSectionSize, activeFields.ToImmutableArray(), activeFieldCount);
    }

    private static void ValidateFieldIndices(string name, List<SszMember> members)
    {
        HashSet<int> seen = [];
        foreach (SszMember member in members)
        {
            if (member.FieldIndex is not int index)
            {
                throw new InvalidOperationException($"Progressive container {name}.{member.Name} must be marked with SszField.");
            }
            if (index is < 0 or > 255)
            {
                throw new InvalidOperationException($"Progressive container {name}.{member.Name} must use a field index in the range [0, 255].");
            }
            if (!seen.Add(index))
            {
                throw new InvalidOperationException($"Progressive container {name} uses the field index {index} more than once.");
            }
        }
    }

    private static UnionSchema BuildUnion(INamedTypeSymbol type, IPropertySymbol? selector, List<SszMember> members)
    {
        if (selector is null)
        {
            throw new InvalidOperationException($"Compatible union {type.Name} must declare a public Selector property.");
        }
        if (selector.Type is not INamedTypeSymbol { EnumUnderlyingType.SpecialType: SpecialType.System_Byte } selectorType)
        {
            throw new InvalidOperationException($"Compatible union {type.Name}.Selector must use a byte-backed enum.");
        }
        if (members.Count == 0)
        {
            throw new InvalidOperationException($"Compatible union {type.Name} must declare at least one member property.");
        }
        Dictionary<string, byte> selectors = new(StringComparer.Ordinal);
        foreach (ISymbol symbol in selectorType.GetMembers())
        {
            if (symbol is IFieldSymbol { HasConstantValue: true, IsImplicitlyDeclared: false } field)
            {
                selectors.Add(field.Name, (byte)field.ConstantValue!);
            }
        }
        if (selectors.Count != members.Count)
        {
            throw new InvalidOperationException($"Compatible union {type.Name} must define one selector enum member for each union property.");
        }
        HashSet<byte> seen = [];
        ImmutableArray<SszSchema>.Builder options = ImmutableArray.CreateBuilder<SszSchema>(members.Count);
        for (int index = 0; index < members.Count; index++)
        {
            SszMember member = members[index];
            if (!selectors.TryGetValue(member.Name, out byte value))
            {
                throw new InvalidOperationException($"Compatible union {type.Name}.{member.Name} must have a matching selector enum member.");
            }
            if (value is < 1 or > 127)
            {
                throw new InvalidOperationException($"Compatible union {type.Name}.{member.Name} must use a selector in the range [1, 127].");
            }
            if (!seen.Add(value))
            {
                throw new InvalidOperationException($"Compatible union {type.Name} uses the selector {value} more than once.");
            }
            members[index] = member with { SelectorValue = value };
            options.Add(member.Value.Schema);
            for (int earlier = 0; earlier < index; earlier++)
            {
                if (!SszSchemaCompatibility.AreCompatible(members[earlier].Value.Schema, member.Value.Schema))
                {
                    throw new InvalidOperationException($"Compatible union {type.Name} must contain pairwise-compatible member types.");
                }
            }
        }
        return new(options.MoveToImmutable());
    }

    private static string GetConstraints(INamedTypeSymbol type)
    {
        List<string> clauses = [];
        foreach (ITypeParameterSymbol parameter in type.TypeParameters)
        {
            List<string> constraints = [];
            if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
            else if (parameter.HasReferenceTypeConstraint) constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
            else if (parameter.HasNotNullConstraint) constraints.Add("notnull");
            foreach (ITypeSymbol constraint in parameter.ConstraintTypes)
            {
                constraints.Add(constraint.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
            if (parameter.HasConstructorConstraint) constraints.Add("new()");
            if (parameter.AllowsRefLikeType) constraints.Add("allows ref struct");
            if (constraints.Count > 0) clauses.Add($"where {parameter.Name} : {string.Join(", ", constraints)}");
        }
        return string.Join(" ", clauses);
    }

    private bool HasCodecConstraint(ITypeParameterSymbol parameter)
    {
        Stack<ITypeSymbol> pending = new(parameter.ConstraintTypes);
        HashSet<ITypeSymbol> visited = new(SymbolEqualityComparer.Default);
        while (pending.Count > 0)
        {
            ITypeSymbol constraint = pending.Pop();
            if (!visited.Add(constraint)) continue;
            if (IsCodecConstraint(constraint, parameter)) return true;
            foreach (INamedTypeSymbol contract in constraint.AllInterfaces)
            {
                if (IsCodecConstraint(contract, parameter)) return true;
            }
            if (constraint is ITypeParameterSymbol constrainedParameter)
            {
                foreach (ITypeSymbol inherited in constrainedParameter.ConstraintTypes) pending.Push(inherited);
            }
        }
        return false;
    }

    private bool IsCodecConstraint(ITypeSymbol constraint, ITypeParameterSymbol parameter) =>
        constraint is INamedTypeSymbol contract
        && _discovery.IsType(contract.OriginalDefinition, "Nethermind.Serialization.Ssz.ISszCodec`1")
        && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], parameter);
}
