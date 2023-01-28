// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableUInt256ConverterTests : ConverterTestBase<UInt256?>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            NullableUInt256Converter converter = new(numberConversion);
            TestConverter(null, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Regression_0xa00000()
        {
            NullableUInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("10485760"), result);
        }

        [Test]
        public void Can_read_0x0()
        {
            NullableUInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("0"), result);
        }

        [Test]
        public void Can_read_0()
        {
            NullableUInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("0"), result);
        }

        [Test]
        public void Can_read_1()
        {
            NullableUInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("1"), result);
        }
    }
}
