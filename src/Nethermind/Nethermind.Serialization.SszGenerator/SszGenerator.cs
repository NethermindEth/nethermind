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
        context.RegisterPostInitializationOutput(static spc =>
            spc.AddSource("Serialization.SszEncoding.Helpers.cs", SourceText.From(GenerateHelpersCode(), Encoding.UTF8)));

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
            string generatedCode = GenerateClassCode(decl.Value.Item1, decl.Value.Item2);
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
                if (methodSymbol is not null && IsSszRootAttribute(methodSymbol.ContainingType))
                {
                    List<SszType> foundTypes = new(SszType.BasicTypes);
                    return (SszType.From(context.SemanticModel, foundTypes, (ITypeSymbol)context.SemanticModel.GetDeclaredSymbol(classDeclaration)!), foundTypes);
                }
            }
        }
        return null;
    }

    private static bool IsSszRootAttribute(INamedTypeSymbol attributeType) =>
        attributeType.ToString() is
            "Nethermind.Serialization.Ssz.SszContainerAttribute"
            or "Nethermind.Serialization.Ssz.SszCompatibleUnionAttribute";

    const string Whitespace = "/**/";
    private const int UnboundedBitlistLimit = 0;
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

    private static string GenerateHelpersCode() =>
"""
using Nethermind.Int256;
using Nethermind.Merkleization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization;

public partial class SszEncoding
{
    private const int SszOffsetSize = 4;

    private static T ThrowInvalidSszData<T>(string typeName, string message) =>
        throw new InvalidDataException($"Invalid SSZ data for {typeName}: {message}");

    private static void ThrowInvalidSszData(string typeName, string message) =>
        throw new InvalidDataException($"Invalid SSZ data for {typeName}: {message}");

    private static void ThrowInvalidSszValue(string typeName, string fieldName, string message) =>
        throw new InvalidDataException($"Invalid SSZ value for {typeName}.{fieldName}: {message}");

    private static void ValidateSszExactLength(int actualLength, int expectedLength, string typeName)
    {
        if (actualLength != expectedLength)
        {
            ThrowInvalidSszData(typeName, $"expected {expectedLength} bytes but found {actualLength}.");
        }
    }

    private static void ValidateSszMinimumLength(int actualLength, int minimumLength, string typeName)
    {
        if (actualLength < minimumLength)
        {
            ThrowInvalidSszData(typeName, $"expected at least {minimumLength} bytes but found {actualLength}.");
        }
    }

    private static void ValidateSszMultipleOf(int actualLength, int itemLength, string typeName)
    {
        if (actualLength % itemLength != 0)
        {
            ThrowInvalidSszData(typeName, $"expected a multiple of {itemLength} bytes but found {actualLength}.");
        }
    }

    private static void ValidateSszDynamicOffsets(ReadOnlySpan<byte> data, int fixedLength, string typeName, ReadOnlySpan<int> offsets)
    {
        ValidateSszMinimumLength(data.Length, fixedLength, typeName);

        if (offsets.Length is 0)
        {
            return;
        }

        if (offsets[0] != fixedLength)
        {
            ThrowInvalidSszData(typeName, $"expected the first offset to be {fixedLength} but found {offsets[0]}.");
        }

        int previous = fixedLength;
        for (int index = 0; index < offsets.Length; index++)
        {
            int current = offsets[index];
            if (current < previous)
            {
                ThrowInvalidSszData(typeName, $"offset #{index} is out of order ({current} < {previous}).");
            }

            if (current > data.Length)
            {
                ThrowInvalidSszData(typeName, $"offset #{index} ({current}) exceeds the input length {data.Length}.");
            }

            previous = current;
        }
    }

    private static int ValidateSszVariableItemCount(ReadOnlySpan<byte> data, int firstOffset, string typeName)
    {
        ValidateSszMinimumLength(data.Length, SszOffsetSize, typeName);

        if (firstOffset < SszOffsetSize)
        {
            ThrowInvalidSszData(typeName, $"expected the first offset to be at least {SszOffsetSize} but found {firstOffset}.");
        }

        if (firstOffset > data.Length)
        {
            ThrowInvalidSszData(typeName, $"the first offset {firstOffset} exceeds the input length {data.Length}.");
        }

        if (firstOffset % SszOffsetSize != 0)
        {
            ThrowInvalidSszData(typeName, $"the first offset {firstOffset} is not aligned to {SszOffsetSize}-byte offsets.");
        }

        return firstOffset / SszOffsetSize;
    }

    private static void ValidateSszNextOffset(ReadOnlySpan<byte> data, int currentOffset, int nextOffset, string typeName)
    {
        if (nextOffset < currentOffset)
        {
            ThrowInvalidSszData(typeName, $"offsets are out of order ({nextOffset} < {currentOffset}).");
        }

        if (nextOffset > data.Length)
        {
            ThrowInvalidSszData(typeName, $"offset {nextOffset} exceeds the input length {data.Length}.");
        }
    }

    private static void ValidateSszVectorLength<T>(ICollection<T>? items, int expectedLength, string typeName, string fieldName)
    {
        int actualLength = items?.Count ?? 0;
        if (items is null || actualLength != expectedLength)
        {
            ThrowInvalidSszValue(typeName, fieldName, $"expected {expectedLength} elements but found {actualLength}.");
        }
    }

    private static void ValidateSszListLimit<T>(ICollection<T>? items, int limit, string typeName, string fieldName)
    {
        if (items is not null && items.Count > limit)
        {
            ThrowInvalidSszValue(typeName, fieldName, $"expected at most {limit} elements but found {items.Count}.");
        }
    }

    private static void ValidateSszBitvectorLength(BitArray? bits, int expectedLength, string typeName, string fieldName)
    {
        int actualLength = bits?.Length ?? 0;
        if (bits is null || actualLength != expectedLength)
        {
            ThrowInvalidSszValue(typeName, fieldName, $"expected {expectedLength} bits but found {actualLength}.");
        }
    }

    private static void ValidateSszBitlistLimit(BitArray? bits, int limit, string typeName, string fieldName)
    {
        if (bits is not null && bits.Length > limit)
        {
            ThrowInvalidSszValue(typeName, fieldName, $"expected at most {limit} bits but found {bits.Length}.");
        }
    }

    private static void DecodeSszBitVector(ReadOnlySpan<byte> data, int vectorLength, out BitArray value)
    {
        ValidateSszExactLength(data.Length, (vectorLength + 7) / 8, $"bitvector[{vectorLength}]");
        Nethermind.Serialization.Ssz.Ssz.Decode(data, vectorLength, out value);
    }

    private static void DecodeSszBitList(ReadOnlySpan<byte> data, string typeName, out BitArray value)
    {
        ValidateSszMinimumLength(data.Length, 1, typeName);
        Nethermind.Serialization.Ssz.Ssz.Decode(data, out value);
    }

    private static void MerkleizeBasicVector<T>(IEnumerable<T>? value, int itemSize, ulong length, out UInt256 root)
        where T : struct
    {
        T[] values = value as T[] ?? (value is null ? [] : [.. value]);
        ulong chunkCount = (length * (ulong)itemSize + 31UL) / 32UL;
        Merkle.Merkleize(out root, MemoryMarshal.AsBytes(values.AsSpan()), chunkCount);
    }

    private static void MerkleizeBasicList<T>(IEnumerable<T>? value, int itemSize, ulong limit, out UInt256 root)
        where T : struct
    {
        T[] values = value as T[] ?? (value is null ? [] : [.. value]);
        ulong chunkCount = (limit * (ulong)itemSize + 31UL) / 32UL;
        Merkle.Merkleize(out root, MemoryMarshal.AsBytes(values.AsSpan()), chunkCount);
        Merkle.MixIn(ref root, values.Length);
    }

    private static void MerkleizeProgressiveBytes(ReadOnlySpan<byte> value, out UInt256 root)
    {
        if (value.Length is 0)
        {
            root = UInt256.Zero;
            return;
        }

        int chunkCount = (value.Length + 31) / 32;
        UInt256[] chunks = new UInt256[chunkCount];
        int fullByteLength = value.Length / 32 * 32;
        if (fullByteLength > 0)
        {
            MemoryMarshal.Cast<byte, UInt256>(value[..fullByteLength]).CopyTo(chunks);
        }

        if (fullByteLength != value.Length)
        {
            Span<byte> lastChunk = stackalloc byte[32];
            value[fullByteLength..].CopyTo(lastChunk);
            chunks[^1] = new UInt256(lastChunk);
        }

        Merkle.MerkleizeProgressive(out root, chunks);
    }

    private static void MerkleizeProgressiveBasicList<T>(IEnumerable<T>? value, out UInt256 root)
        where T : struct
    {
        T[] values = value as T[] ?? (value is null ? [] : [.. value]);
        MerkleizeProgressiveBytes(MemoryMarshal.AsBytes(values.AsSpan()), out root);
        Merkle.MixIn(ref root, values.Length);
    }

    private static void MerkleizeProgressiveBitList(BitArray? value, out UInt256 root)
    {
        BitArray bits = value ?? new BitArray(0);
        int byteLength = (bits.Length + 7) / 8;
        byte[] bytes = new byte[byteLength];
        bits.CopyTo(bytes, 0);
        MerkleizeProgressiveBytes(bytes, out root);
        Merkle.MixIn(ref root, bits.Length);
    }
}
""";

    private static string VarName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        string lowerCased = name.Substring(0, 1).ToLower() + name.Substring(1);

        return lowerCased == "data" || lowerCased == "container" || lowerCased.Contains("offset") ? $"_{lowerCased}" : lowerCased;
    }

    private static string ValidationStatement(SszType decl, SszProperty property, string expression)
    {
        return property.Kind switch
        {
            Kind.Vector when property.Type.Name == "BitArray" => $"ValidateSszBitvectorLength({expression}, {property.Length}, nameof({decl.Name}), nameof({decl.Name}.{property.Name}));",
            Kind.Vector => $"ValidateSszVectorLength({expression}, {property.Length}, nameof({decl.Name}), nameof({decl.Name}.{property.Name}));",
            Kind.List when property.Type.Name == "BitArray" => $"ValidateSszBitlistLimit({expression}, {property.Limit}, nameof({decl.Name}), nameof({decl.Name}.{property.Name}));",
            Kind.List => $"ValidateSszListLimit({expression}, {property.Limit}, nameof({decl.Name}), nameof({decl.Name}.{property.Name}));",
            Kind.BitVector => $"ValidateSszBitvectorLength({expression}, {property.Length}, nameof({decl.Name}), nameof({decl.Name}.{property.Name}));",
            Kind.BitList => $"ValidateSszBitlistLimit({expression}, {property.Limit}, nameof({decl.Name}), nameof({decl.Name}.{property.Name}));",
            _ => string.Empty,
        };
    }

    private static string EncodeValueExpression(SszProperty property, string expression) =>
        property.Kind is Kind.BitList or Kind.ProgressiveBitList ? $"{expression} ?? new BitArray(0)" : expression;

    private static IEnumerable<string> ValidationStatements(SszType decl, IEnumerable<SszProperty> properties, string expressionPrefix)
    {
        foreach (SszProperty property in properties)
        {
            string validation = ValidationStatement(decl, property, $"{expressionPrefix}.{property.Name}");
            if (!string.IsNullOrEmpty(validation))
            {
                yield return validation;
            }
        }
    }

    private static string DecodeAndAssign(SszType decl, SszProperty property, string sliceExpression)
    {
        string variableName = VarName(property.Name);
        string outType = property.Kind switch
        {
            Kind.BitVector or Kind.BitList or Kind.ProgressiveBitList => "BitArray",
            _ when property.IsCollection => property.HandledByStd ? $"ReadOnlySpan<{property.Type.Name}>" : $"{property.Type.Name}[]",
            _ => property.Type.Name,
        };

        string decode = property.Kind switch
        {
            Kind.BitVector => $"DecodeSszBitVector({sliceExpression}, {property.Length}, out {outType} {variableName});",
            Kind.BitList or Kind.ProgressiveBitList => $"DecodeSszBitList({sliceExpression}, nameof({decl.Name}), out {outType} {variableName});",
            _ when property.HandledByStd => $"SszLib.Decode({sliceExpression}, out {outType} {variableName});",
            _ => $"Decode({sliceExpression}, out {outType} {variableName});",
        };

        string assignment = property.IsCollection ? $"container.{property.Name} = [ ..{variableName}];" : $"container.{property.Name} = {variableName};";
        string validation = ValidationStatement(decl, property, $"container.{property.Name}");
        return string.IsNullOrEmpty(validation) ? $"{decode} {assignment}" : $"{decode} {assignment} {validation}";
    }

    private static string MerkleizeRootStatement(SszProperty property, string expression, string rootName)
    {
        return property.Kind switch
        {
            Kind.Basic => $"Merkle.Merkleize(out {rootName}, {expression});",
            Kind.BitVector => $"Merkle.Merkleize(out {rootName}, {expression}!);",
            Kind.BitList => $"Merkle.Merkleize(out {rootName}, {expression} ?? new BitArray(0), {property.Limit});",
            Kind.ProgressiveBitList => $"MerkleizeProgressiveBitList({expression}, out {rootName});",
            Kind.Vector when property.Type.Kind == Kind.Basic => $"MerkleizeBasicVector({expression}, {property.Type.StaticLength}, {property.Length}, out {rootName});",
            Kind.List when property.Type.Kind == Kind.Basic => $"MerkleizeBasicList({expression}, {property.Type.StaticLength}, {property.Limit}, out {rootName});",
            Kind.ProgressiveList when property.Type.Kind == Kind.Basic => $"MerkleizeProgressiveBasicList({expression}, out {rootName});",
            Kind.Vector => $"MerkleizeVector({expression}, out {rootName});",
            Kind.List => $"MerkleizeList({expression}, {property.Limit}, out {rootName});",
            Kind.ProgressiveList => $"MerkleizeProgressiveList({expression}, out {rootName});",
            _ => $"Merkleize({expression}, out {rootName});",
        };
    }

    private static string MerkleizeFeedStatement(SszProperty property, string expression)
    {
        return property.Kind switch
        {
            Kind.Basic => $"merkleizer.Feed({expression});",
            Kind.BitVector => $"merkleizer.Feed({expression});",
            Kind.BitList => $"merkleizer.Feed({expression} ?? new BitArray(0), {property.Limit});",
            Kind.ProgressiveBitList => $"{MerkleizeRootStatement(property, expression, $"UInt256 {VarName(property.Name)}Root")} merkleizer.Feed({VarName(property.Name)}Root);",
            _ => $"{MerkleizeRootStatement(property, expression, $"UInt256 {VarName(property.Name)}Root")} merkleizer.Feed({VarName(property.Name)}Root);",
        };
    }

    private static string DynamicLength(SszType container, SszProperty m)
    {
        if ((m.Kind & (Kind.Collection | Kind.BitList | Kind.ProgressiveBitList)) != Kind.None && m.Type.Kind == Kind.Basic)
        {
            return m.Kind is Kind.BitList or Kind.ProgressiveBitList
                ? $"(container.{m.Name} is not null ? container.{m.Name}.Length / 8 + 1 : 1)"
                : $"(container.{m.Name} is not null ? {m.Type.StaticLength} * ((ICollection<{m.Type.Name}>)container.{m.Name}).Count : 0)";
        }

        return $"GetLength(container.{m.Name})";
    }

    private static string ByteArrayLiteral(byte[] value) => $"[{string.Join(", ", value)}]";

    private static string ProgressiveContainerMerkleizeBody(SszType decl)
    {
        string activeFields = ByteArrayLiteral(decl.ActiveFieldsBytes ?? []);
        IEnumerable<string> memberRoots = decl.Members!.Select((m, index) =>
        {
            string validation = ValidationStatement(decl, m, $"container.{m.Name}");
            string merkleize = MerkleizeRootStatement(m, $"container.{m.Name}", $"subRoots[{index}]");
            return string.IsNullOrEmpty(validation) ? merkleize : $"{validation} {merkleize}";
        });

        return $@"UInt256[] subRoots = new UInt256[{decl.Members!.Length}];
{Shift(2, memberRoots)}
        Merkle.MerkleizeProgressive(out root, subRoots);
        Merkle.MixInActiveFields(ref root, {activeFields});";
    }

    private static string EncodeStatement(string target, SszProperty property, string expression)
    {
        string arguments = $"{target}, {EncodeValueExpression(property, expression)}";
        if (property.Kind == Kind.BitList)
        {
            arguments += $", {property.Limit}";
        }
        else if (property.Kind == Kind.ProgressiveBitList)
        {
            arguments += $", {UnboundedBitlistLimit}"; // Progressive bitlists are intentionally unbounded.
        }

        return $"{(property.HandledByStd ? "SszLib.Encode" : "Encode")}({arguments});";
    }

    private static bool RequiresNullGuard(SszProperty property) => !(property.Type.IsStruct || property.Kind is Kind.BitList or Kind.ProgressiveBitList);

    private static string GenerateClassCode(SszType decl, List<SszType> foundTypes)
    {
        try
        {
            List<SszProperty> variables = decl.Members.Where(m => m.IsVariable).ToList();
            int encodeOffsetIndex = 0, encodeStaticOffset = 0;
            int offsetIndex = 0, offset = 0;
            string containerMerkleizeBody = decl.Kind == Kind.ProgressiveContainer
                ? ProgressiveContainerMerkleizeBody(decl)
                : $@"Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent({decl.Members!.Length}));
{Shift(2, decl.Members.Select(m => MerkleizeFeedStatement(m, $"container.{m.Name}")))}
        merkleizer.CalculateRoot(out root);";
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
        {ValidationStatement(decl, variables[0], $"container.{variables[0].Name}")}
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
        {ValidationStatement(decl, variables[0], $"container.{variables[0].Name}")}
        {(RequiresNullGuard(variables[0]) ? $"if (container.{variables[0].Name} is not null) " : string.Empty)}{EncodeStatement("data", variables[0], $"container.{variables[0].Name}")}
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
        {DecodeAndAssign(decl, variables[0], "data")}
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
        {(decl.IsVariable ? $@"ValidateSszMinimumLength(data.Length, {SszType.PointerLength}, ""{decl.Name}[]"");
        SszLib.Decode(data.Slice(0, 4), out int firstOffset);
        int length = ValidateSszVariableItemCount(data, firstOffset, ""{decl.Name}[]"")" : $@"ValidateSszMultipleOf(data.Length, {decl.StaticLength}, ""{decl.Name}[]"");
        int length = data.Length / {decl.StaticLength}")};
        container = new {decl.Name}[length];
{Whitespace}
        {(decl.IsVariable ? @$"int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
        {{
            SszLib.Decode(data.Slice(nextOffsetIndex, {SszType.PointerLength}), out int nextOffset);
            ValidateSszNextOffset(data, offset, nextOffset, ""{decl.Name}[]"");
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
        {ValidationStatement(decl, variables[0], $"container.{variables[0].Name}")}
        {MerkleizeRootStatement(variables[0], $"container.{variables[0].Name}", "root")}
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
        int count = container?.Count ?? 0;
        UInt256[] subRoots = new UInt256[count];
        for(int i = 0; i < count; i++)
        {{
            Merkleize(container![i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.Merkleize(out root, subRoots, limit);
        Merkle.MixIn(ref root, count);
    }}
{Whitespace}
    public static void MerkleizeProgressiveList(IList<{decl.Name}>? container, out UInt256 root)
    {{
        int count = container?.Count ?? 0;
        UInt256[] subRoots = new UInt256[count];
        for(int i = 0; i < count; i++)
        {{
            Merkleize(container![i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.MerkleizeProgressive(out root, subRoots);
        Merkle.MixIn(ref root, count);
    }}
}}
" :
(decl.Kind == Kind.Container || decl.Kind == Kind.ProgressiveContainer) ? $@"using Nethermind.Merkleization;
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
{Shift(2, ValidationStatements(decl, decl.Members!, "container"))}
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
{Shift(2, ValidationStatements(decl, decl.Members!, "container"))}
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
{Shift(2, variables.Select((m, i) => (RequiresNullGuard(m) ? $"if (container.{m.Name} is not null) " : "") + EncodeStatement($"data.Slice(offset{i + 1}, {(i + 1 == variables.Count ? "data.Length" : $"offset{i + 2}")} - offset{i + 1})", m, $"container.{m.Name}")))}
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
        {(variables.Any() ? $"if (data.Length < {decl.StaticLength}) throw new System.IO.InvalidDataException($\"Data too short for {decl.Name}: expected at least {decl.StaticLength} bytes but got {{data.Length}} bytes.\");" : $"if (data.Length != {decl.StaticLength}) throw new System.IO.InvalidDataException($\"Invalid data length for {decl.Name}: expected {decl.StaticLength} bytes but got {{data.Length}} bytes.\");")}
        container = new();
{Whitespace}
        {(decl.IsVariable ? $"ValidateSszMinimumLength(data.Length, {decl.StaticLength}, nameof({decl.Name}));" : $"ValidateSszExactLength(data.Length, {decl.StaticLength}, nameof({decl.Name}));")}
{Whitespace}
{Shift(2, decl.Members.Select(m =>
{
    if (m.IsVariable) offsetIndex++;
    string result = m.IsVariable ? $"SszLib.Decode(data.Slice({offset}, 4), out int offset{offsetIndex});"
                                    : DecodeAndAssign(decl, m, $"data.Slice({offset}, {m.StaticLength})");
    offset += m.StaticLength;
    return result;
}))}
{Whitespace}
        {(variables.Any() ? $"ValidateSszDynamicOffsets(data, {decl.StaticLength}, nameof({decl.Name}), [{string.Join(", ", Enumerable.Range(1, variables.Count).Select(i => $"offset{i}"))}]);" : string.Empty)}
{Whitespace}
{Shift(2, variables.Select((m, i) => DecodeAndAssign(decl, m, $"data.Slice(offset{i + 1}, {(i + 1 == variables.Count ? "data.Length" : $"offset{i + 2}")} - offset{i + 1})")))}
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
        {(decl.IsVariable ? $@"ValidateSszMinimumLength(data.Length, {SszType.PointerLength}, ""{decl.Name}[]"");
        SszLib.Decode(data.Slice(0, 4), out int firstOffset);
        int length = ValidateSszVariableItemCount(data, firstOffset, ""{decl.Name}[]"")" : $@"ValidateSszMultipleOf(data.Length, {decl.StaticLength}, ""{decl.Name}[]"");
        int length = data.Length / {decl.StaticLength}")};
        container = new {decl.Name}[length];
{Whitespace}
        {(decl.IsVariable ? @$"int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
        {{
            SszLib.Decode(data.Slice(nextOffsetIndex, {SszType.PointerLength}), out int nextOffset);
            ValidateSszNextOffset(data, offset, nextOffset, ""{decl.Name}[]"");
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
{Shift(2, ValidationStatements(decl, decl.Members!, "container"))}
{Whitespace}
{Shift(2, containerMerkleizeBody.Split('\n'))}
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
        int count = container?.Count ?? 0;
        UInt256[] subRoots = new UInt256[count];
        for(int i = 0; i < count; i++)
        {{
            Merkleize(container![i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.Merkleize(out root, subRoots, limit);
        Merkle.MixIn(ref root, count);
    }}
{Whitespace}
    public static void MerkleizeProgressiveList(IList<{decl.Name}>? container, out UInt256 root)
    {{
        int count = container?.Count ?? 0;
        UInt256[] subRoots = new UInt256[count];
        for(int i = 0; i < count; i++)
        {{
            Merkleize(container![i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.MerkleizeProgressive(out root, subRoots);
        Merkle.MixIn(ref root, count);
    }}
}}
" :
// Compatible unions
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
        switch(container.Selector)
        {{
{Shift(3, decl.CompatibleUnionMembers!.Select(m =>
{
    string validation = ValidationStatement(decl, m, $"container.{m.Name}");
    string length = m.IsVariable ? DynamicLength(decl, m) : m.StaticLength.ToString();
    return string.IsNullOrEmpty(validation)
        ? $"case {decl.Selector!.Type.Name}.{m.Name}: return 1 + {length};"
        : $"case {decl.Selector!.Type.Name}.{m.Name}: {validation} return 1 + {length};";
}))}
            default:
                return ThrowInvalidSszData<int>(nameof({decl.Name}), $""unsupported union selector {{(byte)container.Selector}}."");
        }}
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
        ValidateSszExactLength(data.Length, GetLength(container), nameof({decl.Name}));
        SszLib.Encode(data.Slice(0, 1), (byte)container.Selector);
{Whitespace}
        switch(container.Selector) {{
{Shift(3, decl.CompatibleUnionMembers!.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {EncodeStatement("data.Slice(1)", m, $"container.{m.Name}")} break;"))}
            default:
                ThrowInvalidSszData(nameof({decl.Name}), $""unsupported union selector {{(byte)container.Selector}}."");
                break;
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
        ValidateSszMinimumLength(data.Length, {SszType.PointerLength}, ""{decl.Name}[]"");
        SszLib.Decode(data.Slice(0, 4), out int firstOffset);
        int length = ValidateSszVariableItemCount(data, firstOffset, ""{decl.Name}[]"");
        container = new {decl.Name}[length];
{Whitespace}
        int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = {SszType.PointerLength}; index < length - 1; index++, nextOffsetIndex += {SszType.PointerLength})
        {{
            SszLib.Decode(data.Slice(nextOffsetIndex, {SszType.PointerLength}), out int nextOffset);
            ValidateSszNextOffset(data, offset, nextOffset, ""{decl.Name}[]"");
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
        ValidateSszMinimumLength(data.Length, 1, nameof({decl.Name}));
        container.Selector = ({decl.Selector!.Type.Name})data[0];
        switch(container.Selector) {{
{Shift(3, decl.CompatibleUnionMembers!.Select(m => $"case {decl.Selector!.Type.Name}.{m.Name}: {DecodeAndAssign(decl, m, "data.Slice(1)")} break;"))}
            default:
                ThrowInvalidSszData(nameof({decl.Name}), $""unsupported union selector {{data[0]}}."");
                break;
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
        switch(container.Selector) {{
{Shift(3, decl.CompatibleUnionMembers!.Select(m =>
{
    string validation = ValidationStatement(decl, m, $"container.{m.Name}");
    string merkleize = MerkleizeRootStatement(m, $"container.{m.Name}", "root");
    return string.IsNullOrEmpty(validation)
        ? $"case {decl.Selector!.Type.Name}.{m.Name}: {merkleize} break;"
        : $"case {decl.Selector!.Type.Name}.{m.Name}: {validation} {merkleize} break;";
}))}
            default:
                root = ThrowInvalidSszData<UInt256>(nameof({decl.Name}), $""unsupported union selector {{(byte)container.Selector}}."");
                break;
        }};
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
        int count = container?.Count ?? 0;
        UInt256[] subRoots = new UInt256[count];
        for(int i = 0; i < count; i++)
        {{
            Merkleize(container![i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.Merkleize(out root, subRoots, limit);
        Merkle.MixIn(ref root, count);
    }}
{Whitespace}
    public static void MerkleizeProgressiveList(IList<{decl.Name}>? container, out UInt256 root)
    {{
        int count = container?.Count ?? 0;
        UInt256[] subRoots = new UInt256[count];
        for(int i = 0; i < count; i++)
        {{
            Merkleize(container![i], out subRoots[i]);
        }}
{Whitespace}
        Merkle.MerkleizeProgressive(out root, subRoots);
        Merkle.MixIn(ref root, count);
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
