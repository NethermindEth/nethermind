// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;
using System;
using System.Buffers.Binary;
using System.Collections;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.SszGenerator.Test
{
    [SszContainer]
    public partial struct ComplexStruct
    {
        public ulong Test1 { get; set; }

        [SszVector(10)]
        public ulong[] Test2 { get; set; }

        [SszList(10)]
        public ulong[] Test3 { get; set; }
        public FixedC FixedC { get; set; }
        public VariableC VariableC { get; set; }


        [SszVector(10)]
        public FixedC[] FixedCVec { get; set; }

        [SszList(10)]
        public FixedC[] FixedCList { get; set; }


        [SszVector(10)]
        public VariableC[] VariableCVec { get; set; }

        [SszList(10)]
        public VariableC[] VariableCList { get; set; }

        public CompatibleNumberUnion Test2Union { get; set; }

        [SszVector(10)]
        public CompatibleNumberUnion[]? Test2UnionVec { get; set; }

        [SszList(10)]
        public CompatibleNumberUnion[]? Test2UnionList { get; set; }

        [SszVector(10)]
        public BitArray? BitVec { get; set; }

        [SszList(10)]
        public BitArray? BitList2 { get; set; }
    }

    [SszContainer]
    public partial struct FixedC
    {
        public ulong Fixed1 { get; set; }
        public ulong Fixed2 { get; set; }
    }

    [SszContainer]
    public partial class NullableStaticChild
    {
        public ulong Fixed1 { get; set; }
        public ulong Fixed2 { get; set; }
    }

    [SszContainer]
    public partial class NullableStaticChildContainer
    {
        public NullableStaticChild? Child { get; set; }
    }

    [SszContainer]
    public partial class NullableVariableChild
    {
        [SszList(4)]
        public ulong[]? Items { get; set; }
    }

    [SszContainer]
    public partial class NullableVariableChildContainer
    {
        public NullableVariableChild? Child { get; set; }
    }

    [SszContainer]
    public partial struct VariableC
    {
        public ulong Fixed1 { get; set; }

        [SszList(10)]
        public ulong[]? Fixed2 { get; set; }
    }

    [SszContainer]
    public partial struct SingleListContainer
    {
        [SszList(4)]
        public ulong[]? Items { get; set; }
    }

    [SszContainer(isCollectionItself: true)]
    public partial struct ByteListItself
    {
        [SszList(3)]
        public byte[]? Bytes { get; set; }
    }

    [SszContainer]
    public partial struct ArrayPoolListContainer
    {
        [SszList(4)]
        public ArrayPoolList<ulong> Items { get; set; }
    }

    [SszContainer]
    public partial struct NullableArrayPoolListContainer
    {
        [SszList(4)]
        public ArrayPoolList<ulong>? Items { get; set; }
    }

    [SszContainer]
    public partial struct NullableFixedArrayPoolListContainer
    {
        [SszList(4)]
        public ArrayPoolList<FixedC>? Items { get; set; }
    }

    [SszContainer(isCollectionItself: true)]
    public partial struct NullableArrayPoolListItself
    {
        [SszList(4)]
        public ArrayPoolList<ulong>? Items { get; set; }
    }

    [SszContainer]
    public partial struct DoubleListContainer
    {
        [SszList(4)]
        public ulong[]? First { get; set; }

        [SszList(4)]
        public ulong[]? Second { get; set; }
    }

    [SszContainer]
    public partial struct BitVectorContainer
    {
        public ulong Value { get; set; }

        [SszVector(10)]
        public BitArray? Bits { get; set; }
    }

    [SszContainer]
    public partial struct BoolVectorContainer
    {
        [SszVector(3)]
        public bool[]? Items { get; set; }
    }

    [SszContainer]
    public partial struct NullableByteVectorContainer
    {
        [SszVector(64)]
        public byte[]? Bytes { get; set; }
    }

    [SszContainer]
    public partial struct ProgressiveNullableByteVectorContainer
    {
        [SszField(0)]
        [SszVector(64)]
        public byte[]? Bytes { get; set; }
    }

    [SszContainer]
    public partial struct NullableCompatibleUnionVectorContainer
    {
        [SszVector(2)]
        public CompatibleNumberUnion[]? Items { get; set; }
    }

    [SszContainer]
    public partial struct NullableCompatibleUnionArrayPoolListVectorContainer
    {
        [SszVector(2)]
        public ArrayPoolList<CompatibleNumberUnion>? Items { get; set; }
    }

    [SszContainer]
    public partial struct NullableCompatibleUnionArrayPoolListContainer
    {
        [SszList(2)]
        public ArrayPoolList<CompatibleNumberUnion>? Items { get; set; }
    }

    [SszContainer]
    public partial struct SignedPrimitiveCollectionContainer
    {
        [SszVector(3)]
        public bool[]? Bools { get; set; }

        [SszVector(2)]
        public int[]? Ints { get; set; }

        [SszList(2)]
        public long[]? Longs { get; set; }

        [SszVector(2)]
        public UInt128[]? Wides { get; set; }
    }

    [SszContainer]
    public partial struct UInt128VectorContainer
    {
        [SszVector(3)]
        public UInt128[]? Wides { get; set; }
    }

    [SszContainer]
    public partial struct PrimitiveEnumVectorContainer
    {
        [SszVector(3)]
        public PrimitiveEnum[]? Items { get; set; }
    }

    public enum PrimitiveEnum : uint
    {
        One = 1,
        Two = 2,
        Max = uint.MaxValue,
    }

    [SszContainer]
    public partial struct FixedVectorContainer
    {
        [SszVector(2)]
        public FixedC[]? Items { get; set; }
    }

    [SszContainer]
    public partial struct ReadOnlyMemoryVectorContainer
    {
        [SszVector(4)]
        public ReadOnlyMemory<byte> Bytes { get; set; }
    }

    [SszContainer]
    public partial struct MemoryVectorContainer
    {
        [SszVector(4)]
        public Memory<byte> Bytes { get; set; }
    }

    [SszContainer]
    public partial struct ConverterContainer
    {
        public TestBytes4 FixedBytes { get; set; }

        [SszVector(2)]
        public TestBytes4[]? FixedBytesVector { get; set; }

        public ValueHash256 Hash { get; set; }

        [SszVector(2)]
        public ValueHash256[]? HashVector { get; set; }
    }

    [SszContainer]
    public partial struct ConverterNameShadowContainer
    {
        public TestBytes4 TestBytes4SszVectorTypeConverter { get; set; }
    }

    [SszContainer]
    public partial struct NullableLongConverterVectorContainer
    {
        [SszVector(2)]
        public TestBytes48[]? Items { get; set; }
    }

    public readonly struct TestBytes4(uint value)
    {
        public uint Value { get; } = value;
    }

    [SszVectorTypeConverter<TestBytes4>]
    public static class TestBytes4SszVectorTypeConverter
    {
        public const int Length = sizeof(uint);

        public static int FeedCallCount { get; set; }

        public static TestBytes4 FromSpan(ReadOnlySpan<byte> span) =>
            new(BinaryPrimitives.ReadUInt32LittleEndian(span));

        public static void FromSpan(ReadOnlySpan<byte> span, Span<TestBytes4> values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = FromSpan(span.Slice(i * Length, Length));
            }
        }

        public static void ToSpan(Span<byte> span, TestBytes4 value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(span, value.Value);

        public static void ToSpan(Span<byte> span, ReadOnlySpan<TestBytes4> values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                ToSpan(span.Slice(i * Length, Length), values[i]);
            }
        }

        public static void Feed(ref Merkleizer merkleizer, TestBytes4 value)
        {
            FeedCallCount++;
            merkleizer.Feed(new UInt256(value.Value));
        }
    }

    public readonly struct TestBytes48(ReadOnlyMemory<byte> data)
    {
        public ReadOnlyMemory<byte> Data { get; } = data;
    }

    [SszVectorTypeConverter<TestBytes48>]
    public static class TestBytes48SszVectorTypeConverter
    {
        public const int Length = 48;

        public static TestBytes48 FromSpan(ReadOnlySpan<byte> span) =>
            new(span.ToArray());

        public static void FromSpan(ReadOnlySpan<byte> span, Span<TestBytes48> values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = FromSpan(span.Slice(i * Length, Length));
            }
        }

        public static void ToSpan(Span<byte> span, TestBytes48 value)
        {
            span.Clear();
            value.Data.Span.CopyTo(span);
        }

        public static void ToSpan(Span<byte> span, ReadOnlySpan<TestBytes48> values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                ToSpan(span.Slice(i * Length, Length), values[i]);
            }
        }

        public static void Feed(ref Merkleizer merkleizer, TestBytes48 value)
        {
            Span<byte> encoded = stackalloc byte[Length];
            ToSpan(encoded, value);
            Merkle.Merkleize(out UInt256 root, encoded);
            merkleizer.Feed(root);
        }
    }

    //// Does not compile
    //[SszContainer]
    //public partial struct NoProps
    //{

    //}

    [SszCompatibleUnion]
    public partial struct CompatibleNumberUnion
    {
        public CompatibleNumberUnionSelector Selector { get; set; }
        public ulong PreviousValue { get; set; }
        public ulong CurrentValue { get; set; }
    }

    [SszCompatibleUnion]
    public partial struct CompatibleBoolUnion
    {
        public CompatibleBoolUnionSelector Selector { get; set; }
        public bool PreviousValue { get; set; }
        public bool CurrentValue { get; set; }
    }

    [SszCompatibleUnion]
    public partial struct CompatibleNullableVectorUnion
    {
        public CompatibleNullableVectorUnionSelector Selector { get; set; }

        [SszVector(2)]
        public TestBytes48[]? Items { get; set; }
    }

    [SszContainer]
    public partial struct ProgressiveContainerSample
    {
        [SszField(2)]
        public ulong Tail { get; set; }

        [SszField(0)]
        public ulong Head { get; set; }
    }

    [SszContainer]
    public partial struct ProgressiveListContainer
    {
        [SszProgressiveList]
        public ulong[]? Items { get; set; }
    }

    [SszContainer(isCollectionItself: true)]
    public partial struct ProgressiveVarTestStructList
    {
        [SszProgressiveList]
        public VariableC[]? Items { get; set; }
    }

    [SszContainer]
    public partial struct NestedProgressiveListContainer
    {
        [SszProgressiveList]
        public ProgressiveVarTestStructList[]? Items { get; set; }
    }

    [SszContainer]
    public partial struct ProgressiveBitlistContainer
    {
        [SszProgressiveBitlist]
        public BitArray? Bits { get; set; }
    }

    public enum CompatibleNumberUnionSelector : byte
    {
        PreviousValue = 1,
        CurrentValue = 2,
    }

    public enum CompatibleBoolUnionSelector : byte
    {
        PreviousValue = 1,
        CurrentValue = 2,
    }

    public enum CompatibleNullableVectorUnionSelector : byte
    {
        Items = 1,
    }

    [SszContainer]
    public partial class ShadowBase
    {
        public ulong A { get; set; }
        public ulong X { get; set; }
    }

    [SszContainer]
    public partial class ShadowDerived : ShadowBase
    {
        public new uint X { get; set; }
    }

    [SszContainer(isCollectionItself: true)]
    public partial struct HugeLimitBasicList
    {
        // VALIDATOR_REGISTRY_LIMIT-sized list (2^40), exceeds int.MaxValue
        [SszList(1_099_511_627_776)]
        public ulong[] Items { get; set; }
    }

    [SszContainer(isCollectionItself: true)]
    public partial struct HugeLimitCompositeList
    {
        [SszList(1_099_511_627_776)]
        public FixedC[] Items { get; set; }
    }

}
