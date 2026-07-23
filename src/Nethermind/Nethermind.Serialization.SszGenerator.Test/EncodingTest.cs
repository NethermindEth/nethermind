// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz.SszBasicTypeConverters;
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decodedTest.VariableC.Fixed1, Is.EqualTo(test.VariableC.Fixed1));
            Assert.That(decodedTest.VariableC.Fixed2, Is.EqualTo(test.VariableC.Fixed2));
            Assert.That(decodedTest.Test2Union.Selector, Is.EqualTo(test.Test2Union.Selector));
            Assert.That(decodedTest.Test2Union.PreviousValue, Is.EqualTo(test.Test2Union.PreviousValue));
            Assert.That(root, Is.EqualTo(decodedRoot));
        }
    }

    [Test]
    public void Decode_empty_variable_lists_as_empty_collections()
    {
        byte[] encoded = [8, 0, 0, 0, 8, 0, 0, 0];

        Decode(encoded, out DoubleListContainer decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.First, Is.Not.Null);
            Assert.That(decoded.First, Is.Empty);
            Assert.That(decoded.Second, Is.Not.Null);
            Assert.That(decoded.Second, Is.Empty);
        }
    }

    [Test]
    public void Decode_collection_itself_byte_lists()
    {
        ByteListItself[] original = [new() { Bytes = [] }, new() { Bytes = [1, 2, 3] }];

        byte[] encoded = ByteListItself.Encode(original);
        ByteListItself.Decode(encoded, out ByteListItself[] decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Has.Length.EqualTo(2));
            Assert.That(decoded[0].Bytes, Is.Empty);
            Assert.That(decoded[1].Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }

    [Test]
    public void Decode_collection_itself_byte_lists_enforces_item_limit()
    {
        byte[] encoded = [8, 0, 0, 0, 12, 0, 0, 0, 1, 2, 3, 4];

        Assert.That(() => ByteListItself.Decode(encoded, out ByteListItself[] _), Throws.InstanceOf<InvalidDataException>());
    }

    private static BitArray MakeSampleBits10()
    {
        BitArray bits = new(10);
        bits[0] = true;
        bits[3] = true;
        bits[9] = true;
        return bits;
    }

    [Test]
    public void Decode_bitvector_preserves_declared_length()
    {
        BitVectorContainer container = new() { Bits = MakeSampleBits10() };

        byte[] encoded = Encode(container);
        Decode(encoded, out BitVectorContainer decoded);

        Assert.That(decoded.Bits, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Bits!.Length, Is.EqualTo(10));
            Assert.That(decoded.Bits.Cast<bool>(), Is.EqualTo(container.Bits!.Cast<bool>()));
            Assert.That(decoded.Bits[0], Is.True);
            Assert.That(decoded.Bits[3], Is.True);
            Assert.That(decoded.Bits[9], Is.True);
            Assert.That(decoded.Bits[1], Is.False);
        }
    }

    [Test]
    public void Supports_list_limits_beyond_int_range()
    {
        const ulong Limit = 1_099_511_627_776; // 2^40, VALIDATOR_REGISTRY_LIMIT
        ulong[] basicItems = [1, 2, 3];
        FixedC[] compositeItems = [new() { Fixed1 = 1, Fixed2 = 2 }, new() { Fixed1 = 3, Fixed2 = 4 }];

        HugeLimitBasicList basicList = new() { Items = basicItems };
        byte[] encoded = HugeLimitBasicList.Encode(basicList);
        HugeLimitBasicList.Decode(encoded, out HugeLimitBasicList decodedBasic);
        HugeLimitBasicList.Merkleize(basicList, out UInt256 basicRoot);

        // Reference roots computed via the runtime ulong-limit merkleization primitives
        Merkle.Merkleize(out UInt256 expectedBasicRoot, MemoryMarshal.AsBytes<ulong>(basicItems), Limit / 4);
        Merkle.MixIn(ref expectedBasicRoot, basicItems.Length);

        HugeLimitCompositeList compositeList = new() { Items = compositeItems };
        HugeLimitCompositeList.Merkleize(compositeList, out UInt256 compositeRoot);

        Span<UInt256> itemRoots = stackalloc UInt256[compositeItems.Length];
        for (int i = 0; i < compositeItems.Length; i++)
        {
            FixedC.Merkleize(compositeItems[i], out itemRoots[i]);
        }
        Merkle.Merkleize(out UInt256 expectedCompositeRoot, itemRoots, Limit);
        Merkle.MixIn(ref expectedCompositeRoot, compositeItems.Length);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decodedBasic.Items, Is.EqualTo(basicItems));
            Assert.That(basicRoot, Is.EqualTo(expectedBasicRoot));
            Assert.That(compositeRoot, Is.EqualTo(expectedCompositeRoot));
        }
    }

    [Test]
    public void Encode_and_decode_signed_primitive_collections_round_trip()
    {
        SignedPrimitiveCollectionContainer container = new()
        {
            Bools = [true, false, true],
            Ints = [-1, int.MaxValue],
            Longs = [long.MinValue, 7],
            Wides = [UInt128.One, UInt128.MaxValue],
        };

        byte[] encoded = Encode(container);
        Decode(encoded, out SignedPrimitiveCollectionContainer decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Bools, Is.EqualTo(container.Bools));
            Assert.That(decoded.Ints, Is.EqualTo(container.Ints));
            Assert.That(decoded.Longs, Is.EqualTo(container.Longs));
            Assert.That(decoded.Wides, Is.EqualTo(container.Wides));
        }
    }

    [Test]
    public void Merkleize_uint128_vector_matches_encoded_bytes()
    {
        UInt128VectorContainer container = new()
        {
            Wides =
            [
                UInt128.One,
                new UInt128(0x0102030405060708UL, 0x1112131415161718UL),
                UInt128.MaxValue,
            ],
        };

        byte[] expectedBytes = new byte[3 * UInt128SszBasicTypeConverter.Length];
        UInt128SszBasicTypeConverter.ToSpan(expectedBytes, container.Wides);

        Merkleize(container, out UInt256 actual);
        Merkle.Merkleize(out UInt256 expected, expectedBytes, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Encode(container), Is.EqualTo(expectedBytes));
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Encode_decode_and_merkleize_enum_vector_uses_underlying_converter()
    {
        PrimitiveEnumVectorContainer container = new()
        {
            Items = [PrimitiveEnum.One, PrimitiveEnum.Max, PrimitiveEnum.Two],
        };
        byte[] expectedBytes = new byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(expectedBytes.AsSpan(0, sizeof(uint)), (uint)PrimitiveEnum.One);
        BinaryPrimitives.WriteUInt32LittleEndian(expectedBytes.AsSpan(sizeof(uint), sizeof(uint)), (uint)PrimitiveEnum.Max);
        BinaryPrimitives.WriteUInt32LittleEndian(expectedBytes.AsSpan(2 * sizeof(uint), sizeof(uint)), (uint)PrimitiveEnum.Two);

        byte[] encoded = Encode(container);
        Decode(encoded, out PrimitiveEnumVectorContainer decoded);
        Merkleize(container, out UInt256 actual);
        Merkle.Merkleize(out UInt256 expected, expectedBytes, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(expectedBytes));
            Assert.That(decoded.Items, Is.EqualTo(container.Items));
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Decode_bool_vector_rejects_non_boolean_values() =>
        Assert.That(() => Decode([0, 2, 1], out BoolVectorContainer _), Throws.InstanceOf<InvalidDataException>());

    [Test]
    public void Converter_static_calls_ignore_member_name_shadowing()
    {
        ConverterNameShadowContainer container = new() { TestBytes4SszVectorTypeConverter = new TestBytes4(42) };

        byte[] encoded = Encode(container);
        Decode(encoded, out ConverterNameShadowContainer decoded);

        Assert.That(decoded.TestBytes4SszVectorTypeConverter.Value, Is.EqualTo(container.TestBytes4SszVectorTypeConverter.Value));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Encode(decoded), Is.EqualTo(encoded));
            Assert.That(decoded.Items, Has.Length.EqualTo(2));
            Assert.That(decoded.Items![0].Items, Has.Length.EqualTo(2));
            Assert.That(decoded.Items[0].Items![0].Fixed2, Is.EqualTo([2UL, 3UL]));
            Assert.That(decoded.Items[1].Items, Has.Length.EqualTo(1));
            Assert.That(decoded.Items[1].Items![0].Fixed2, Is.Empty);
        }
    }

    [Test]
    public void Merkleize_basic_list_mixes_in_the_actual_length()
    {
        SingleListContainer container = new() { Items = [1UL, 2UL] };

        Merkleize(container, out UInt256 actual);

        ulong[] items = [1UL, 2UL];
        Merkle.Merkleize(out UInt256 expected, MemoryMarshal.AsBytes(items.AsSpan()), 1);
        Merkle.MixIn(ref expected, items.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Encode_decode_and_merkleize_array_pool_list()
    {
        using ArrayPoolList<ulong> items = new(4);
        items.Add(1);
        items.Add(2);
        ArrayPoolListContainer container = new() { Items = items };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 actual);
        Decode(encoded, out ArrayPoolListContainer decoded);

        try
        {
            ulong[] expectedItems = [1UL, 2UL];
            Merkle.Merkleize(out UInt256 expected, MemoryMarshal.AsBytes(expectedItems.AsSpan()), 1);
            Merkle.MixIn(ref expected, expectedItems.Length);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Items.AsSpan().ToArray(), Is.EqualTo(expectedItems));
                Assert.That(actual, Is.EqualTo(expected));
            }
        }
        finally
        {
            decoded.Items.Dispose();
        }
    }

    [Test]
    public void Nullable_array_pool_list_encodes_null_as_empty_list()
    {
        NullableArrayPoolListContainer container = new() { Items = null };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out NullableArrayPoolListContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded, Is.EqualTo(new byte[] { 4, 0, 0, 0 }));
                Assert.That(decoded.Items, Is.Not.Null);
                Assert.That(decoded.Items, Is.Empty);
                Assert.That(root, Is.EqualTo(decodedRoot));
            }
        }
        finally
        {
            decoded.Items?.Dispose();
        }
    }

    [Test]
    public void Nullable_fixed_array_pool_list_encodes_null_as_empty_list()
    {
        NullableFixedArrayPoolListContainer container = new() { Items = null };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out NullableFixedArrayPoolListContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded, Is.EqualTo(new byte[] { 4, 0, 0, 0 }));
                Assert.That(decoded.Items, Is.Not.Null);
                Assert.That(decoded.Items, Is.Empty);
                Assert.That(root, Is.EqualTo(decodedRoot));
            }
        }
        finally
        {
            decoded.Items?.Dispose();
        }
    }

    [Test]
    public void Nullable_compatible_union_array_pool_list_encodes_null_as_empty_list()
    {
        NullableCompatibleUnionArrayPoolListContainer container = new() { Items = null };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out NullableCompatibleUnionArrayPoolListContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded, Is.EqualTo(new byte[] { 4, 0, 0, 0 }));
                Assert.That(decoded.Items, Is.Not.Null);
                Assert.That(decoded.Items, Is.Empty);
                Assert.That(root, Is.EqualTo(decodedRoot));
            }
        }
        finally
        {
            decoded.Items?.Dispose();
        }
    }

    [Test]
    public void Nullable_collection_itself_encodes_null_as_empty_list()
    {
        NullableArrayPoolListItself container = new() { Items = null };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out NullableArrayPoolListItself decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded, Is.Empty);
                Assert.That(decoded.Items, Is.Not.Null);
                Assert.That(decoded.Items, Is.Empty);
                Assert.That(root, Is.EqualTo(decodedRoot));
            }
        }
        finally
        {
            decoded.Items?.Dispose();
        }
    }

    [Test]
    public void Merkleize_nullable_list_matches_empty_decoded_list()
    {
        SingleListContainer container = new() { Items = null };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out SingleListContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Items, Is.Empty);
            Assert.That(root, Is.EqualTo(decodedRoot));
        }
    }

    [Test]
    public void Merkleize_nullable_vector_matches_zero_decoded_vector()
    {
        NullableByteVectorContainer container = new() { Bytes = null };

        byte[] encoded = Encode(container);
        byte[] reusedBuffer = Enumerable.Repeat((byte)0xFF, 64).ToArray();
        NullableByteVectorContainer.Encode(reusedBuffer, container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out NullableByteVectorContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);
        Merkle.Merkleize(out UInt256 expected, ReadOnlySpan<byte>.Empty, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(new byte[64]));
            Assert.That(reusedBuffer, Is.EqualTo(new byte[64]));
            Assert.That(decoded.Bytes, Is.EqualTo(new byte[64]));
            Assert.That(root, Is.EqualTo(expected));
            Assert.That(root, Is.EqualTo(decodedRoot));
        }
    }

    [Test]
    public void Merkleize_nullable_static_container_matches_zero_decoded_container()
    {
        const int childLength = 2 * sizeof(ulong);
        NullableStaticChildContainer container = new() { Child = null };

        byte[] encoded = Encode(container);
        byte[] reusedBuffer = Enumerable.Repeat((byte)0xFF, childLength).ToArray();
        NullableStaticChildContainer.Encode(reusedBuffer, container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out NullableStaticChildContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Child, Is.Not.Null);
            Assert.That(root, Is.EqualTo(decodedRoot));
            Assert.That(encoded, Is.EqualTo(new byte[childLength]));
            Assert.That(reusedBuffer, Is.EqualTo(new byte[childLength]));
        }
    }

    [Test]
    public void Nullable_variable_container_without_decodable_default_is_rejected()
    {
        NullableVariableChildContainer container = new() { Child = null };

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<InvalidDataException>(() => NullableVariableChildContainer.GetLength(container));
            Assert.Throws<InvalidDataException>(() => Encode(container));
            Assert.Throws<InvalidDataException>(() => Merkleize(container, out _));
        }
    }

    [Test]
    public void Merkleize_nullable_converter_vector_uses_default_item_root()
    {
        NullableLongConverterVectorContainer container = new() { Items = null };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out NullableLongConverterVectorContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        Span<byte> zeroItem = stackalloc byte[TestBytes48SszVectorTypeConverter.Length];
        Merkle.Merkleize(out UInt256 itemRoot, zeroItem);
        Span<UInt256> itemRoots = stackalloc UInt256[2];
        itemRoots[0] = itemRoot;
        itemRoots[1] = itemRoot;
        Merkle.Merkleize(out UInt256 expected, itemRoots);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(new byte[TestBytes48SszVectorTypeConverter.Length * 2]));
            Assert.That(decoded.Items, Has.Length.EqualTo(2));
            Assert.That(root, Is.EqualTo(expected));
            Assert.That(root, Is.EqualTo(decodedRoot));
        }
    }

    [Test]
    public void Merkleize_progressive_nullable_vector_matches_zero_decoded_vector()
    {
        ProgressiveNullableByteVectorContainer container = new() { Bytes = null };

        byte[] encoded = Encode(container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out ProgressiveNullableByteVectorContainer decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(new byte[64]));
            Assert.That(decoded.Bytes, Is.EqualTo(new byte[64]));
            Assert.That(root, Is.EqualTo(decodedRoot));
        }
    }

    [Test]
    public void Compatible_union_nullable_vector_clears_and_merkleizes_default_root()
    {
        CompatibleNullableVectorUnion container = new() { Selector = CompatibleNullableVectorUnionSelector.Items, Items = null };

        byte[] encoded = Encode(container);
        byte[] reusedBuffer = Enumerable.Repeat((byte)0xFF, 1 + TestBytes48SszVectorTypeConverter.Length * 2).ToArray();
        CompatibleNullableVectorUnion.Encode(reusedBuffer, container);
        Merkleize(container, out UInt256 root);
        Decode(encoded, out CompatibleNullableVectorUnion decoded);
        Merkleize(decoded, out UInt256 decodedRoot);

        Span<byte> zeroItem = stackalloc byte[TestBytes48SszVectorTypeConverter.Length];
        Merkle.Merkleize(out UInt256 itemRoot, zeroItem);
        Span<UInt256> itemRoots = stackalloc UInt256[2];
        itemRoots[0] = itemRoot;
        itemRoots[1] = itemRoot;
        Merkle.Merkleize(out UInt256 expected, itemRoots);
        Merkle.MixIn(ref expected, (byte)container.Selector);

        byte[] expectedBytes = new byte[1 + TestBytes48SszVectorTypeConverter.Length * 2];
        expectedBytes[0] = (byte)container.Selector;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(expectedBytes));
            Assert.That(reusedBuffer, Is.EqualTo(expectedBytes));
            Assert.That(decoded.Items, Has.Length.EqualTo(2));
            Assert.That(root, Is.EqualTo(expected));
            Assert.That(root, Is.EqualTo(decodedRoot));
        }
    }

    [Test]
    public void Nullable_non_basic_vector_without_zero_item_encoding_is_rejected()
    {
        NullableCompatibleUnionVectorContainer container = new() { Items = null };

        Assert.That(() => Encode(container), Throws.InstanceOf<InvalidDataException>());
        Assert.That(() => Merkleize(container, out UInt256 _), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Nullable_non_basic_vector_collection_without_zero_item_encoding_is_rejected()
    {
        NullableCompatibleUnionArrayPoolListVectorContainer container = new() { Items = null };

        Assert.That(() => Encode(container), Throws.InstanceOf<InvalidDataException>());
        Assert.That(() => Merkleize(container, out UInt256 _), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Merkleize_compatible_union_matches_the_selected_value_root()
    {
        CompatibleNumberUnion container = new() { Selector = CompatibleNumberUnionSelector.PreviousValue, PreviousValue = 123UL };

        Merkleize(container, out UInt256 actual);

        UInt256 expected = MerkleizeWithConverter(container.PreviousValue, UInt64SszBasicTypeConverter.Feed);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(expected));
            Assert.That(decoded.Head, Is.EqualTo(container.Head));
            Assert.That(decoded.Tail, Is.EqualTo(container.Tail));
        }
    }

    [Test]
    public void Merkleize_progressive_container_mixes_in_active_fields()
    {
        ProgressiveContainerSample container = new() { Head = 1, Tail = 2 };

        Merkleize(container, out UInt256 actual);

        UInt256 headRoot = MerkleizeWithConverter(container.Head, UInt64SszBasicTypeConverter.Feed);
        UInt256 tailRoot = MerkleizeWithConverter(container.Tail, UInt64SszBasicTypeConverter.Feed);
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
        BitArray bits = MakeSampleBits10();
        ProgressiveBitlistContainer container = new() { Bits = bits };

        byte[] encoded = Encode(container);
        Decode(encoded, out ProgressiveBitlistContainer decoded);

        Assert.That(decoded.Bits, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Bits!.Length, Is.EqualTo(bits.Length));
            Assert.That(decoded.Bits.Cast<bool>(), Is.EqualTo(bits.Cast<bool>()));
        }
    }

    [Test]
    public void Merkleize_progressive_bitlist_uses_progressive_merkleization()
    {
        BitArray bits = MakeSampleBits10();
        ProgressiveBitlistContainer container = new() { Bits = bits };

        Merkleize(container, out UInt256 actual);

        byte[] bytes = new byte[(bits.Length + 7) / 8];
        bits.CopyTo(bytes, 0);
        UInt256 expected = ProgressiveMerkleizeBytes(bytes);
        Merkle.MixIn(ref expected, bits.Length);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Shadowing_uses_derived_property_type_not_base()
    {
        ShadowDerived value = new() { A = 0UL, X = 0u };

        byte[] encoded = Encode(value);

        Assert.That(encoded.Length, Is.EqualTo(12),
            "derived uint X (4 bytes) must be used instead of base ulong X (8 bytes)");
    }

    [Test]
    public void Shadowing_preserves_field_order_from_base_declaration()
    {
        ShadowDerived value = new() { A = 0xAABBCCDDEEFF0011UL, X = 0x12345678u };

        byte[] encoded = Encode(value);

        ulong encodedA = BitConverter.ToUInt64(encoded, 0);
        uint encodedX = BitConverter.ToUInt32(encoded, 8);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encodedA, Is.EqualTo(value.A),
                "A must be at offset 0 (first field in ShadowBase)");
            Assert.That(encodedX, Is.EqualTo(value.X),
                "X must be at offset 8 (second field in ShadowBase), using derived uint type");
        }
    }

    [Test]
    public void Shadowing_encode_decode_round_trip()
    {
        ShadowDerived original = new() { A = 0xDEADBEEFCAFEBABEUL, X = 0xC0FFEE01u };

        byte[] encoded = Encode(original);
        Decode(encoded, out ShadowDerived decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.A, Is.EqualTo(original.A));
            Assert.That(decoded.X, Is.EqualTo(original.X));
        }
    }

    [Test]
    public void ReadOnlyMemory_vector_encodes_and_decodes()
    {
        ReadOnlyMemoryVectorContainer original = new() { Bytes = new byte[] { 1, 2, 3, 4 } };

        byte[] encoded = Encode(original);
        Decode(encoded, out ReadOnlyMemoryVectorContainer decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(original.Bytes.ToArray()));
            Assert.That(decoded.Bytes.ToArray(), Is.EqualTo(original.Bytes.ToArray()));
        }
    }

    [Test]
    public void Memory_vector_encodes_and_decodes()
    {
        MemoryVectorContainer original = new() { Bytes = new byte[] { 1, 2, 3, 4 } };

        byte[] encoded = Encode(original);
        Decode(encoded, out MemoryVectorContainer decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(original.Bytes.ToArray()));
            Assert.That(decoded.Bytes.ToArray(), Is.EqualTo(original.Bytes.ToArray()));
        }
    }

    [Test]
    public void Vector_converters_encode_and_decode_custom_type_and_value_hash()
    {
        ValueHash256 firstHash = new(Enumerable.Range(0, ValueHash256.MemorySize).Select(i => (byte)i).ToArray());
        ValueHash256 secondHash = new(Enumerable.Range(0, ValueHash256.MemorySize).Select(i => (byte)(255 - i)).ToArray());
        ConverterContainer original = new()
        {
            FixedBytes = new TestBytes4(0x01020304),
            FixedBytesVector = [new TestBytes4(0x05060708), new TestBytes4(0x11121314)],
            Hash = firstHash,
            HashVector = [firstHash, secondHash],
        };

        byte[] encoded = Encode(original);
        Decode(encoded, out ConverterContainer decoded);
        TestBytes4SszVectorTypeConverter.FeedCallCount = 0;
        Merkleize(original, out UInt256 _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.Length, Is.EqualTo(108));
            Assert.That(encoded.AsSpan(0, 4).ToArray(), Is.EqualTo([0x04, 0x03, 0x02, 0x01]));
            Assert.That(decoded.FixedBytes.Value, Is.EqualTo(original.FixedBytes.Value));
            Assert.That(decoded.FixedBytesVector!.Select(x => x.Value), Is.EqualTo(original.FixedBytesVector!.Select(x => x.Value)));
            Assert.That(decoded.Hash, Is.EqualTo(original.Hash));
            Assert.That(decoded.HashVector, Is.EqualTo(original.HashVector));
            Assert.That(TestBytes4SszVectorTypeConverter.FeedCallCount, Is.EqualTo(3));
        }
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
        chunk.Clear();
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
            lastChunk.Clear();
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

    private delegate void FeedItem<T>(ref Merkleizer merkleizer, T value);

    private static UInt256 MerkleizeWithConverter<T>(T value, FeedItem<T> feed)
    {
        Merkleizer merkleizer = new(0);
        feed(ref merkleizer, value);
        merkleizer.CalculateRoot(out UInt256 root);
        return root;
    }
}
