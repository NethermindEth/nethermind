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
    private string[] primitiveTypes = new string[]
        {
            "byte",
            "sbyte",
            "short",
            "ushort",
            "int",
            "uint",
            "long",
            "ulong",
            "float",
            "double",
            "decimal",
            "char",
            "bool",
            "object",
            "string",
        };
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsClassWithAttribute(syntaxNode),
                transform: (context, _) => GetClassWithAttribute(context))
            .Where(classNode => classNode is not null);

        var fieldDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsFieldWithAttribute(syntaxNode),
                transform: (context, _) => GetFieldWithAttribute(context))
            .Where(fieldNode => fieldNode is not null);

        var structDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsStructWithAttribute(syntaxNode),
                transform: (context, _) => GetStructWithAttribute(context))
            .Where(structNode => structNode is not null);

        var combinedDeclarations = fieldDeclarations.Collect()
            .Combine(structDeclarations.Collect())
            .Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(combinedDeclarations, (spc, combined) =>
        {
            var ((fields, structs), classes) = combined;
            var sb = new StringBuilder();
            foreach (var classDecl in classes)
            {
                if (classDecl != null)
                {
                    var className = classDecl.Identifier.Text;
                    var namespaceName = GetNamespace(classDecl);
                    var nonNullFields = fields.Where(f => f != null && className == GetClassNameOfFieldAndStruct(f)).Cast<FieldDeclarationSyntax>().ToImmutableArray();
                    var nonNullStructs = structs.Where(s => s != null && className == GetClassNameOfFieldAndStruct(s) ).Cast<StructDeclarationSyntax>().ToImmutableArray();
                    var generatedCode = GenerateCombinedCode(namespaceName, className, nonNullFields, nonNullStructs);
                    spc.AddSource($"{className}_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
                }
            }
        });
    }

    private static bool IsClassWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is ClassDeclarationSyntax classDeclaration &&
               classDeclaration.AttributeLists.Any();
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
                    methodSymbol.ContainingType.Name == "SSZClassAttribute")
                {
                    return classDeclaration;
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
                    methodSymbol.ContainingType.Name == "SSZFieldAttribute")
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
                    methodSymbol.ContainingType.Name == "SSZStructAttribute")
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

    struct FieldInfo {
        public string FieldType {get;set;}
        public string FieldName {get;set;}
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
        sb.AppendLine($"using System;");
        sb.AppendLine($"using System.Text;");
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    public partial class {className}Generated");
        sb.AppendLine("    {");


        // Generate fields
        List<FieldInfo> fieldList = new List<FieldInfo>();
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
                    fieldList.Add(new FieldInfo{FieldType=fieldType, FieldName=fieldName});
                    var fieldInitializer = variable.Initializer?.ToFullString() ?? string.Empty;
                    sb.AppendLine($"{modifiers}{fieldType} {fieldName} {fieldInitializer}; {(fieldType!="dynamic" ? "// fixed type" : "//dynamic type")}");
                }
            }
        }

        // Generate structs
        List<string> structNames = new List<string>();
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
                        var propertyType = propertyDecl.Type.ToString();
                        var name = propertyDecl.Identifier.Text;
                        var accessors = propertyDecl.AccessorList != null ? propertyDecl.AccessorList.ToString() : string.Empty;

                        sb.AppendLine($"            {modifiers} {propertyType} {name} {accessors} {(propertyType!="dynamic" ? "// fixed type" : "//dynamic type")}");

                    }
                    else if (member is FieldDeclarationSyntax fieldDecl)
                    {
                        var modifiers = fieldDecl.Modifiers.ToString();
                        var fieldType = fieldDecl.Declaration.Type.ToString();
                        var variable = fieldDecl.Declaration.Variables.FirstOrDefault();
                        if (variable != null)
                        {
                            var fieldName = variable.Identifier.Text;
                            var fieldInitializer = variable.Initializer?.ToFullString() ?? string.Empty;
                            sb.AppendLine($"            {modifiers} {fieldType} {fieldName} {fieldInitializer}; {(fieldType!="dynamic" ? "// fixed type" : "//dynamic type")}");
                        }
                    }
                }
                sb.AppendLine("        }");
            }
        }

        sb.Append(GenerateMainFunction(fieldList, structNames));

        //generate 1 encoding function for all the fields
        sb.Append(GenerateFieldsEncodingFunctions(fieldList));
        foreach (var structDecl in structs)
            sb.Append(GenerateStructsEncodingFunctions(structDecl));

        sb.Append(GenerateStringToByteArrayConversion());
        sb.AppendLine();
        sb.Append(GenerateGetByteSizeFunction());

        sb.AppendLine("}");
        sb.AppendLine("}");

        return sb.ToString();
    }


    //generate the very first function for developers to call, which takes in instances of the data structure as parameters
    //and calls the Encode functions generated for each data structure
    private static string GenerateMainFunction(List<FieldInfo> fieldList, List<string> structNames)
    {
        var sb = new StringBuilder();
        
        sb.Append("        public static byte[] GenerateStart(");

        /*** 
        generate parameters
        each parameter followed by a comma ","
        except for the last one
        */
        for(int i=0; i < fieldList.Count;i++){ //generate field parameters
            string fieldName = char.ToLower(fieldList[i].FieldName[0]) + fieldList[i].FieldName.Substring(1);
            sb.Append($"{fieldList[i].FieldType} {fieldName}{(i+1==fieldList.Count && structNames.Count == 0 ? "" : ",")} ");
        }
        for(int i=0;i<structNames.Count;i++){ //generate struct parameters
            char[] varNameChar = structNames[i].ToCharArray();
            varNameChar[0] = char.ToLower(varNameChar[0]);
            string structVarName = new string(varNameChar);
            sb.Append($"{structNames[i]} {structVarName}{(i+1==structNames.Count ? "" : ",")} ");
        }
        sb.AppendLine(")");
        sb.AppendLine("        {");


        /*** generate contents */
        sb.AppendLine("            var buffer = new byte[100];");
        sb.AppendLine("            int offset = 0;");

        //this for-loop passes all the fields parameter into EncodeAllFields() function.
        for(int i=0; i < fieldList.Count;i++){
            if(i==0)
                sb.Append($"            EncodeAllFields(buffer, ref offset, ");
            string fieldName = char.ToLower(fieldList[i].FieldName[0]) + fieldList[i].FieldName.Substring(1);
            sb.Append($" {fieldName} {(i+1 == fieldList.Count ? ");" : ",")}");
        }
        sb.AppendLine();
        //this for-loop calls on different Encode function for each struct
        foreach(var structName in structNames){
            char[] varNameChar = structName.ToCharArray();
            varNameChar[0] = char.ToLower(varNameChar[0]);
            string structVarName = new string(varNameChar);
            sb.AppendLine($"            Encode(buffer, {structVarName}, ref offset);");
        }
        sb.AppendLine("            var slicedBuffer = new ArraySegment<byte>(buffer, 0, offset).ToArray();");
        // sb.AppendLine($"            Console.WriteLine(string.Join(\" \", slicedBuffer));");
        sb.AppendLine($"            return slicedBuffer;");
        sb.AppendLine("        }");
        
        return sb.ToString();
    }

    //generate 1 large Encode function for all the fields
    private static string GenerateFieldsEncodingFunctions(List<FieldInfo> fieldList)
    {
        var sb = new StringBuilder();
        sb.Append($"        public static void EncodeAllFields(Span<byte> span, ref int offset{(fieldList.Count>0 ? ",": "")}");
        for(int i=0;i<fieldList.Count;i++){
            string fieldName = char.ToLower(fieldList[i].FieldName[0]) + fieldList[i].FieldName.Substring(1);
            sb.Append($" {fieldList[i].FieldType} {fieldName} {(i+1==fieldList.Count ? "" : ",")}");
        }
        sb.AppendLine(")");
        sb.AppendLine("        {");
        for(int i=0;i<fieldList.Count;i++){
            string fieldName = char.ToLower(fieldList[i].FieldName[0]) + fieldList[i].FieldName.Substring(1);

            //if the type is string, need to convert to byte[], as SSZ library doesn't support string
            if(fieldList[i].FieldType == "string")
                sb.AppendLine($"            Ssz.Encode(span, ConvertStringIntoByteArray({fieldName}), ref offset);");
            else if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_OFFSET.Contains(fieldList[i].FieldType))
                sb.AppendLine($"            Ssz.Encode(span, {fieldName}, ref offset);");
            else if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_NOOFFSET.Contains(fieldList[i].FieldType))
                sb.AppendLine($"            Ssz.Encode(span, {fieldName});");
        }
        sb.AppendLine("        }");

        return sb.ToString();
    }

    //generate Encode functions for each every struct
    private static string GenerateStructsEncodingFunctions(StructDeclarationSyntax structDecl)
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

                        //if the type is string, need to convert to byte[], as SSZ library doesn't support string
                        if(type == "string")
                            sb.AppendLine($"            Ssz.Encode(span, ConvertStringIntoByteArray({structVarName}.{name}), ref offset);");
                        else if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_OFFSET.Contains(type))
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
                            
                            //if the type is string, need to convert to byte[], as SSZ library doesn't support string
                            if(type == "string")
                                sb.AppendLine($"            Ssz.Encode(span, ConvertStringIntoByteArray({structVarName}.{name}), ref offset);");
                            else if(SSZ_LIB_SUPPORTED_ENCODING_TYPES_OFFSET.Contains(type))
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


    private static string GenerateStringToByteArrayConversion(){
        return @"
        public static byte[] ConvertStringIntoByteArray(string stringToConvert){
            return Encoding.UTF8.GetBytes(stringToConvert);
        }
        ";
    }


    private static string GetClassNameOfFieldAndStruct(SyntaxNode node){
        return node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";
    }

    private static string GenerateGetByteSizeFunction(){
        
        string byteSizeFunction = @"
             public static int CalculateByteSize(object obj)
    {
        if (obj == null) return 0;
        Type type = obj.GetType();
        int totalSize = 0;

        //since string is a special type, it will not go through checks like other types will
        if(type == typeof(string))
            return Encoding.UTF8.GetByteCount((string)obj);
        
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            if(field!=null){
                object? fieldValue = field.GetValue(obj);
                totalSize += CalculateFieldSize(field.FieldType, fieldValue??0);
            }
        }
        
        return totalSize;
    }


    //calculates primitive types, arrays and enums
    private static int CalculateFieldSize(Type fieldType, object fieldValue)
    {
        if (fieldValue == null) return 0;

        if (fieldType == typeof(byte) || fieldType == typeof(sbyte))
            return sizeof(byte);

        if (fieldType == typeof(bool))
            return sizeof(bool);

        if (fieldType == typeof(short) || fieldType == typeof(ushort))
            return sizeof(short);

        if (fieldType == typeof(int) || fieldType == typeof(uint))
            return sizeof(int);

        if (fieldType == typeof(long) || fieldType == typeof(ulong))
            return sizeof(long);

        if (fieldType == typeof(float))
            return sizeof(float);

        if (fieldType == typeof(double))
            return sizeof(double);

        if (fieldType == typeof(char))
            return sizeof(char);

        if (fieldType == typeof(decimal))
            return sizeof(decimal);

        if (fieldType == typeof(string))
            return Encoding.UTF8.GetByteCount((string)fieldValue);

        if (fieldType.IsArray)
        {
            Array array = (Array)fieldValue;
            int arraySize = 0;
            foreach (var element in array)
            {
                arraySize += CalculateFieldSize(element.GetType(), element);
            }
            return arraySize;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(fieldType))
        {
            int enumerableSize = 0;
            foreach (var element in (System.Collections.IEnumerable)fieldValue)
            {
                enumerableSize += CalculateFieldSize(element.GetType(), element);
            }
            return enumerableSize;
        }

        /***
        If type is custom defined struct / type, calls CalculateByteSize()
        This creates a recursive function calling
        */
        return CalculateByteSize(fieldValue);
    }
    ";

    return byteSizeFunction;
    }

}
// 