// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableULongConverterTests : ConverterTestBase<ulong?>
    {
        static NullableULongConverter converter = new();
        static JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter);
            TestConverter(1L, (a, b) => a.Equals(b), converter);
            TestConverter(0L, (a, b) => a.Equals(b), converter);
        }

        [Test]
        public void Regression_0xa00000()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("\"0xa00000\"", options);
            Assert.That(result, Is.EqualTo(10485760));
        }

        [Test]
        public void Can_read_0x0()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("\"0x0\"", options);
            Assert.That(result, Is.EqualTo(ulong.Parse("0")));
        }

        [Test]
        public void Can_read_0x000()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("\"0x000\"", options);
            Assert.That(result, Is.EqualTo(ulong.Parse("0")));
        }

        [Test]
        public void Can_read_0()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("0", options);
            Assert.That(result, Is.EqualTo(ulong.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("1", options);
            Assert.That(result, Is.EqualTo(ulong.Parse("1")));
        }

        [Test]
        public void Can_read_null()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("null", options);
            Assert.That(result, Is.EqualTo(null));
        }

        [Test]
        public void Throws_on_negative_numbers()
        {
            Assert.Throws<JsonException>(
                () => JsonSerializer.Deserialize<ulong?>("-1", options));
        }
    }
}
