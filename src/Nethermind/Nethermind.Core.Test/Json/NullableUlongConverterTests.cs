// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableULongConverterTests : ConverterTestBase<ulong?>
    {
        static readonly NullableULongConverter converter = new();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            TestConverter(int.MaxValue, static (a, b) => a.Equals(b), converter);
            TestConverter(1L, static (a, b) => a.Equals(b), converter);
            TestConverter(0L, static (a, b) => a.Equals(b), converter);
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
                static () => JsonSerializer.Deserialize<ulong?>("-1", options));
        }

        [TestCase(0UL, "\"0x0\"")]
        [TestCase(1UL, "\"0x1\"")]
        [TestCase(15UL, "\"0xf\"")]
        [TestCase(16UL, "\"0x10\"")]
        [TestCase(255UL, "\"0xff\"")]
        [TestCase(0xabcdefUL, "\"0xabcdef\"")]
        [TestCase(0xffffffffUL, "\"0xffffffff\"")]
        [TestCase(0x100000000UL, "\"0x100000000\"")]
        [TestCase(ulong.MaxValue, "\"0xffffffffffffffff\"")]
        public void Writes_correct_hex(ulong value, string expected)
        {
            string result = JsonSerializer.Serialize((ulong?)value, options);
            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void Writes_hex_roundtrip_all_nibble_counts()
        {
            for (int nibbles = 1; nibbles <= 16; nibbles++)
            {
                ulong value = 1UL << ((nibbles - 1) * 4);
                string json = JsonSerializer.Serialize((ulong?)value, options);
                ulong? deserialized = JsonSerializer.Deserialize<ulong?>(json, options);
                Assert.That(deserialized, Is.EqualTo(value), $"Roundtrip failed for nibbles={nibbles}, value=0x{value:x}");
            }
        }
    }
}
