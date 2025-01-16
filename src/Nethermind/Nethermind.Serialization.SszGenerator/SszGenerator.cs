using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Text.RegularExpressions;

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
                    return (SszType.From(context.SemanticModel, foundTypes, (ITypeSymbol)context.SemanticModel.GetDeclaredSymbol(classDeclaration)!), foundTypes);
                }
            }
        }
        return null;
    }

    const string Whitespace = "/**/";
    static readonly Regex OpeningWhiteSpaceRegex = new("{/(\\n\\s+)+\\n/");
    static readonly Regex ClosingWhiteSpaceRegex = new("/(\\s+\\n)+    }/");
    public static string FixWhitespace(string data) => OpeningWhiteSpaceRegex.Replace(
                                                        ClosingWhiteSpaceRegex.Replace(
                                                            string.Join("\n", data.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Contains(Whitespace) ? "" : x)),
                                                            "    }"),
                                                        "{\n"
        );
    public static string Shift(int tabCount, string data) => string.Empty.PadLeft(4 * tabCount) + data;
    public static string Shift(int tabCount, IEnumerable<string> data, string? end = null) => string.Join("\n", data.Select(d => Shift(tabCount, d))) + (end is null || !data.Any() ? "" : end);

    private static string VarName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        string lowerCased = name.Substring(0, 1).ToLower() + name.Substring(1);

        return lowerCased == "data" || lowerCased == "container" || lowerCased.Contains("offset") ? $"_{lowerCased}" : lowerCased;
    }

    private static string DynamicLength(SszType container, SszProperty m)
    {
        if ((m.Kind & (Kind.Collection | Kind.BitList)) != Kind.None && m.Type.Kind == Kind.Basic)
        {
            return $"(container.{m.Name} is not null ? {(m.Kind == Kind.BitList ? $"container.{m.Name}.Length / 8 + 1" : $"{m.Type.StaticLength} * container.{m.Name}.Count()")} : 0)";
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
            string result = FixWhitespace(decl.IsSszListItself ?
$@"using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
{string.Join("\n", foundTypes.Select(x => x.Namespace).Distinct().OrderBy(x => x).Where(x => !string.IsNullOrEmpty(x)).Select(n => $"using {n};"))}
{Whitespace}
using SszLib = Nethermind.Serialization.Ssz.Ssz;
{Whitespace}
namespace Nethermind.Serialization;
{Whitespace}
public partial class SszEncoding
{{
    public static int GetLength({decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return 0;
        }")}
{Whitespace}
        return {DynamicLength(decl, variables[0])};
    }}
{Whitespace}
    public static int GetLength(ICollection<{decl.Name}>? container)
    {{
        if(container is null)
        {{
            return 0;
        }}
{Whitespace}
        int length = container.Count * {SszType.PointerLength};
        foreach({decl.Name} item in container)
        {{
            length += GetLength(item);
        }}
{Whitespace}
        return length;
    }}
{Whitespace}
    public static byte[] Encode({decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return [];
        }")}
        byte[] buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, {decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return;
        }")}
{Whitespace}
        if (container.{variables[0].Name} is not null) {(variables[0].HandledByStd ? "SszLib.Encode" : "Encode")}(data, container.{variables[0].Name});
    }}
{Whitespace}
    public static byte[] Encode(ICollection<{decl.Name}>? items)
    {{
        if (items is null)
        {{
            return [];
        }}
        byte[] buf = new byte[GetLength(items)];
        Encode(buf, items);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, ICollection<{decl.Name}>? items)
    {{
        if(items is null) return;
{Whitespace}
        int offset = items.Count * {(SszType.PointerLength)};
        int itemOffset = 0;
{Whitespace}
        foreach({decl.Name} item in items)
        {{
            SszLib.Encode(data.Slice(itemOffset, {(SszType.PointerLength)}), offset);
            itemOffset += {(SszType.PointerLength)};
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }}
    }}
{Whitespace}
    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name} container)
    {{
        container = new();
{Whitespace}
        if (data.Length > 0) {{ {(variables[0].HandledByStd ? "SszLib.Decode" : "Decode")}(data, out {(variables[0].HandledByStd ? $"ReadOnlySpan<{variables[0].Type.Name}>" : $"{variables[0].Type.Name}[]")} {VarName(variables[0].Name)}); container.{variables[0].Name} = [ ..{VarName(variables[0].Name)}]; }}
    }}
{Whitespace}
    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name}[] container)
    {{
        if(data.Length is 0)
        {{
            container = [];
            return;
        }}
{Whitespace}
        {(decl.IsVariable ? $@"SszLib.Decode(data.Slice(0, 4), out int firstOffset);
        int length = firstOffset / {SszType.PointerLength}" : $"int length = data.Length / {decl.StaticLength}")};
        container = new {decl.Name}[length];
{Whitespace}
        {(decl.IsVariable ? @$"int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
        {{
            SszLib.Decode(data.Slice(nextOffsetIndex, {SszType.PointerLength}), out int nextOffset);
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }}
{Whitespace}
        Decode(data.Slice(offset), out container[index]);" : @$"int offset = 0;
        for(int index = 0; index < length; index++)
        {{
            Decode(data.Slice(offset, {decl.StaticLength}), out container[index]);
            offset += {decl.StaticLength};
        }}")}
    }}
{Whitespace}
    public static void Merkleize({decl.Name}{(decl.IsStruct ? "" : "?")} container, out UInt256 root)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            root = 0;
            return;
        }")}
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent({decl.Members!.Length}));
        {(variables[0].HandledByStd ? $"merkleizer.Feed(container.{variables[0].Name}, {variables[0].Limit});" : $"MerkleizeList(container.{variables[0].Name}, {variables[0].Limit}, out UInt256 {VarName(variables[0].Name)}Root); merkleizer.Feed({VarName(variables[0].Name)}Root);")}
        merkleizer.CalculateRoot(out root);
    }}
{Whitespace}
    public static void MerkleizeVector(IList<{decl.Name}>? container, out UInt256 root)
    {{
        if(container is null)
        {{
            root = 0;
            return;
        }}
{Whitespace}
        UInt256[] subRoots = new UInt256[container.Count];
        for(int i = 0; i < container.Count; i++)
        {{
            Merkleize(container[i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.Merkleize(out root, subRoots);
    }}
{Whitespace}
    public static void MerkleizeList(IList<{decl.Name}>? container, ulong limit, out UInt256 root)
    {{
        if(container is null || container.Count is 0)
        {{
            root = 0;
            Merkle.MixIn(ref root, (int)limit);
            return;
        }}
{Whitespace}
        MerkleizeVector(container, out root);
        Merkle.MixIn(ref root, container.Count);
    }}
}}
" :
decl.Kind == Kind.Container ? $@"using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
{string.Join("\n", foundTypes.Select(x => x.Namespace).Distinct().OrderBy(x => x).Where(x => !string.IsNullOrEmpty(x)).Select(n => $"using {n};"))}
{Whitespace}
using SszLib = Nethermind.Serialization.Ssz.Ssz;
{Whitespace}
namespace Nethermind.Serialization;
{Whitespace}
public partial class SszEncoding
{{
    public static int GetLength({decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return 0;
        }")}
{Whitespace}
        return {decl.StaticLength}{(variables.Any() ? "" : ";")}
{Shift(4, variables.Select(m => $"+ {DynamicLength(decl, m)}"), ";")}
    }}
{Whitespace}
    public static int GetLength(ICollection<{decl.Name}>? container)
    {{
        if(container is null)
        {{
            return 0;
        }}
{Whitespace}
        {(decl.IsVariable ? @$"int length = container.Count * {SszType.PointerLength};
        foreach({decl.Name} item in container)
        {{
            length += GetLength(item);
        }}
{Whitespace}
        return length;" : $"return container.Count * {(decl.StaticLength)};")}
    }}
{Whitespace}
    public static byte[] Encode({decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return [];
        }")}
        byte[] buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, {decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return;
        }")}
{Whitespace}
{Shift(2, variables.Select((_, i) => $"int offset{i + 1} = {(i == 0 ? decl.StaticLength : $"offset{i} + {DynamicLength(decl, variables[i - 1])}")};"))}
{Whitespace}
{Shift(2, decl.Members.Select(m =>
{
    if (m.IsVariable) encodeOffsetIndex++;
    string result = m.IsVariable ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, 4), offset{encodeOffsetIndex});"
                                    : m.HandledByStd ? $"SszLib.Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name});"
                                                     : $"Encode(data.Slice({encodeStaticOffset}, {m.StaticLength}), container.{m.Name});";
    encodeStaticOffset += m.StaticLength;
    return result;
}))}
{Whitespace}
{Shift(2, variables.Select((m, i) => (m.Type.IsStruct ? "" : $"if (container.{m.Name} is not null) ") + $"{(m.HandledByStd ? "SszLib.Encode" : "Encode")}(data.Slice(offset{i + 1}, {(i + 1 == variables.Count ? "data.Length" : $"offset{i + 2}")} - offset{i + 1}), container.{m.Name}{(m.Kind == Kind.BitList ? $", {m.Limit}" : "")});"))}
    }}
{Whitespace}
    public static byte[] Encode(ICollection<{decl.Name}>? items)
    {{
        if (items is null)
        {{
            return [];
        }}
        byte[] buf = new byte[GetLength(items)];
        Encode(buf, items);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, ICollection<{decl.Name}>? items)
    {{
        if(items is null) return;
{Whitespace}
        {(decl.IsVariable ? @$"int offset = items.Count * {(SszType.PointerLength)};
        int itemOffset = 0;
{Whitespace}
        foreach({decl.Name} item in items)
        {{
            SszLib.Encode(data.Slice(itemOffset, {(SszType.PointerLength)}), offset);
            itemOffset += {(SszType.PointerLength)};
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }}" : @$"int offset = 0;
        foreach({decl.Name} item in items)
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
{Whitespace}
{Shift(2, decl.Members.Select(m =>
{
    if (m.IsVariable) offsetIndex++;
    string result = m.IsVariable ? $"SszLib.Decode(data.Slice({offset}, 4), out int offset{offsetIndex});"
                                    : m.HandledByStd ? $"SszLib.Decode(data.Slice({offset}, {m.StaticLength}), out {(m.IsCollection ? $"ReadOnlySpan<{m.Type.Name}>" : m.Type.Name)} {VarName(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{VarName(m.Name)}]" : VarName(m.Name))};"
                                                     : $"Decode(data.Slice({offset}, {m.StaticLength}), out {(m.IsCollection ? $"{m.Type.Name}[]" : m.Type.Name)} {VarName(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{VarName(m.Name)}]" : VarName(m.Name))};";
    offset += m.StaticLength;
    return result;
}))}
{Whitespace}
{Shift(2, variables.Select((m, i) => string.Format($"if ({(i + 1 == variables.Count ? "data.Length" : $"offset{i + 2}")} - offset{i + 1} > 0) {{{{ {{0}} }}}}",
            $"{(m.HandledByStd ? "SszLib.Decode" : "Decode")}(data.Slice(offset{i + 1}, {(i + 1 == variables.Count ? "data.Length" : $"offset{i + 2}")} - offset{i + 1}), out {(m.IsCollection ? (m.HandledByStd ? $"ReadOnlySpan<{m.Type.Name}>" : $"{m.Type.Name}[]") : m.Type.Name)} {VarName(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{VarName(m.Name)}]" : VarName(m.Name))};")))}
    }}
{Whitespace}
    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name}[] container)
    {{
        if(data.Length is 0)
        {{
            container = [];
            return;
        }}
{Whitespace}
        {(decl.IsVariable ? $@"SszLib.Decode(data.Slice(0, 4), out int firstOffset);
        int length = firstOffset / {SszType.PointerLength}" : $"int length = data.Length / {decl.StaticLength}")};
        container = new {decl.Name}[length];
{Whitespace}
        {(decl.IsVariable ? @$"int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
        {{
            SszLib.Decode(data.Slice(nextOffsetIndex, {SszType.PointerLength}), out int nextOffset);
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }}
{Whitespace}
        Decode(data.Slice(offset), out container[index]);" : @$"int offset = 0;
        for(int index = 0; index < length; index++)
        {{
            Decode(data.Slice(offset, {decl.StaticLength}), out container[index]);
            offset += {decl.StaticLength};
        }}")}
    }}
{Whitespace}
    public static void Merkleize({decl.Name}{(decl.IsStruct ? "" : "?")} container, out UInt256 root)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            root = 0;
            return;
        }")}
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent({decl.Members!.Length}));
{Shift(2, decl.Members.Select(m =>
            {
                if (m.IsVariable) offsetIndex++;
                string result = m.HandledByStd ? $"merkleizer.Feed(container.{m.Name}{(m.Kind == Kind.List || m.Kind == Kind.BitList ? $", {m.Limit}" : "")});"
                                                : m.Kind == Kind.List ? $"MerkleizeList(container.{m.Name}, {m.Limit}, out UInt256 {VarName(m.Name)}Root); merkleizer.Feed({VarName(m.Name)}Root);"
                                                                      : m.Kind == Kind.Vector ? $"MerkleizeVector(container.{m.Name}, out UInt256 {VarName(m.Name)}Root); merkleizer.Feed({VarName(m.Name)}Root);"
                                                                                              : $"Merkleize(container.{m.Name}, out UInt256 {VarName(m.Name)}Root); merkleizer.Feed({VarName(m.Name)}Root);";
                offset += m.StaticLength;
                return result;
            }))}
        merkleizer.CalculateRoot(out root);
    }}
{Whitespace}
    public static void MerkleizeVector(IList<{decl.Name}>? container, out UInt256 root)
    {{
        if(container is null)
        {{
            root = 0;
            return;
        }}
{Whitespace}
        UInt256[] subRoots = new UInt256[container.Count];
        for(int i = 0; i < container.Count; i++)
        {{
            Merkleize(container[i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.Merkleize(out root, subRoots);
    }}
{Whitespace}
    public static void MerkleizeList(IList<{decl.Name}>? container, ulong limit, out UInt256 root)
    {{
        if(container is null || container.Count is 0)
        {{
            root = 0;
            Merkle.MixIn(ref root, (int)limit);
            return;
        }}
{Whitespace}
        MerkleizeVector(container, out root);
        Merkle.MixIn(ref root, container.Count);
    }}
}}
" :
// Unions
$@"using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
{string.Join("\n", foundTypes.Select(x => x.Namespace).Distinct().OrderBy(x => x).Where(x => !string.IsNullOrEmpty(x)).Select(n => $"using {n};"))}
{Whitespace}
using SszLib = Nethermind.Serialization.Ssz.Ssz;
{Whitespace}
namespace Nethermind.Serialization;
{Whitespace}
public partial class SszEncoding
{{
    public static int GetLength({decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return 0;
        }")}
        return 1 + container.Selector switch {{
{Shift(3, decl.UnionMembers.Select(m => $"{decl.Selector!.Type.Name}.{m.Name} => {(m.IsVariable ? DynamicLength(decl, m) : m.StaticLength.ToString())},"))}
            _ => 0,
        }};
    }}
{Whitespace}
    public static int GetLength(ICollection<{decl.Name}>? container)
    {{
        if(container is null)
        {{
            return 0;
        }}
{Whitespace}
        int length = container.Count * {SszType.PointerLength};
        foreach({decl.Name} item in container)
        {{
            length += GetLength(item);
        }}
{Whitespace}
        return length;
    }}
{Whitespace}
    public static byte[] Encode({decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return [];
        }")}
        byte[] buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, {decl.Name}{(decl.IsStruct ? "" : "?")} container)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            return;
        }")}
        SszLib.Encode(data.Slice(0, 1), (byte)container.Selector);
        if(data.Length is 1)
        {{
            return;
        }}
{Whitespace}
        switch(container.Selector) {{
{Shift(3, decl.UnionMembers.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {(m.IsVariable ? $"{(m.HandledByStd ? "SszLib.Encode" : "Encode")}(data.Slice(1), container.{m.Name}{(m.Kind == Kind.BitList ? $", {m.Limit}" : "")});"
                                                : m.HandledByStd ? $"SszLib.Encode(data.Slice(1), container.{m.Name});"
                                                                 : $"Encode(data.Slice(1), container.{m.Name});")} break;"))}
        }};
    }}
{Whitespace}
    public static byte[] Encode(ICollection<{decl.Name}>? items)
    {{
        if (items is null)
        {{
            return [];
        }}
        byte[] buf = new byte[GetLength(items)];
        Encode(buf, items);
        return buf;
    }}
{Whitespace}
    public static void Encode(Span<byte> data, ICollection<{decl.Name}>? items)
    {{
        if(items is null) return;
        int offset = items.Count * {(SszType.PointerLength)};
        int itemOffset = 0;
        foreach({decl.Name} item in items)
        {{
            SszLib.Encode(data.Slice(itemOffset, {(SszType.PointerLength)}), offset);
            itemOffset += {(SszType.PointerLength)};
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }}
    }}
{Whitespace}
    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name}[] container)
    {{
        if(data.Length is 0)
        {{
            container = [];
            return;
        }}
{Whitespace}
        SszLib.Decode(data.Slice(0, 4), out int firstOffset);
        int length = firstOffset / {SszType.PointerLength};
        container = new {decl.Name}[length];
{Whitespace}
        int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
        {{
            SszLib.Decode(data.Slice(nextOffsetIndex, {SszType.PointerLength}), out int nextOffset);
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }}
{Whitespace}
        Decode(data.Slice(offset), out container[index]);
    }}
{Whitespace}
    public static void Decode(ReadOnlySpan<byte> data, out {decl.Name} container)
    {{
        container = new();
        container.Selector = ({decl.Selector!.Type.Name})data[0];
        switch(container.Selector) {{
{Shift(3, decl.UnionMembers.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {(m.IsVariable ? $"{(m.HandledByStd ? "SszLib.Decode" : "Decode")}(data.Slice(1), out {(m.IsCollection ? (m.HandledByStd ? $"ReadOnlySpan<{m.Type.Name}>" : $"{m.Type.Name}[]") : m.Type.Name)} {VarName(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{VarName(m.Name)}]" : VarName(m.Name))};"
                                    : m.HandledByStd ? $"SszLib.Decode(data.Slice(1), out {(m.IsCollection ? $"ReadOnlySpan<{m.Type.Name}>" : m.Type.Name)} {VarName(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{VarName(m.Name)}]" : VarName(m.Name))};"
                                                     : $"Decode(data.Slice(1), out {(m.IsCollection ? $"{m.Type.Name}[]" : m.Type.Name)} {VarName(m.Name)}); container.{m.Name} = {(m.IsCollection ? $"[ ..{VarName(m.Name)}]" : VarName(m.Name))};")} break;"))}
        }};
    }}
{Whitespace}
    public static void Merkleize({decl.Name}{(decl.IsStruct ? "" : "?")} container, out UInt256 root)
    {{
        {(decl.IsStruct ? "" : @"if(container is null)
        {
            root = 0;
            return;
        }")}
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent({decl.Members!.Length}));
        switch(container.Selector) {{
{Shift(3, decl.UnionMembers.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {(m.HandledByStd ? $"merkleizer.Feed(container.{m.Name}{(m.Kind == Kind.List || m.Kind == Kind.BitList ? $", {m.Limit}" : "")});"
                                    : m.Kind == Kind.List ? $"MerkleizeList(container.{m.Name}, {m.Limit}, out UInt256 {VarName(m.Name)}Root); merkleizer.Feed({VarName(m.Name)}Root);"
                                                          : m.Kind == Kind.Vector ? $"MerkleizeVector(container.{m.Name}, out UInt256 {VarName(m.Name)}Root); merkleizer.Feed({VarName(m.Name)}Root);"
                                                                                  : $"Merkleize(container.{m.Name}, out UInt256 {VarName(m.Name)}Root); merkleizer.Feed({VarName(m.Name)}Root);")} break;"))}
        }};
{Whitespace}
        merkleizer.CalculateRoot(out root);
        Merkle.MixIn(ref root, (byte)container.Selector);
    }}
{Whitespace}
    public static void MerkleizeVector(IList<{decl.Name}>? container, out UInt256 root)
    {{
        if(container is null)
        {{
            root = 0;
            return;
        }}
{Whitespace}
        UInt256[] subRoots = new UInt256[container.Count];
        for(int i = 0; i < container.Count; i++)
        {{
            Merkleize(container[i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.Merkleize(out root, subRoots);
    }}
{Whitespace}
    public static void MerkleizeList(IList<{decl.Name}>? container, ulong limit, out UInt256 root)
    {{
        if(container is null || container.Count is 0)
        {{
            root = 0;
            Merkle.MixIn(ref root, (int)limit);
            return;
        }}
{Whitespace}
        MerkleizeVector(container, out root);
        Merkle.MixIn(ref root, (int)limit);
    }}
}}
");
#if DEBUG
#pragma warning disable RS1035 // Allow console for debugging
            Console.WriteLine(WithLineNumbers(result, false));
#pragma warning restore RS1035
#endif
            return result;
        }
        catch (Exception e)
        {
            return $"/* Failed due to error: {e.Message}*/";
        }
    }

#if DEBUG
    static string WithLineNumbers(string input, bool bypass = false)
    {
        if (bypass) return input;

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
#endif
}
