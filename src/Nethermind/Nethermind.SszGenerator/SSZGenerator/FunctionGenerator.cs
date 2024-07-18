using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

[Generator]
public class SSZGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsClassWithAttribute(syntaxNode),
                transform: (context, _) => GetClassWithAttribute(context))
            .Where(classNode => classNode is not null);

        var methodDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsMethodWithAttribute(syntaxNode),
                transform: (context, _) => GetMethodWithAttribute(context))
            .Where(methodNode => methodNode is not null);

        var fieldDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsFieldWithAttribute(syntaxNode),
                transform: (context, _) => GetFieldWithAttribute(context))
            .Where(fieldNode => fieldNode is not null);

        context.RegisterSourceOutput(classDeclarations, (spc, classNode) =>
        {
            if (classNode is ClassDeclarationSyntax classDeclaration)
            {
                var className = classDeclaration.Identifier.Text;
                var namespaceName = GetNamespace(classDeclaration);
                var generatedCode = GenerateClassCode(namespaceName, className);
                spc.AddSource($"{className}_class_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
        });

        context.RegisterSourceOutput(methodDeclarations, (spc, methodNode) =>
        {
            if (methodNode is MethodDeclarationSyntax methodDeclaration)
            {
                var methodName = methodDeclaration.Identifier.Text;
                var classDeclaration = methodDeclaration.Parent as ClassDeclarationSyntax;
                var className = classDeclaration?.Identifier.Text ?? "default";
                var namespaceName = GetNamespace(methodDeclaration);
                var generatedCode = GenerateMethodCode(namespaceName, className, methodName);
                spc.AddSource($"{className}_{methodName}_method_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
        });

        context.RegisterSourceOutput(fieldDeclarations, (spc, fieldNode) =>
        {
            if (fieldNode is FieldDeclarationSyntax fieldDeclaration)
            {
                var variable = fieldDeclaration.Declaration.Variables.FirstOrDefault();
                if (variable != null)
                {
                    var fieldName = variable.Identifier.Text;
                    var classDeclaration = fieldDeclaration.Parent as ClassDeclarationSyntax;
                    var className = classDeclaration?.Identifier.Text ?? "default";
                    var namespaceName = GetNamespace(fieldDeclaration);
                    var generatedCode = GenerateFieldCode(namespaceName, className, fieldName);
                    spc.AddSource($"{className}_{fieldName}_field_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
                }
            }
        });

    }

    private static bool IsClassWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is ClassDeclarationSyntax classDeclaration &&
               classDeclaration.AttributeLists.Any();
    }

    private static bool IsMethodWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is MethodDeclarationSyntax methodDeclaration &&
               methodDeclaration.AttributeLists.Any();
    }

    private static bool IsFieldWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is FieldDeclarationSyntax fieldDeclaration &&
               fieldDeclaration.AttributeLists.Any();
    }

    private static ClassDeclarationSyntax? GetClassWithAttribute(GeneratorSyntaxContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        foreach (var attributeList in classDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                    methodSymbol.ContainingType.Name == "ClassAttribute")
                {
                    return classDeclaration;
                }
            }
        }
        return null;
    }

    private static MethodDeclarationSyntax? GetMethodWithAttribute(GeneratorSyntaxContext context)
    {
        var methodDeclaration = (MethodDeclarationSyntax)context.Node;
        foreach (var attributeList in methodDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                    methodSymbol.ContainingType.Name == "FunctionAttribute")
                {
                    return methodDeclaration;
                }
            }
        }
        return null;
    }

    private static FieldDeclarationSyntax? GetFieldWithAttribute(GeneratorSyntaxContext context)
    {
        var fieldDeclaration = (FieldDeclarationSyntax)context.Node;
        foreach (var attributeList in fieldDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                    methodSymbol.ContainingType.Name == "FieldAttribute")
                {
                    return fieldDeclaration;
                }
            }
        }
        return null;
    }

    private static string GetNamespace(SyntaxNode syntaxNode)
    {
        var namespaceDeclaration = syntaxNode.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        return namespaceDeclaration?.Name.ToString() ?? "GlobalNamespace";
    }

    private static string GenerateClassCode(string namespaceName, string className)
    {
        return $@"
            using Nethermind.Serialization.Ssz;

            namespace {namespaceName}
            {{
                public partial class {className}
                {{
                    public string GetClassName()
                    {{
                        return ""{className}"";
                    }}

                    public byte[] GenerateBasicType(){{
                        
                        byte[] buffer = new byte[100];
                        Span<byte> span = buffer;

                        // byte valueByte=4;

                        byte[] value1 = {{1,2,3,4}};
                        byte[] value2 = {{5,6,7,8}};

                        Ssz.Encode(span,1);
                        Console.WriteLine(string.Join("". "", buffer));

                        return buffer;
                    }}
                }}
            }}
            ";
    }

    private static string GenerateMethodCode(string namespaceName, string className, string methodName)
    {
        return $@"
            namespace {namespaceName}
            {{
                public partial class {className}
                {{
                    public string GetMethodName()
                    {{
                        return ""{methodName}"";
                    }}
                }}
            }}
            ";
    }

    private static string GenerateFieldCode(string namespaceName, string className, string fieldName)
    {
        return $@"
            namespace {namespaceName}
            {{
                public partial class {className}
                {{
                    public string GetFieldName()
                    {{
                        return ""{fieldName}"";
                    }}
                }}
            }}
            ";
    }

}