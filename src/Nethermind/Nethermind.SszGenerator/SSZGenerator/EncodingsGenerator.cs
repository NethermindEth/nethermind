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
    private static readonly string[] SSZ_LIB_SUPPORTED_ENCODING_TYPES_OFFSET={
        "string", "byte[]", "int", "uint", "ulong", "UInt256", "bool", "byte", "ushort", "UInt258"
    };
    private static readonly string[] SSZ_LIB_SUPPORTED_ENCODING_TYPES_NOOFFSET={
        "ushort", "UInt258", "UInt256[]", "UInt258[]"
    };
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsClassWithAttribute(syntaxNode),
                transform: (context, _) => GetClassWithAttribute(context))
            .Where(classNode => classNode is not null);

        var methodDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsMethodWithAttribute(syntaxNode),
                transform: (context, _) => GetMethodWithAttribute(context))
            .Where(methodNode => methodNode is not null);

        var fieldDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsFieldWithAttribute(syntaxNode),
                transform: (context, _) => GetFieldWithAttribute(context))
            .Where(fieldNode => fieldNode is not null);

        var structDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsStructWithAttribute(syntaxNode),
                transform: (context, _) => GetStructWithAttribute(context))
            .Where(structNode => structNode is not null);

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
                var className = classDeclaration?.Identifier.Text ?? "DefaultMethod";
                var namespaceName = GetNamespace(methodDeclaration);
                var generatedCode = GenerateMethodCode(namespaceName, className, methodName);
                spc.AddSource($"{className}_{methodName}_method_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
        });

        var combinedDeclarations = fieldDeclarations.Collect()
            .Combine(structDeclarations.Collect())
            .Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(combinedDeclarations, (spc, combined) =>
        {
            var ((fields, structs), classes) = combined;
            foreach (var classDeclaration in classes)
            {
                if (classDeclaration != null)
                {
                    var className = classDeclaration.Identifier.Text;
                    var namespaceName = GetNamespace(classDeclaration);
                    var nonNullFields = fields.Where(f => f != null).Cast<FieldDeclarationSyntax>().ToImmutableArray();
                    var nonNullStructs = structs.Where(s => s != null).Cast<StructDeclarationSyntax>().ToImmutableArray();
                    var generatedCode = GenerateCombinedCode(namespaceName, className, nonNullFields, nonNullStructs);
                    spc.AddSource($"{className}_Combined_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
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

    private static bool IsStructWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is StructDeclarationSyntax structDeclaration &&
            structDeclaration.AttributeLists.Any();
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

    private static StructDeclarationSyntax? GetStructWithAttribute(GeneratorSyntaxContext context)
    {
        var structDeclaration = (StructDeclarationSyntax)context.Node;
        foreach (var attributeList in structDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                    methodSymbol.ContainingType.Name == "FieldStructAttribute")
                {
                    return structDeclaration;
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

                    public byte[] GenerateBasicType()
                    {{
                        byte[] buffer = new byte[100];
                        Span<byte> span = buffer;

                        Ssz.Encode(span, 1);
                        Console.WriteLine(string.Join("", "", buffer));

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

    //generate structs and fields together in a file
    private static string GenerateCombinedCode(
        string namespaceName,
        string className,
        ImmutableArray<FieldDeclarationSyntax> fields, 
        ImmutableArray<StructDeclarationSyntax> structs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"using Nethermind.Serialization.Ssz;");
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    public partial class {className}Generated");
        sb.AppendLine("    {");

        // Generate fields
        foreach (var field in fields)
        {
            if (field != null)  // Add this null check
            {
                var variable = field.Declaration.Variables.FirstOrDefault();
                if (variable != null)
                {
                    var modifiers = field.Modifiers.ToFullString();
                    var fieldName = variable.Identifier.Text;
                    var fieldType = field.Declaration.Type.ToString();
                    var fieldInitializer = variable.Initializer?.ToFullString() ?? string.Empty;
                    sb.AppendLine($"{modifiers}{fieldType} {fieldName} {fieldInitializer};");
                }
            }
        }

        List<string> structNames = new List<string>();
        // Generate structs
        foreach (var structDecl in structs)
        {
            if (structDecl != null)  // Add this null check
            {
                sb.AppendLine($"        public partial struct {structDecl.Identifier.Text}");
                sb.AppendLine("        {");
                
                structNames.Add(structDecl.Identifier.Text);

                foreach (var member in structDecl.Members)
                {
                    if(member is PropertyDeclarationSyntax propertyDecl){
                        var modifiers = propertyDecl.Modifiers.ToString();
                        var type = propertyDecl.Type.ToString();
                        var name = propertyDecl.Identifier.Text;
                        var accessors = propertyDecl.AccessorList != null ? propertyDecl.AccessorList.ToString() : string.Empty;

                        sb.AppendLine($"            {modifiers} {type} {name} {accessors}");

                    }
                    else if (member is FieldDeclarationSyntax fieldDecl)
                    {
                        var modifiers = fieldDecl.Modifiers.ToString();
                        var type = fieldDecl.Declaration.Type.ToString();
                        var variable = fieldDecl.Declaration.Variables.FirstOrDefault();
                        if (variable != null)
                        {
                            var fieldName = variable.Identifier.Text;
                            var fieldInitializer = variable.Initializer?.ToFullString() ?? string.Empty;
                            sb.AppendLine($"            {modifiers} {type} {fieldName} {fieldInitializer};");
                        }
                    }
                }
                sb.AppendLine("        }");
            }
        }

        sb.AppendLine(GenerateMainFunction(structNames));
        
        foreach (var structDecl in structs)
        {
            sb.Append(GenerateSerializingStruct(structDecl));

        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

private static string GenerateMainFunction(List<string> structNames){
    var sb = new StringBuilder();

    sb.AppendLine("        public static void GenerateStart(");
    foreach(var structName in structNames){
        char[] varNameChar = structName.ToCharArray();
        varNameChar[0] = char.ToLower(varNameChar[0]);
        string structVarName = new string(varNameChar);
        sb.AppendLine($"            {structName} {structVarName},");
    }
    sb.AppendLine("        )");
    sb.AppendLine("        {");
    sb.AppendLine("            var buffer = new byte[100];");
    sb.AppendLine("            int offset = 0;");
    foreach(var structName in structNames){
        char[] varNameChar = structName.ToCharArray();
        varNameChar[0] = char.ToLower(varNameChar[0]);
        string structVarName = new string(varNameChar);
        sb.AppendLine($"            Encode(buffer, {structVarName}, ref offset);");
    }
    sb.AppendLine("            var slicedBuffer = new ArraySegment<byte>(buffer, 0, offset).ToArray();");
    sb.AppendLine($"            Console.WriteLine(string.Join(\" \", slicedBuffer));");
    sb.AppendLine("         }");
    
    return sb.ToString();
}


private static string GenerateSerializingStruct(StructDeclarationSyntax structDecl)
    {
        var sb = new StringBuilder();
        string structName = structDecl.Identifier.Text;
        char[] varNameChar = structName.ToCharArray();
        varNameChar[0] = char.ToLower(varNameChar[0]);
        string structVarName = new string(varNameChar);
        sb.AppendLine($"        public static void Encode(Span<byte> span, {structName} {structVarName}, ref int offset)");
        sb.AppendLine("        {");


            if (structDecl != null)  // Add this null check
            {
                foreach (var member in structDecl.Members)
                {
                    if(member is PropertyDeclarationSyntax propertyDecl){
                        var name = propertyDecl.Identifier.Text;
                        var type = propertyDecl.Type.ToString();
                        if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_OFFSET.Contains(type))
                            sb.AppendLine($"            Ssz.Encode(span, {structVarName}.{name}, ref offset);");
                        else if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_NOOFFSET.Contains(type))
                            sb.AppendLine($"            Ssz.Encode(span, {structVarName}.{name});");
                        else
                            sb.AppendLine($"            Encode(span, {structVarName}.{name}, ref offset);");
                    }
                    else if (member is FieldDeclarationSyntax fieldDecl)
                    {
                        var variable = fieldDecl.Declaration.Variables.FirstOrDefault();
                        if (variable != null)
                        {
                            var name = variable.Identifier.Text;
                            var type = fieldDecl.Declaration.Type.ToString();
                            if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_OFFSET.Contains(type))
                                sb.AppendLine($"            Ssz.Encode(span, {structVarName}.{name}, ref offset);");
                            else if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_NOOFFSET.Contains(type))
                                sb.AppendLine($"            Ssz.Encode(span, {structVarName}.{name});");
                            else
                                sb.AppendLine($"            Encode(span, {structVarName}.{name}, ref offset);");
                         }
                    }
                }
            }
        

        sb.AppendLine("        }");

        return sb.ToString();
    }



}
// 