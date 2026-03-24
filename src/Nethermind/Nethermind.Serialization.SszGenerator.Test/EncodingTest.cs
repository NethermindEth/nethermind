// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;

namespace Nethermind.Serialization.SszGenerator.Test;

public class EncodingTest
{
    [Test]
    public void Test_ComplexStructure_EncodingRoundTrip()
    {
        ComplexStruct test = new()
        {
            VariableC = new VariableC { Fixed1 = 2, Fixed2 = [1, 2, 3, 4] },
            Test2 = Enumerable.Range(0, 10).Select(i => (ulong)i).ToArray(),
            FixedCVec = Enumerable.Range(0, 10).Select(_ => new FixedC()).ToArray(),
            VariableCVec = Enumerable.Range(0, 10).Select(_ => new VariableC()).ToArray(),
            Test2Union = new CompatibleNumberUnion { Selector = CompatibleNumberUnionSelector.PreviousValue, PreviousValue = 7 },
            Test2UnionVec = Enumerable.Range(0, 10).Select(i => new CompatibleNumberUnion
            {
                Selector = CompatibleNumberUnionSelector.CurrentValue,
                CurrentValue = (ulong)i,
            }).ToArray(),
            BitVec = new BitArray(10),
        };

        byte[] encoded = SszEncoding.Encode(test);
        SszEncoding.Merkleize(test, out UInt256 root);
        SszEncoding.Decode(encoded, out ComplexStruct decodedTest);
        SszEncoding.Merkleize(decodedTest, out UInt256 decodedRoot);

        Assert.That(decodedTest.VariableC.Fixed1, Is.EqualTo(test.VariableC.Fixed1));
        Assert.That(decodedTest.VariableC.Fixed2, Is.EqualTo(test.VariableC.Fixed2));
        Assert.That(decodedTest.Test2Union.Selector, Is.EqualTo(test.Test2Union.Selector));
        Assert.That(decodedTest.Test2Union.PreviousValue, Is.EqualTo(test.Test2Union.PreviousValue));
        Assert.That(root, Is.EqualTo(decodedRoot));
    }

    [Test]
    public void Decode_empty_variable_lists_as_empty_collections()
    {
        byte[] encoded = [8, 0, 0, 0, 8, 0, 0, 0];

        SszEncoding.Decode(encoded, out DoubleListContainer decoded);

        Assert.That(decoded.First, Is.Not.Null);
        Assert.That(decoded.First, Is.Empty);
        Assert.That(decoded.Second, Is.Not.Null);
        Assert.That(decoded.Second, Is.Empty);
    }

    [Test]
    public void Decode_bitvector_preserves_declared_length()
    {
        BitVectorContainer container = new() { Bits = new BitArray(10) };

        byte[] encoded = SszEncoding.Encode(container);
        SszEncoding.Decode(encoded, out BitVectorContainer decoded);

        Assert.That(decoded.Bits, Is.Not.Null);
        Assert.That(decoded.Bits!.Length, Is.EqualTo(10));
        Assert.That(decoded.Bits.Cast<bool>(), Is.EqualTo(container.Bits!.Cast<bool>()));
    }

    [Test]
    public void Encode_and_decode_nested_progressive_list_round_trip()
    {
        NestedProgressiveListContainer container = new()
        {
            Items =
            [
                new ProgressiveVarTestStructList
                {
                    Items =
                    [
                        new VariableC { Fixed1 = 1, Fixed2 = [2, 3] },
                        new VariableC { Fixed1 = 4, Fixed2 = [5] },
                    ],
                },
                new ProgressiveVarTestStructList
                {
                    Items =
                    [
                        new VariableC { Fixed1 = 6, Fixed2 = [] },
                    ],
                },
            ],
        };

        byte[] encoded = SszEncoding.Encode(container);
        SszEncoding.Decode(encoded, out NestedProgressiveListContainer decoded);

        Assert.That(SszEncoding.Encode(decoded), Is.EqualTo(encoded));
        Assert.That(decoded.Items, Has.Length.EqualTo(2));
        Assert.That(decoded.Items![0].Items, Has.Length.EqualTo(2));
        Assert.That(decoded.Items[0].Items![0].Fixed2, Is.EqualTo([2UL, 3UL]));
        Assert.That(decoded.Items[1].Items, Has.Length.EqualTo(1));
        Assert.That(decoded.Items[1].Items![0].Fixed2, Is.Empty);
    }

    [Test]
    public void Merkleize_basic_list_mixes_in_the_actual_length()
    {
        SingleListContainer container = new() { Items = [1UL, 2UL] };

        SszEncoding.Merkleize(container, out UInt256 actual);

        ulong[] items = [1UL, 2UL];
        Merkle.Merkleize(out UInt256 expected, items, 4);
        Merkle.MixIn(ref expected, items.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Merkleize_compatible_union_matches_the_selected_value_root()
    {
        CompatibleNumberUnion container = new() { Selector = CompatibleNumberUnionSelector.PreviousValue, PreviousValue = 123UL };

        SszEncoding.Merkleize(container, out UInt256 actual);

        Merkle.Merkleize(out UInt256 expected, container.PreviousValue);
        Merkle.MixIn(ref expected, (byte)container.Selector);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Decode_rejects_unknown_compatible_union_selector()
    {
        Assert.That(
            () => SszEncoding.Decode([99], out CompatibleNumberUnion _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Encode_and_decode_progressive_container_uses_field_indices()
    {
        ProgressiveContainerSample container = new() { Head = 1, Tail = 2 };

        byte[] encoded = SszEncoding.Encode(container);
        SszEncoding.Decode(encoded, out ProgressiveContainerSample decoded);

        byte[] expected = new byte[16];
        BitConverter.TryWriteBytes(expected.AsSpan(0, 8), container.Head);
        BitConverter.TryWriteBytes(expected.AsSpan(8, 8), container.Tail);

        Assert.That(encoded, Is.EqualTo(expected));
        Assert.That(decoded.Head, Is.EqualTo(container.Head));
        Assert.That(decoded.Tail, Is.EqualTo(container.Tail));
    }

    [Test]
    public void Merkleize_progressive_container_mixes_in_active_fields()
    {
        ProgressiveContainerSample container = new() { Head = 1, Tail = 2 };

        SszEncoding.Merkleize(container, out UInt256 actual);

        Merkle.Merkleize(out UInt256 headRoot, container.Head);
        Merkle.Merkleize(out UInt256 tailRoot, container.Tail);
        MerkleizeProgressiveSpec([headRoot, tailRoot], out UInt256 expected);
        expected = MixInActiveFieldsSpec(expected, 0b00000101);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Encode_and_decode_progressive_list_round_trip()
    {
        ProgressiveListContainer container = new() { Items = [1UL, 2UL, 3UL] };

        byte[] encoded = SszEncoding.Encode(container);
        SszEncoding.Decode(encoded, out ProgressiveListContainer decoded);

        Assert.That(decoded.Items, Is.EqualTo(container.Items));
    }

    [Test]
    public void Merkleize_progressive_list_uses_progressive_merkleization()
    {
        ProgressiveListContainer container = new() { Items = [1UL, 2UL, 3UL] };

        SszEncoding.Merkleize(container, out UInt256 actual);

        ulong[] items = container.Items!;
        UInt256 expected = ProgressiveMerkleizeBytes(MemoryMarshal.AsBytes(items.AsSpan()));
        Merkle.MixIn(ref expected, items.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Encode_and_decode_progressive_bitlist_round_trip()
    {
        BitArray bits = new BitArray(10);
        bits[0] = true;
        bits[3] = true;
        bits[9] = true;
        ProgressiveBitlistContainer container = new() { Bits = bits };

        byte[] encoded = SszEncoding.Encode(container);
        SszEncoding.Decode(encoded, out ProgressiveBitlistContainer decoded);

        Assert.That(decoded.Bits, Is.Not.Null);
        Assert.That(decoded.Bits!.Length, Is.EqualTo(bits.Length));
        Assert.That(decoded.Bits.Cast<bool>(), Is.EqualTo(bits.Cast<bool>()));
    }

    [Test]
    public void Merkleize_progressive_bitlist_uses_progressive_merkleization()
    {
        BitArray bits = new BitArray(10);
        bits[0] = true;
        bits[3] = true;
        bits[9] = true;
        ProgressiveBitlistContainer container = new() { Bits = bits };

        SszEncoding.Merkleize(container, out UInt256 actual);

        byte[] bytes = new byte[(bits.Length + 7) / 8];
        bits.CopyTo(bytes, 0);
        UInt256 expected = ProgressiveMerkleizeBytes(bytes);
        Merkle.MixIn(ref expected, bits.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Decode_rejects_truncated_variable_container_input()
    {
        Assert.That(
            () => SszEncoding.Decode(new byte[4], out VariableC _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Decode_rejects_offsets_that_point_into_the_fixed_section()
    {
        byte[] encoded = [4, 0, 0, 0, 8, 0, 0, 0];

        Assert.That(
            () => SszEncoding.Decode(encoded, out DoubleListContainer _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Decode_rejects_offsets_that_are_out_of_order()
    {
        byte[] encoded = [8, 0, 0, 0, 7, 0, 0, 0];

        Assert.That(
            () => SszEncoding.Decode(encoded, out DoubleListContainer _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Decode_rejects_offsets_that_point_past_the_end()
    {
        VariableC valid = new() { Fixed1 = 1, Fixed2 = [10, 20] };
        byte[] encoded = SszEncoding.Encode(valid);

        encoded[8] = 0xFF;
        encoded[9] = 0xFF;
        encoded[10] = 0x00;
        encoded[11] = 0x00;

        Assert.That(
            () => SszEncoding.Decode(encoded, out VariableC _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Encode_rejects_vectors_with_the_wrong_length()
    {
        FixedVectorContainer container = new() { Items = [new FixedC()] };

        Assert.That(
            () => SszEncoding.Encode(container),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Encode_rejects_lists_above_the_declared_limit()
    {
        SingleListContainer container = new() { Items = [1UL, 2UL, 3UL, 4UL, 5UL] };

        Assert.That(
            () => SszEncoding.Encode(container),
            Throws.InstanceOf<InvalidDataException>());
    }

    private static UInt256 MixInActiveFieldsSpec(UInt256 root, byte activeFields)
    {
        Span<byte> chunk = stackalloc byte[32];
        chunk[0] = activeFields;
        return HashConcat(root, new UInt256(chunk));
    }

    private static UInt256 ProgressiveMerkleizeBytes(ReadOnlySpan<byte> bytes)
    {
        MerkleizeProgressiveSpec(PackToChunks(bytes), out UInt256 root);
        return root;
    }

    private static UInt256[] PackToChunks(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0)
        {
            return [];
        }

        int chunkCount = (bytes.Length + 31) / 32;
        UInt256[] chunks = new UInt256[chunkCount];
        int fullByteLength = bytes.Length / 32 * 32;
        if (fullByteLength > 0)
        {
            MemoryMarshal.Cast<byte, UInt256>(bytes[..fullByteLength]).CopyTo(chunks);
        }

        if (fullByteLength != bytes.Length)
        {
            Span<byte> lastChunk = stackalloc byte[32];
            bytes[fullByteLength..].CopyTo(lastChunk);
            chunks[^1] = new UInt256(lastChunk);
        }

        return chunks;
    }

    private static void MerkleizeProgressiveSpec(ReadOnlySpan<UInt256> chunks, out UInt256 root, ulong numLeaves = 1)
    {
        if (chunks.Length is 0)
        {
            root = UInt256.Zero;
            return;
        }

        int rightCount = (int)Math.Min((ulong)chunks.Length, Math.Min(numLeaves, (ulong)int.MaxValue));
        ReadOnlySpan<UInt256> leftChunks = chunks[rightCount..];
        UInt256 left = UInt256.Zero;
        if (!leftChunks.IsEmpty)
        {
            MerkleizeProgressiveSpec(leftChunks, out left, checked(numLeaves * 4));
        }

        Merkle.Merkleize(out UInt256 right, chunks[..rightCount], numLeaves);
        root = HashConcat(left, right);
    }

    private static UInt256 HashConcat(UInt256 left, UInt256 right)
    {
        Span<UInt256> values = stackalloc UInt256[2];
        values[0] = left;
        values[1] = right;
        return new UInt256(SHA256.HashData(MemoryMarshal.Cast<UInt256, byte>(values)));
    }
}
