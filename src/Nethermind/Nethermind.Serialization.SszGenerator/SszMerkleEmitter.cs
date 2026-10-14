// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using static SszCSharp;

internal static class SszMerkleEmitter
{
    internal static int Depth(ulong count)
    {
        int depth = 0;
        for (ulong remaining = count == 0 ? 0 : count - 1; remaining > 0; remaining >>= 1) depth++;
        return depth;
    }

    internal static string Merkleize(SszCodecDefinition definition)
    {
        if (definition.WrappedMember is { } wrapped)
            return Join(SszEncodeEmitter.Validate(definition, wrapped), Value(wrapped, Member(wrapped), "root"));
        if (definition.Schema is UnionSchema)
            return Join(UnionSwitch(definition, member => Join(SszEncodeEmitter.Validate(definition, member), Value(member, Member(member), "root"), "break;"),
                $"root = ThrowInvalidData<global::Nethermind.Int256.UInt256>(nameof({definition.Declaration.TypeName}), \"unsupported union selector.\"); break;"),
                $"global::Nethermind.Serialization.Ssz.Merkleization.Merkle.MixIn(ref root, (byte){Member(definition.Selector!)});");

        ContainerSchema schema = (ContainerSchema)definition.Schema;
        if (schema.Kind == ContainerKind.ProgressiveContainer)
        {
            bool useStack = schema.ActiveFieldCount <= 32;
            string roots = string.Join("\n", definition.Members.Select(member => Value(member, Member(member), $"__roots[{member.FieldIndex}]")));
            return Join(SszEncodeEmitter.Validate(definition),
                useStack ? $"global::System.Span<global::Nethermind.Int256.UInt256> __roots = stackalloc global::Nethermind.Int256.UInt256[{schema.ActiveFieldCount}];"
                    : $"global::Nethermind.Int256.UInt256[] __rented = global::System.Buffers.ArrayPool<global::Nethermind.Int256.UInt256>.Shared.Rent({schema.ActiveFieldCount});\nglobal::System.Span<global::Nethermind.Int256.UInt256> __roots = __rented.AsSpan(0, {schema.ActiveFieldCount});\ntry\n{{",
                "__roots.Clear();", roots,
                "global::Nethermind.Serialization.Ssz.Merkleization.Merkle.MerkleizeProgressive(out root, __roots);",
                useStack ? string.Empty : "}\nfinally\n{\n    global::System.Buffers.ArrayPool<global::Nethermind.Int256.UInt256>.Shared.Return(__rented);\n}",
                $"global::Nethermind.Serialization.Ssz.Merkleization.Merkle.MixInActiveFields(ref root, [{string.Join(", ", schema.ActiveFields)}]);");
        }
        return Join(SszEncodeEmitter.Validate(definition),
            $"global::Nethermind.Serialization.Ssz.Merkleization.Merkleizer __tree = new(stackalloc global::Nethermind.Int256.UInt256[{Depth((ulong)definition.Members.Length) + 1}]);",
            string.Join("\n", definition.Members.Select((member, index) =>
                member.Value.Binding is ScalarBinding { CodecKind: CodecKind.Converter, IsValueType: true } scalar
                    ? $"{scalar.CodecType}.Feed(ref __tree, {Scalar(scalar, Member(member))});"
                    : Join($"global::Nethermind.Int256.UInt256 __root{index};", Value(member, Member(member), $"__root{index}"), $"__tree.Feed(__root{index});"))),
            "__tree.CalculateRoot(out root);");
    }

    internal static string Value(SszMember member, string expression, string root)
    {
        string body = Value(member.Value, expression, root);
        if (member.Value.Binding.IsValueType || member.Value.Schema is BitSequenceSchema { Kind: not SequenceKind.Vector }) return body;
        return member.NullMapping switch
        {
            NullMapping.ZeroEncoded => $"if ({expression} is null) {{ {Zero(member.Value.Schema, root)} }} else {{ {body} }}",
            NullMapping.EmptyCollection => $"if ({expression} is null) {{ {Empty(member.Value, root)} }} else {{ {body} }}",
            _ => body,
        };
    }

    private static string Value(SszValue value, string expression, string root) => value.Binding switch
    {
        ScalarBinding { CodecKind: CodecKind.Converter } scalar =>
            $"MerkleizeWithConverter({Scalar(scalar, expression)}, {scalar.CodecType}.Feed, out {root});",
        ScalarBinding scalar => $"{scalar.CodecType}.Merkleize({expression}, out {root});",
        CollectionBinding collection => Collection((SequenceSchema)value.Schema, collection, Span(collection, expression), root),
        BitArrayBinding => value.Schema switch
        {
            BitSequenceSchema { Kind: SequenceKind.Vector } => $"global::Nethermind.Serialization.Ssz.Merkleization.Merkle.Merkleize(out {root}, {expression}!);",
            BitSequenceSchema { Kind: SequenceKind.List } bits => $"global::Nethermind.Serialization.Ssz.Merkleization.Merkle.Merkleize(out {root}, {expression} ?? new global::System.Collections.BitArray(0), {bits.Bound}UL);",
            _ => $"MerkleizeProgressiveBitList({expression}, out {root});",
        },
        _ => throw new InvalidOperationException("No Merkle operation for this binding."),
    };

    internal static string Empty(SszValue value, string root) => value.Binding switch
    {
        CollectionBinding collection => Collection((SequenceSchema)value.Schema, collection, $"global::System.ReadOnlySpan<{collection.Element.Binding.TypeName}>.Empty", root),
        ScalarBinding { Definition.WrappedMember: { } wrapped } => Empty(wrapped.Value, root),
        _ => throw new InvalidOperationException("Only collection bindings have an empty collection root."),
    };

    private static string Collection(SequenceSchema schema, CollectionBinding collection, string span, string root)
    {
        ScalarBinding element = (ScalarBinding)collection.Element.Binding;
        string bound = schema.Kind == SequenceKind.ProgressiveList ? string.Empty : $", {schema.Bound}UL";
        if (element.CodecKind != CodecKind.Converter)
        {
            string operation = schema.Kind switch { SequenceKind.Vector => "Vector", SequenceKind.List => "List", _ => "ProgressiveList" };
            return $"{element.CodecType}.Merkleize{operation}({span}{(schema.Kind == SequenceKind.List ? bound : string.Empty)}, out {root});";
        }
        if (schema.Element is BasicSchema basic)
        {
            string elementType = element.EnumUnderlyingType ?? element.TypeName;
            if (element.EnumUnderlyingType is not null)
                span = $"global::System.Runtime.InteropServices.MemoryMarshal.Cast<{element.TypeName}, {elementType}>({span})";
            string operation = schema.Kind switch { SequenceKind.Vector => "BasicVector", SequenceKind.List => "BasicList", _ => "ProgressiveBasicList" };
            return $"Merkleize{operation}WithConverter<{elementType}>({span}, {basic.Size}{bound}, {element.CodecType}.ToSpan, out {root});";
        }
        string composite = schema.Kind switch { SequenceKind.Vector => "Vector", SequenceKind.List => "List", _ => "ProgressiveList" };
        return $"MerkleizeComposite{composite}WithConverter({span}{bound}, {element.CodecType}.Feed, out {root});";
    }

    internal static string Zero(SszSchema schema, string root)
    {
        int index = 0;
        return Zero(schema, root, ref index);
    }

    private static string Zero(SszSchema schema, string root, ref int index)
    {
        if (schema is BasicSchema or BitSequenceSchema ||
            schema is SequenceSchema { Kind: SequenceKind.Vector, Element: BasicSchema })
            return $"global::Nethermind.Serialization.Ssz.Merkleization.Merkle.Merkleize(out {root}, global::System.ReadOnlySpan<byte>.Empty, {((ulong)schema.FixedSize!.Value + 31) / 32}UL);";

        if (schema is SequenceSchema { Kind: SequenceKind.Vector } sequence)
        {
            string item = $"__zeroRoot{index++}";
            string tree = $"__zeroTree{index++}";
            string iterator = $"__zeroIndex{index++}";
            return Join("{", $"global::Nethermind.Int256.UInt256 {item};", Zero(sequence.Element, item, ref index),
                $"global::Nethermind.Serialization.Ssz.Merkleization.Merkleizer {tree} = new(stackalloc global::Nethermind.Int256.UInt256[{Depth(sequence.Bound!.Value) + 1}]);",
                $"for (int {iterator} = 0; {iterator} < {sequence.Bound}; {iterator}++) {tree}.Feed({item});",
                $"{tree}.CalculateRoot(out {root});", "}");
        }
        ContainerSchema container = (ContainerSchema)schema;
        if (container.Kind == ContainerKind.ProgressiveContainer)
        {
            string roots = $"__zeroFields{index++}";
            List<string> lines = ["{", $"global::System.Span<global::Nethermind.Int256.UInt256> {roots} = stackalloc global::Nethermind.Int256.UInt256[{container.ActiveFieldCount}];", $"{roots}.Clear();"];
            foreach (SszField field in container.Fields) lines.Add(Zero(field.Schema, $"{roots}[{field.Index}]", ref index));
            lines.Add($"global::Nethermind.Serialization.Ssz.Merkleization.Merkle.MerkleizeProgressive(out {root}, {roots});");
            lines.Add($"global::Nethermind.Serialization.Ssz.Merkleization.Merkle.MixInActiveFields(ref {root}, [{string.Join(", ", container.ActiveFields)}]);");
            lines.Add("}");
            return string.Join("\n", lines);
        }
        string merkleizer = $"__zeroTree{index++}";
        List<string> statements = ["{", $"global::Nethermind.Serialization.Ssz.Merkleization.Merkleizer {merkleizer} = new(stackalloc global::Nethermind.Int256.UInt256[{Depth((ulong)container.Fields.Length) + 1}]);"];
        foreach (SszField field in container.Fields)
        {
            string item = $"__zeroRoot{index++}";
            statements.Add($"global::Nethermind.Int256.UInt256 {item};");
            statements.Add(Zero(field.Schema, item, ref index));
            statements.Add($"{merkleizer}.Feed({item});");
        }
        statements.Add($"{merkleizer}.CalculateRoot(out {root});");
        statements.Add("}");
        return string.Join("\n", statements);
    }
}
