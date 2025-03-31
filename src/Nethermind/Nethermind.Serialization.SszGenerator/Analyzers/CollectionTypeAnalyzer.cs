using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class CollectionTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SSZ002";
    private static readonly LocalizableString Title = "Property with a collection type should be marked as SszList or SszVector";
    private static readonly LocalizableString MessageFormat = "Property {0} should be marked as SszList or SszVector";
    private const string Category = "Design";

    private static readonly DiagnosticDescriptor Rule = new(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return [Rule]; } }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeNode, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration);
    }

    private static void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        TypeDeclarationSyntax typeDeclaration = (TypeDeclarationSyntax)context.Node;

        if (!typeDeclaration.AttributeLists.SelectMany(attrList => attrList.Attributes).Any(attr => attr.ToString() == "SszSerializable" || attr.ToString() == "SszSerializableAttribute"))
        {
            return;
        }

        foreach (var property in typeDeclaration.Members.OfType<PropertyDeclarationSyntax>()
            .Where(prop =>
                prop.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                prop.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.GetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true &&
                prop.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true))
        {
            CheckProperty(context, property);
        }
    }

    private static void CheckProperty(SyntaxNodeAnalysisContext context, PropertyDeclarationSyntax propertyDeclaration)
    {
        static bool IsCollectionType(ITypeSymbol typeSymbol)
        {
            if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            {
                ImmutableArray<INamedTypeSymbol> interfaces = namedTypeSymbol.AllInterfaces;
                return interfaces.Any(i => i.Name == "IEnumerable");
            }

            return false;
        }

        ITypeSymbol? typeSymbol = context.SemanticModel.GetTypeInfo(propertyDeclaration.Type).Type;

        if (typeSymbol is not null && (typeSymbol is IArrayTypeSymbol || IsCollectionType(typeSymbol)))
        {
            bool hasRequiredAttribute = propertyDeclaration.AttributeLists
                .SelectMany(attrList => attrList.Attributes)
                .Any(attr =>
                {
                    var name = attr.Name.ToString();
                    return name == "SszList" || name == "SszVector" || name == "SszListAttribute" || name == "SszVectorAttribute" || name == "BitArray";
                });

            if (!hasRequiredAttribute)
            {
                var diagnostic = Diagnostic.Create(Rule, propertyDeclaration.GetLocation(), propertyDeclaration.Identifier.Text);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }
}
