using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Nethermind.Serialization.Ssz;
using System.Runtime.CompilerServices;
using System.Text;

[Generator]
public class SszGenerator : IIncrementalGenerator
{
    private static readonly ConditionalWeakTable<Compilation, Lazy<SszTypeConverterInfo[]>> ConverterCache = new();

    private sealed class SszDeclaration
    {
        public required string TypeName { get; init; }
        public SszType? Type { get; init; }
        public Location? Location { get; init; }
        public bool IsNested { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private static readonly DiagnosticDescriptor GenerationFailureRule = new(
        "SSZ003",
        "SSZ source generation failed",
        "Failed to generate SSZ code for '{0}': {1}",
        "SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NestedTypeNotSupportedRule = new(
        "SSZ004",
        "SSZ types must be top-level",
        "SSZ container '{0}' is nested inside another type and is not supported by the source generator",
        "SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<string> helperNamespace = context.CompilationProvider
            .Select(static (compilation, _) => GetHelperNamespace(compilation.AssemblyName));
        context.RegisterSourceOutput(helperNamespace, static (spc, name) =>
            spc.AddSource("Serialization.SszCodec.Helpers.cs", SourceText.From(SszRuntimeHelpers.GenerateSource(name), Encoding.UTF8)));

        IncrementalValuesProvider<SszDeclaration?> classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsClassWithAttribute(syntaxNode),
                transform: (context, _) => GetClassWithAttribute(context))
            .Where(classNode => classNode is not null);

        context.RegisterSourceOutput(classDeclarations.Combine(helperNamespace), (spc, value) =>
        {
            SszDeclaration? decl = value.Left;
            if (decl is null)
            {
                return;
            }

            if (decl.ErrorMessage is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(GenerationFailureRule, decl.Location ?? Location.None, decl.TypeName, decl.ErrorMessage));
                return;
            }

            if (decl.Type is null)
            {
                return;
            }

            if (decl.IsNested)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NestedTypeNotSupportedRule, decl.Location ?? Location.None, decl.Type.Name));
                return;
            }

            try
            {
                string generatedCode = SszCodecEmitter.GenerateSource(decl.Type, value.Right);
                spc.AddSource($"Serialization.SszCodec.{decl.Type.HintName}.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
            catch (Exception e)
            {
                spc.ReportDiagnostic(Diagnostic.Create(GenerationFailureRule, decl.Location ?? Location.None, decl.Type.Name, e.Message));
            }
        });
    }

    private static string GetHelperNamespace(string? assemblyName)
    {
        StringBuilder name = new("Nethermind.Serialization.GeneratedSsz_");
        foreach (byte value in Encoding.UTF8.GetBytes(assemblyName ?? string.Empty))
        {
            name.Append(value.ToString("X2"));
        }
        return name.ToString();
    }

    private static bool IsClassWithAttribute(SyntaxNode syntaxNode) =>
        syntaxNode is TypeDeclarationSyntax classDeclaration &&
        classDeclaration.AttributeLists.Any(x => x.Attributes.Any());

    private static SszDeclaration? GetClassWithAttribute(GeneratorSyntaxContext context)
    {
        TypeDeclarationSyntax classDeclaration = (TypeDeclarationSyntax)context.Node;
        foreach (AttributeListSyntax attributeList in classDeclaration.AttributeLists)
        {
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                IMethodSymbol? methodSymbol = context.SemanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
                if (methodSymbol is not null && IsSszRootAttribute(methodSymbol.ContainingType))
                {
                    INamedTypeSymbol typeSymbol = (INamedTypeSymbol)context.SemanticModel.GetDeclaredSymbol(classDeclaration)!;
                    Location location = classDeclaration.Identifier.GetLocation();
                    try
                    {
                        List<SszType> foundTypes = SszType.CreateKnownTypes(FindConverters(context.SemanticModel.Compilation));
                        SszType sszType = SszType.From(context.SemanticModel, foundTypes, typeSymbol);
                        // Carry the `where T : ...` clauses verbatim from syntax — the symbol-side API
                        // would require us to reassemble each constraint, and the textual form already matches.
                        sszType.TypeParameterConstraints = string.Join(" ", classDeclaration.ConstraintClauses.Select(c => c.ToString()));
                        foreach (ITypeParameterSymbol tp in typeSymbol.TypeParameters)
                        {
                            foreach (ITypeSymbol ct in tp.ConstraintTypes)
                            {
                                string? ns = ct.ContainingNamespace?.ToString();
                                if (!string.IsNullOrEmpty(ns))
                                    sszType.TypeParameterConstraintNamespaces.Add(ns!);
                            }
                        }

                        return new()
                        {
                            TypeName = sszType.Name,
                            Type = sszType,
                            Location = location,
                            IsNested = typeSymbol.ContainingType is not null,
                        };
                    }
                    catch (Exception e)
                    {
                        return new()
                        {
                            TypeName = typeSymbol.Name,
                            Location = location,
                            IsNested = typeSymbol.ContainingType is not null,
                            ErrorMessage = e.Message,
                        };
                    }
                }
            }
        }
        return null;
    }

    private static IEnumerable<SszTypeConverterInfo> FindConverters(Compilation compilation) =>
        ConverterCache.GetValue(
            compilation,
            static currentCompilation => new Lazy<SszTypeConverterInfo[]>(() => SszTypeConverterInfo.Find(currentCompilation).ToArray()))
            .Value;

    private static bool IsSszRootAttribute(INamedTypeSymbol attributeType) =>
        attributeType.Name is nameof(SszContainerAttribute) or nameof(SszCompatibleUnionAttribute);

}
