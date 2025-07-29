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

        public UnionTest3 Test2Union { get; set; }

        [SszVector(10)]
        public UnionTest3[]? Test2UnionVec { get; set; }

        [SszList(10)]
        public UnionTest3[]? Test2UnionList { get; set; }

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

    //// Does not compile
    //[SszSerializable]
    //public struct NoProps
    //{

    //}

    [SszSerializable]
    public struct UnionTest3
    {
        public Test3Union Selector { get; set; }
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

        public Test2 Test2Union { get; set; }

        [SszVector(10)]
        public Test2[]? Test2UnionVec { get; set; }

        [SszList(10)]
        public Test2[]? Test2UnionList { get; set; }

        [SszVector(10)]
        public BitArray? BitVec { get; set; }

        [SszList(10)]
        public BitArray? BitList { get; set; }
    }

    [SszSerializable]
    public struct Test2
    {
        public Test2()
        {
            int[] x = [];
            Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(14));
            merkleizer.Feed(x);
        }

        public Test2Union Selector { get; set; }
        public long Type1 { get; set; }
        public int Type2 { get; set; }
    }

    public enum Test2Union : byte
    {
        None,
        Type1,
        Type2,
    }
    public enum Test3Union : byte
    {
        Test1,
        Test2,
        Test3,
        FixedC,
        VariableC,
        FixedCVec,
        FixedCList,
        VariableCVec,
        VariableCList,
        Test2Union,
        Test2UnionVec,
        Test2UnionList,
        BitVec,
        BitList,
    }
}
