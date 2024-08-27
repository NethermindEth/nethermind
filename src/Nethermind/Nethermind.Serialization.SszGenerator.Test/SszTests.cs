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

        //public enum SomeEnum
        //{
        //    None,
        //    Type1,
        //    Type2,
        //}

        ////[SszSerializable]
        //public class UnionClass
        //{
        //    public SomeEnum Selector { get; set; }
        //    public BasicSzzStruct? Type2 { get; set; }
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
    //    public ulong InstanceID { get; set; }
    //    public ulong Eon { get; set; }
    //    public ulong Slot { get; set; }
    //    public ulong TxPointer { get; set; }

    //    [SszList(1024)]
    //    public List<byte[]> IdentityPreimages { get; set; }
    //}

    [SszSerializable]
    public struct Test0
    {
        public Test0()
        {
            
        }
        //public ulong Test1 { get; set; } = 1;

        [SszList(1024)]
        public long[] Test2 { get; set; } = [2];

        [SszList(1024)]
        public List<long> Test3 { get; set; } = [2];

        [SszVector(5)]
        public List<long> Test4 { get; set; } = [1, 2, 3, 4, 5];

        [SszList(1024)]
        public List<byte[]> Test5 { get; set; } = [[2]];
    }
}

