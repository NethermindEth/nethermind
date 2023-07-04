// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class UInt256ConverterTests : ConverterTestBase<UInt256>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            UInt256Converter converter = new(numberConversion);
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [TestCase((NumberConversion)99)]
        [TestCase(NumberConversion.Raw)]
        public void Raw_not_supported(NumberConversion notSupportedConversion)
        {
            UInt256Converter converter = new(notSupportedConversion);
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(UInt256.One, (integer, bigInteger) => integer.Equals(bigInteger), converter));
        }

        [Test]
        public void Raw_works_with_zero_and_this_is_ok()
        {
            UInt256Converter converter = new(NumberConversion.Raw);
            TestConverter(0, (integer, bigInteger) => integer.Equals(bigInteger), converter);

            converter = new UInt256Converter((NumberConversion)99);
            TestConverter(0, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Regression_0xa00000()
        {
            UInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            UInt256 result = converter.ReadJson(reader, typeof(UInt256), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(UInt256.Parse("10485760")));
        }

        [Test]
        public void Can_read_0x0()
        {
            UInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            UInt256 result = converter.ReadJson(reader, typeof(UInt256), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(UInt256.Parse("0")));
        }

        [Test]
        public void Can_read_0x000()
        {
            UInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0000"));
            reader.ReadAsString();
            UInt256 result = converter.ReadJson(reader, typeof(UInt256), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(UInt256.Parse("0")));
        }

        [Test]
        public void Can_read_0()
        {
            UInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            UInt256 result = converter.ReadJson(reader, typeof(UInt256), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(UInt256.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            UInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            UInt256 result = converter.ReadJson(reader, typeof(UInt256), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(UInt256.Parse("1")));
        }

        [Test]
        public void Can_read_unmarked_hex()
        {
            UInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("\"de\""));
            reader.ReadAsString();
            UInt256 result = converter.ReadJson(reader, typeof(UInt256), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(UInt256.Parse("de", NumberStyles.HexNumber)));
        }

        [Test]
        public void Throws_on_null()
        {
            UInt256Converter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("null"));
            reader.ReadAsString();
            Assert.Throws<JsonException>(
                () => converter.ReadJson(reader, typeof(UInt256), UInt256.Zero, false, JsonSerializer.CreateDefault()));
        }
    }
}
