// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;
using System;
using System.Buffers.Binary;
using System.Collections;

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

    public readonly struct TestBytes4(uint value)
    {
        public uint Value { get; } = value;
    }

    public sealed class TestBytes4SszVectorConverter : SszVectorConverter<TestBytes4>
    {
        public const int Length = sizeof(uint);

        private TestBytes4SszVectorConverter() { }

        public static TestBytes4 FromSpan(ReadOnlySpan<byte> span) =>
            new(BinaryPrimitives.ReadUInt32LittleEndian(span));

        public static void ToSpan(Span<byte> span, TestBytes4 value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(span, value.Value);
    }

    public sealed class ValueHash256SszVectorConverter : SszVectorConverter<ValueHash256>
    {
        public const int Length = ValueHash256.MemorySize;

        private ValueHash256SszVectorConverter() { }

        public static ValueHash256 FromSpan(ReadOnlySpan<byte> span) => new(span);

        public static void ToSpan(Span<byte> span, ValueHash256 value) => value.Bytes.CopyTo(span);
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

}
