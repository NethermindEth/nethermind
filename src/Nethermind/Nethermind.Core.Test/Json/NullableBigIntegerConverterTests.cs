// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Text.Json;

using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableBigIntegerConverterTests : ConverterTestBase<BigInteger?>
    {
        static readonly NullableBigIntegerConverter converter = new NullableBigIntegerConverter();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };
        public void Test_roundtrip()
        {
            TestConverter(null, static (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(int.MaxValue, static (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.One, static (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.Zero, static (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Can_read_0()
        {
            BigInteger? result = JsonSerializer.Deserialize<BigInteger?>("0", options);
            Assert.That(result, Is.EqualTo(BigInteger.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            BigInteger? result = JsonSerializer.Deserialize<BigInteger?>("1", options);
            Assert.That(result, Is.EqualTo(BigInteger.Parse("1")));
        }

        [Test]
        public void Can_read_null()
        {
            BigInteger? result = JsonSerializer.Deserialize<BigInteger?>("null", options);
            Assert.That(result, Is.EqualTo(null));
        }
    }
}
