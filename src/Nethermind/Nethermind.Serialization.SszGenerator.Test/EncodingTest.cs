// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Int256;
using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;
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
            BitVec = new(10),
        };

        byte[] encoded = Encode(test);
        Merkleize(test, out UInt256 root);
        Decode(encoded, out ComplexStruct decodedTest);
        Merkleize(decodedTest, out UInt256 decodedRoot);

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

        Decode(encoded, out DoubleListContainer decoded);

        Assert.That(decoded.First, Is.Not.Null);
        Assert.That(decoded.First, Is.Empty);
        Assert.That(decoded.Second, Is.Not.Null);
        Assert.That(decoded.Second, Is.Empty);
    }

    [Test]
    public void Decode_bitvector_preserves_declared_length()
    {
        BitArray bits = new(10);
        bits[0] = true;
        bits[3] = true;
        bits[9] = true;
        BitVectorContainer container = new() { Bits = bits };

        byte[] encoded = Encode(container);
        Decode(encoded, out BitVectorContainer decoded);

        Assert.That(decoded.Bits, Is.Not.Null);
        Assert.That(decoded.Bits!.Length, Is.EqualTo(10));
        Assert.That(decoded.Bits.Cast<bool>(), Is.EqualTo(container.Bits!.Cast<bool>()));
        Assert.That(decoded.Bits[0], Is.True);
        Assert.That(decoded.Bits[3], Is.True);
        Assert.That(decoded.Bits[9], Is.True);
        Assert.That(decoded.Bits[1], Is.False);
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

        byte[] encoded = Encode(container);
        Decode(encoded, out NestedProgressiveListContainer decoded);

        Assert.That(Encode(decoded), Is.EqualTo(encoded));
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

        Merkleize(container, out UInt256 actual);

        ulong[] items = [1UL, 2UL];
        Merkle.Merkleize(out UInt256 expected, items, 4);
        Merkle.MixIn(ref expected, items.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Merkleize_compatible_union_matches_the_selected_value_root()
    {
        CompatibleNumberUnion container = new() { Selector = CompatibleNumberUnionSelector.PreviousValue, PreviousValue = 123UL };

        Merkleize(container, out UInt256 actual);

        Merkle.Merkleize(out UInt256 expected, container.PreviousValue);
        Merkle.MixIn(ref expected, (byte)container.Selector);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Encode_and_decode_progressive_container_uses_field_indices()
    {
        ProgressiveContainerSample container = new() { Head = 1, Tail = 2 };

        byte[] encoded = Encode(container);
        Decode(encoded, out ProgressiveContainerSample decoded);

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

        Merkleize(container, out UInt256 actual);

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

        byte[] encoded = Encode(container);
        Decode(encoded, out ProgressiveListContainer decoded);

        Assert.That(decoded.Items, Is.EqualTo(container.Items));
    }

    [Test]
    public void Merkleize_progressive_list_uses_progressive_merkleization()
    {
        ProgressiveListContainer container = new() { Items = [1UL, 2UL, 3UL] };

        Merkleize(container, out UInt256 actual);

        ulong[] items = container.Items!;
        UInt256 expected = ProgressiveMerkleizeBytes(MemoryMarshal.AsBytes(items.AsSpan()));
        Merkle.MixIn(ref expected, items.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Encode_and_decode_progressive_bitlist_round_trip()
    {
        BitArray bits = new(10);
        bits[0] = true;
        bits[3] = true;
        bits[9] = true;
        ProgressiveBitlistContainer container = new() { Bits = bits };

        byte[] encoded = Encode(container);
        Decode(encoded, out ProgressiveBitlistContainer decoded);

        Assert.That(decoded.Bits, Is.Not.Null);
        Assert.That(decoded.Bits!.Length, Is.EqualTo(bits.Length));
        Assert.That(decoded.Bits.Cast<bool>(), Is.EqualTo(bits.Cast<bool>()));
    }

    [Test]
    public void Merkleize_progressive_bitlist_uses_progressive_merkleization()
    {
        BitArray bits = new(10);
        bits[0] = true;
        bits[3] = true;
        bits[9] = true;
        ProgressiveBitlistContainer container = new() { Bits = bits };

        Merkleize(container, out UInt256 actual);

        byte[] bytes = new byte[(bits.Length + 7) / 8];
        bits.CopyTo(bytes, 0);
        UInt256 expected = ProgressiveMerkleizeBytes(bytes);
        Merkle.MixIn(ref expected, bits.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(InvalidInputCases))]
    public void Encoding_rejects_invalid_input(Action action) =>
        Assert.That(action, Throws.InstanceOf<InvalidDataException>());

    [Test]
    public void ISszCodec_constraint_is_usable_from_generic_code()
    {
        FixedC fixture = new() { Fixed1 = 1, Fixed2 = 2 };

        byte[] viaInterface = EncodeViaInterface(fixture);

        Assert.That(viaInterface, Is.EqualTo(Encode(fixture)));

        static byte[] EncodeViaInterface<T>(T value) where T : ISszCodec<T>
        {
            byte[] buffer = new byte[T.GetLength(value)];
            T.Encode(buffer, value);
            return buffer;
        }
    }

    private static byte[] Encode<T>(T value) where T : ISszCodec<T> =>
        T.Encode(value);

    private static void Decode<T>(ReadOnlySpan<byte> data, out T value) where T : ISszCodec<T> =>
        T.Decode(data, out value);

    private static void Merkleize<T>(T value, out UInt256 root) where T : ISszCodec<T> =>
        T.Merkleize(value, out root);

    private static IEnumerable<TestCaseData> InvalidInputCases()
    {
        yield return new TestCaseData((Action)(() => Decode([99], out CompatibleNumberUnion _)))
            .SetName("Decode rejects unknown compatible union selector");
        yield return new TestCaseData((Action)(() => Decode([1, 1, 0], out CompatibleBoolUnion _)))
            .SetName("Decode rejects compatible union trailing bytes");
        yield return new TestCaseData((Action)(() => Decode(new byte[4], out VariableC _)))
            .SetName("Decode rejects truncated variable container input");
        yield return new TestCaseData((Action)(() => Decode([4, 0, 0, 0, 8, 0, 0, 0], out DoubleListContainer _)))
            .SetName("Decode rejects offsets that point into the fixed section");
        yield return new TestCaseData((Action)(() => Decode([8, 0, 0, 0, 7, 0, 0, 0], out DoubleListContainer _)))
            .SetName("Decode rejects offsets that are out of order");
        yield return new TestCaseData((Action)(() =>
        {
            byte[] encoded = Encode(new VariableC { Fixed1 = 1, Fixed2 = [10, 20] });
            encoded[8] = 0xFF;
            encoded[9] = 0xFF;
            encoded[10] = 0x00;
            encoded[11] = 0x00;
            Decode(encoded, out VariableC _);
        })).SetName("Decode rejects offsets that point past the end");
        yield return new TestCaseData((Action)(() => Encode(new FixedVectorContainer { Items = [new FixedC()] })))
            .SetName("Encode rejects vectors with the wrong length");
        yield return new TestCaseData((Action)(() => Encode(new SingleListContainer { Items = [1UL, 2UL, 3UL, 4UL, 5UL] })))
            .SetName("Encode rejects lists above the declared limit");
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
