// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using static SszCSharp;

internal static class SszCollectionEmitter
{
    internal static string Emit(SszCodecDefinition definition)
    {
        string name = definition.Declaration.TypeName;
        string itemRoot = ItemRoot(definition);
        string zeroRootMethod = !definition.Declaration.IsStruct && definition.Schema.FixedSize is not null ? $$"""
            private static void MerkleizeZeroEncoded(out global::Nethermind.Int256.UInt256 root)
            {
            {{Indent(SszMerkleEmitter.Zero(definition.Schema, "root"))}}
            }
            """ : string.Empty;
        return $$"""
            public static int GetLength(global::System.ReadOnlySpan<{{name}}> container)
            {
            {{Indent(Measure(definition))}}
            }

            public static byte[] Encode(global::System.ReadOnlySpan<{{name}}> items)
            {
                byte[] data = global::System.GC.AllocateUninitializedArray<byte>(GetLength(items));
                EncodeCore(data, items);
                return data;
            }

            public static void Encode(global::System.Span<byte> data, global::System.ReadOnlySpan<{{name}}> items)
            {
                ValidateExactLength(data.Length, GetLength(items), "{{name}}[]");
                EncodeCore(data, items);
            }

            private static void EncodeCore(global::System.Span<byte> data, global::System.ReadOnlySpan<{{name}}> items)
            {
            {{Indent(Encode(definition))}}
            }

            public static void Decode(global::System.ReadOnlySpan<byte> data, out {{name}}[] container)
            {
                {{(definition.HasOwnedValues ? "container = [];" : string.Empty)}}
                if (data.IsEmpty)
                {
                    {{(definition.HasOwnedValues ? string.Empty : "container = [];")}}
                    return;
                }
            {{Indent(Decode(definition))}}
            }

            public static void MerkleizeVector(global::System.ReadOnlySpan<{{name}}> container, out global::Nethermind.Int256.UInt256 root)
            {
                MerkleizeCollection(container, (ulong)container.Length, out root);
            }

            public static void MerkleizeList(global::System.ReadOnlySpan<{{name}}> container, ulong limit, out global::Nethermind.Int256.UInt256 root)
            {
                MerkleizeCollection(container, limit, out root);
                global::Nethermind.Serialization.Ssz.Merkleization.Merkle.MixIn(ref root, container.Length);
            }

            private static void MerkleizeCollection(global::System.ReadOnlySpan<{{name}}> container, ulong limit, out global::Nethermind.Int256.UInt256 root)
            {
                if ((ulong)container.Length > limit)
                {
                    ThrowInvalidData("{{name}}[]", "element count exceeds the SSZ limit.");
                }
                global::Nethermind.Serialization.Ssz.Merkleization.Merkleizer merkleizer = new(stackalloc global::Nethermind.Int256.UInt256[global::Nethermind.Serialization.Ssz.Merkleization.Merkle.NextPowerOfTwoExponent(limit) + 1]);
                for (int i = 0; i < container.Length; i++)
                {
            {{Indent(itemRoot, 2)}}
                    merkleizer.Feed(itemRoot);
                }
                merkleizer.CalculateRoot(out root);
            }

            public static void MerkleizeProgressiveList(global::System.ReadOnlySpan<{{name}}> container, out global::Nethermind.Int256.UInt256 root)
            {
                global::System.Span<global::Nethermind.Int256.UInt256> subtrees = stackalloc global::Nethermind.Int256.UInt256[17];
                global::System.Span<global::Nethermind.Int256.UInt256> scratch = stackalloc global::Nethermind.Int256.UInt256[33];
                int subtreeCount = 0;
                int offset = 0;
                ulong capacity = 1;
                while (offset < container.Length)
                {
                    int count = (int)global::System.Math.Min((ulong)(container.Length - offset), capacity);
                    global::Nethermind.Serialization.Ssz.Merkleization.Merkleizer merkleizer = new(scratch[..(global::Nethermind.Serialization.Ssz.Merkleization.Merkle.NextPowerOfTwoExponent(capacity) + 1)]);
                    for (int i = offset; i < offset + count; i++)
                    {
            {{Indent(itemRoot, 3)}}
                        merkleizer.Feed(itemRoot);
                    }
                    merkleizer.CalculateRoot(out subtrees[subtreeCount++]);
                    offset += count;
                    capacity *= 4;
                }
                MerkleizeProgressiveSubtrees(subtrees[..subtreeCount], out root);
                global::Nethermind.Serialization.Ssz.Merkleization.Merkle.MixIn(ref root, container.Length);
            }

            {{zeroRootMethod}}
            """;
    }

    private static string Measure(SszCodecDefinition definition) => definition.Schema.FixedSize is { } size
        ? $"return checked(container.Length * {size});"
        : $$"""
            int length = checked(container.Length * 4);
            foreach ({{definition.Declaration.TypeName}} item in container)
            {
                {{NullVariableGuard(definition)}}
                length = checked(length + GetLength(item));
            }
            return length;
            """;

    private static string Encode(SszCodecDefinition definition)
    {
        string name = definition.Declaration.TypeName;
        if (definition.Schema.FixedSize is not { } size)
        {
            int cachedLengths = SszEncodeEmitter.CachedLengthCount(definition);
            return $$"""
                int offset = checked(items.Length * 4);
                int itemOffset = 0;
                {{(cachedLengths == 0 ? string.Empty : $"global::System.Span<int> __lengths = stackalloc int[{cachedLengths}];")}}
                foreach ({{name}} item in items)
                {
                    {{NullVariableGuard(definition)}}
                    EncodeOffset(data.Slice(itemOffset, 4), offset);
                    itemOffset += 4;
                    int length = {{(cachedLengths == 0 ? "GetLength(item)" : "MeasureForEncode(item, __lengths)")}};
                    EncodeCore(data.Slice(offset, length), item{{(cachedLengths == 0 ? string.Empty : ", __lengths")}});
                    offset = checked(offset + length);
                }
                """;
        }

        string encodeItem = Join("_ = GetLength(item);", $"EncodeCore(data.Slice(offset, {size}), item);");
        if (!definition.Declaration.IsStruct)
            encodeItem = $"if (item is null) data.Slice(offset, {size}).Clear();\nelse\n{{\n{Indent(encodeItem)}\n}}";
        return $$"""
            int offset = 0;
            foreach ({{name}} item in items)
            {
            {{Indent(encodeItem)}}
                offset += {{size}};
            }
            """;
    }

    private static string Decode(SszCodecDefinition definition)
    {
        string name = definition.Declaration.TypeName;
        if (definition.Schema.FixedSize is { } size)
            return Join($"ValidateMultipleOf(data.Length, {size}, \"{name}[]\");",
                $"int length = data.Length / {size};", $"container = new {name}[length];", "int offset = 0;",
                DecodeLoop(definition, Join($"Decode(data.Slice(offset, {size}), out container[index]);", $"offset += {size};")));

        return Join($$"""
            ValidateMinimumLength(data.Length, 4, "{{name}}[]");
            int firstOffset = DecodeOffset(data.Slice(0, 4));
            int length = ValidateVariableItemCount(data, firstOffset, "{{name}}[]");
            int offset = firstOffset;
            for (int offsetIndex = 1; offsetIndex < length; offsetIndex++)
            {
                int nextOffset = DecodeOffset(data.Slice(offsetIndex * 4, 4));
                ValidateNextOffset(data, offset, nextOffset, "{{name}}[]");
                offset = nextOffset;
            }
            container = new {{name}}[length];
            offset = firstOffset;
            """, DecodeLoop(definition, Join(
                "int nextOffset = index + 1 == length ? data.Length : DecodeOffset(data.Slice((index + 1) * 4, 4));",
                DecodeItem(definition), "offset = nextOffset;")));
    }

    private static string DecodeLoop(SszCodecDefinition definition, string body)
    {
        string loop = $$"""
            for ({{(definition.HasOwnedValues ? string.Empty : "int index = 0")}}; index < length; index++)
            {
            {{Indent(body)}}
            }
            """;
        if (!definition.HasOwnedValues) return loop;

        return $$"""
            int index = 0;
            try
            {
            {{Indent(loop)}}
            }
            catch
            {
                for (int releaseIndex = 0; releaseIndex < index; releaseIndex++)
                {
            {{Indent(SszOwnershipEmitter.Release(definition, "container[releaseIndex]"), 2)}}
                }
                container = [];
                throw;
            }
            """;
    }

    private static string DecodeItem(SszCodecDefinition definition)
    {
        if (definition.Declaration.IsStruct && definition.WrappedMember is
            {
                Value.Schema: SequenceSchema { Kind: SequenceKind.List, Bound: { } limit },
                Value.Binding: CollectionBinding
                {
                    Storage: CollectionStorage.Array or CollectionStorage.Memory or CollectionStorage.ReadOnlyMemory,
                    Element.Schema: BasicSchema { Size: 1 },
                    Element.Binding.TypeName: "byte" or "global::System.Byte",
                } collection,
            } wrapped)
            return Join($"byte[] __bytes = DecodeByteList(data.Slice(offset, nextOffset - offset), {limit}UL, nameof({definition.Declaration.TypeName}), nameof(@{wrapped.Name}));",
                $"{Member(wrapped, "container[index]")} = {Construct(collection, "__bytes")};");

        return "Decode(data.Slice(offset, nextOffset - offset), out container[index]);";
    }

    private static string ItemRoot(SszCodecDefinition definition)
    {
        string item = $"{definition.Declaration.TypeName} item = container[i];";
        if (!definition.Declaration.IsStruct)
        {
            if (definition.WrappedMember is { } wrapped)
                return Join(item, "global::Nethermind.Int256.UInt256 itemRoot;",
                    $"if (item is null) {{ {SszMerkleEmitter.Empty(wrapped.Value, "itemRoot")} }} else {{ Merkleize(item, out itemRoot); }}");
            if (definition.Schema.FixedSize is not null)
                return Join(item, "global::Nethermind.Int256.UInt256 itemRoot;",
                    "if (item is null) MerkleizeZeroEncoded(out itemRoot); else Merkleize(item, out itemRoot);");
        }
        return Join(item, NullVariableGuard(definition), "Merkleize(item, out global::Nethermind.Int256.UInt256 itemRoot);");
    }

    private static string NullVariableGuard(SszCodecDefinition definition) =>
        !definition.Declaration.IsStruct && definition.Schema.FixedSize is null && definition.WrappedMember is null
            ? $"if (item is null) ThrowInvalidData(\"{definition.Declaration.TypeName}[]\", \"null variable elements are not decodable.\");"
            : string.Empty;
}
