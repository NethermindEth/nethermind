using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using Nethermind.Generated.Ssz;
using Microsoft.CodeAnalysis.Operations;

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

        //var methodDeclarations = context.SyntaxProvider
        //    .CreateSyntaxProvider(
        //        predicate: (syntaxNode, _) => IsMethodWithAttribute(syntaxNode),
        //        transform: (context, _) => GetMethodWithAttribute(context))
        //    .Where(methodNode => methodNode is not null);

        //var fieldDeclarations = context.SyntaxProvider
        //    .CreateSyntaxProvider(
        //        predicate: (syntaxNode, _) => IsFieldWithAttribute(syntaxNode),
        //        transform: (context, _) => GetFieldWithAttribute(context))
        //    .Where(fieldNode => fieldNode is not null);

        context.RegisterSourceOutput(classDeclarations, (spc, decl) =>
        {
            if(decl is null)
            {
                return;
            }
            var generatedCode = GenerateClassCode(decl);
            spc.AddSource($"{decl.TypeName}SszSerializer.cs", SourceText.From(generatedCode, Encoding.UTF8));
        });

        //context.RegisterSourceOutput(methodDeclarations, (spc, methodNode) =>
        //{
        //    if (methodNode is MethodDeclarationSyntax methodDeclaration)
        //    {
        //        var methodName = methodDeclaration.Identifier.Text;
        //        var classDeclaration = methodDeclaration.Parent as ClassDeclarationSyntax;
        //        var className = classDeclaration?.Identifier.Text ?? "default";
        //        var namespaceName = GetNamespace(methodDeclaration);
        //        var generatedCode = GenerateMethodCode(namespaceName, className, methodName);
        //        spc.AddSource($"{className}_{methodName}_method_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
        //    }
        //});

        //context.RegisterSourceOutput(fieldDeclarations, (spc, fieldNode) =>
        //{
        //    if (fieldNode is FieldDeclarationSyntax fieldDeclaration)
        //    {
        //        var variable = fieldDeclaration.Declaration.Variables.FirstOrDefault();
        //        if (variable != null)
        //        {
        //            var fieldName = variable.Identifier.Text;
        //            var classDeclaration = fieldDeclaration.Parent as ClassDeclarationSyntax;
        //            var className = classDeclaration?.Identifier.Text ?? "default";
        //            var namespaceName = GetNamespace(fieldDeclaration);
        //            var generatedCode = GenerateFieldCode(namespaceName, className, fieldName);
        //            spc.AddSource($"{className}_{fieldName}_field_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
        //        }
        //    }
        //});

    }

    private static bool IsClassWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is TypeDeclarationSyntax classDeclaration &&
               classDeclaration.AttributeLists.Any(x=>x.Attributes.Any());
    }

    //private static bool IsMethodWithAttribute(SyntaxNode syntaxNode)
    //{
    //    return syntaxNode is MethodDeclarationSyntax methodDeclaration &&
    //           methodDeclaration.AttributeLists.Any();
    //}

    //private static bool IsFieldWithAttribute(SyntaxNode syntaxNode)
    //{
    //    return syntaxNode is FieldDeclarationSyntax fieldDeclaration &&
    //           fieldDeclaration.AttributeLists.Any();
    //}

    class TypeDeclaration(string typeNamespaceName, string typeName, bool isStruct, PropertyDeclaration[] members)
    {
        public string TypeNamespaceName { get; } = typeNamespaceName;
        public string TypeName { get; } = typeName;
        public bool IsStruct { get; } = isStruct;
        public PropertyDeclaration[] Members { get; } = members;
    }

    class PropertyDeclaration(string typeName, string name)
    {
        public string TypeName { get; } = typeName;
        public string Name { get; } = name;
    }


    private static TypeDeclaration? GetClassWithAttribute(GeneratorSyntaxContext context)
    {
        var classDeclaration = (TypeDeclarationSyntax)context.Node;
        foreach (var attributeList in classDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                    methodSymbol.ContainingType.ToString() == "Nethermind.Generated.Ssz.SszSerializableAttribute")
                {
                    var props = context.SemanticModel.GetDeclaredSymbol(classDeclaration)?.GetMembers().OfType<IPropertySymbol>();
                    if(props?.Any() != true)
                    {
                        continue;
                    }

                    return new(
                        GetNamespace(classDeclaration),
                        classDeclaration.Identifier.Text,
                        classDeclaration is StructDeclarationSyntax,
                        props.Select(x=> new PropertyDeclaration(x.Type.ToString(), x.Name)).ToArray());
                }
            }
        }
        return null;
    }

    //private static MethodDeclarationSyntax? GetMethodWithAttribute(GeneratorSyntaxContext context)
    //{
    //    var methodDeclaration = (MethodDeclarationSyntax)context.Node;
    //    foreach (var attributeList in methodDeclaration.AttributeLists)
    //    {
    //        foreach (var attribute in attributeList.Attributes)
    //        {
    //            var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
    //            if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
    //                methodSymbol.ContainingType.Name == "FunctionAttribute")
    //            {
    //                return methodDeclaration;
    //            }
    //        }
    //    }
    //    return null;
    //}

    //private static FieldDeclarationSyntax? GetFieldWithAttribute(GeneratorSyntaxContext context)
    //{
    //    var fieldDeclaration = (FieldDeclarationSyntax)context.Node;
    //    foreach (var attributeList in fieldDeclaration.AttributeLists)
    //    {
    //        foreach (var attribute in attributeList.Attributes)
    //        {
    //            var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
    //            if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
    //                methodSymbol.ContainingType.Name == "FieldAttribute")
    //            {
    //                return fieldDeclaration;
    //            }
    //        }
    //    }
    //    return null;
    //}

    private static string GetNamespace(SyntaxNode syntaxNode)
    {
        var namespaceDeclaration = syntaxNode.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        return namespaceDeclaration?.Name.ToString() ?? "GlobalNamespace";
    }

    private static string GenerateClassCode(TypeDeclaration decl)
    {
        var offset = 0;
        var dynIndex = 0;
        var dynCounter = 0;
        var c = 0;
        var c2 = 0;
        var dynIndex2 = 0;
        PropertyDeclaration? prevM = null;
        var result = $@"using Nethermind.Serialization.Ssz;

namespace {decl.TypeNamespaceName}.Generated;

public partial class {decl.TypeName}SszSerializer
{{
    public int GetLength({(decl.IsStruct ? "ref " : "")}{decl.TypeName} container)
    {{
        return {string.Join(" +\n               ", decl.Members.Select(m => LengthOf(m) ?? $"Ssz.GetLength(container.{m.Name})"))};
    }}

    public ReadOnlySpan<byte> Serialize({(decl.IsStruct ? "ref " : "")}{decl.TypeName} container)
    {{
        Span<byte> buf = new byte[GetLength({(decl.IsStruct ? "ref " : "")}container)];

        {string.Join(";\n        ", decl.Members.Where(x => IsDynamic(x, ref c)).Select(m => DynamicLengthOf(m, decl.Members.Sum(StaticLengthOf), ref dynIndex, ref prevM)))};

        {string.Join(";\n        ", decl.Members.Select(m => Encode(m, ref offset, ref dynCounter)))};

        {string.Join(";\n        ", decl.Members.Where(x => IsDynamic(x, ref c2)).Select(m => DynamicEncode(m, ref dynIndex2)))};

        return buf;
    }}
}}
";
        return result;
    }

    private static int PointerLength = 4;


    private static int StaticLengthOf(PropertyDeclaration m)
    {
        return m.TypeName switch
        {
            "byte[]" => 4,
            "byte[]?" => 4,
            "int" => 4,
            "int?" => 4,
            _ => 0,
        };
    }
    private static string DynamicLengthOf(PropertyDeclaration m, int staticLength, ref int dynIndex, ref PropertyDeclaration? prevM)
    {
        dynIndex++;
        var prev = prevM;
        prevM = m;

        if (dynIndex == 1)
        {
            return $"int dynOffset{dynIndex} = {staticLength}";
        }

        return $"int dynOffset{dynIndex} = dynOffset{dynIndex - 1} + " + prev!.TypeName switch
        {
            "byte[]" or "byte[]?" => $"(container.{prev.Name}?.Length ?? 0)",
            "int?" => $"(container.{prev.Name} is null ? 0 : 4)",
            _ => "0",
        };
    }

    private static string DynamicEncode(PropertyDeclaration m, ref int dynIndex)
    {
        dynIndex++;

        var nullable = m!.TypeName switch
        {
            _ => true,
        };

        var offset = m!.TypeName switch
        {
            "byte[]" => $"container.{m.Name}.Length",
            "byte[]?" => $"container.{m.Name}.Length",
            "int?" => "4",
            _ => "0",
        };

        var accessValue = m!.TypeName switch
        {
            "int?" => $"{m.Name}.Value",
            _ => $"{m.Name}",
        };

        return $"{(nullable ? $"if(container.{m.Name} is not null)" : "")} Ssz.Encode(buf.Slice(dynOffset{dynIndex}, {offset}), container.{accessValue})";
    }

    private static string? LengthOf(PropertyDeclaration m)
    {
        return m.TypeName switch
        {
            "byte[]?" or "byte[]" => $"{PointerLength}" + $" + (container.{m.Name}?.Length ?? 0)",
            "int" => "4",
            "int?" => $"(container.{m.Name} is null ? 4 : 8)",
            _ => null,
        };
    }

    private static bool IsDynamic(PropertyDeclaration m, ref int dynCounter)
    {
        var isDynamic = m.TypeName switch
        {
            "int" => false,
            _ => true,
        };

        if (isDynamic) dynCounter++;
        return isDynamic;
    }

    private static string Encode(PropertyDeclaration m, ref int offset, ref int dynCounter)
    {
        var result = $"Ssz.Encode(buf.Slice({offset}, {StaticOffset(m)}), {(IsDynamic(m, ref dynCounter) ? $"dynOffset{dynCounter}" : $"container.{m.Name}")})";
        offset += StaticOffset(m);
        return result;
    }

    private static int StaticOffset(PropertyDeclaration m)
    {
        return m.TypeName switch
        {
            "byte[]" => 4,
            "byte[]?" => 4,
            "int" => 4,
            "int?" => 4,
            _ => 0,
        };
    }

    private static int DyanmicOffset(PropertyDeclaration m)
    {
        return m.TypeName switch
        {
            "byte[]" => 4,
            "byte[]?" => 4,
            "int" => 4,
            "int?" => 4,
            _ => 0,
        };
    }


    //private static string GenerateMethodCode(string namespaceName, string className, string methodName)
    //{
    //    return $@"
    //        namespace {namespaceName}
    //        {{
    //            public partial class {className}
    //            {{
    //                public string GetMethodName()
    //                {{
    //                    return ""{methodName}"";
    //                }}
    //            }}
    //        }}
    //        ";
    //}

    //private static string GenerateFieldCode(string namespaceName, string className, string fieldName)
    //{
    //    return $@"
    //        namespace {namespaceName}
    //        {{
    //            public partial class {className}
    //            {{
    //                public string GetFieldName()
    //                {{
    //                    return ""{fieldName}"";
    //                }}
    //            }}
    //        }}
    //        ";
    //}

}


