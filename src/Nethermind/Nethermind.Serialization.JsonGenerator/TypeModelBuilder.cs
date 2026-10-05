// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Nethermind.Serialization.JsonGenerator;

/// <summary>
/// Builds the metadata contract of a type the way the System.Text.Json source generator and its run-time property list do
/// (<c>JsonSourceGenerator.Parser.ParsePropertyGenerationSpecs</c>, <c>JsonMetadataServices.PopulateProperties</c>).
/// </summary>
internal static class TypeModelBuilder
{
    private const string SerializationNamespace = "System.Text.Json.Serialization.";
    private const string JsonIgnore = SerializationNamespace + "JsonIgnoreAttribute";
    private const string JsonPropertyName = SerializationNamespace + "JsonPropertyNameAttribute";
    private const string JsonPropertyOrder = SerializationNamespace + "JsonPropertyOrderAttribute";
    private const string JsonInclude = SerializationNamespace + "JsonIncludeAttribute";
    private const string JsonConverterAttribute = SerializationNamespace + "JsonConverterAttribute";
    private const string JsonExtensionData = SerializationNamespace + "JsonExtensionDataAttribute";
    private const string JsonNumberHandling = SerializationNamespace + "JsonNumberHandlingAttribute";
    private const string JsonPolymorphic = SerializationNamespace + "JsonPolymorphicAttribute";
    private const string JsonDerivedType = SerializationNamespace + "JsonDerivedTypeAttribute";
    private const string JsonConverterOfT = SerializationNamespace + "JsonConverter<T>";
    private const string JsonConverterFactory = SerializationNamespace + "JsonConverterFactory";
    private const string OnSerializing = SerializationNamespace + "IJsonOnSerializing";
    private const string OnSerialized = SerializationNamespace + "IJsonOnSerialized";

    private sealed class Entry(IPropertySymbol symbol, PropertyModel model, int order)
    {
        public IPropertySymbol Symbol { get; set; } = symbol;
        public PropertyModel Model { get; set; } = model;
        public int Order { get; set; } = order;
        public bool IsIgnored => Model.Kind == ContractKind.Ignored;
    }

    public static TypeModel Build(INamedTypeSymbol type, AttributeData attribute, Compilation compilation)
    {
        bool registerWithSerializer = true;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == "RegisterWithSerializer" && argument.Value.Value is bool value) registerWithSerializer = value;
        }

        List<DiagnosticModel> diagnostics = [];
        CheckType(type, diagnostics);

        List<Entry> entries = [];
        Dictionary<string, int> indexByName = new(StringComparer.Ordinal);
        Dictionary<string, IPropertySymbol>? ignoredMembers = null;

        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            ImmutableArray<ISymbol> members = current.GetMembers();
            foreach (ISymbol member in members)
            {
                if (member is not IPropertySymbol property || property.IsStatic || property.Parameters.Length > 0) continue;
                if (IsOverriddenAndIgnored(property, ignoredMembers)) continue;

                Entry? entry = CreateEntry(property, compilation, diagnostics);
                if (entry is null) continue;

                AddWithConflictResolution(entry, entries, indexByName, ref ignoredMembers, diagnostics);
            }

            foreach (ISymbol member in members)
            {
                // Whether a public field joins the contract depends on IncludeFields at run time, so fields are not supported.
                if (member is IFieldSymbol { IsStatic: false, IsConst: false, AssociatedSymbol: null } field &&
                    (field.DeclaredAccessibility == Accessibility.Public || HasAttribute(field, JsonInclude)))
                {
                    diagnostics.Add(new DiagnosticModel("NJW003", $"field '{field.Name}' may be serialized, and the generator does not write fields"));
                }
            }
        }

        if (entries.Exists(static e => e.Order != 0))
        {
            // List<T>.Sort is not stable; the metadata path sorts stably by order.
            entries = entries.Select(static (e, i) => (e, i)).OrderBy(static t => t.e.Order).ThenBy(static t => t.i).Select(static t => t.e).ToList();
        }

        string fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string? ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : null;

        return new TypeModel(
            fullName,
            ns,
            GetWriterName(type),
            Implements(type, OnSerializing),
            Implements(type, OnSerialized),
            registerWithSerializer,
            new EquatableArray<PropertyModel>([.. entries.Select(static e => e.Model)]),
            new EquatableArray<DiagnosticModel>([.. diagnostics]),
            type.Locations.FirstOrDefault());
    }

    /// <summary>Gets the type a hand-written <c>JsonConverter&lt;T&gt;</c> declared by <paramref name="converter"/> converts.</summary>
    public static string? GetConverterTarget(INamedTypeSymbol converter)
    {
        for (INamedTypeSymbol? current = converter.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.ConstructedFrom.ToDisplayString() == JsonConverterOfT)
            {
                return current.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }

        return null;
    }

    private static void CheckType(INamedTypeSymbol type, List<DiagnosticModel> diagnostics)
    {
        if (type.TypeKind != TypeKind.Class || type.IsStatic) diagnostics.Add(new DiagnosticModel("NJW002", "it is not a non-static class"));
        if (type.IsAbstract) diagnostics.Add(new DiagnosticModel("NJW002", "it is abstract, and the converter only handles the exact type"));

        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType) diagnostics.Add(new DiagnosticModel("NJW002", "it is generic or nested in a generic type"));
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                diagnostics.Add(new DiagnosticModel("NJW002", "it is not accessible from its namespace"));
            }
        }

        if (GetAttribute(type, JsonConverterAttribute) is not null)
        {
            diagnostics.Add(new DiagnosticModel("NJW001", "a type-level [JsonConverter]"));
        }

        if (GetAttribute(type, JsonPolymorphic) is not null || GetAttribute(type, JsonDerivedType) is not null)
        {
            diagnostics.Add(new DiagnosticModel("NJW002", "it is configured for polymorphic serialization"));
        }

        if (GetAttribute(type, JsonNumberHandling) is not null)
        {
            diagnostics.Add(new DiagnosticModel("NJW002", "it sets [JsonNumberHandling]"));
        }
    }

    private static Entry? CreateEntry(IPropertySymbol property, Compilation compilation, List<DiagnosticModel> diagnostics)
    {
        AttributeData? ignore = GetAttribute(property, JsonIgnore);
        string? ignoreCondition = ignore is null ? null : GetIgnoreCondition(ignore);
        bool hasInclude = HasAttribute(property, JsonInclude);

        bool canUseGetter = false;
        bool canUseSetter = false;
        bool includeInaccessible = false;
        if (property.GetMethod is { } getMethod)
        {
            if (getMethod.DeclaredAccessibility == Accessibility.Public) canUseGetter = true;
            else if (compilation.IsSymbolAccessibleWithin(getMethod, compilation.Assembly)) canUseGetter = hasInclude;
            else includeInaccessible |= hasInclude;
        }

        if (property.SetMethod is { } setMethod)
        {
            if (setMethod.DeclaredAccessibility == Accessibility.Public) canUseSetter = true;
            else if (compilation.IsSymbolAccessibleWithin(setMethod, compilation.Assembly)) canUseSetter = hasInclude;
            else includeInaccessible |= hasInclude;
        }

        if (hasInclude && (includeInaccessible || property.DeclaredAccessibility != Accessibility.Public))
        {
            diagnostics.Add(new DiagnosticModel("NJW003", $"property '{property.Name}' carries [JsonInclude] on a non-public member"));
            return null;
        }

        // The metadata path skips members it can neither read nor write, and ref-like members.
        if (!canUseGetter && !canUseSetter) return null;
        if (property.Type.IsRefLikeType) return null;

        bool ignored = ignoreCondition == "Always";
        if (!ignored)
        {
            if (HasAttribute(property, JsonExtensionData)) diagnostics.Add(new DiagnosticModel("NJW003", $"property '{property.Name}' carries [JsonExtensionData]"));
            if (HasAttribute(property, JsonNumberHandling)) diagnostics.Add(new DiagnosticModel("NJW003", $"property '{property.Name}' carries [JsonNumberHandling]"));
            if (property.ReturnsByRef || property.ReturnsByRefReadonly) diagnostics.Add(new DiagnosticModel("NJW003", $"property '{property.Name}' returns by reference"));
        }

        ITypeSymbol propertyType = property.Type;
        NullKind nullKind = propertyType.IsReferenceType || propertyType.TypeKind == TypeKind.TypeParameter
            ? NullKind.Reference
            : propertyType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? NullKind.NullableValue : NullKind.Value;

        if (!ignored && ignoreCondition == "WhenWritingNull" && nullKind == NullKind.Value)
        {
            diagnostics.Add(new DiagnosticModel("NJW003", $"property '{property.Name}' uses WhenWritingNull on a non-nullable value type"));
        }

        string? converterTypeName = null;
        if (!ignored && GetAttribute(property, JsonConverterAttribute) is { } converterAttribute)
        {
            converterTypeName = GetPropertyConverter(property, converterAttribute, diagnostics);
        }

        string? explicitName = GetSingleArgument(GetAttribute(property, JsonPropertyName)) as string;
        int order = GetSingleArgument(GetAttribute(property, JsonPropertyOrder)) is int value ? value : 0;

        ContractKind kind = ignored ? ContractKind.Ignored : canUseGetter ? ContractKind.Written : ContractKind.NotWritten;
        PropertyModel model = new(
            property.Name,
            explicitName,
            property.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            propertyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            converterTypeName,
            kind,
            Emit: kind == ContractKind.Written && ignoreCondition != "WhenWriting",
            ignoreCondition,
            nullKind,
            IsObject: propertyType.SpecialType == SpecialType.System_Object || propertyType.TypeKind == TypeKind.Dynamic);

        return new Entry(property, model, order);
    }

    private static string? GetPropertyConverter(IPropertySymbol property, AttributeData attribute, List<DiagnosticModel> diagnostics)
    {
        if (GetSingleArgument(attribute) is not INamedTypeSymbol converterType)
        {
            diagnostics.Add(new DiagnosticModel("NJW003", $"property '{property.Name}' has a [JsonConverter] without a converter type"));
            return null;
        }

        bool hasParameterlessConstructor = converterType.InstanceConstructors.Any(static c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
        bool isFactory = InheritsFrom(converterType, JsonConverterFactory);
        string? target = GetConverterTarget(converterType);
        string propertyType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (converterType.IsAbstract || !hasParameterlessConstructor || (!isFactory && target != propertyType))
        {
            diagnostics.Add(new DiagnosticModel("NJW003", $"property '{property.Name}' has a [JsonConverter] the generator cannot build for its exact type"));
            return null;
        }

        return converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static void AddWithConflictResolution(Entry entry, List<Entry> entries, Dictionary<string, int> indexByName, ref Dictionary<string, IPropertySymbol>? ignoredMembers, List<DiagnosticModel> diagnostics)
    {
        string key = entry.Model.ExplicitName ?? entry.Model.MemberName;
        if (!indexByName.TryGetValue(key, out int index))
        {
            indexByName[key] = entries.Count;
            entries.Add(entry);
        }
        else
        {
            Entry other = entries[index];
            if (other.IsIgnored)
            {
                entries[index] = entry;
            }
            else
            {
                bool ignoreCurrent = entry.IsIgnored ||
                    IsOverriddenOrShadowedBy(entry.Symbol, other.Symbol) ||
                    (ignoredMembers is not null && ignoredMembers.TryGetValue(entry.Symbol.Name, out IPropertySymbol? ignoredMember) && IsOverriddenOrShadowedBy(entry.Symbol, ignoredMember));

                if (!ignoreCurrent)
                {
                    diagnostics.Add(new DiagnosticModel("NJW003", $"property '{entry.Symbol.Name}' conflicts with another property named '{key}'"));
                }
            }
        }

        if (entry.IsIgnored)
        {
            (ignoredMembers ??= new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal))[entry.Symbol.Name] = entry.Symbol;
        }
    }

    private static bool IsOverriddenAndIgnored(IPropertySymbol property, Dictionary<string, IPropertySymbol>? ignoredMembers) =>
        IsVirtual(property) &&
        ignoredMembers is not null &&
        ignoredMembers.TryGetValue(property.Name, out IPropertySymbol? ignoredMember) &&
        IsVirtual(ignoredMember) &&
        SymbolEqualityComparer.Default.Equals(property.Type, ignoredMember.Type);

    private static bool IsVirtual(ISymbol symbol) => symbol.IsVirtual || symbol.IsOverride || symbol.IsAbstract;

    private static bool IsOverriddenOrShadowedBy(ISymbol member, ISymbol other)
    {
        if (member.Name != other.Name) return false;
        for (INamedTypeSymbol? current = other.ContainingType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, member.ContainingType)) return true;
        }

        return false;
    }

    private static string GetIgnoreCondition(AttributeData attribute)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == "Condition" && argument.Value.Type is INamedTypeSymbol enumType)
            {
                foreach (ISymbol member in enumType.GetMembers())
                {
                    if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, argument.Value.Value)) return field.Name;
                }
            }
        }

        return "Always";
    }

    private static string GetWriterName(INamedTypeSymbol type)
    {
        string name = type.Name;
        for (INamedTypeSymbol? current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            name = current.Name + "_" + name;
        }

        return name + "JsonWriter";
    }

    private static bool Implements(INamedTypeSymbol type, string interfaceName) =>
        type.AllInterfaces.Any(i => i.ToDisplayString() == interfaceName);

    private static bool InheritsFrom(INamedTypeSymbol type, string baseName)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseName) return true;
        }

        return false;
    }

    private static object? GetSingleArgument(AttributeData? attribute) =>
        attribute is not null && attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value : null;

    private static bool HasAttribute(ISymbol symbol, string attributeName) => GetAttribute(symbol, attributeName) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, string attributeName)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == attributeName) return attribute;
        }

        return null;
    }
}
