// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;
using System.Collections;

namespace Nethermind.Serialization.SszGenerator.Test
{
    [SszSerializable]
    public struct ComplexStruct
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

    [SszSerializable]
    public struct FixedC
    {
        public ulong Fixed1 { get; set; }
        public ulong Fixed2 { get; set; }
    }

    [SszSerializable]
    public struct VariableC
    {
        public ulong Fixed1 { get; set; }

        [SszList(10)]
        public ulong[]? Fixed2 { get; set; }
    }

    [SszSerializable]
    public struct SingleListContainer
    {
        [SszList(4)]
        public ulong[]? Items { get; set; }
    }

    [SszSerializable]
    public struct DoubleListContainer
    {
        [SszList(4)]
        public ulong[]? First { get; set; }

        [SszList(4)]
        public ulong[]? Second { get; set; }
    }

    [SszSerializable]
    public struct BitVectorContainer
    {
        [SszVector(10)]
        public BitArray? Bits { get; set; }
    }

    [SszSerializable]
    public struct FixedVectorContainer
    {
        [SszVector(2)]
        public FixedC[]? Items { get; set; }
    }

    //// Does not compile
    //[SszSerializable]
    //public struct NoProps
    //{

    //}

    [SszSerializable]
    [SszCompatibleUnion]
    public struct CompatibleNumberUnion
    {
        public CompatibleNumberUnionSelector Selector { get; set; }
        public ulong PreviousValue { get; set; }
        public ulong CurrentValue { get; set; }
    }

    [SszSerializable]
    [SszProgressiveContainer]
    public struct ProgressiveContainerSample
    {
        [SszField(2)]
        public ulong Tail { get; set; }

        [SszField(0)]
        public ulong Head { get; set; }
    }

    [SszSerializable]
    public struct ProgressiveListContainer
    {
        [SszProgressiveList]
        public ulong[]? Items { get; set; }
    }

    [SszSerializable]
    public struct ProgressiveBitlistContainer
    {
        [SszProgressiveBitlist]
        public BitArray? Bits { get; set; }
    }

    public enum CompatibleNumberUnionSelector : byte
    {
        PreviousValue = 1,
        CurrentValue = 2,
    }
}
