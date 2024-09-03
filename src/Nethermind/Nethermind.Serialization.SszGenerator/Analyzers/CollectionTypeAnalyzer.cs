using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class CollectionTypeAnalyzer : SszDiagnosticAnalyzer
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

        if (!IsSerializableType(typeDeclaration))
        {
            return;
        }

        foreach (PropertyDeclarationSyntax? property in typeDeclaration.Members.OfType<PropertyDeclarationSyntax>().Where(IsPublicGetSetProperty))
        {
            CheckProperty(context, property);
        }
    }

    private static void CheckProperty(SyntaxNodeAnalysisContext context, PropertyDeclarationSyntax propertyDeclaration)
    {
        ITypeSymbol? typeSymbol = context.SemanticModel.GetTypeInfo(propertyDeclaration.Type).Type;

        if (typeSymbol is not null && IsCollectionType(typeSymbol))
        {
            if (!IsPropertyMarkedWithCollectionAttribute(propertyDeclaration))
            {
                Diagnostic diagnostic = Diagnostic.Create(Rule, propertyDeclaration.GetLocation(), propertyDeclaration.Identifier.Text);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }
}
