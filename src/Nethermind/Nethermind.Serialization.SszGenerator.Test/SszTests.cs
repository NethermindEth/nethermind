// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;
using System.Collections.Generic;

namespace Nethermind.Serialization.SszGenerator.Test
{
    public class SszTests
    {
        //[SszSerializable]
        //public struct BasicSzzStruct
        //{
        //    public BasicSzzStruct()
        //    {

        //    }

        //    public int Number { get; set; }
        //    public int? NumberOrNull { get; set; }
        //    public byte[] Array { get; set; } = [];
        //    public byte[]? ArrayOrNull { get; set; }
        //}


        //[SszSerializable]
        //public struct StaticStruct
        //{
        //    public long X { get; set; }
        //    public long Y { get; set; }
        //}

        //[SszSerializable]
        //public class BasicSzzClass
        //{
        //    //public int Number { get; set; }
        //    //public int? NumberOrNull { get; set; }
        //    //public byte[] Array { get; set; } = [];
        //    //public byte[]? ArrayOrNull { get; set; }

        //    //BasicSzzClass? Recursive { get; set; }
        //    //BasicSzzStruct Struct { get; set; }
        //    public StaticStruct FixedStruct { get; set; }
        //}

    

        //[Test]
        //public void Test_roundtrip()
        //{
        //    BasicSzzStruct s = new BasicSzzStruct { Number = 42, Array = [1, 2, 3] };
        //    Generated.BasicSzzStructSszSerializer serializer = new();
        //    ReadOnlySpan<byte> data = serializer.Serialize(ref s);
        //    s = serializer.Deserialize(data);
        //    Assert.That(s.Number, Is.EqualTo(42));
        //    Assert.That(s.Array, Is.EqualTo(new byte[] { 1, 2, 3 }));
        //}

        //[Test]
        //public void Test_union_roundtrip()
        //{
        //    BasicSzzStruct s = new BasicSzzStruct { Number = 42, Array = [1, 2, 3] };
        //    Generated.BasicSzzStructSszSerializer serializer = new();
        //    ReadOnlySpan<byte> data = serializer.Serialize(s);
        //    s = serializer.Deserialize(data);
        //    Assert.That(s.Number, Is.EqualTo(42));
        //    Assert.That(s.Array, Is.EqualTo(new byte[] { 1, 2, 3 }));
        //}
    }

    //[SszSerializable]
    //public struct SlotDecryptionIdentites
    //{
    //    public ulong Test1 { get; set; }

    //    [SszVector(10)]
    //    public ulong[] Test2 { get; set; }

    //    [SszList(10)]
    //    public ulong[] Test3 { get; set; }
    //    public FixedC FixedC { get; set; }
    //    public VariableC VariableC { get; set; }


    //    [SszVector(10)]
    //    public FixedC[] FixedCVec { get; set; }

    //    [SszList(10)]
    //    public FixedC[] FixedCList { get; set; }


    //    [SszVector(10)]
    //    public VariableC[] VariableCVec { get; set; }

    //    [SszList(10)]
    //    public VariableC[] VariableCList { get; set; }
    //}

    //[SszSerializable]
    //public struct FixedC
    //{
    //    public ulong Fixed1 { get; set; }
    //    public ulong Fixed2 { get; set; }


    //}

    //[SszSerializable]
    //public class VariableC
    //{
    //    public ulong Fixed1 { get; set; }

    //    [SszList(10)]
    //    public ulong[]? Fixed2 { get; set; }


    //}

    [SszSerializable]
    public struct Test2
    {
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

    //[SszSerializable]
    //public struct IdentityPreimage
    //{
    //    [SszVector(52)]
    //    public byte[] Data { get; set; }
    //}

    //[SszSerializable]
    //public struct Test1
    //{
    //    public List<ulong> Test10 { get; set; }
    //    public ulong[] Test11 { get; set; }

    //    [SszList]
    //    public List<ulong> Test12 { get; set; }

    //    [SszList(1024)]
    //    public List<ulong> Test13 { get; set; }

    //    [SszVector(1024)]
    //    public List<ulong> Test14 { get; set; }

    //    [SszVector]
    //    public List<ulong> Test15 { get; set; }
    //}

    //public enum SomeEnum
    //{
    //    None,
    //    Type1,
    //    Type2,
    //}

    //[SszSerializable]
    //public class Test2
    //{
    //    public SomeEnum Selector { get; set; }
    //    public long? Type1 { get; set; }
    //    public Test1? Type2 { get; set; }
    //}

    ////[SszSerializable]
    ////public class Test3
    ////{
    ////    public Test3()
    ////    {

    ////    }
    ////    //public ulong Test1 { get; set; } = 1;

    ////    [SszList(1024)]
    ////    public long[] Test2 { get; set; } = [2];

    ////    [SszList(1024)]
    ////    public List<long> Test3 { get; set; } = [2];

    ////    [SszVector(5)]
    ////    public List<long> Test4 { get; set; } = [1, 2, 3, 4, 5];

    ////    [SszList(1024)]
    ////    public List<byte[]> Test5 { get; set; } = [[2]];
    ////}

}

