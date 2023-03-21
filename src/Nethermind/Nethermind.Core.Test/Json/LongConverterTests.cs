// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class LongConverterTests : ConverterTestBase<long>
    {
        static LongConverter converter = new();
        static JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter);
            TestConverter(1L, (a, b) => a.Equals(b), converter);
            TestConverter(0L, (a, b) => a.Equals(b), converter);
        }

        [TestCase((NumberConversion)99)]
        public void Unknown_not_supported(NumberConversion notSupportedConversion)
        {
            //LongConverter converter = new(notSupportedConversion);
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(1L, (a, b) => a.Equals(b), converter));
        }

        [Test]
        public void Regression_0xa00000()
        {
            long result = JsonSerializer.Deserialize<long>("0xa00000", options);
            Assert.AreEqual(10485760, result);
        }

        [Test]
        public void Can_read_0x0()
        {
            long result = JsonSerializer.Deserialize<long>("0x0", options);
            Assert.AreEqual(long.Parse("0"), result);
        }

        [Test]
        public void Can_read_0x000()
        {
            long result = JsonSerializer.Deserialize<long>("0x0000", options);
            Assert.AreEqual(long.Parse("0"), result);
        }

        [Test]
        public void Can_read_0()
        {
            long result = JsonSerializer.Deserialize<long>("0", options);
            Assert.AreEqual(long.Parse("0"), result);
        }

        [Test]
        public void Can_read_1()
        {
            long result = JsonSerializer.Deserialize<long>("1", options);
            Assert.AreEqual(long.Parse("1"), result);
        }

        [Test]
        public void Throws_on_null()
        {
            Assert.Throws<JsonException>(
                () => JsonSerializer.Deserialize<long>("null", options));
        }
    }
}
