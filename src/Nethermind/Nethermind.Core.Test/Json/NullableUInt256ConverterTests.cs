// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

using Nethermind.Int256;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableUInt256ConverterTests : ConverterTestBase<UInt256?>
    {
        static NullableUInt256Converter converter = new();
        static JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            TestConverter(null, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Regression_0xa00000()
        {
            UInt256? result = JsonSerializer.Deserialize<UInt256?>("\"0xa00000\"", options);
            Assert.AreEqual(UInt256.Parse("10485760"), result);
        }

        [Test]
        public void Can_read_0x0()
        {
            UInt256? result = JsonSerializer.Deserialize<UInt256?>("\"0x0\"", options);
            Assert.AreEqual(UInt256.Parse("0"), result);
        }

        [Test]
        public void Can_read_0()
        {
            UInt256? result = JsonSerializer.Deserialize<UInt256?>("0", options);
            Assert.AreEqual(UInt256.Parse("0"), result);
        }

        [Test]
        public void Can_read_1()
        {
            UInt256? result = JsonSerializer.Deserialize<UInt256?>("1", options);
            Assert.AreEqual(UInt256.Parse("1"), result);
        }
    }
}
