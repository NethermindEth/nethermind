using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

[Generator]
public partial class SszGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<SszType?> classDeclarations = context.SyntaxProvider
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
            if (decl is null)
            {
                return;
            }
            var generatedCode = GenerateClassCode(decl);
            spc.AddSource($"{decl.Name}SszSerializer.cs", SourceText.From(generatedCode, Encoding.UTF8));
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
               classDeclaration.AttributeLists.Any(x => x.Attributes.Any());
    }

    private static SszType? GetClassWithAttribute(GeneratorSyntaxContext context)
    {
        TypeDeclarationSyntax classDeclaration = (TypeDeclarationSyntax)context.Node;
        foreach (AttributeListSyntax attributeList in classDeclaration.AttributeLists)
        {
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                IMethodSymbol? methodSymbol = context.SemanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
                if (methodSymbol is not null && methodSymbol.ContainingType.ToString() == "Nethermind.Serialization.Ssz.SszSerializableAttribute")
                {
                    return SszType.From(context.SemanticModel, new Dictionary<string, SszType>(SszType.InbuiltTypes), context.SemanticModel.GetDeclaredSymbol(classDeclaration)!);
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

    struct Dyn
    {
        public string OffsetDeclaration { get; init; }
        public string DynamicEncode { get; init; }
        public string DynamicLength { get; init; }
        public string DynamicDecode { get; init; }
    }

    private static string GenerateClassCode(SszType decl)
    {
        try
        {
            decl.IsProcessed = true;
            int staticLength = decl.Members.Sum(prop => prop.Type.StaticLength);
            List<Dyn> dynOffsets = new();
            SszProperty? prevM = null;

            foreach (SszProperty prop in decl.Members)
            {
                if (prop.Type.IsVariable)
                {
                    dynOffsets.Add(new Dyn
                    {
                        OffsetDeclaration = prevM is null ? $"int dynOffset{dynOffsets.Count + 1} = {staticLength}" : ($"int dynOffset{dynOffsets.Count + 1} = dynOffset{dynOffsets.Count} + {prevM!.DynamicLength}"),
                        DynamicEncode = prop.DynamicEncode ?? "",
                        DynamicLength = prop.DynamicLength!,
                        DynamicDecode = prop.DynamicDecode ?? "", //prevM is null ? $"int dynOffset{dynOffsets.Count + 1} = {staticLength}" : ($"int dynOffset{dynOffsets.Count + 1} = dynOffset{dynOffsets.Count} + {prevM!.Type.DynamicLength}"),
                    });
                    prevM = prop;
                }
            }

            var offset = 0;
            var offsetDecode = 0;
            var dynOffset = 0;
            var dynOffsetDecode = 1;

            var result = $@"using Nethermind.Int256;
using Nethermind.Merkleization;
using System;

using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace {(decl.Namespace is not null ? decl.Namespace + "." : "")}Serialization;

public partial class SszEncoding
{{
    public static int GetLength({decl.Name} container)
    {{
        return {staticLength}{(dynOffsets.Any() ? $" + \n               {string.Join(" +\n               ", dynOffsets.Select(m => m.DynamicLength))}" : "")};
    }}


    public static ReadOnlySpan<byte> Encode({decl.Name} container)
    {{
        Span<byte> buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }}

    public static void Encode(Span<byte> buf, {decl.Name} container)
    {{
        {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.Select(m => m.OffsetDeclaration)) + ";\n        \n" : "")}
        {string.Join(";\n        ", decl.Members.Select(m =>
            {
                if (m.Type.IsVariable) dynOffset++;
                string result = m.Type.Members.Any() ? m.StaticEncode.Replace("{offset}", $"{offset}").Replace("{dynOffset}", $"dynOffset{dynOffset}").Replace("SszLib.Encode", "Encode") : m.StaticEncode.Replace("{offset}", $"{offset}").Replace("{dynOffset}", $"dynOffset{dynOffset}");
                offset += m.Type.StaticLength;
                return result;
            }))};

            {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.Select((m, i) => m.DynamicEncode.Replace("{dynOffset}", $"dynOffset{i + 1}").Replace("{length}", m.DynamicLength))) + ";\n        \n" : "")}
    }}

    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name} container)
    {{
        container = new();
        {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.First().OffsetDeclaration) + ";\n" : "")}
        {string.Join(";\n        ", decl.Members.Select(m =>
            {
                if (m.Type.IsVariable) dynOffsetDecode++;
                string result = m.StaticDecode.Replace("{offset}", $"{offsetDecode}").Replace("{dynOffset}", $"dynOffset{dynOffsetDecode}");
                offsetDecode += m.Type.StaticLength;
                return result;
            }))};
        {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.Select((m, i) => m.DynamicDecode.Replace("{dynOffset}", $"dynOffset{i + 1}").Replace("{dynOffsetNext}", i + 1 == dynOffsets.Count ? "data.Length" : $"dynOffset{i + 2}"))) + ";\n        \n" : "")}
    }}

    public static void Merkleize({decl.Name} container, out UInt256 root)
    {{
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent({decl.Members.Length}));

        {string.Join(";\n        ", decl.Members.Select(m => m.Type.IsSimple ? $"merkleizer.Feed(container.{m.Name})" : $"Merkleize(container.{m.Name}, out UInt256 rootOf{m.Name});\n        merkleizer.Feed(rootOf{m.Name})"))};

        merkleizer.CalculateRoot(out root);
    }}
}}
";

            return result;
        }
        catch
        {
            throw;
        }
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
}


