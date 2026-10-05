// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Serialization.JsonGenerator;

/// <summary>
/// Emits a converter per type marked <c>[GenerateJsonWriter]</c> that writes it the way the System.Text.Json metadata path
/// does, and a module initializer that registers them.
/// </summary>
[Generator]
public sealed class JsonWriterGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Nethermind.Serialization.Json.GenerateJsonWriterAttribute";

    private static readonly DiagnosticDescriptor HandWrittenConverterRule = new(
        "NJW001",
        "Type already has a hand-written JSON converter",
        "No JSON writer is generated for '{0}': it already has {1}",
        "SourceGeneration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedTypeRule = new(
        "NJW002",
        "Type shape is not supported by the JSON writer generator",
        "No JSON writer is generated for '{0}': {1}",
        "SourceGeneration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMemberRule = new(
        "NJW003",
        "Member shape is not supported by the JSON writer generator",
        "No JSON writer is generated for '{0}': {1}",
        "SourceGeneration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<TypeModel> types = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => TypeModelBuilder.Build((INamedTypeSymbol)ctx.TargetSymbol, ctx.SemanticModel.Compilation));

        IncrementalValueProvider<ImmutableArray<ConverterTarget>> handWritten = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol converter && TypeModelBuilder.GetConverterTarget(converter) is { } target
                    ? new ConverterTarget(target, converter.ToDisplayString())
                    : null)
            .Where(static t => t is not null)
            .Select(static (t, _) => t!)
            .Collect();

        IncrementalValuesProvider<(TypeModel Type, ImmutableArray<ConverterTarget> HandWritten)> resolved = types.Combine(handWritten);

        context.RegisterSourceOutput(resolved, static (spc, item) =>
        {
            if (Report(spc, item.Type, item.HandWritten)) return;
            spc.AddSource(item.Type.WriterName + ".g.cs", SourceText.From(JsonWriterEmitter.EmitWriter(item.Type), System.Text.Encoding.UTF8));
        });

        context.RegisterSourceOutput(resolved.Collect(), static (spc, items) =>
        {
            List<TypeModel> emitted = [];
            foreach ((TypeModel type, ImmutableArray<ConverterTarget> handWrittenTargets) in items)
            {
                if (!IsSkipped(type, handWrittenTargets)) emitted.Add(type);
            }

            if (emitted.Count == 0) return;
            emitted.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            spc.AddSource("GeneratedJsonWriterRegistration.g.cs", SourceText.From(JsonWriterEmitter.EmitRegistration(emitted), System.Text.Encoding.UTF8));
        });
    }

    private static bool IsSkipped(TypeModel type, ImmutableArray<ConverterTarget> handWritten) =>
        type.Diagnostics.Length > 0 || handWritten.Any(t => t.Target == type.FullName);

    private static bool Report(SourceProductionContext spc, TypeModel type, ImmutableArray<ConverterTarget> handWritten)
    {
        Location location = type.Location ?? Location.None;
        string name = type.FullName.Replace("global::", string.Empty);
        foreach (ConverterTarget target in handWritten)
        {
            if (target.Target == type.FullName)
            {
                spc.ReportDiagnostic(Diagnostic.Create(HandWrittenConverterRule, location, name, $"the hand-written converter '{target.Converter}'"));
            }
        }

        foreach (DiagnosticModel diagnostic in type.Diagnostics)
        {
            DiagnosticDescriptor rule = diagnostic.Id switch
            {
                "NJW001" => HandWrittenConverterRule,
                "NJW002" => UnsupportedTypeRule,
                _ => UnsupportedMemberRule,
            };
            spc.ReportDiagnostic(Diagnostic.Create(rule, location, name, diagnostic.Detail));
        }

        return IsSkipped(type, handWritten);
    }

    private sealed record ConverterTarget(string Target, string Converter);
}
