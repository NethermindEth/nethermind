// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using System;

namespace Nethermind.Serialization.SszGenerator.Test
{
    public class SszTests
    {
        [SszSerializable]
        public class EmptyStruct
        {

        }

        [SszSerializable]
        public class BasicSzzStruct
        {
            public int Number { get; set; }
            public int? NumberOrNull { get; set; }
            public byte[] Array { get; set; } = [];
            public byte[]? ArrayOrNull { get; set; }
        }

        [Test]
        public void Test_roundtrip()
        {
            BasicSzzStruct s = new BasicSzzStruct { Number = 42, Array = [1, 2, 3] };
            Generated.BasicSzzStructSszSerializer serializer = new();
            ReadOnlySpan<byte> data = serializer.Serialize(s);
            //s = serializer.Deserialize(data);
            Assert.That(s.Number, Is.EqualTo(42));
            Assert.That(s.Array, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }
}


