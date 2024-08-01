// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Numerics;
using System.Text.Json;

using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class BigIntegerConverterTests : ConverterTestBase<BigInteger>
    {
        static readonly BigIntegerConverter converter = new BigIntegerConverter();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        public void Test_roundtrip()
        {
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Can_read_0()
        {
            BigInteger result = JsonSerializer.Deserialize<BigInteger>("0", options);
            Assert.That(result, Is.EqualTo(BigInteger.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            BigInteger result = JsonSerializer.Deserialize<BigInteger>("1", options);
            Assert.That(result, Is.EqualTo(BigInteger.Parse("1")));
        }
    }
}
