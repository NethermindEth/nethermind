// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Numerics;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableBigIntegerConverterTests : ConverterTestBase<BigInteger?>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Raw)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            NullableBigIntegerConverter converter = new(numberConversion);
            TestConverter(null, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Regression_0xa00000()
        {
            BigIntegerConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            BigInteger result = converter.ReadJson(reader, typeof(BigInteger), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(BigInteger.Parse("10485760")));
        }

        [Test]
        public void Can_read_0x0()
        {
            NullableBigIntegerConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            BigInteger? result = converter.ReadJson(reader, typeof(BigInteger?), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(BigInteger.Parse("0")));
        }

        [Test]
        public void Can_read_0()
        {
            NullableBigIntegerConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            BigInteger? result = converter.ReadJson(reader, typeof(BigInteger?), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(BigInteger.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            NullableBigIntegerConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            BigInteger? result = converter.ReadJson(reader, typeof(BigInteger?), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(BigInteger.Parse("1")));
        }

        [Test]
        public void Can_read_null()
        {
            NullableBigIntegerConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("null"));
            reader.ReadAsString();
            BigInteger? result = converter.ReadJson(reader, typeof(BigInteger?), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(null));
        }
    }
}
