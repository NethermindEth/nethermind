// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
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
    }
}
