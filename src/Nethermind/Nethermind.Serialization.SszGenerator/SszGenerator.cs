using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.CompilerServices;
using System.Text;

[Generator]
public class SszGenerator : IIncrementalGenerator
{
    private static readonly ConditionalWeakTable<Compilation, Lazy<SszTypeConverterInfo[]>> ConverterCache = new();

    private static readonly DiagnosticDescriptor GenerationFailureRule = new(
        "SSZ003", "SSZ source generation failed",
        "Failed to generate SSZ code for '{0}': {1}",
        "SourceGeneration", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NestedTypeNotSupportedRule = new(
        "SSZ004", "SSZ types must be top-level",
        "SSZ container '{0}' is nested inside another type and is not supported by the source generator",
        "SourceGeneration", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        Register(context, "Nethermind.Serialization.Ssz.SszContainerAttribute");
        Register(context, "Nethermind.Serialization.Ssz.SszCompatibleUnionAttribute");
    }

    private static void Register(IncrementalGeneratorInitializationContext context, string attributeName)
    {
        IncrementalValuesProvider<GeneratedCodec> codecs = context.SyntaxProvider.ForAttributeWithMetadataName(
            attributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attribute, cancellationToken) => Generate(attribute, cancellationToken));
        context.RegisterSourceOutput(codecs, static (output, codec) =>
        {
            if (codec.Error is not null) output.ReportDiagnostic(codec.Error);
            else output.AddSource(codec.HintName!, SourceText.From(codec.Source!, Encoding.UTF8));
        });
    }

    private static GeneratedCodec Generate(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        INamedTypeSymbol symbol = (INamedTypeSymbol)context.TargetSymbol;
        Location location = ((TypeDeclarationSyntax)context.TargetNode).Identifier.GetLocation();
        if (symbol.ContainingType is not null)
        {
            return new(null, null, Diagnostic.Create(NestedTypeNotSupportedRule, location, symbol.Name));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation compilation = context.SemanticModel.Compilation;
            SszTypeConverterInfo[] converters = ConverterCache.GetValue(compilation,
                static current => new Lazy<SszTypeConverterInfo[]>(() => SszTypeConverterInfo.Find(current).ToArray())).Value;
            SszCodecDefinition definition = new SszModelBuilder(compilation, converters).Build(symbol);
            return new($"Serialization.SszCodec.{definition.Declaration.HintName}.cs", SszCodecEmitter.GenerateSource(definition), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(null, null, Diagnostic.Create(GenerationFailureRule, location, symbol.Name, exception.Message));
        }
    }

    // Only equatable rendered output crosses the semantic boundary; symbols remain compilation-local.
    private sealed record GeneratedCodec(string? HintName, string? Source, Diagnostic? Error);
}
