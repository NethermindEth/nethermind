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
        IncrementalValuesProvider<(SszType, List<SszType>)?> classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsClassWithAttribute(syntaxNode),
                transform: (context, _) => GetClassWithAttribute(context))
            .Where(classNode => classNode is not null);

        context.RegisterSourceOutput(classDeclarations, (spc, decl) =>
        {
            if (decl is null)
            {
                return;
            }
            var generatedCode = GenerateClassCode(decl.Value.Item1, decl.Value.Item2);
            spc.AddSource($"Serialization.SszEncoding.{decl.Value.Item1.Name}.cs", SourceText.From(generatedCode, Encoding.UTF8));
        });
    }

    private static bool IsClassWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is TypeDeclarationSyntax classDeclaration &&
               classDeclaration.AttributeLists.Any(x => x.Attributes.Any());
    }

    private static (SszType, List<SszType>)? GetClassWithAttribute(GeneratorSyntaxContext context)
    {
        TypeDeclarationSyntax classDeclaration = (TypeDeclarationSyntax)context.Node;
        foreach (AttributeListSyntax attributeList in classDeclaration.AttributeLists)
        {
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                IMethodSymbol? methodSymbol = context.SemanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
                if (methodSymbol is not null && methodSymbol.ContainingType.ToString() == "Nethermind.Serialization.Ssz.SszSerializableAttribute")
                {
                    var foundTypes = new List<SszType>(SszType.BasicTypes);
                    return (SszType.From(context.SemanticModel, foundTypes, context.SemanticModel.GetDeclaredSymbol(classDeclaration)!), foundTypes);
                }
            }
        }
        return null;
    }

    struct Dyn
    {
        public string OffsetDeclaration { get; init; }
        public string DynamicEncode { get; init; }
        public string DynamicLength { get; init; }
        public string DynamicDecode { get; init; }
    }

    const string Whitespace = "/**/";
    public static string FixWhitespace(string data) => string.Join("\n", data.Split('\n').Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=> x.Contains(Whitespace) ? "" : x));
    public static string Shift(int tabCount, string data) => string.Empty.PadLeft(4 * tabCount) + data;
    public static string Shift(int tabCount, IEnumerable<string> data, string? end = null) => string.Join("\n", data.Select(d=>Shift(tabCount, d))) + (end is null || !data.Any() ? "" : end);

    private static string LowerStart(string name) => string.IsNullOrEmpty(name) ? name : (name.Substring(0, 1).ToLower() + name.Substring(1));

    private static string DynamicLength(SszProperty m)
    {
        if((m.Kind & Kind.Collection) != Kind.None && m.Type.Kind == Kind.Basic)
        {
            return $"(container.{m.Name} is not null ? {m.Type.StaticLength} * container.{m.Name}.Count() : 0)";
        }

        return $"GetLength(container.{m.Name})";
    }

    private static string GenerateClassCode(SszType decl, List<SszType> foundTypes)
    {
        try
        {
            List<SszProperty> variables = decl.Members.Where(m => m.IsVariable).ToList();
            int encodeOffsetIndex = 0, encodeStaticOffset = 0;
            int offsetIndex = 0, offset = 0;
            string result = FixWhitespace(decl.Kind == Kind.Container ? $@"using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
{string.Join("\n", foundTypes.Select(x => x.Namespace).Distinct().Select(n => $"using {n};"))}
{Whitespace}
using SszLib = Nethermind.Serialization.Ssz.Ssz;
{Whitespace}
namespace Nethermind.Serialization;
{Whitespace}
public partial class SszEncoding
{{
    public static int GetLength({decl.Name} container)
    {{
        return {decl.StaticLength}{(variables.Any() ? "" : ";")}
{               Shift(4, variables.Select(m => $"+ {DynamicLength(m)}"), ";")}
    }}
{Whitespace}
    public static int GetLength(ICollection<{decl.Name}>? container)
    {{
        if(container is null)
        {{
            return 0;
        }}
        {(decl.IsVariable ? @$"int length = container.Count * {SszType.PointerLength};
        foreach({decl.Name} item in container)
        {{
            length += GetLength(item);
        }}
        return length;" : $"return container.Count * {(decl.StaticLength)};")}
    }}
{Whitespace}
    public static ReadOnlySpan<byte> Encode({decl.Name} container)
    {{
        Span<byte> buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, {decl.Name} container)
    {{
{Shift(2, variables.Select((m, i) => $"int offset{i + 1} = {(i == 0 ? decl.StaticLength : $"offset{i} + {DynamicLength(m)}")};"))}
{Shift(2, decl.Members.Select(m =>
            {
                if (m.IsVariable) encodeOffsetIndex++;
                string result = m.IsVariable ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), offset{encodeOffsetIndex});"
                                                : m.HandledByStd ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name});"
                                                                 : $"Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name});";
                encodeStaticOffset += m.StaticLength;
                return result;
            }))}

{Shift(2, variables.Select((m, i) => $"if (container.{m.Name} is not null) " + $"{(m.HandledByStd ? "SszLib.Encode" : "Encode")}(data.Slice(offset{i + 1}, {(i + 1 == variables.Count ? "data.Length" : $"offset{i + 2}")} - offset{i + 1}), container.{m.Name});"))}
    }}
{Whitespace}
    public static void Encode(Span<byte> data, ICollection<{decl.Name}> container)
    {{
        {(decl.IsVariable ? @$"int offset = container.Count * {(SszType.PointerLength)};
        int itemOffset = 0;
        foreach({decl.Name} item in container)
        {{
            SszLib.Encode(data.Slice(itemOffset, {(SszType.PointerLength)}), offset);
            itemOffset += {(SszType.PointerLength)};
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }}" : @$"int offset = 0;
        foreach({decl.Name} item in container)
        {{
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }}")}
    }}
{Whitespace}
    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name} container)
    {{
        container = new();
{       Shift(2, decl.Members.Select(m =>
{
    if (m.IsVariable) offsetIndex++;
    string result = m.IsVariable ? $"SszLib.Decode(data.Slice({offset}, 4), out int offset{offsetIndex});"
                                    : m.HandledByStd ? $"SszLib.Decode(data.Slice({offset}, {m.StaticLength}), out {(m.IsCollection ? $"ReadOnlySpan<{m.Type.Name}>" : m.Type.Name)} {LowerStart(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{LowerStart(m.Name)}]" : LowerStart(m.Name))};"
                                                     : $"Decode(data.Slice({offset}, {m.StaticLength}), out {(m.IsCollection ? $"{m.Type.Name}[]" : m.Type.Name)} {LowerStart(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{LowerStart(m.Name)}]" : LowerStart(m.Name))};";
    offset += m.StaticLength;
    return result;
}))}
{       Shift(2, variables.Select((m, i) => string.Format($"if (container.{m.Name} is not null) {{{{ {{0}} }}}}", 
            $"{(m.HandledByStd ? "SszLib.Decode" : "Decode")}(data.Slice(offset{i + 1}, {(i + 1 == variables.Count ? "data.Length" : $"offset{i + 2}")} - offset{i + 1}), out {(m.IsCollection ? (m.HandledByStd ? $"ReadOnlySpan<{m.Type.Name}>" : $"{m.Type.Name}[]") : m.Type.Name)} {LowerStart(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{LowerStart(m.Name)}]" : LowerStart(m.Name))};")))}
    }}
{Whitespace}
    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name}[] container)
    {{
        if(data.Length is 0)
        {{
            container = [];
            return;
        }}

        {(decl.IsVariable ? $@"int firstOffset = SszLib.DecodeInt(data.Slice(0, 4));
        int length = firstOffset / {SszType.PointerLength}" : $"int length = data.Length / {decl.StaticLength}")};

        container = new {decl.Name}[length];

        {(decl.IsVariable ? @$"int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
        {{
            int nextOffset = SszLib.DecodeInt(data.Slice(nextOffsetIndex, {SszType.PointerLength}));
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }}
        Decode(data.Slice(offset, length), out container[index]);" : @$"int offset = 0;
        for(int index = 0; index < length; index++)
        {{
            Decode(data.Slice(offset, {decl.StaticLength}), out container[index]);
            offset += {decl.StaticLength};
        }}")}
    }}
{Whitespace}
    public static void Merkleize({decl.Name} container, out UInt256 root)
    {{
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent({decl.Members!.Length}));
{Shift(2, decl.Members.Select(m =>
{
    if (m.IsVariable) offsetIndex++;
    string result = m.HandledByStd ? $"merkleizer.Feed(container.{m.Name}{(m.Kind == Kind.List ? $", {m.Limit}" : "")});"
                                    : m.Kind == Kind.List ? $"MerkleizeList(container.{m.Name}, {m.Limit}, out UInt256 {LowerStart(m.Name)}Root); merkleizer.Feed({LowerStart(m.Name)}Root);"
                                                          : m.Kind == Kind.Vector ? $"MerkleizeVector(container.{m.Name}, out UInt256 {LowerStart(m.Name)}Root); merkleizer.Feed({LowerStart(m.Name)}Root);"
                                                                                  : $"Merkleize(container.{m.Name}, out UInt256 {LowerStart(m.Name)}Root); merkleizer.Feed({LowerStart(m.Name)}Root);";
    offset += m.StaticLength;
    return result;
}))}
        merkleizer.CalculateRoot(out root);
    }}
{Whitespace}
    public static void MerkleizeVector(IList<{decl.Name}> container, out UInt256 root)
    {{
        UInt256[] subRoots = new UInt256[container.Count];
        for(int i = 0; i < container.Count; i++)
        {{
            Merkleize(container[i], out subRoots[i]);
        }}
        Merkle.Ize(out root, subRoots);
    }}
{Whitespace}
    public static void MerkleizeList(IList<{decl.Name}> container, ulong limit, out UInt256 root)
    {{
        MerkleizeVector(container, out root);
        Merkle.MixIn(ref root, container.Count);
    }}
}}
" :








$@"using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
{string.Join("\n", foundTypes.Select(x => x.Namespace).Distinct().Select(n => $"using {n};"))}
{Whitespace}
using SszLib = Nethermind.Serialization.Ssz.Ssz;
{Whitespace}
namespace Nethermind.Serialization;
{Whitespace}
public partial class SszEncoding
{{
    public static int GetLength({decl.Name} container)
    {{
        return 1 + container.Selector switch {{
{           Shift(3, decl.UnionMembers.Select(m=> $"{decl.Selector!.Type.Name}.{m.Name} => {(m.IsVariable ? DynamicLength(m) : m.StaticLength.ToString())},"))}
            _ => 0,
        }};
    }}
{Whitespace}
    public static int GetLength(ICollection<{decl.Name}> container)
    {{
        int length = container.Count * {SszType.PointerLength};
        foreach({decl.Name} item in container)
        {{
            length += GetLength(item);
        }}
        return length;
    }}
{Whitespace}
    public static ReadOnlySpan<byte> Encode({decl.Name} container)
    {{
        Span<byte> buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, {decl.Name} container)
    {{
        SszLib.Encode(data.Slice(0, 1), (byte)container.Selector);
        switch(container.Selector) {{
{           Shift(3, decl.UnionMembers.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {(m.IsVariable ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), offset{encodeOffsetIndex})"
                                    : m.Kind == Kind.Basic ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), container.{m.Name})"
                                                        : m.Type.Kind == Kind.Container ? $"Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name})"
                                                                                    : $"SszLib.Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name})")}; break;"))}
        }};
    }}
{Whitespace}
    public static void Decode(Span<byte> data, {decl.Name} container)
    {{
        SszLib.Encode(data.Slice(0, 1), (byte)container.Selector);
        switch(container.Selector) {{
{Shift(3, decl.UnionMembers.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {(m.IsVariable ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), offset{encodeOffsetIndex})"
                                    : m.Kind == Kind.Basic ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), container.{m.Name})"
                                                        : m.Type.Kind == Kind.Container ? $"Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name})"
                                                                                    : $"SszLib.Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name})")}; break;"))}
        }};
    }}
{Whitespace}
    public static void Merkleize({decl.Name} container, out UInt256 root)
    {{
        SszLib.Encode(data.Slice(0, 1), (byte)container.Selector);
        switch(container.Selector) {{
{Shift(3, decl.UnionMembers.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {(m.IsVariable ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), offset{encodeOffsetIndex})"
                                    : m.Kind == Kind.Basic ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), container.{m.Name})"
                                                        : m.Type.Kind == Kind.Container ? $"Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name})"
                                                                                    : $"SszLib.Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name})")}; break;"))}
        }};
    }}
}}
");
            Console.WriteLine(WithLineNumbers(result));
            return result;
        }
        catch
        {
            throw;
        }
    }

    static string WithLineNumbers(string input)
    {

        string[] lines = input.Split('\n');
        int lineNumberWidth = lines.Length.ToString().Length;

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string lineNumber = (i + 1).ToString().PadLeft(lineNumberWidth);
            sb.AppendLine($"{lineNumber}: {lines[i]}");
        }

        return sb.ToString();
    }

    //    private static string GenerateClassCode2(SszType decl)
    //    {
    //        try
    //        {
    //            int staticLength = decl.Members.Sum(prop => prop.StaticLength);
    //            List<Dyn> dynOffsets = new();
    //            SszProperty? prevM = null;

    //            foreach (SszProperty prop in decl.Members)
    //            {
    //                if (prop.IsVariable)
    //                {
    //                    dynOffsets.Add(new Dyn
    //                    {
    //                        OffsetDeclaration = prevM is null ? $"int dynOffset{dynOffsets.Count + 1} = {staticLength}" : ($"int dynOffset{dynOffsets.Count + 1} = dynOffset{dynOffsets.Count} + {prevM!.DynamicLength}"),
    //                        DynamicEncode = prop.DynamicEncode ?? "",
    //                        DynamicLength = prop.DynamicLength!,
    //                        DynamicDecode = prop.DynamicDecode ?? "",
    //                    });
    //                    prevM = prop;
    //                }
    //            }

    //            var offset = 0;
    //            var offsetDecode = 0;
    //            var dynOffset = 0;
    //            var dynOffsetDecode = 1;

    //            var result = $@"using Nethermind.Int256;
    //using Nethermind.Merkleization;
    //using System;
    //using System.Collections.Generic;
    //using System.Linq;
    //using {decl.Namespace};

    //using SszLib = Nethermind.Serialization.Ssz.Ssz;

    //namespace Nethermind.Serialization;

    //public partial class SszEncoding
    //{{
    //    public static int GetLength({decl.Name} container)
    //    {{
    //        return {staticLength}{(dynOffsets.Any() ? $" + \n               {string.Join(" +\n               ", dynOffsets.Select(m => m.DynamicLength))}" : "")};
    //    }}

    //    public static int GetLength(ICollection<{decl.Name}> container)
    //    {{
    //        {(decl.IsVariable ? @$"int length = container.Count * {(decl.IsVariable ? SszType.PointerLength : decl.StaticLength)};
    //        foreach({decl.Name} item in container)
    //        {{
    //            length += GetLength(item);
    //        }}
    //        return length;" : $"return container.Count * {(decl.StaticLength)};")}
    //    }}

    //    public static ReadOnlySpan<byte> Encode({decl.Name} container)
    //    {{
    //        Span<byte> buf = new byte[GetLength(container)];
    //        Encode(buf, container);
    //        return buf;
    //    }}

    //    public static void Encode(Span<byte> buf, {decl.Name} container)
    //    {{
    //        {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.Select(m => m.OffsetDeclaration)) + ";\n        \n" : "")}
    //        {string.Join(";\n        ", decl.Members.Select(m =>
    //            {
    //                if (m.IsVariable) dynOffset++;
    //                string result = !m.IsVariable && !m.Type.IsBasic ? m.StaticEncode.Replace("{offset}", $"{offset}").Replace("{dynOffset}", $"dynOffset{dynOffset}").Replace("SszLib.Encode", "Encode") : m.StaticEncode.Replace("{offset}", $"{offset}").Replace("{dynOffset}", $"dynOffset{dynOffset}");
    //                offset += m.StaticLength;
    //                return result;
    //            }))};

    //            {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.Select((m, i) => m.DynamicEncode.Replace("{dynOffset}", $"dynOffset{i + 1}").Replace("{length}", m.DynamicLength))) + ";\n        \n" : "")}
    //    }}

    //    public static void Encode(Span<byte> buf, ICollection<{decl.Name}> container)
    //    {{
    //        {(decl.IsVariable ? @$"int offset = container.Count * {(SszType.PointerLength)};
    //        int itemOffset = 0;
    //        foreach({decl.Name} item in container)
    //        {{
    //            SszLib.Encode(buf.Slice(itemOffset, {(SszType.PointerLength)}), offset);
    //            itemOffset += {(SszType.PointerLength)};
    //            int length = GetLength(item);
    //            Encode(buf.Slice(offset, length), item);
    //            offset += length;
    //        }}" : @$"int offset = 0;
    //        foreach({decl.Name} item in container)
    //        {{
    //            int length = GetLength(item);
    //            Encode(buf.Slice(offset, length), item);
    //            offset += length;
    //        }}")}
    //    }}

    //    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name} container)
    //    {{
    //        container = new();
    //        {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.First().OffsetDeclaration) + ";\n" : "")}
    //        {string.Join(";\n        ", decl.Members.Select(m =>
    //            {
    //                if (m.IsVariable) dynOffsetDecode++;
    //                string result = m.StaticDecode.Replace("{offset}", $"{offsetDecode}").Replace("{dynOffset}", $"dynOffset{dynOffsetDecode}");
    //                offsetDecode += m.StaticLength;
    //                return result;
    //            }))};
    //        {(dynOffsets.Any() ? string.Join(";\n        ", dynOffsets.Select((m, i) =>
    //                m.DynamicDecode.Replace("{dynOffset}", $"dynOffset{i + 1}").Replace("{dynOffsetNext}", i + 1 == dynOffsets.Count ? "data.Length" : $"dynOffset{i + 2}"))) : "")}
    //    }}

    //    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name}[] container)
    //    {{
    //        if(data.Length is 0)
    //        {{
    //            container = [];
    //            return;
    //        }}

    //        {(decl.IsVariable ? $@"int firstOffset = SszLib.DecodeInt(data.Slice(0, 4));
    //        int length = firstOffset / {SszType.PointerLength}" : $"int length = data.Length / {decl.StaticLength}")};

    //        container = new {decl.Name}[length];

    //        {(decl.IsVariable ? @$"int index = 0;
    //        int offset = firstOffset;
    //        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
    //        {{
    //            int nextOffset = SszLib.DecodeInt(data.Slice(nextOffsetIndex, {SszType.PointerLength}));
    //            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
    //            offset = nextOffset;
    //        }}
    //        Decode(data.Slice(offset, length), out container[index]);" : @$"int offset = 0;
    //        for(int index = 0; index < length; index++)
    //        {{
    //            Decode(data.Slice(offset, {decl.StaticLength}), out container[index]);
    //            offset += {decl.StaticLength};
    //        }}")}
    //    }}

    //    public static void Decode(ReadOnlySpan<byte> data, out List<{decl.Name}> container)
    //    {{
    //        Decode(data, out {decl.Name}[] array);
    //        container = array.ToList();
    //    }}

    //    public static void Merkleize({decl.Name} container, out UInt256 root)
    //    {{
    //        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent({decl.Members.Length}));

    //        {string.Join(";\n        ", decl.Members.Select(m => m.Type.IsBasic || (m.Type.IsCollection && m.Type.ElementType!.IsBasic) ? $"merkleizer.Feed(container.{m.Name})" : $"Merkleize(container.{m.Name}, out UInt256 rootOf{m.Name});\n        merkleizer.Feed(rootOf{m.Name})"))};

    //        merkleizer.CalculateRoot(out root);
    //    }}

    //    public static void Merkleize(ICollection<{decl.Name}> container, out UInt256 root)
    //    {{
    //        Merkleize(container, (ulong)container.Count, out root);
    //    }}

    //    public static void Merkleize(ICollection<{decl.Name}> container, ulong limit, out UInt256 root)
    //    {{
    //        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(limit));

    //        foreach({decl.Name} item in container)
    //        {{
    //            {(decl.IsBasic || (decl.IsCollection && decl.ElementType!.IsBasic) ? $"merkleizer.Feed(item)" : $"Merkleize(item, out UInt256 localRoot);\n        merkleizer.Feed(localRoot)")};
    //        }}

    //        merkleizer.CalculateRoot(out root);
    //    }}
    //}}
    //";
    //            return result;
    //        }
    //        catch
    //        {
    //            throw;
    //        }
    //    }

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


