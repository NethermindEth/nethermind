// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SszDiagnosticAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor PropertyRequired = new(
        "SSZ001",
        "Class or struct marked with SszContainer or SszCompatibleUnion must have at least one public property with public getter and setter",
        "Type '{0}' is marked with SszContainer or SszCompatibleUnion, but does not have any public property with public getter and setter",
        "Design", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CollectionAttributeRequired = new(
        "SSZ002",
        "Property with a collection type should be marked as SszList or SszVector",
        "Property {0} should be marked as SszList, SszVector, SszProgressiveList, or SszProgressiveBitlist",
        "Design", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [PropertyRequired, CollectionAttributeRequired];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.Symbol;
        if (SszTypeDiscovery.GetAttribute(type, "SszContainerAttribute") is null
            && SszTypeDiscovery.GetAttribute(type, "SszCompatibleUnionAttribute") is null)
        {
            return;
        }

        List<IPropertySymbol> properties = SszTypeDiscovery.GetProperties(type);
        if (properties.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(PropertyRequired, type.Locations[0], type.Name));
            return;
        }

        SszTypeDiscovery discovery = new(context.Compilation);
        foreach (IPropertySymbol property in properties)
        {
            if ((discovery.IsType(property.Type, "System.Collections.BitArray") || discovery.GetCollection(property.Type, validate: false) is not null)
                && SszTypeDiscovery.GetAttribute(property, "SszVectorAttribute") is null
                && SszTypeDiscovery.GetAttribute(property, "SszListAttribute") is null
                && SszTypeDiscovery.GetAttribute(property, "SszProgressiveListAttribute") is null
                && SszTypeDiscovery.GetAttribute(property, "SszProgressiveBitlistAttribute") is null)
            {
                Location location = property.Locations[0].IsInSource ? property.Locations[0] : type.Locations[0];
                context.ReportDiagnostic(Diagnostic.Create(CollectionAttributeRequired, location, property.Name));
            }
        }
    }
}
