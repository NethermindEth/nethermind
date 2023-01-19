// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableLongConverterTests : ConverterTestBase<long?>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            NullableLongConverter converter = new(numberConversion);
            TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter);
            TestConverter(1L, (a, b) => a.Equals(b), converter);
            TestConverter(0L, (a, b) => a.Equals(b), converter);
        }

        [TestCase((NumberConversion)99)]
        public void Unknown_not_supported(NumberConversion notSupportedConversion)
        {
            NullableLongConverter converter = new(notSupportedConversion);
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(1L, (a, b) => a.Equals(b), converter));
        }

        [Test]
        public void Regression_0xa00000()
        {
            NullableLongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            long? result = converter.ReadJson(reader, typeof(long?), 0, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(10485760, result);
        }

        [Test]
        public void Can_read_0x0()
        {
            NullableLongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            long? result = converter.ReadJson(reader, typeof(long?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("0"), result);
        }

        [Test]
        public void Can_read_0x000()
        {
            NullableLongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0000"));
            reader.ReadAsString();
            long? result = converter.ReadJson(reader, typeof(long?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("0"), result);
        }

        [Test]
        public void Can_read_0()
        {
            NullableLongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            long? result = converter.ReadJson(reader, typeof(long?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("0"), result);
        }

        [Test]
        public void Can_read_1()
        {
            NullableLongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            long? result = converter.ReadJson(reader, typeof(long?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("1"), result);
        }

        [Test]
        public void Can_read_null()
        {
            NullableLongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("null"));
            reader.ReadAsString();
            long? result = converter.ReadJson(reader, typeof(long?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(null, result);
        }

        [Test]
        public void Can_read_negative_numbers()
        {
            NullableLongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("-1"));
            reader.ReadAsString();
            long? result = converter.ReadJson(reader, typeof(long?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("-1"), result);
        }
    }
}
