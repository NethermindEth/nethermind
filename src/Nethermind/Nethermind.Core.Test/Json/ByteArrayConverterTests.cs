// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class ByteArrayConverterTests : ConverterTestBase<byte[]>
    {
        [TestCase(null)]
        [TestCase(new byte[0])]
        [TestCase(new byte[] { 1 })]
        public void Test_roundtrip(byte[]? bytes)
        {
            TestConverter(bytes, static (before, after) => Bytes.AreEqual(before, after), new ByteArrayConverter());
        }

        [Test]
        public void Test_roundtrip_large()
        {
            ByteArrayConverter converter = new();
            for (var i = 0; i < 1024; i++)
            {
                byte[] bytes = new byte[i];
                for (var j = 0; j < i; j++)
                {
                    bytes[j] = (byte)j;
                }

                TestConverter(bytes, static (before, after) => Bytes.AreEqual(before, after), converter);
            }
        }

        [Test]
        public void Direct_null()
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();
            var result = serializer.Serialize<byte[]?>(null);
            result.Should().Be("null");
        }

        [Test]
        public void Test_DictionaryKey()
        {
            var random = new CryptoRandom();
            var dictionary = new Dictionary<byte[], int?>
            {
                { Bytes.FromHexString("0x0"), null },
                { Bytes.FromHexString("0x1"), random.NextInt(int.MaxValue) },
                { Build.An.Address.TestObject.Bytes, random.NextInt(int.MaxValue) },
                { random.GenerateRandomBytes(10), random.NextInt(int.MaxValue) },
                { random.GenerateRandomBytes(32), random.NextInt(int.MaxValue) },
            };

            TestConverter(dictionary, new ByteArrayConverter());
        }
    }
}
