// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz.SszBasicTypeConverters;
using NUnit.Framework;

namespace Nethermind.Serialization.SszGenerator.Test;

public class EncodingTest
{
    [Test, NonParallelizable]
    public void Preset_dimensions_apply_to_nested_offsets_limits_and_roots([Values] bool alternate)
    {
        TestPreset.Alternate = alternate;
        try
        {
            int count = alternate ? 2 : 4;
            PresetContainer value = new() { Head = new() { Values = new ulong[count] }, Tail = [1, 2, 3, 4] };
            byte[] encoded = PresetContainer.Encode(value);
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(count * 8)), Is.EqualTo(count * 8 + 4));
            PresetContainer.Decode(encoded, out PresetContainer decoded);
            PresetContainer.RefReader reader = new(encoded);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Head.Values, Has.Length.EqualTo(count));
                Assert.That(decoded.Tail, Is.EqualTo(value.Tail));
                Assert.That(reader.Head.Values.Count, Is.EqualTo(count));
                Assert.That(ToArray(reader.Tail, static item => item), Is.EqualTo(value.Tail));
                PresetContainer.Merkleize(value, out UInt256 expectedRoot);
                PresetContainer.MerkleizeEncoded(encoded, out UInt256 encodedRoot);
                Assert.That(encodedRoot, Is.EqualTo(expectedRoot));
                Assert.That(WriteTree(output => PresetContainer.WriteTree(output, new(encoded))).Root, Is.EqualTo(expectedRoot));
                AssertWrites(Case("preset", value, output =>
                {
                    using PresetContainer.Writer writer = new(output, new(encoded), new(TreeOf(value)));
                    writer.CopyHead();
                    using SszListWriter<ulong> tail = writer.BeginTail();
                    tail.Copy(0, 4);
                }), chunkSize: 64);
                Assert.That(Write(output =>
                {
                    using PresetContainer.Writer writer = new(output);
                    writer.WriteHead(value.Head);
                    using SszListWriter<ulong> tail = writer.BeginTail();
                    tail.Add(value.Tail);
                }), Is.EqualTo(encoded));
            }
            UInt256 tail = new(1, 2, 3, 4);
            if (!alternate) tail = HashConcat(tail, UInt256.Zero);
            UInt256 expected = HashConcat(UInt256.Zero, HashConcat(tail, (UInt256)4));
            PresetContainer.Merkleize(value, out UInt256 root);
            Assert.That(root, Is.EqualTo(expected));
            value.Head.Values = new ulong[alternate ? 4 : 2];
            Assert.Throws<InvalidDataException>(() => PresetContainer.Encode(value));
            value.Head.Values = new ulong[count];
            value.Tail = new ulong[(alternate ? 4 : 8) + 1];
            Assert.Throws<InvalidDataException>(() => PresetContainer.Encode(value));
        }
        finally
        {
            TestPreset.Alternate = false;
        }
    }

    [Test]
    public void Container_merkleization_does_not_allocate_scratch_arrays([Values] bool progressive)
    {
        FixedC standard = new() { Fixed1 = 1, Fixed2 = 2 };
        ProgressiveContainerSample extended = new() { Head = 1, Tail = 2 };
        UInt256 expected;
        if (progressive)
        {
            MerkleizeProgressiveSpec([(UInt256)1, (UInt256)2], out expected);
            expected = MixInActiveFieldsSpec(expected, 0b00000101);
        }
        else
        {
            expected = HashConcat((UInt256)1, (UInt256)2);
        }

        UInt256 actual = default;
        for (int i = 0; i < 100; i++) CalculateRoot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) CalculateRoot();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(allocated, Is.Zero);
        }

        void CalculateRoot()
        {
            if (progressive) ProgressiveContainerSample.Merkleize(extended, out actual);
            else FixedC.Merkleize(standard, out actual);
        }
    }

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
    public void Span_built_byte_lists_match_byte_array_lists()
    {
        ByteListItself[] arrays = [new() { Bytes = [4, 5] }, new() { Bytes = [] }, new() { Bytes = [1, 2, 3] }];
        byte[] encoded = ByteListItself.Encode(arrays);

        SpanBuiltByteListItself.Decode(encoded, out SpanBuiltByteListItself[] decoded);
        SpanBuiltByteStructListItself.Decode(encoded, out SpanBuiltByteStructListItself[] decodedStructs);
        SpanBuiltByteListItselfNamedIndex.Decode(encoded, out SpanBuiltByteListItselfNamedIndex[] decodedNamedIndex);
        ByteListItself.Merkleize(arrays[2], out UInt256 expectedRoot);
        SpanBuiltByteListItself.Merkleize(decoded[2], out UInt256 root);
        SpanBuiltByteStructListItself.Merkleize(decodedStructs[2], out UInt256 structRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Has.Length.EqualTo(3));
            Assert.That(decoded[0].Bytes!.AsSpan().ToArray(), Is.EqualTo(new byte[] { 4, 5 }));
            Assert.That(decoded[1].Bytes!.AsSpan().ToArray(), Is.Empty);
            Assert.That(decoded[2].Bytes!.AsSpan().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(SpanBuiltByteListItself.Encode(decoded), Is.EqualTo(encoded));
            Assert.That(root, Is.EqualTo(expectedRoot));
            Assert.That(decodedStructs[0].Bytes.AsSpan().ToArray(), Is.EqualTo(new byte[] { 4, 5 }));
            Assert.That(decodedStructs[2].Bytes.AsSpan().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(SpanBuiltByteStructListItself.Encode(decodedStructs), Is.EqualTo(encoded));
            Assert.That(structRoot, Is.EqualTo(expectedRoot));
            Assert.That(SpanBuiltByteListItselfNamedIndex.Encode(decodedNamedIndex), Is.EqualTo(encoded));
        }
    }

    [Test]
    public void Decode_collection_itself_runs_the_item_parameterless_constructor()
    {
        byte[] encoded = ByteListItself.Encode([new() { Bytes = [4, 5] }, new() { Bytes = [1, 2, 3] }]);

        SpanBuiltByteStructListItselfWithConstructor.Decode(encoded, out SpanBuiltByteStructListItselfWithConstructor[] decoded);

        Assert.That(decoded.Select(static item => item.Marker), Is.EqualTo(new[] { 7, 7 }));
    }

    [Test]
    public void Decode_collection_itself_byte_lists_enforces_item_limit([Values] bool spanBuilt)
    {
        byte[] encoded = [8, 0, 0, 0, 12, 0, 0, 0, 1, 2, 3, 4];

        Assert.That(
            () =>
            {
                if (spanBuilt) SpanBuiltByteListItself.Decode(encoded, out SpanBuiltByteListItself[] _);
                else ByteListItself.Decode(encoded, out ByteListItself[] _);
            },
            Throws.InstanceOf<InvalidDataException>().With.Message.EndsWith("expected at most 3 elements but found 4."));
    }

    [TestCase(new byte[] { 8, 0, 0, 0, 4, 0, 0, 0 }, "offsets are out of order (4 < 8).")]
    [TestCase(new byte[] { 8, 0, 0, 0, 9, 0, 0, 0 }, "offset 9 exceeds the input length 8.")]
    public void Decode_collection_itself_byte_lists_rejects_bad_offsets(byte[] encoded, string reason) =>
        Assert.That(() => ByteListItself.Decode(encoded, out ByteListItself[] _),
            Throws.InstanceOf<InvalidDataException>().With.Message.EndsWith(reason));

    [Test]
    public void Decode_collection_itself_byte_lists_supports_class_items()
    {
        ByteListClassItself[] original = [new() { Bytes = [] }, new() { Bytes = [1, 2, 3] }];

        byte[] encoded = ByteListClassItself.Encode(original);
        ByteListClassItself.Decode(encoded, out ByteListClassItself[] decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Has.Length.EqualTo(2));
            Assert.That(decoded[0].Bytes, Is.Empty);
            Assert.That(decoded[1].Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }

    [Test]
    public void Encode_fixed_size_class_collection_clears_null_items()
    {
        StaticClassCollectionItem[] items = [new() { Value = 1 }, null!, new() { Value = 2 }];

        byte[] encoded = StaticClassCollectionItem.Encode(items);

        Assert.That(encoded, Is.EqualTo(new byte[]
        {
            1, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            2, 0, 0, 0, 0, 0, 0, 0,
        }));
    }

    [Test]
    public void Encode_nonnullable_static_class_null_clears_output()
    {
        NonNullableStaticClassContainer container = new() { Child = null! };
        byte[] reusedBuffer = Enumerable.Repeat((byte)0xFF, sizeof(ulong)).ToArray();

        NonNullableStaticClassContainer.Encode(reusedBuffer, container);

        Assert.That(reusedBuffer, Is.EqualTo(new byte[sizeof(ulong)]));
    }

    [Test]
    public void Decode_collection_itself_byte_lists_supports_list_destinations()
    {
        ByteListListItself[] original = [new() { Bytes = [] }, new() { Bytes = [1, 2, 3] }];

        byte[] encoded = ByteListListItself.Encode(original);
        ByteListListItself.Decode(encoded, out ByteListListItself[] decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Has.Length.EqualTo(2));
            Assert.That(decoded[0].Bytes, Is.Empty);
            Assert.That(decoded[1].Bytes, Is.EqualTo(new List<byte> { 1, 2, 3 }));
        }
    }

    [Test]
    public void Decode_collection_itself_byte_vectors()
    {
        ByteVectorItself[] original = [new() { Bytes = [1, 2, 3] }, new() { Bytes = [4, 5, 6] }];

        byte[] encoded = ByteVectorItself.Encode(original);
        ByteVectorItself.Decode(encoded, out ByteVectorItself[] decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Has.Length.EqualTo(2));
            Assert.That(decoded[0].Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(decoded[1].Bytes, Is.EqualTo(new byte[] { 4, 5, 6 }));
        }
    }

    [Test]
    public void Decode_collection_itself_byte_vectors_rejects_wrong_item_length()
    {
        byte[] encoded = [1, 2, 3, 4, 5];

        Assert.That(() => ByteVectorItself.Decode(encoded, out ByteVectorItself[] _), Throws.InstanceOf<InvalidDataException>());
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
                Assert.That(decoded.Items.AsSpan(), Is.SequenceEqualTo(expectedItems));
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

    /// <summary>
    /// A list of fixed-size byte vectors encodes identically whether each item is a wrapper
    /// container holding a <c>byte[]</c> or a converter-backed value type, so replacing the
    /// former with the latter to drop the per-item allocation is a wire-compatible refactor.
    /// </summary>
    [Test]
    public void Converter_item_list_encodes_identically_to_wrapped_byte_vector_list([Values(0, 1, 3)] int itemCount)
    {
        const int itemLength = TestBytes48SszVectorTypeConverter.Length;

        byte[][] items = new byte[itemCount][];
        for (int i = 0; i < itemCount; i++)
        {
            items[i] = new byte[itemLength];
            for (int j = 0; j < itemLength; j++) items[i][j] = (byte)((i * itemLength + j) & 0xFF);
        }

        WrappedByteVectorItem[] wrapped = new WrappedByteVectorItem[itemCount];
        TestBytes48[] converted = new TestBytes48[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            wrapped[i] = new WrappedByteVectorItem { Bytes = items[i] };
            converted[i] = new TestBytes48(items[i]);
        }

        byte[] wrappedEncoded = Encode(new WrappedItemListContainer { Items = wrapped });
        byte[] convertedEncoded = Encode(new ConverterItemListContainer { Items = converted });

        Decode(convertedEncoded, out ConverterItemListContainer decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(convertedEncoded, Is.EqualTo(wrappedEncoded), "converter-backed items must be byte-identical to the wrapper-container encoding");
            // Fixed-size items: after the container's 4-byte list offset the payload is the raw
            // concatenation, with no per-item framing.
            Assert.That(convertedEncoded, Has.Length.EqualTo(4 + itemCount * itemLength));
            Assert.That(decoded.Items, Has.Length.EqualTo(itemCount));
            for (int i = 0; i < itemCount; i++)
                Assert.That(decoded.Items![i].Data, Is.SequenceEqualTo(items[i]), $"item {i} must round-trip exactly");
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
    public void Merkleize_progressive_byte_list_uses_progressive_merkleization([Values(0, 1, 31, 32, 33, 160, 161, 700)] int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1);
        ProgressiveByteListContainer container = new() { Bytes = bytes };

        Merkleize(container, out UInt256 actual);

        UInt256 expected = ProgressiveMerkleizeBytes(bytes);
        Merkle.MixIn(ref expected, bytes.Length);

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
            Assert.That(decoded.Bytes, Is.SequenceEqualTo(original.Bytes));
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
            Assert.That(decoded.Bytes, Is.SequenceEqualTo(original.Bytes));
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
            Assert.That(encoded.AsSpan(0, 4), Is.SequenceEqualTo(new byte[] { 0x04, 0x03, 0x02, 0x01 }));
            Assert.That(decoded.FixedBytes.Value, Is.EqualTo(original.FixedBytes.Value));
            Assert.That(decoded.FixedBytesVector!.Select(x => x.Value), Is.EqualTo(original.FixedBytesVector!.Select(x => x.Value)));
            Assert.That(decoded.Hash, Is.EqualTo(original.Hash));
            Assert.That(decoded.HashVector, Is.EqualTo(original.HashVector));
            Assert.That(TestBytes4SszVectorTypeConverter.FeedCallCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void RefReader_reads_every_member_kind()
    {
        ComplexStruct value = MakeComplexStruct();
        byte[] encoded = Encode(value);
        byte[] bitVector = new byte[2];
        value.BitVec!.CopyTo(bitVector, 0);

        ComplexStruct.RefReader reader = new(encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Test1, Is.EqualTo(value.Test1));
            Assert.That(ToArray(reader.Test2, static item => item), Is.EqualTo(value.Test2));
            Assert.That(ToArray(reader.Test3, static item => item), Is.EqualTo(value.Test3));
            Assert.That(reader.FixedC.Fixed2, Is.EqualTo(value.FixedC.Fixed2));
            Assert.That(reader.VariableC.Fixed1, Is.EqualTo(value.VariableC.Fixed1));
            Assert.That(ToArray(reader.VariableC.Fixed2, static item => item), Is.EqualTo(value.VariableC.Fixed2));
            Assert.That(ToArray(reader.FixedCVec, static item => item.Fixed2), Is.EqualTo(value.FixedCVec.Select(static item => item.Fixed2)));
            Assert.That(ToArray(reader.FixedCList, static item => item.Fixed2), Is.EqualTo(value.FixedCList.Select(static item => item.Fixed2)));
            Assert.That(ToArray(reader.VariableCVec, static item => ToArray(item.Fixed2, static x => x)), Is.EqualTo(value.VariableCVec.Select(static item => item.Fixed2)));
            Assert.That(ToArray(reader.VariableCList, static item => ToArray(item.Fixed2, static x => x)), Is.EqualTo(value.VariableCList.Select(static item => item.Fixed2)));
            Assert.That(reader.Test2Union.Selector, Is.EqualTo(value.Test2Union.Selector));
            Assert.That(reader.Test2Union.PreviousValue, Is.EqualTo(value.Test2Union.PreviousValue));
            Assert.That(ToArray(reader.Test2UnionVec, static item => item.CurrentValue), Is.EqualTo(value.Test2UnionVec!.Select(static item => item.CurrentValue)));
            Assert.That(ToArray(reader.Test2UnionList, static item => item.CurrentValue), Is.EqualTo(value.Test2UnionList!.Select(static item => item.CurrentValue)));
            Assert.That(reader.BitVec.ToArray(), Is.EqualTo(bitVector));
            // Bits 0 and 2 of a 4-bit list, then the sentinel.
            Assert.That(reader.BitList2.ToArray(), Is.EqualTo(Bytes.FromHexString("15")));
        }
    }

    [Test]
    public void RefReader_reads_leaves_as_values_or_bytes()
    {
        LeafContainer value = MakeLeafContainer();
        byte[] encoded = Encode(value);
        LeafContainer.RefReader reader = new(encoded);

        ValueHash256 hash = reader.Hash;
        ref readonly ValueAddress address = ref reader.Address;
        ReadOnlySpan<ValueAddress> addresses = reader.Addresses;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hash, Is.EqualTo(value.Hash.ValueHash256));
            Assert.That(address.ToAddress(), Is.EqualTo(value.Address));
            Assert.That(Unsafe.AreSame(in encoded[ValueHash256.MemorySize], in Unsafe.As<ValueAddress, byte>(ref Unsafe.AsRef(in address))), Is.True, "address is not copied");
            Assert.That(MemoryMarshal.AsBytes(addresses).Overlaps(encoded), Is.True, "addresses are not copied");
            Assert.That(addresses.ToArray().Select(static item => item.ToAddress()), Is.EqualTo(value.Addresses));
            Assert.That(reader.AddressVector.ToArray().Select(static item => item.ToAddress()), Is.EqualTo(value.AddressVector));
            Assert.That(reader.Bloom.ToArray(), Is.EqualTo(value.Bloom.Bytes.ToArray()));
            Assert.That(reader.Enum, Is.EqualTo(value.Enum));
            Assert.That(reader.Reference.ToArray(), Is.EqualTo(value.Reference.Data.ToArray()));
            Assert.That(ToArray(reader.Hashes, static item => item), Is.EqualTo(value.Hashes.Select(static item => item.ValueHash256)));
            Assert.That(ToArray(reader.Blooms, static item => item.ToArray()), Is.EqualTo(value.Blooms.Select(static item => item.Bytes.ToArray())));
            Assert.That(ToArray(reader.Enums, static item => item), Is.EqualTo(value.Enums));
            Assert.That(reader.Bytes.ToArray(), Is.EqualTo(value.Bytes));
        }
    }

    [Test]
    public void RefReader_reads_every_container_shape()
    {
        byte[] listItself = ByteListItself.Encode([new() { Bytes = [4, 5] }, new() { Bytes = [] }]);
        byte[] progressiveBits = Encode(new ProgressiveBitlistContainer { Bits = MakeSampleBits10() });
        byte[] nested = Encode(new NestedProgressiveListContainer
        {
            Items = [new() { Items = [new() { Fixed1 = 1, Fixed2 = [2, 3] }] }, new() { Items = [] }],
        });
        byte[] generic = Encode(new GenericContainerOwner { Inner = new() { Head = 5, Value = new() { Fixed1 = 6, Fixed2 = [7] } } });

        ProgressiveContainerSample.RefReader progressive = new(Encode(new ProgressiveContainerSample { Head = 1, Tail = 2 }));
        ShadowDerived.RefReader derived = new(Encode(new ShadowDerived { A = 3, X = 4 }));
        GenericContainer<VariableC>.RefReader inner = new GenericContainerOwner.RefReader(generic).Inner;
        VariableC.RefReader typeParameter = new(inner.Value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((progressive.Head, progressive.Tail), Is.EqualTo((1UL, 2UL)));
            Assert.That((derived.A, derived.X), Is.EqualTo((3UL, 4U)));
            Assert.That((inner.Head, typeParameter.Fixed1), Is.EqualTo((5UL, 6UL)));
            Assert.That(ToArray(typeParameter.Fixed2, static item => item), Is.EqualTo([7UL]));
            Assert.That(ToArray(new SszListReader<ByteListItself.RefReader>(listItself, 0, static item => new(item)), static item => item.Bytes.ToArray()),
                Is.EqualTo(new byte[][] { [4, 5], [] }));
            Assert.That(new ProgressiveBitlistContainer.RefReader(progressiveBits).Bits.ToArray(), Is.EqualTo(progressiveBits[4..]));
            Assert.That(ToArray(new NestedProgressiveListContainer.RefReader(nested).Items, static item => ToArray(item.Items, static x => ToArray(x.Fixed2, static y => y))),
                Is.EqualTo(new ulong[][][] { [[2, 3]], [] }));
        }
    }

    [Test]
    public void RefReader_does_not_allocate()
    {
        byte[] encoded = Encode(MakeComplexStruct());

        ulong sum = 0;
        for (int i = 0; i < 100; i++) Read();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Read();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sum, Is.Not.Zero);
            Assert.That(allocated, Is.Zero);
        }

        void Read()
        {
            ComplexStruct.RefReader reader = new(encoded);
            sum += reader.Test1 + reader.FixedC.Fixed1 + reader.Test2Union.PreviousValue + reader.BitVec[0] + reader.BitList2[0];
            foreach (ulong item in reader.Test3) sum += item;
            foreach (FixedC.RefReader item in reader.FixedCList) sum += item.Fixed1;
            foreach (VariableC.RefReader item in reader.VariableCList)
            {
                foreach (ulong value in item.Fixed2) sum += value;
            }
        }
    }

    [TestCase(-1)]
    [TestCase(2)]
    public void RefReader_list_rejects_index_out_of_range(int index) =>
        Assert.That(() => new SingleListContainer.RefReader(Encode(new SingleListContainer { Items = [1, 2] })).Items[index],
            Throws.InstanceOf<ArgumentOutOfRangeException>());

    [TestCaseSource(nameof(MerkleizeEncodedCases))]
    public void MerkleizeEncoded_matches_Merkleize(byte[] encoded, UInt256 expected, SszHasher merkleizeEncoded)
    {
        merkleizeEncoded(encoded, out UInt256 root);
        Assert.That(root, Is.EqualTo(expected));
    }

    [TestCase(false, 7)]
    [TestCase(false, 40)]
    [TestCase(true, 0)]
    public void Tree_of_any_leaf_count_matches_Merkleize(bool progressive, int depth)
    {
        SszTreeItems items = ChunkItems(progressive, depth, isList: false);
        for (int count = 0; count <= 90; count++)
        {
            UInt256[] leaves = MakeLeaves(count);
            (byte[] tree, UInt256 root) = WriteTree(output => AddLeaves(output, items, leaves));
            UInt256 expectedRoot;
            if (progressive) Merkle.MerkleizeProgressive(out expectedRoot, leaves);
            else Merkle.Merkleize(out expectedRoot, leaves, 1UL << depth);
            SszTreeReader reader = new(tree, 0, items.Shape);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(expectedRoot), $"root of {count} leaves");
                Assert.That(reader.LeafCount, Is.EqualTo(count));
                Assert.That(tree, Has.Length.EqualTo(items.Shape.Length(count)));
                for (int i = 0; i < count; i++) Assert.That(reader[i], Is.EqualTo(leaves[i]), $"leaf {i} of {count}");
            }
        }
    }

    [Test]
    public void Tree_nodes_are_in_post_order()
    {
        UInt256[] l = MakeLeaves(5);
        UInt256 a = HashConcat(l[0], l[1]);
        UInt256 b = HashConcat(l[2], l[3]);
        (byte[] five, _) = WriteTree(output => AddLeaves(output, ChunkItems(progressive: false, 40, isList: true), l));
        (byte[] full, UInt256 fullRoot) = WriteTree(output => AddLeaves(output, ChunkItems(progressive: false, 2, isList: false), l[..4]));
        (byte[] progressive, _) = WriteTree(output => AddLeaves(output, ChunkItems(progressive: true, 0, isList: true), l));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(five, Is.EqualTo(EncodeNodes(5, l[0], l[1], a, l[2], l[3], b, HashConcat(a, b), l[4])));
            Assert.That(full, Is.EqualTo(EncodeNodes(4, l[0], l[1], a, l[2], l[3], b)), "does not store its own hash");
            Assert.That(fullRoot, Is.EqualTo(HashConcat(a, b)));
            Assert.That(progressive, Is.EqualTo(EncodeNodes(5, l[0], l[1], l[2], HashConcat(l[1], l[2]), l[3], l[4], HashConcat(l[3], l[4]),
                HashConcat(HashConcat(l[1], l[2]), HashConcat(l[3], l[4])))));
        }

        static byte[] EncodeNodes(int leafCount, params ReadOnlySpan<UInt256> nodes) =>
            [.. BitConverter.GetBytes(leafCount), .. MemoryMarshal.AsBytes(nodes)];
    }

    [TestCase(false, 6)]
    [TestCase(true, 0)]
    public void Tree_of_copied_runs_matches_a_new_tree(bool progressive, int depth)
    {
        SszTreeItems items = ChunkItems(progressive, depth, isList: true);
        UInt256[] source = MakeLeaves(30);
        UInt256[] added = MakeLeaves(40)[30..];
        byte[] encoded = MemoryMarshal.AsBytes(source.AsSpan()).ToArray();
        (byte[] sourceTree, _) = WriteTree(output => AddLeaves(output, items, source));

        for (int prefix = 0; prefix <= 9; prefix++)
        {
            for (int start = 0; start <= source.Length; start++)
            {
                for (int count = 0; count <= source.Length - start; count++)
                {
                    UInt256[] expected = [.. added[..prefix], .. source[start..(start + count)], added[9]];
                    (byte[] tree, UInt256 root) = WriteTree(output => AddLeaves(output, items, expected));
                    (byte[] copiedTree, UInt256 copiedRoot) = WriteTree(output =>
                    {
                        using SszListTreeWriter writer = new(output, items, new SszTreeReader(sourceTree, 0, items.Shape));
                        writer.Add(MemoryMarshal.AsBytes(added.AsSpan(0, prefix)));
                        writer.Copy(start, count, encoded);
                        writer.Add(MemoryMarshal.AsBytes(added.AsSpan(9)));
                    });
                    Assert.That((copiedRoot, copiedTree.AsSpan().SequenceEqual(tree)), Is.EqualTo((root, true)), $"{prefix} leaves, then [{start}, {start + count})");
                }
            }
        }
    }

    [Test]
    public void Tree_of_packed_items_matches_a_new_tree([Values] bool progressive, [Values] bool withSourceTree, [Range(0, 9)] int seed)
    {
        Random random = new(seed);
        SszTreeItems items = new(new SszTreeShape(progressive ? 0 : 38, progressive, 0), sizeof(ulong), true, null, null, default);
        ulong[] source = Enumerable.Range(0, random.Next(200)).Select(static i => (ulong)i + 1).ToArray();
        byte[] encoded = MemoryMarshal.AsBytes(source.AsSpan()).ToArray();
        (byte[] sourceTree, _) = WriteTree(output => AddAll(output, items, encoded));

        List<ulong> expected = [];
        (byte[] tree, UInt256 root) = WriteTree(output =>
        {
            using SszListTreeWriter writer = new(output, items, withSourceTree ? new SszTreeReader(sourceTree, 0, items.Shape) : default);
            for (int i = random.Next(12); i >= 0; i--)
            {
                int start = random.Next(source.Length + 1);
                int count = random.Next(source.Length - start + 1);
                writer.Copy(start, count, encoded);
                expected.AddRange(source.AsSpan(start, count));
                ulong[] added = new ulong[random.Next(6)];
                writer.Add(MemoryMarshal.AsBytes(added.AsSpan()));
                expected.AddRange(added);
            }
        });

        (byte[] newTree, UInt256 newRoot) = WriteTree(output => AddAll(output, items, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(expected)).ToArray()));
        UInt256 expectedRoot;
        if (progressive) Merkle.MerkleizeProgressive(out expectedRoot, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(expected)));
        else Merkle.Merkleize(out expectedRoot, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(expected)), 1UL << 38);
        Merkle.MixIn(ref expectedRoot, expected.Count);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newRoot, Is.EqualTo(expectedRoot));
            Assert.That(root, Is.EqualTo(expectedRoot));
            Assert.That(tree, Is.EqualTo(newTree));
        }

        static void AddAll(SszTreeWriter output, SszTreeItems items, byte[] encoded)
        {
            using SszListTreeWriter writer = new(output, items, default);
            writer.AddAll(encoded);
        }
    }

    // A wrong hash in the source can only end up in the new tree if it is copied.
    [Test]
    public void Tree_copies_the_nodes_of_unchanged_leaves([Values] bool copyRun)
    {
        SszTreeItems items = ChunkItems(progressive: false, 40, isList: true);
        UInt256[] leaves = MakeLeaves(8);
        (byte[] source, _) = WriteTree(output => AddLeaves(output, items, leaves));
        const int parentOfFirstTwo = 2;
        const int parentOfLastTwo = 12;
        Span<UInt256> sourceNodes = MemoryMarshal.Cast<byte, UInt256>(source.AsSpan(sizeof(int)));
        sourceNodes[parentOfFirstTwo] = sourceNodes[parentOfLastTwo] = 1234;

        (byte[] tree, _) = WriteTree(output =>
        {
            using SszListTreeWriter writer = new(output, items, new SszTreeReader(source, 0, items.Shape));
            if (copyRun) writer.Copy(0, 7, MemoryMarshal.AsBytes(leaves.AsSpan()));
            else writer.Add(MemoryMarshal.AsBytes(leaves.AsSpan(0, 7)));
            writer.Add(MemoryMarshal.AsBytes(MakeLeaves(9).AsSpan(8)));
        });

        UInt256[] nodes = MemoryMarshal.Cast<byte, UInt256>(tree.AsSpan(sizeof(int))).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes[parentOfFirstTwo], Is.EqualTo((UInt256)1234), "parent of unchanged leaves");
            Assert.That(nodes[parentOfLastTwo], Is.Not.EqualTo((UInt256)1234), "parent of a changed leaf");
        }
    }

    [Test]
    public void Tree_survives_random_updates([Range(0, 9)] int seed, [Values] bool withTree)
    {
        Random random = new(seed);
        List<ulong> basic = [];
        List<FixedC> composite = [];
        (byte[] Ssz, byte[] Tree) basicState = ([], TreeOf(new HugeLimitBasicList { Items = [] }));
        (byte[] Ssz, byte[] Tree) compositeState = ([], TreeOf(new HugeLimitCompositeList { Items = [] }));

        for (int round = 0; round < 12; round++)
        {
            (int start, int count)[] runs = [.. Enumerable.Range(0, random.Next(4)).Select(_ =>
            {
                int start = random.Next(basic.Count + 1);
                return (start, random.Next(basic.Count - start + 1));
            })];
            ulong[] added = [.. Enumerable.Range(0, random.Next(40)).Select(_ => (ulong)random.Next())];

            basicState = Update(basicState, (output, ssz, tree) =>
            {
                using HugeLimitBasicList.Writer writer = new(output, new(ssz), tree);
                using SszListWriter<ulong> items = writer.BeginItems();
                foreach ((int start, int count) in runs) items.Copy(start, count);
                items.Add(added);
            });
            compositeState = UpdateComposite(compositeState, (output, ssz, tree) =>
            {
                using HugeLimitCompositeList.Writer writer = new(output, new(ssz), tree);
                using SszListWriter<FixedC> items = writer.BeginItems();
                foreach ((int start, int count) in runs) items.Copy(start, count);
                foreach (ulong item in added) items.Add(new FixedC { Fixed1 = item });
            });

            basic = [.. runs.SelectMany(run => basic.GetRange(run.start, run.count)), .. added];
            composite = [.. runs.SelectMany(run => composite.GetRange(run.start, run.count)), .. added.Select(static item => new FixedC { Fixed1 = item })];
            HugeLimitBasicList expectedBasic = new() { Items = [.. basic] };
            HugeLimitCompositeList expectedComposite = new() { Items = [.. composite] };
            using (Assert.EnterMultipleScope())
            {
                Assert.That(basicState.Ssz, Is.EqualTo(Encode(expectedBasic)));
                Assert.That(basicState.Tree, Is.EqualTo(TreeOf(expectedBasic)));
                Assert.That(compositeState.Ssz, Is.EqualTo(Encode(expectedComposite)));
                Assert.That(compositeState.Tree, Is.EqualTo(TreeOf(expectedComposite)));
            }
        }

        (byte[], byte[]) Update((byte[] Ssz, byte[] Tree) state, UpdateBasic update)
        {
            using SszTreeWriter trees = new(64);
            using SszWriter output = new(64) { Trees = trees };
            update(output, state.Ssz, withTree ? new(new SszTreeReader(state.Tree, 0, HugeLimitBasicList.RefTreeReader.TreeShape)) : default);
            return (output.ToSequence().ToArray(), trees.ToSequence().ToArray());
        }

        (byte[], byte[]) UpdateComposite((byte[] Ssz, byte[] Tree) state, UpdateCompositeList update)
        {
            using SszTreeWriter trees = new(64);
            using SszWriter output = new(64) { Trees = trees };
            update(output, state.Ssz, withTree ? new(new SszTreeReader(state.Tree, 0, HugeLimitCompositeList.RefTreeReader.TreeShape)) : default);
            return (output.ToSequence().ToArray(), trees.ToSequence().ToArray());
        }
    }

    private delegate void UpdateBasic(SszWriter output, byte[] ssz, HugeLimitBasicList.RefTreeReader tree);

    private delegate void UpdateCompositeList(SszWriter output, byte[] ssz, HugeLimitCompositeList.RefTreeReader tree);

    // A wrong hash in the source can only end up in the new tree if it is copied.
    [Test]
    public void Tree_of_an_unchanged_member_is_copied()
    {
        ComplexStruct value = MakeComplexStruct();
        byte[] encoded = Encode(value);
        byte[] source = TreeOf(value);
        UInt256 leaf = new(17, 18, 19);
        int position = source.AsSpan().IndexOf(MemoryMarshal.AsBytes(new ReadOnlySpan<UInt256>(in leaf)));
        source.AsSpan(position, 32).Fill(0xAB);

        (byte[] tree, _) = WriteTree(output =>
        {
            using ComplexStruct.TreeWriter writer = new(output, new ComplexStruct.RefTreeReader(source));
            WriteChangedTree(writer, value, encoded);
        });

        Assert.That(tree.AsSpan().IndexOf(source.AsSpan(position, 32)), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void TreeWriter_updates_a_tree()
    {
        ComplexStruct value = MakeComplexStruct();
        byte[] encoded = Encode(value);
        byte[] source = TreeOf(value);
        ComplexStruct changed = MakeComplexStruct();
        changed.Test1 = 100;
        changed.VariableCList = [value.VariableCList[2], new VariableC { Fixed1 = 101, Fixed2 = value.VariableCList[0].Fixed2 }, new VariableC { Fixed1 = 102, Fixed2 = [103] }];
        Merkleize(changed, out UInt256 expectedRoot);

        (byte[] tree, UInt256 root) = WriteTree(output =>
        {
            using ComplexStruct.TreeWriter writer = new(output, new ComplexStruct.RefTreeReader(source));
            WriteChangedTree(writer, value, encoded);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expectedRoot));
            Assert.That(tree, Is.EqualTo(TreeOf(changed)));
        }
    }

    private static void WriteChangedTree(ComplexStruct.TreeWriter writer, ComplexStruct value, byte[] encoded)
    {
        writer.WriteTest1(BitConverter.GetBytes(100UL));
        writer.CopyTest2();
        writer.CopyFixedC();
        writer.CopyFixedCVec();
        writer.CopyBitVec();
        writer.CopyTest3();
        writer.CopyVariableC();
        writer.CopyFixedCList();
        writer.CopyVariableCVec();
        using (SszVariableListTreeWriter<VariableC.TreeWriter> items = writer.BeginVariableCList())
        {
            byte[] sourceItems = VariableC.Encode(value.VariableCList);
            items.Copy(2, 1, sourceItems);
            using (VariableC.TreeWriter item = items.BeginItem(0))
            {
                item.WriteFixed1(BitConverter.GetBytes(101UL));
                item.CopyFixed2();
            }
            using (VariableC.TreeWriter item = items.BeginItem())
            {
                item.WriteFixed1(BitConverter.GetBytes(102UL));
                using SszListTreeWriter numbers = item.BeginFixed2();
                numbers.Add(BitConverter.GetBytes(103UL));
            }
        }
        writer.CopyTest2Union();
        writer.CopyTest2UnionVec();
        writer.CopyTest2UnionList();
        writer.CopyBitList2();
    }

    [TestCaseSource(nameof(TreeMisuseCases))]
    public void TreeWriter_rejects_misuse(Action<SszTreeWriter> misuse) =>
        Assert.That(() => WriteTree(misuse), Throws.InvalidOperationException);

    [TestCaseSource(nameof(TreeCases))]
    public void WriteTree_matches_Merkleize(byte[] encoded, UInt256 expected, Action<SszTreeWriter, byte[]> writeTree, int chunkSize)
    {
        using SszTreeWriter output = new(chunkSize);
        writeTree(output, encoded);
        Assert.That(output.Root, Is.EqualTo(expected));
    }

    [Test]
    public void RefTreeReader_reads_every_tree()
    {
        ComplexStruct value = MakeComplexStruct();
        (byte[] trees, _) = WriteTree(output => ComplexStruct.WriteTree(output, new(Encode(value))));
        ComplexStruct.RefTreeReader reader = new(trees);
        NestedProgressiveListContainer nested = new() { Items = [new() { Items = [new() { Fixed1 = 1, Fixed2 = [2, 3] }] }, new() { Items = [] }] };
        (byte[] nestedTrees, _) = WriteTree(output => NestedProgressiveListContainer.WriteTree(output, new(Encode(nested))));
        NestedProgressiveListContainer.RefTreeReader nestedReader = new(nestedTrees);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Test1Root, Is.EqualTo((UInt256)value.Test1));
            Assert.That(reader.FixedCRoot, Is.EqualTo(Root(value.FixedC)));
            Assert.That(reader.Test2.LeafCount, Is.EqualTo(3), "10 numbers");
            Assert.That(reader.Test3[0], Is.EqualTo(new UInt256(12, 13)));
            Assert.That(reader.FixedCList[0], Is.EqualTo(Root(value.FixedCList[0])));
            Assert.That(reader.VariableCRoot, Is.EqualTo(Root(value.VariableC)));
            Assert.That(reader.VariableC.Fixed1Root, Is.EqualTo((UInt256)value.VariableC.Fixed1));
            Assert.That(reader.VariableC.Fixed2[0], Is.EqualTo(new UInt256(17, 18, 19)));
            Assert.That(reader.VariableCList.Count, Is.EqualTo(value.VariableCList.Length));
            Assert.That(reader.VariableCList.Root(0), Is.EqualTo(Root(value.VariableCList[0])));
            Assert.That(reader.VariableCList[0].Fixed2[0], Is.EqualTo(new UInt256(22, 23)));
            Assert.That(reader.VariableCList[1].Fixed2.LeafCount, Is.Zero);
            Assert.That(reader.VariableCList[2].Fixed2[0], Is.EqualTo((UInt256)24));
            Assert.That(reader.Test2UnionVec[3], Is.EqualTo(Root(value.Test2UnionVec![3])));
            Assert.That(nestedReader.Items[0].Items[0].Fixed2[0], Is.EqualTo(new UInt256(2, 3)));
            Assert.That(nestedReader.Items[1].Items.Count, Is.Zero);
        }

        static UInt256 Root<T>(T value) where T : ISszCodec<T>
        {
            Merkleize(value, out UInt256 root);
            return root;
        }
    }

    [Test]
    public void Writer_matches_Encode([ValueSource(nameof(WriterCases))] WriterCase testCase, [Values(1, 64, 65536)] int chunkSize) =>
        AssertWrites(testCase, chunkSize);

    [Test]
    public void Writer_copies_unchanged_parts([ValueSource(nameof(WriterCopyCases))] WriterCase testCase, [Values(1, 64, 65536)] int chunkSize) =>
        AssertWrites(testCase, chunkSize);

    [TestCase(-1, 1)]
    [TestCase(0, -1)]
    [TestCase(0, 3)]
    [TestCase(2, 1)]
    public void Writer_rejects_copying_items_out_of_range(int start, int count)
    {
        byte[] fixedItems = Encode(new SingleListContainer { Items = [1, 2] });
        byte[] variableItems = ByteListItself.Encode([new() { Bytes = [1] }, new() { Bytes = [2] }]);
        byte[] vectorItems = new byte[32];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => Write(output => new SingleListContainer.Writer(output, new(fixedItems)).BeginItems().Copy(start, count)),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => Write(output => new FixedVectorContainer.Writer(output, new(vectorItems)).BeginItems().Copy(start, count)),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => Write(output => new SszVariableListWriter<ByteListItself.Writer>(output, 4, OpenByteList, variableItems, default).Copy(start, count)),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        }
    }

    [TestCase(-1)]
    [TestCase(3)]
    public void Writer_rejects_popping_items_out_of_range(int popCount)
    {
        byte[] encoded = Encode(new SingleListContainer { Items = [1, 2] });
        Assert.That(() => Write(output => new SingleListContainer.Writer(output, new(encoded)).PopAndPushItems(popCount, [])),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [TestCaseSource(nameof(WriterMisuseCases))]
    public void Writer_rejects_misuse(Action<SszWriter> misuse) =>
        Assert.That(() => Write(misuse), Throws.InvalidOperationException);

    [Test]
    public void SszWriter_keeps_reserved_spans_writable([Values(1, 64, 65536)] int chunkSize)
    {
        using SszWriter output = new(chunkSize);
        Span<byte> first = output.Reserve(3);
        Span<byte> oversized = output.Reserve(100_000);
        Span<byte> last = output.Reserve(2);
        last.Fill(3);
        oversized.Fill(2);
        first.Fill(1);

        byte[] expected = [.. Enumerable.Repeat((byte)1, 3), .. Enumerable.Repeat((byte)2, 100_000), .. Enumerable.Repeat((byte)3, 2)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output.Length, Is.EqualTo(expected.Length));
            Assert.That(output.ToSequence().ToArray(), Is.EqualTo(expected));
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

    private static ComplexStruct MakeComplexStruct() => new()
    {
        Test1 = 11,
        Test2 = Enumerable.Range(0, 10).Select(static i => (ulong)i).ToArray(),
        Test3 = [12, 13],
        FixedC = new FixedC { Fixed1 = 14, Fixed2 = 15 },
        VariableC = new VariableC { Fixed1 = 16, Fixed2 = [17, 18, 19] },
        FixedCVec = Enumerable.Range(0, 10).Select(static i => new FixedC { Fixed2 = (ulong)i }).ToArray(),
        FixedCList = [new FixedC { Fixed1 = 20, Fixed2 = 21 }],
        VariableCVec = Enumerable.Range(0, 10).Select(static i => new VariableC { Fixed2 = [(ulong)i] }).ToArray(),
        VariableCList = [new VariableC { Fixed2 = [22, 23] }, new VariableC { Fixed2 = [] }, new VariableC { Fixed2 = [24] }],
        Test2Union = new CompatibleNumberUnion { Selector = CompatibleNumberUnionSelector.PreviousValue, PreviousValue = 25 },
        Test2UnionVec = Enumerable.Range(0, 10).Select(MakeCurrentValue).ToArray(),
        Test2UnionList = [MakeCurrentValue(26)],
        BitVec = MakeSampleBits10(),
        BitList2 = new BitArray([true, false, true, false]),
    };

    private static LeafContainer MakeLeafContainer() => new()
    {
        Hash = TestItem.KeccakA,
        Address = TestItem.AddressA,
        Bloom = new Bloom(Enumerable.Repeat((byte)7, Bloom.ByteLength).ToArray()),
        Enum = PrimitiveEnum.Two,
        Reference = new TestBytes48(Enumerable.Repeat((byte)9, TestBytes48SszVectorTypeConverter.Length).ToArray()),
        Hashes = [TestItem.KeccakB, TestItem.KeccakC],
        Blooms = [Bloom.Empty, new Bloom(Enumerable.Repeat((byte)3, Bloom.ByteLength).ToArray())],
        Addresses = [TestItem.AddressB, TestItem.AddressC],
        AddressVector = [TestItem.AddressD, TestItem.AddressE],
        Enums = [PrimitiveEnum.Max, PrimitiveEnum.One],
        Bytes = Bytes.FromHexString("010203"),
    };

    private static CompatibleNumberUnion MakeCurrentValue(int value) =>
        new() { Selector = CompatibleNumberUnionSelector.CurrentValue, CurrentValue = (ulong)value };

    private static TResult[] ToArray<T, TResult>(SszListReader<T> items, Func<T, TResult> select) where T : allows ref struct
    {
        TResult[] result = new TResult[items.Count];
        int index = 0;
        foreach (T item in items) result[index++] = select(item);
        return result;
    }

    private static byte[] Encode<T>(T value) where T : ISszCodec<T> =>
        T.Encode(value);

    private static void Decode<T>(ReadOnlySpan<byte> data, out T value) where T : ISszCodec<T> =>
        T.Decode(data, out value);

    private static void Merkleize<T>(T value, out UInt256 root) where T : ISszCodec<T> =>
        T.Merkleize(value, out root);

    private static void AssertWrites(WriterCase testCase, int chunkSize)
    {
        using SszTreeWriter trees = new(chunkSize);
        using SszWriter output = new(chunkSize) { Trees = trees };
        testCase.Write(output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Write(testCase.Write, chunkSize), Is.EqualTo(testCase.Expected), "without hashing");
            Assert.That(output.ToSequence().ToArray(), Is.EqualTo(testCase.Expected));
            Assert.That(trees.Root, Is.EqualTo(testCase.ExpectedRoot));
            Assert.That(trees.ToSequence().ToArray(), Is.EqualTo(testCase.ExpectedTree));
        }
    }

    private static byte[] Write(Action<SszWriter> write, int chunkSize = 64)
    {
        using SszWriter output = new(chunkSize);
        write(output);
        return output.ToSequence().ToArray();
    }

    private static IEnumerable<TestCaseData> MerkleizeEncodedCases()
    {
        yield return Case(MakeComplexStruct(), ComplexStruct.MerkleizeEncoded);
        yield return Case(MakeLeafContainer(), LeafContainer.MerkleizeEncoded);
        yield return Case(new FixedC { Fixed1 = 1, Fixed2 = 2 }, FixedC.MerkleizeEncoded);
        yield return Case(new ProgressiveContainerSample { Head = 1, Tail = 2 }, ProgressiveContainerSample.MerkleizeEncoded);
        yield return Case(new ShadowDerived { A = 3, X = 4 }, ShadowDerived.MerkleizeEncoded);
        yield return Case(new GenericContainerOwner { Inner = new() { Head = 5, Value = new() { Fixed1 = 6, Fixed2 = [7] } } }, GenericContainerOwner.MerkleizeEncoded);
        yield return Case(new NullableVariableChildContainer { Child = new() { Items = [1, 2] } }, NullableVariableChildContainer.MerkleizeEncoded);
        yield return Case(new ByteListItself { Bytes = [4, 5] }, ByteListItself.MerkleizeEncoded);
        yield return Case(new ByteListItself { Bytes = [] }, ByteListItself.MerkleizeEncoded);
        yield return Case(new WrappedByteVectorItem { Bytes = Enumerable.Repeat((byte)3, TestBytes48SszVectorTypeConverter.Length).ToArray() }, WrappedByteVectorItem.MerkleizeEncoded);
        yield return Case(MakeCurrentValue(7), CompatibleNumberUnion.MerkleizeEncoded);
        yield return Case(new CompatibleNullableVectorUnion { Selector = CompatibleNullableVectorUnionSelector.Items, Items = [MakeBytes48(1), MakeBytes48(2)] }, CompatibleNullableVectorUnion.MerkleizeEncoded);
        yield return Case(new ConverterContainer { FixedBytes = new(1), FixedBytesVector = [new(2), new(3)], Hash = TestItem.KeccakA, HashVector = [TestItem.KeccakB, TestItem.KeccakC] }, ConverterContainer.MerkleizeEncoded);
        yield return Case(new ConverterItemListContainer { Items = [MakeBytes48(1), MakeBytes48(2), MakeBytes48(3)] }, ConverterItemListContainer.MerkleizeEncoded);
        yield return Case(new SignedPrimitiveCollectionContainer { Bools = [true, false, true], Ints = [-1, 2], Longs = [-3], Wides = [1, UInt128.MaxValue] }, SignedPrimitiveCollectionContainer.MerkleizeEncoded);
        yield return Case(new PrimitiveEnumVectorContainer { Items = [PrimitiveEnum.Max, PrimitiveEnum.One, PrimitiveEnum.Two] }, PrimitiveEnumVectorContainer.MerkleizeEncoded);
        yield return Case(new HugeLimitBasicList { Items = [1, 2, 3, 4, 5] }, HugeLimitBasicList.MerkleizeEncoded);
        yield return Case(new HugeLimitCompositeList { Items = [new FixedC { Fixed1 = 1 }, new FixedC { Fixed2 = 2 }] }, HugeLimitCompositeList.MerkleizeEncoded);
        yield return Case(new ProgressiveListContainer { Items = [1, 2, 3] }, ProgressiveListContainer.MerkleizeEncoded);
        yield return Case(new NestedProgressiveListContainer { Items = [new() { Items = [new() { Fixed1 = 1, Fixed2 = [2, 3] }] }, new() { Items = [] }] }, NestedProgressiveListContainer.MerkleizeEncoded);
        foreach (int length in (int[])[0, 1, 7, 8, 9, 255, 256, 257])
        {
            yield return Case(new ProgressiveBitlistContainer { Bits = new BitArray(length, true) }, ProgressiveBitlistContainer.MerkleizeEncoded);
        }
        foreach (int length in (int[])[0, 1, 7, 8, 9, 10])
        {
            ComplexStruct complex = MakeComplexStruct();
            complex.BitList2 = new BitArray(length, true);
            yield return Case(complex, ComplexStruct.MerkleizeEncoded);
        }

        static TestCaseData Case<T>(T value, SszHasher merkleizeEncoded) where T : ISszCodec<T>
        {
            Merkleize(value, out UInt256 root);
            byte[] encoded = Encode(value);
            return new TestCaseData(encoded, root, merkleizeEncoded).SetArgDisplayNames(typeof(T).Name, $"{encoded.Length} bytes");
        }
    }

    private static IEnumerable<TestCaseData> TreeCases()
    {
        foreach (int chunkSize in (int[])[1, 64, 65536])
        {
            yield return Case(MakeComplexStruct());
            yield return Case(MakeLeafContainer());
            yield return Case(new ProgressiveContainerSample { Head = 1, Tail = 2 });
            yield return Case(new ProgressiveListContainer { Items = [1, 2, 3, 4, 5, 6] });
            yield return Case(new ProgressiveListContainer { Items = [] });
            yield return Case(new ShadowDerived { A = 3, X = 4 });
            yield return Case(new GenericContainer<VariableC> { Head = 5, Value = new() { Fixed1 = 6, Fixed2 = [7] } });
            yield return Case(new NullableVariableChildContainer { Child = new() { Items = [1, 2] } });
            yield return Case(new NestedProgressiveListContainer { Items = [new() { Items = [new() { Fixed1 = 1, Fixed2 = [2, 3] }] }, new() { Items = [] }] });
            yield return Case(new HugeLimitBasicList { Items = [1, 2, 3, 4, 5] });
            yield return Case(new HugeLimitCompositeList { Items = [new FixedC { Fixed1 = 1 }, new FixedC { Fixed2 = 2 }] });
            yield return Case(new FixedVectorContainer { Items = [new FixedC { Fixed1 = 1 }, new FixedC { Fixed2 = 2 }] });
            yield return Case(new ConverterItemListContainer { Items = [MakeBytes48(1), MakeBytes48(2), MakeBytes48(3)] });
            yield return Case(new FixedTreeItemList { Items = [MakeFixedTreeItem(1), MakeFixedTreeItem(2)] });

            TestCaseData Case<T>(T value) where T : ISszCodec<T>
            {
                Merkleize(value, out UInt256 root);
                byte[] encoded = Encode(value);
                return new TestCaseData(encoded, root, TreeWriters[typeof(T)], chunkSize).SetArgDisplayNames(typeof(T).Name, $"{encoded.Length} bytes", $"{chunkSize}-byte chunks");
            }
        }
    }

    private static IEnumerable<TestCaseData> TreeMisuseCases()
    {
        yield return Misuse("TreeWriter rejects a member out of order", static output => new ProgressiveContainerSample.TreeWriter(output).WriteTail(new byte[8]));
        yield return Misuse("TreeWriter rejects copying without a source", static output => new ProgressiveContainerSample.TreeWriter(output).CopyHead());
        yield return Misuse("TreeWriter rejects copying a tree without a source", static output => new ProgressiveListContainer.TreeWriter(output).CopyItems());
        yield return Misuse("TreeWriter rejects a member while a nested writer is open", static output =>
        {
            DoubleListContainer.TreeWriter writer = new(output);
            writer.BeginFirst();
            writer.WriteSecond([]);
        });
        yield return Misuse("TreeWriter rejects reading the output of an incomplete tree", static output =>
        {
            using ProgressiveContainerSample.TreeWriter writer = new(output);
            writer.WriteHead(new byte[8]);
        });
        yield return Misuse("TreeWriter rejects a member after it is closed", static output =>
        {
            ProgressiveListContainer.TreeWriter writer = new(output);
            writer.WriteItems([]);
            writer.Dispose();
            writer.WriteItems([]);
        });

        static TestCaseData Misuse(string name, Action<SszTreeWriter> misuse) => new TestCaseData(misuse).SetName(name);
    }

    private static FixedTreeItem MakeFixedTreeItem(ulong value) =>
        new() { Value = value, Values = Enumerable.Range(0, 8).Select(i => value + (ulong)i).ToArray() };

    private static UInt256[] MakeLeaves(int count) =>
        // Some zero leaves, whose parents have a known hash.
        Enumerable.Range(0, count).Select(static i => i % 11 is 4 or 5 ? UInt256.Zero : new UInt256((ulong)i + 1, 0, 0, 7)).ToArray();

    private static SszTreeItems ChunkItems(bool progressive, int depth, bool isList) =>
        new(new SszTreeShape(depth, progressive, 0), 32, isList, null, null, default);

    private static void AddLeaves(SszTreeWriter output, SszTreeItems items, UInt256[] leaves)
    {
        using SszListTreeWriter writer = new(output, items, default);
        writer.AddAll(MemoryMarshal.AsBytes(leaves.AsSpan()));
    }

    private static (byte[] Trees, UInt256 Root) WriteTree(Action<SszTreeWriter> write, int chunkSize = 64)
    {
        using SszTreeWriter output = new(chunkSize);
        write(output);
        return (output.ToSequence().ToArray(), output.Root);
    }

    private static TestBytes48 MakeBytes48(byte value) => new(Enumerable.Repeat(value, TestBytes48SszVectorTypeConverter.Length).ToArray());

    public sealed record WriterCase(string Name, byte[] Expected, UInt256 ExpectedRoot, byte[] ExpectedTree, Action<SszWriter> Write)
    {
        public override string ToString() => Name;
    }

    private static WriterCase Case<T>(string name, T value, Action<SszWriter> write) where T : ISszCodec<T>
    {
        Merkleize(value, out UInt256 root);
        return new(name, Encode(value), root, TreeOf(value), write);
    }

    /// <summary>The hash tree of <paramref name="value"/>, empty when its type has none.</summary>
    private static byte[] TreeOf<T>(T value) where T : ISszCodec<T> =>
        TreeWriters.TryGetValue(typeof(T), out Action<SszTreeWriter, byte[]>? writeTree) ? WriteTree(output => writeTree(output, Encode(value))).Trees : [];

    private static readonly Dictionary<Type, Action<SszTreeWriter, byte[]>> TreeWriters = new()
    {
        [typeof(ComplexStruct)] = static (output, encoded) => ComplexStruct.WriteTree(output, new(encoded)),
        [typeof(LeafContainer)] = static (output, encoded) => LeafContainer.WriteTree(output, new(encoded)),
        [typeof(ProgressiveContainerSample)] = static (output, encoded) => ProgressiveContainerSample.WriteTree(output, new(encoded)),
        [typeof(ProgressiveListContainer)] = static (output, encoded) => ProgressiveListContainer.WriteTree(output, new(encoded)),
        [typeof(ShadowDerived)] = static (output, encoded) => ShadowDerived.WriteTree(output, new(encoded)),
        [typeof(GenericContainer<VariableC>)] = static (output, encoded) => GenericContainer<VariableC>.WriteTree(output, new(encoded)),
        [typeof(NullableVariableChildContainer)] = static (output, encoded) => NullableVariableChildContainer.WriteTree(output, new(encoded)),
        [typeof(NestedProgressiveListContainer)] = static (output, encoded) => NestedProgressiveListContainer.WriteTree(output, new(encoded)),
        [typeof(HugeLimitBasicList)] = static (output, encoded) => HugeLimitBasicList.WriteTree(output, new(encoded)),
        [typeof(HugeLimitCompositeList)] = static (output, encoded) => HugeLimitCompositeList.WriteTree(output, new(encoded)),
        [typeof(FixedVectorContainer)] = static (output, encoded) => FixedVectorContainer.WriteTree(output, new(encoded)),
        [typeof(ConverterItemListContainer)] = static (output, encoded) => ConverterItemListContainer.WriteTree(output, new(encoded)),
        [typeof(FixedTreeItemList)] = static (output, encoded) => FixedTreeItemList.WriteTree(output, new(encoded)),
        [typeof(DoubleListContainer)] = static (output, encoded) => DoubleListContainer.WriteTree(output, new(encoded)),
        [typeof(PresetContainer)] = static (output, encoded) => PresetContainer.WriteTree(output, new(encoded)),
    };

    private static IEnumerable<WriterCase> WriterCases()
    {
        ComplexStruct complex = MakeComplexStruct();
        yield return Case("ComplexStruct values", complex, output =>
        {
            using ComplexStruct.Writer writer = new(output);
            WriteFixedMembers(writer, complex);
            writer.WriteTest3(complex.Test3);
            writer.WriteVariableC(complex.VariableC);
            writer.WriteFixedCList(complex.FixedCList);
            writer.WriteVariableCVec(complex.VariableCVec);
            writer.WriteVariableCList(complex.VariableCList);
            writer.WriteTest2Union(complex.Test2Union);
            writer.WriteTest2UnionVec(complex.Test2UnionVec);
            writer.WriteTest2UnionList(complex.Test2UnionList);
            writer.WriteBitList2(complex.BitList2!);
        });
        yield return Case("ComplexStruct nested writers", complex, output =>
        {
            using ComplexStruct.Writer writer = new(output);
            writer.WriteTest1(complex.Test1);
            using (SszVectorWriter<ulong> items = writer.BeginTest2())
            {
                items.Add(complex.Test2.AsSpan(0, 9));
                items.Add(complex.Test2[9]);
            }
            writer.WriteFixedC(complex.FixedC);
            using (SszVectorWriter<FixedC> items = writer.BeginFixedCVec()) items.Add(complex.FixedCVec);
            writer.WriteBitVec(complex.BitVec!);
            using (SszListWriter<ulong> items = writer.BeginTest3())
            {
                items.Add(complex.Test3[0]);
                items.Add(complex.Test3.AsSpan(1));
            }
            WriteVariableC(writer.BeginVariableC(), complex.VariableC);
            using (SszListWriter<FixedC> items = writer.BeginFixedCList()) items.Add(complex.FixedCList);
            using (SszVariableListWriter<VariableC.Writer> items = writer.BeginVariableCVec())
            {
                foreach (VariableC item in complex.VariableCVec) WriteVariableC(items.BeginItem(), item);
            }
            using (SszVariableListWriter<VariableC.Writer> items = writer.BeginVariableCList(complex.VariableCList.Length))
            {
                foreach (VariableC item in complex.VariableCList) WriteVariableC(items.BeginItem(), item);
            }
            WriteUnion(writer.BeginTest2Union(), complex.Test2Union);
            using (SszVariableListWriter<CompatibleNumberUnion.Writer> items = writer.BeginTest2UnionVec())
            {
                foreach (CompatibleNumberUnion item in complex.Test2UnionVec!) WriteUnion(items.BeginItem(), item);
            }
            using (SszVariableListWriter<CompatibleNumberUnion.Writer> items = writer.BeginTest2UnionList(complex.Test2UnionList!.Length))
            {
                foreach (CompatibleNumberUnion item in complex.Test2UnionList) WriteUnion(items.BeginItem(), item);
            }
            writer.WriteBitList2(complex.BitList2!);
        });

        LeafContainer leaves = MakeLeafContainer();
        yield return Case("LeafContainer", leaves, output =>
        {
            using LeafContainer.Writer writer = new(output);
            writer.WriteHash(leaves.Hash);
            writer.WriteAddress(leaves.Address);
            writer.WriteBloom(leaves.Bloom);
            writer.WriteEnum(leaves.Enum);
            writer.WriteReference(leaves.Reference);
            writer.WriteHashes(leaves.Hashes);
            writer.WriteAddressVector(leaves.AddressVector);
            using (SszListWriter<Bloom> items = writer.BeginBlooms())
            {
                foreach (Bloom item in leaves.Blooms) items.Add(item);
            }
            writer.WriteAddresses(leaves.Addresses);
            using (SszListWriter<PrimitiveEnum> items = writer.BeginEnums()) items.Add(leaves.Enums);
            writer.WriteBytes(leaves.Bytes);
        });

        // Declared as Tail, Head; the field indices reverse the order.
        yield return Case("Progressive container", new ProgressiveContainerSample { Head = 1, Tail = 2 }, static output =>
        {
            using ProgressiveContainerSample.Writer writer = new(output);
            writer.WriteHead(1);
            writer.WriteTail(2);
        });
        yield return Case("Progressive list", new ProgressiveListContainer { Items = [1, 2, 3] }, static output =>
        {
            using ProgressiveListContainer.Writer writer = new(output);
            using SszListWriter<ulong> items = writer.BeginItems();
            items.Add([1, 2, 3]);
        });
        yield return Case("Progressive bitlist", new ProgressiveBitlistContainer { Bits = MakeSampleBits10() }, static output =>
        {
            using ProgressiveBitlistContainer.Writer writer = new(output);
            writer.WriteBits(MakeSampleBits10());
        });
        yield return Case("Derived container", new ShadowDerived { A = 3, X = 4 }, static output =>
        {
            using ShadowDerived.Writer writer = new(output);
            writer.WriteA(3);
            writer.WriteX(4);
        });
        yield return Case("Generic container", new GenericContainerOwner { Inner = new() { Head = 5, Value = new() { Fixed1 = 6, Fixed2 = [7] } } }, static output =>
        {
            using GenericContainerOwner.Writer writer = new(output);
            using GenericContainer<VariableC>.Writer inner = writer.BeginInner();
            inner.WriteHead(5);
            inner.WriteValue(new() { Fixed1 = 6, Fixed2 = [7] });
        });
        yield return Case("Nested class", new NullableVariableChildContainer { Child = new() { Items = [1, 2] } }, static output =>
        {
            using NullableVariableChildContainer.Writer writer = new(output);
            using NullableVariableChild.Writer child = writer.BeginChild();
            child.WriteItems([1, 2]);
        });
        yield return Case("Collection itself", new ByteListItself { Bytes = [4, 5] }, static output =>
        {
            using ByteListItself.Writer writer = new(output);
            writer.WriteBytes([4, 5]);
        });
        yield return Case("Nested collections", new NestedProgressiveListContainer
        {
            Items = [new() { Items = [new() { Fixed1 = 1, Fixed2 = [2, 3] }, new() { Fixed1 = 4, Fixed2 = [] }] }, new() { Items = [] }, new() { Items = [new() { Fixed1 = 5, Fixed2 = [6] }] }],
        }, static output =>
        {
            using NestedProgressiveListContainer.Writer writer = new(output);
            using SszVariableListWriter<ProgressiveVarTestStructList.Writer> lists = writer.BeginItems(3);
            using (ProgressiveVarTestStructList.Writer list = lists.BeginItem())
            {
                using SszVariableListWriter<VariableC.Writer> items = list.BeginItems(2);
                WriteVariableC(items.BeginItem(), new() { Fixed1 = 1, Fixed2 = [2, 3] });
                WriteVariableC(items.BeginItem(), new() { Fixed1 = 4, Fixed2 = [] });
            }
            using (ProgressiveVarTestStructList.Writer list = lists.BeginItem()) list.BeginItems(0).Dispose();
            using (ProgressiveVarTestStructList.Writer list = lists.BeginItem()) list.WriteItems([new() { Fixed1 = 5, Fixed2 = [6] }]);
        });
        yield return Case("Union", MakeCurrentValue(7), static output => WriteUnion(new(output), MakeCurrentValue(7)));
        HashOnlyOwner owner = new() { Child = new() { Fixed1 = 1, Fixed2 = [2, 3] }, Children = [new() { Fixed1 = 4, Fixed2 = [5] }, new() { Fixed1 = 6, Fixed2 = [] }] };
        yield return Case("Members with a tree in a container without", owner, output =>
        {
            using HashOnlyOwner.Writer writer = new(output);
            WriteVariableC(writer.BeginChild(), owner.Child);
            using SszVariableListWriter<VariableC.Writer> items = writer.BeginChildren(2);
            foreach (VariableC item in owner.Children) WriteVariableC(items.BeginItem(), item);
        });
        FixedTreeItemList fixedItems = new() { Items = [MakeFixedTreeItem(1), MakeFixedTreeItem(2)] };
        yield return Case("Fixed-size items with a tree", fixedItems, output =>
        {
            using FixedTreeItemList.Writer writer = new(output);
            using SszListWriter<FixedTreeItem> items = writer.BeginItems();
            items.Add(fixedItems.Items);
        });
        yield return Case("Collection itself with a tree", new HugeLimitCompositeList { Items = [new FixedC { Fixed1 = 1 }, new FixedC { Fixed2 = 2 }] }, static output =>
        {
            using HugeLimitCompositeList.Writer writer = new(output);
            writer.WriteItems([new FixedC { Fixed1 = 1 }, new FixedC { Fixed2 = 2 }]);
        });
        yield return Case("Empty lists", new DoubleListContainer { First = [], Second = [] }, static output =>
        {
            using DoubleListContainer.Writer writer = new(output);
            writer.BeginFirst().Dispose();
            writer.WriteSecond([]);
        });
    }

    private static IEnumerable<WriterCase> WriterCopyCases() =>
        CopyCases(withTree: true).Concat(CopyCases(withTree: false).Select(static testCase => testCase with { Name = $"{testCase.Name}, no source tree" }));

    private static IEnumerable<WriterCase> CopyCases(bool withTree)
    {
        ComplexStruct complex = MakeComplexStruct();
        byte[] source = Encode(complex);
        byte[] sourceTree = TreeOf(complex);
        yield return Case("ComplexStruct unchanged", complex, output =>
        {
            using ComplexStruct.Writer writer = new(output, new(source), withTree ? new(sourceTree) : default);
            CopyFixedMembers(writer);
            writer.CopyTest3();
            writer.CopyVariableC();
            writer.CopyFixedCList();
            writer.CopyVariableCVec();
            writer.CopyVariableCList();
            writer.CopyTest2Union();
            writer.CopyTest2UnionVec();
            writer.CopyTest2UnionList();
            writer.CopyBitList2();
        });

        ComplexStruct fixedChanged = MakeComplexStruct();
        fixedChanged.Test1 = 100;
        fixedChanged.Test2[4] = 101;
        fixedChanged.FixedCVec = [.. fixedChanged.FixedCVec[1..], new FixedC { Fixed1 = 102 }];
        fixedChanged.BitVec = new BitArray(10, true);
        yield return Case("ComplexStruct with changed fixed-size members", fixedChanged, output =>
        {
            using ComplexStruct.Writer writer = new(output, new(source), withTree ? new(sourceTree) : default);
            writer.WriteTest1(100);
            using (SszVectorWriter<ulong> items = writer.BeginTest2())
            {
                items.Copy(0, 4);
                items.Add(101);
                items.Copy(5, 5);
            }
            writer.CopyFixedC();
            using (SszVectorWriter<FixedC> items = writer.BeginFixedCVec())
            {
                items.Copy(1, 9);
                items.Add(new FixedC { Fixed1 = 102 });
            }
            writer.WriteBitVec(fixedChanged.BitVec);
            writer.CopyTest3();
            writer.CopyVariableC();
            writer.CopyFixedCList();
            writer.CopyVariableCVec();
            writer.CopyVariableCList();
            writer.CopyTest2Union();
            writer.CopyTest2UnionVec();
            writer.CopyTest2UnionList();
            writer.CopyBitList2();
        });

        ComplexStruct changed = MakeComplexStruct();
        changed.Test3 = [];
        changed.VariableC = new VariableC { Fixed1 = complex.VariableC.Fixed1, Fixed2 = [18, 19, 100] };
        changed.FixedCList = [complex.FixedCList[0], new FixedC { Fixed1 = 101, Fixed2 = 102 }];
        changed.VariableCVec = [.. complex.VariableCVec[1..], new VariableC { Fixed1 = 103, Fixed2 = [104, 105] }];
        changed.VariableCList = [complex.VariableCList[2], new VariableC { Fixed1 = complex.VariableCList[0].Fixed1, Fixed2 = [23] }, complex.VariableCList[1]];
        changed.Test2Union = MakeCurrentValue(106);
        yield return Case("ComplexStruct with changed nested values", changed, output =>
        {
            using ComplexStruct.Writer writer = new(output, new(source), withTree ? new(sourceTree) : default);
            CopyFixedMembers(writer);
            writer.WriteTest3([]);
            using (VariableC.Writer child = writer.BeginVariableC())
            {
                child.CopyFixed1();
                using SszListWriter<ulong> items = child.BeginFixed2();
                items.Copy(1, 2);
                items.Add(100);
            }
            using (SszListWriter<FixedC> items = writer.BeginFixedCList())
            {
                items.Copy(0, 1);
                items.Add(new FixedC { Fixed1 = 101, Fixed2 = 102 });
            }
            using (SszVariableListWriter<VariableC.Writer> items = writer.BeginVariableCVec())
            {
                items.Copy(1, 9);
                WriteVariableC(items.BeginItem(), new VariableC { Fixed1 = 103, Fixed2 = [104, 105] });
            }
            using (SszVariableListWriter<VariableC.Writer> items = writer.BeginVariableCList(3))
            {
                items.Copy(2, 1);
                using (VariableC.Writer item = items.BeginItem(0))
                {
                    item.CopyFixed1();
                    using SszListWriter<ulong> values = item.BeginFixed2();
                    values.Copy(1, 1);
                }
                items.Copy(1, 1);
                items.Copy(0, 0);
            }
            WriteUnion(writer.BeginTest2Union(), changed.Test2Union);
            using (SszVariableListWriter<CompatibleNumberUnion.Writer> items = writer.BeginTest2UnionVec())
            {
                items.Copy(0, 4);
                using (CompatibleNumberUnion.Writer item = items.BeginItem(4)) item.CopyCurrentValue();
                items.Copy(5, 5);
            }
            writer.CopyTest2UnionList();
            writer.CopyBitList2();
        });

        // A list that is in front of another one, so a change of its length moves the offset of the other one.
        byte[] lists = Encode(new DoubleListContainer { First = [1, 2, 3], Second = [4, 5] });
        byte[] listsTree = TreeOf(new DoubleListContainer { First = [1, 2, 3], Second = [4, 5] });
        yield return ListCase("List item replaced", [1, 9, 3], static items =>
        {
            items.Copy(0, 1);
            items.Add(9);
            items.Copy(2, 1);
        });
        yield return ListCase("List items appended", [1, 2, 3, 9], static items =>
        {
            items.Copy(0, 3);
            items.Add(9);
        });
        yield return ListCase("List items removed from the front", [2, 3], static items => items.Copy(1, 2));
        yield return ListCase("List emptied", [], static items => items.Copy(0, 0));

        yield return QueueCase("Queue items pushed", 0, [9]);
        yield return QueueCase("Queue items popped", 2, []);
        yield return QueueCase("Queue items popped and pushed", 2, [9, 10]);
        yield return QueueCase("Queue items all popped", 3, []);
        yield return QueueCase("Queue items all replaced", 3, [9]);
        yield return QueueCase("Queue unchanged", 0, []);
        byte[] emptyLists = Encode(new DoubleListContainer { First = [], Second = [] });
        byte[] emptyTree = TreeOf(new DoubleListContainer { First = [], Second = [] });
        yield return Case("Queue items pushed to empty queues", new DoubleListContainer { First = [1], Second = [2, 3] }, output =>
        {
            using DoubleListContainer.Writer writer = new(output, new(emptyLists), withTree ? new(emptyTree) : default);
            writer.PopAndPushFirst(0, [1]);
            writer.PopAndPushSecond(0, [2, 3]);
        });
        byte[] progressive = Encode(new ProgressiveListContainer { Items = [1, 2] });
        byte[] progressiveTree = TreeOf(new ProgressiveListContainer { Items = [1, 2] });
        yield return Case("Progressive queue items popped and pushed", new ProgressiveListContainer { Items = [2, 3] }, output =>
        {
            using ProgressiveListContainer.Writer writer = new(output, new(progressive), withTree ? new(progressiveTree) : default);
            writer.PopAndPushItems(1, [3]);
        });

        LeafContainer leaves = MakeLeafContainer();
        byte[] leavesEncoded = Encode(leaves);
        byte[] leavesTree = TreeOf(leaves);
        LeafContainer swapped = MakeLeafContainer();
        Array.Reverse(swapped.Blooms);
        Array.Reverse(swapped.Addresses);
        Array.Reverse(swapped.Enums);
        yield return Case("Leaf queue items popped and pushed", swapped, output =>
        {
            using LeafContainer.Writer writer = new(output, new(leavesEncoded), withTree ? new(leavesTree) : default);
            writer.CopyHash();
            writer.CopyAddress();
            writer.CopyBloom();
            writer.CopyEnum();
            writer.CopyReference();
            writer.CopyHashes();
            writer.CopyAddressVector();
            writer.PopAndPushBlooms(1, [leaves.Blooms[0]]);
            writer.PopAndPushAddresses(1, [leaves.Addresses[0]]);
            writer.PopAndPushEnums(1, [leaves.Enums[0]]);
            writer.CopyBytes();
        });

        FixedTreeItemList fixedItems = new() { Items = [MakeFixedTreeItem(1), MakeFixedTreeItem(2), MakeFixedTreeItem(3)] };
        byte[] fixedItemsEncoded = Encode(fixedItems);
        byte[] fixedItemsTree = TreeOf(fixedItems);
        yield return Case("Fixed-size items with a tree", new FixedTreeItemList { Items = [MakeFixedTreeItem(2), MakeFixedTreeItem(3), MakeFixedTreeItem(4), MakeFixedTreeItem(1)] }, output =>
        {
            using FixedTreeItemList.Writer writer = new(output, new(fixedItemsEncoded), withTree ? new(fixedItemsTree) : default);
            using SszListWriter<FixedTreeItem> items = writer.BeginItems();
            items.Copy(1, 2);
            items.Add(MakeFixedTreeItem(4));
            items.Copy(0, 1);
        });
        FixedVectorContainer vector = new() { Items = [new FixedC { Fixed1 = 1 }, new FixedC { Fixed2 = 2 }] };
        byte[] vectorEncoded = Encode(vector);
        byte[] vectorTree = TreeOf(vector);
        yield return Case("Vector item replaced", new FixedVectorContainer { Items = [new FixedC { Fixed1 = 1 }, new FixedC { Fixed2 = 3 }] }, output =>
        {
            using FixedVectorContainer.Writer writer = new(output, new(vectorEncoded), withTree ? new(vectorTree) : default);
            using SszVectorWriter<FixedC> items = writer.BeginItems();
            items.Copy(0, 1);
            items.Add(new FixedC { Fixed2 = 3 });
        });
        NullableVariableChildContainer parent = new() { Child = new() { Items = [1, 2] } };
        byte[] parentEncoded = Encode(parent);
        byte[] parentTree = TreeOf(parent);
        yield return Case("Member without a tree in a container with one", new NullableVariableChildContainer { Child = new() { Items = [2, 3] } }, output =>
        {
            using NullableVariableChildContainer.Writer writer = new(output, new(parentEncoded), withTree ? new(parentTree) : default);
            using NullableVariableChild.Writer child = writer.BeginChild();
            child.PopAndPushItems(1, [3]);
        });
        yield return Unchanged("Collection itself with a tree unchanged", new HugeLimitBasicList { Items = [1, 2, 3, 4, 5] }, (output, encoded, tree) =>
        {
            using HugeLimitBasicList.Writer writer = new(output, new(encoded), withTree ? new(new SszTreeReader(tree, 0, HugeLimitBasicList.RefTreeReader.TreeShape)) : default);
            writer.CopyItems();
        });

        yield return Unchanged("LeafContainer unchanged", MakeLeafContainer(), (output, encoded, tree) =>
        {
            using LeafContainer.Writer writer = new(output, new(encoded), withTree ? new(tree) : default);
            writer.CopyHash();
            writer.CopyAddress();
            writer.CopyBloom();
            writer.CopyEnum();
            writer.CopyReference();
            writer.CopyHashes();
            writer.CopyAddressVector();
            writer.CopyBlooms();
            writer.CopyAddresses();
            writer.CopyEnums();
            writer.CopyBytes();
        });
        yield return Unchanged("Progressive container unchanged", new ProgressiveContainerSample { Head = 1, Tail = 2 }, (output, encoded, tree) =>
        {
            using ProgressiveContainerSample.Writer writer = new(output, new(encoded), withTree ? new(tree) : default);
            writer.CopyHead();
            writer.CopyTail();
        });
        yield return Unchanged("Derived container unchanged", new ShadowDerived { A = 3, X = 4 }, (output, encoded, tree) =>
        {
            using ShadowDerived.Writer writer = new(output, new(encoded), withTree ? new(tree) : default);
            writer.CopyA();
            writer.CopyX();
        });
        yield return Unchanged("Generic container unchanged", new GenericContainerOwner { Inner = new() { Head = 5, Value = new() { Fixed1 = 6, Fixed2 = [7] } } }, (output, encoded, tree) =>
        {
            using GenericContainerOwner.Writer writer = new(output, new(encoded));
            using GenericContainer<VariableC>.Writer inner = writer.BeginInner();
            inner.CopyHead();
            inner.CopyValue();
        });
        yield return Unchanged("Collection itself unchanged", new ByteListItself { Bytes = [4, 5] }, (output, encoded, tree) =>
        {
            using ByteListItself.Writer writer = new(output, new(encoded));
            writer.CopyBytes();
        });
        yield return Unchanged("Union unchanged", MakeCurrentValue(7), (output, encoded, tree) =>
        {
            using CompatibleNumberUnion.Writer writer = new(output, new(encoded));
            writer.CopyCurrentValue();
        });

        NestedProgressiveListContainer nestedValue = new()
        {
            Items = [new() { Items = [new() { Fixed1 = 1, Fixed2 = [2, 3] }, new() { Fixed1 = 4, Fixed2 = [] }] }, new() { Items = [] }],
        };
        byte[] nested = Encode(nestedValue);
        byte[] nestedTree = TreeOf(nestedValue);
        yield return Case("Nested collections", new NestedProgressiveListContainer
        {
            Items = [new() { Items = [] }, new() { Items = [new() { Fixed1 = 4, Fixed2 = [] }, new() { Fixed1 = 5, Fixed2 = [6] }] }],
        }, output =>
        {
            using NestedProgressiveListContainer.Writer writer = new(output, new(nested), withTree ? new(nestedTree) : default);
            using SszVariableListWriter<ProgressiveVarTestStructList.Writer> lists = writer.BeginItems(2);
            lists.Copy(1, 1);
            using ProgressiveVarTestStructList.Writer list = lists.BeginItem(0);
            using SszVariableListWriter<VariableC.Writer> items = list.BeginItems(2);
            items.Copy(1, 1);
            WriteVariableC(items.BeginItem(), new() { Fixed1 = 5, Fixed2 = [6] });
        });

        WriterCase ListCase(string name, ulong[] expected, WriteList write) => Case(name, new DoubleListContainer { First = expected, Second = [4, 5] }, output =>
        {
            using DoubleListContainer.Writer writer = new(output, new(lists), withTree ? new(listsTree) : default);
            using (SszListWriter<ulong> items = writer.BeginFirst()) write(items);
            writer.CopySecond();
        });

        WriterCase QueueCase(string name, int popCount, ulong[] items) => Case(name, new DoubleListContainer { First = [.. new ulong[] { 1, 2, 3 }[popCount..], .. items], Second = [4, 5] }, output =>
        {
            using DoubleListContainer.Writer writer = new(output, new(lists), withTree ? new(listsTree) : default);
            writer.PopAndPushFirst(popCount, items);
            writer.CopySecond();
        });

        static WriterCase Unchanged<T>(string name, T value, Action<SszWriter, byte[], byte[]> copy) where T : ISszCodec<T>
        {
            byte[] encoded = Encode(value);
            byte[] tree = TreeOf(value);
            return Case(name, value, output => copy(output, encoded, tree));
        }
    }

    private delegate void WriteList(SszListWriter<ulong> items);

    private static void CopyFixedMembers(ComplexStruct.Writer writer)
    {
        writer.CopyTest1();
        writer.CopyTest2();
        writer.CopyFixedC();
        writer.CopyFixedCVec();
        writer.CopyBitVec();
    }

    private static ByteListItself.Writer OpenByteList(SszWriter writer, ReadOnlySpan<byte> source, bool hasSource, SszTreeWriter? trees, SszTreeReader sourceTree) => new(writer);

    // Takes a copy of the writer, which shares its progress with the original.
    private static void WriteFixedMembers(ComplexStruct.Writer writer, ComplexStruct value)
    {
        writer.WriteTest1(value.Test1);
        writer.WriteTest2(value.Test2);
        writer.WriteFixedC(value.FixedC);
        writer.WriteFixedCVec(value.FixedCVec);
        writer.WriteBitVec(value.BitVec!);
    }

    private static void WriteVariableC(VariableC.Writer itemWriter, VariableC value)
    {
        using VariableC.Writer writer = itemWriter;
        writer.WriteFixed1(value.Fixed1);
        using SszListWriter<ulong> items = writer.BeginFixed2();
        foreach (ulong item in value.Fixed2!) items.Add(item);
    }

    private static void WriteUnion(CompatibleNumberUnion.Writer itemWriter, CompatibleNumberUnion value)
    {
        using CompatibleNumberUnion.Writer writer = itemWriter;
        if (value.Selector == CompatibleNumberUnionSelector.PreviousValue) writer.WritePreviousValue(value.PreviousValue);
        else writer.WriteCurrentValue(value.CurrentValue);
    }

    private static IEnumerable<TestCaseData> WriterMisuseCases()
    {
        yield return Misuse("Writer rejects a variable-size member before a fixed-size one", static output => new VariableC.Writer(output).WriteFixed2([1]));
        yield return Misuse("Writer rejects a fixed-size member out of order", static output => new FixedC.Writer(output).WriteFixed2(1));
        yield return Misuse("Writer rejects a member written twice", static output =>
        {
            FixedC.Writer writer = new(output);
            writer.WriteFixed1(1);
            writer.WriteFixed1(1);
        });
        yield return Misuse("Writer rejects a second union member", static output =>
        {
            CompatibleNumberUnion.Writer writer = new(output);
            writer.WritePreviousValue(1);
            writer.WriteCurrentValue(1);
        });
        yield return Misuse("Writer rejects a member while a nested writer is open", static output =>
        {
            DoubleListContainer.Writer writer = new(output);
            SszListWriter<ulong> first = writer.BeginFirst();
            first.Add(1);
            writer.WriteSecond([]);
        });
        yield return Misuse("Writer rejects a member after an incomplete nested writer", static output =>
        {
            ComplexStruct.Writer writer = new(output);
            WriteFixedMembers(writer, MakeComplexStruct());
            writer.WriteTest3([]);
            using (VariableC.Writer child = writer.BeginVariableC()) child.WriteFixed1(1);
            writer.WriteFixedCList([]);
        });
        yield return Misuse("Writer rejects use after it is closed", static output =>
        {
            SszListWriter<ulong> items = new SingleListContainer.Writer(output).BeginItems();
            items.Dispose();
            items.Add(1);
        });
        yield return Misuse("Writer rejects reading the output of an incomplete value", static output =>
        {
            using FixedC.Writer writer = new(output);
            writer.WriteFixed1(1);
        });
        yield return Misuse("Writer rejects more items than declared", static output =>
        {
            SszVariableListWriter<ByteListItself.Writer> items = new(output, 1, OpenByteList, default, default);
            using (ByteListItself.Writer item = items.BeginItem()) item.WriteBytes([1]);
            items.BeginItem();
        });
        yield return Misuse("Writer rejects fewer items than declared", static output =>
        {
            using SszVariableListWriter<ByteListItself.Writer> items = new(output, 2, OpenByteList, default, default);
            using ByteListItself.Writer item = items.BeginItem();
            item.WriteBytes([1]);
        });
        yield return Misuse("Writer rejects fewer vector items than the length", static output =>
        {
            using FixedVectorContainer.Writer writer = new(output);
            using SszVectorWriter<FixedC> items = writer.BeginItems();
            items.Add(new FixedC());
        });
        yield return Misuse("Writer rejects the next item while the previous one is open", static output =>
        {
            SszVariableListWriter<ByteListItself.Writer> items = new(output, 2, OpenByteList, default, default);
            items.BeginItem();
            items.BeginItem();
        });

        yield return Misuse("Writer rejects copying a member without a source", static output => new FixedC.Writer(output).CopyFixed1());
        yield return Misuse("Writer rejects popping and pushing without a source", static output => new SingleListContainer.Writer(output).PopAndPushItems(0, [1]));
        yield return Misuse("Writer rejects popping and pushing twice", static output =>
        {
            SingleListContainer.Writer writer = new(output, new(Encode(new SingleListContainer { Items = [1] })));
            writer.PopAndPushItems(0, [2]);
            writer.PopAndPushItems(0, [3]);
        });
        yield return Misuse("Writer rejects copying a member of a nested value without a source", static output =>
        {
            SszVariableListWriter<VariableC.Writer> items = new(output, 1, static (writer, source, hasSource, _, _) => hasSource ? new(writer, new(source)) : new(writer), VariableC.Encode([new VariableC { Fixed2 = [] }]), default);
            items.BeginItem().CopyFixed1();
        });
        yield return Misuse("Writer rejects copying a union member that the source does not have", static output =>
            new CompatibleNumberUnion.Writer(output, new(CompatibleNumberUnion.Encode(MakeCurrentValue(1)))).CopyPreviousValue());
        yield return Misuse("Writer rejects copying a member out of order", static output => new FixedC.Writer(output, new(new byte[16])).CopyFixed2());

        static TestCaseData Misuse(string name, Action<SszWriter> misuse) => new TestCaseData(misuse).SetName(name);
    }

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
        yield return new TestCaseData((Action)(() => _ = new FixedC.RefReader(new byte[15])))
            .SetName("RefReader rejects fixed container input of the wrong length");
        yield return new TestCaseData((Action)(() => _ = new VariableC.RefReader(new byte[4])))
            .SetName("RefReader rejects truncated variable container input");
        yield return new TestCaseData((Action)(() => _ = new DoubleListContainer.RefReader(Bytes.FromHexString("0400000008000000"))))
            .SetName("RefReader rejects offsets that point into the fixed section");
        yield return new TestCaseData((Action)(() => _ = new DoubleListContainer.RefReader(Bytes.FromHexString("0800000007000000"))))
            .SetName("RefReader rejects offsets that are out of order");
        yield return new TestCaseData((Action)(() => _ = new DoubleListContainer.RefReader(Bytes.FromHexString("0800000009000000"))))
            .SetName("RefReader rejects offsets that point past the end");
        yield return new TestCaseData((Action)(() => _ = new SingleListContainer.RefReader(Bytes.FromHexString("0400000001")).Items))
            .SetName("RefReader rejects a list with a partial item");
        yield return new TestCaseData((Action)(() => _ = new SingleListContainer.RefReader([4, 0, 0, 0, .. new byte[40]]).Items))
            .SetName("RefReader rejects lists above the declared limit");
        yield return new TestCaseData((Action)(() => _ = new ByteListItself.RefReader(new byte[4]).Bytes))
            .SetName("RefReader rejects byte lists above the declared limit");
        yield return new TestCaseData((Action)(() => _ = new NullableCompatibleUnionVectorContainer.RefReader([4, 0, 0, 0, 4, 0, 0, 0, .. CompatibleNumberUnion.Encode(MakeCurrentValue(1))]).Items))
            .SetName("RefReader rejects vectors with the wrong length");
        yield return new TestCaseData((Action)(() => _ = new SszListReader<ByteListItself.RefReader>(Bytes.FromHexString("060000000000"), 0, static item => new(item))))
            .SetName("RefReader rejects a misaligned first item offset");
        yield return new TestCaseData((Action)(() => _ = new SszListReader<ByteListItself.RefReader>(Bytes.FromHexString("0800000004000000"), 0, static item => new(item))[0]))
            .SetName("RefReader rejects item offsets that are out of order");
        yield return new TestCaseData((Action)(() => _ = new SszListReader<ByteListItself.RefReader>(Bytes.FromHexString("0800000009000000"), 0, static item => new(item))[0]))
            .SetName("RefReader rejects item offsets that point past the end");
        yield return new TestCaseData((Action)(() => _ = new BitVectorContainer.RefReader(Bytes.FromHexString("00000000000000000004")).Bits))
            .SetName("RefReader rejects bitvectors with unused bits set");
        yield return new TestCaseData((Action)(() => _ = new ProgressiveBitlistContainer.RefReader(Bytes.FromHexString("0400000000")).Bits))
            .SetName("RefReader rejects bitlists without a sentinel bit");
        yield return new TestCaseData((Action)(() => _ = new CompatibleNumberUnion.RefReader([99])))
            .SetName("RefReader rejects unknown compatible union selector");
        yield return new TestCaseData((Action)(() => _ = new CompatibleBoolUnion.RefReader([1, 1, 0])))
            .SetName("RefReader rejects compatible union trailing bytes");
        yield return new TestCaseData((Action)(() => _ = new CompatibleNumberUnion.RefReader(CompatibleNumberUnion.Encode(MakeCurrentValue(1))).PreviousValue))
            .SetName("RefReader rejects reading a compatible union member that is not selected");
        foreach ((string name, Action<byte[]> hash) in ((string, Action<byte[]>)[])
        [
            ("MerkleizeEncoded", static encoded => DoubleListContainer.MerkleizeEncoded(encoded, out _)),
            ("WriteTree", static encoded => WriteTree(output => DoubleListContainer.WriteTree(output, new(encoded)))),
        ])
        {
            yield return new TestCaseData((Action)(() => hash([8, 0, 0, 0, 48, 0, 0, 0, .. new byte[40]])))
                .SetName($"{name} rejects lists above the declared limit");
            yield return new TestCaseData((Action)(() => hash([8, 0, 0, 0, 9, 0, 0, 0, 1])))
                .SetName($"{name} rejects a list with a partial item");
            yield return new TestCaseData((Action)(() => hash([8, 0, 0, 0, 7, 0, 0, 0])))
                .SetName($"{name} rejects offsets that are out of order");
        }
        yield return new TestCaseData((Action)(() => ByteListItself.MerkleizeEncoded(new byte[4], out _)))
            .SetName("MerkleizeEncoded rejects byte lists above the declared limit");
        yield return new TestCaseData((Action)(() => ProgressiveBitlistContainer.MerkleizeEncoded(Bytes.FromHexString("0400000000"), out _)))
            .SetName("MerkleizeEncoded rejects bitlists without a sentinel bit");
        yield return new TestCaseData((Action)(() => CompatibleNumberUnion.MerkleizeEncoded([99], out _)))
            .SetName("MerkleizeEncoded rejects unknown compatible union selector");
        yield return new TestCaseData((Action)(() => NullableCompatibleUnionVectorContainer.MerkleizeEncoded([4, 0, 0, 0, 4, 0, 0, 0, .. CompatibleNumberUnion.Encode(MakeCurrentValue(1))], out _)))
            .SetName("MerkleizeEncoded rejects vectors with the wrong length");
        yield return new TestCaseData((Action)(() => Write(static output => new SingleListContainer.Writer(output).WriteItems([1, 2, 3, 4, 5]))))
            .SetName("Writer rejects lists above the declared limit");
        yield return new TestCaseData((Action)(() => Write(static output =>
        {
            SszListWriter<ulong> items = new SingleListContainer.Writer(output).BeginItems();
            items.Add([1, 2, 3]);
            items.Add([4, 5]);
        }))).SetName("Writer rejects items above the declared limit");
        yield return new TestCaseData((Action)(() => Write(static output =>
            new SingleListContainer.Writer(output, new(Encode(new SingleListContainer { Items = [1, 2, 3] }))).PopAndPushItems(0, [4, 5]))))
            .SetName("Writer rejects pushing above the declared limit");
        yield return new TestCaseData((Action)(() => Write(static output => new NullableCompatibleUnionArrayPoolListContainer.Writer(output).BeginItems(3))))
            .SetName("Writer rejects an item count above the declared limit");
        yield return new TestCaseData((Action)(() => Write(static output => new FixedVectorContainer.Writer(output).WriteItems([new FixedC()]))))
            .SetName("Writer rejects vectors with the wrong length");
        yield return new TestCaseData((Action)(() => Write(static output => new FixedVectorContainer.Writer(output).BeginItems().Add([new(), new(), new()]))))
            .SetName("Writer rejects vector items above the length");
        yield return new TestCaseData((Action)(() => Write(static output => new BitVectorContainer.Writer(output).WriteBits(new BitArray(9)))))
            .SetName("Writer rejects bitvectors with the wrong length");
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

        int subtreeCount = (int)Math.Min((ulong)chunks.Length, Math.Min(numLeaves, (ulong)int.MaxValue));
        Merkle.Merkleize(out UInt256 subtree, chunks[..subtreeCount], numLeaves);

        ReadOnlySpan<UInt256> remainingChunks = chunks[subtreeCount..];
        UInt256 continuation = UInt256.Zero;
        if (!remainingChunks.IsEmpty)
        {
            MerkleizeProgressiveSpec(remainingChunks, out continuation, checked(numLeaves * 4));
        }

        root = HashConcat(subtree, continuation);
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
        Merkleizer merkleizer = new(new UInt256[1]);
        feed(ref merkleizer, value);
        merkleizer.CalculateRoot(out UInt256 root);
        return root;
    }
}
