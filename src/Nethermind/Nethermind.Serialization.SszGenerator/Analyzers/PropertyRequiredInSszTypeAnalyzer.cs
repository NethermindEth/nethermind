using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PropertyRequiredInSszTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SSZ001";
    private static readonly LocalizableString Title = "Class or struct marked with SszSerializable must have at least one public property with public getter and setter";
    private static readonly LocalizableString MessageFormat = "Type '{0}' is marked with SszSerializable, but does not have any public property with public getter and setter";
    private static readonly LocalizableString Description = "A class or struct marked with SszSerializable should have at least one public property with both a public getter and setter.";
    private const string Category = "Design";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

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

        bool hasValidProperty = typeDeclaration.Members.OfType<PropertyDeclarationSyntax>()
            .Any(prop =>
                prop.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                prop.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.GetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true &&
                prop.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration && !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true);

        if (!hasValidProperty)
        {
            Diagnostic diagnostic = Diagnostic.Create(Rule, typeDeclaration.Identifier.GetLocation(), typeDeclaration.Identifier.Text);
            context.ReportDiagnostic(diagnostic);
        }
    }
}
