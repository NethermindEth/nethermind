// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class LongConverterTests : ConverterTestBase<long>
    {
        static readonly LongConverter converter = new();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [Test]
        public void Test_roundtrip()
        {
            TestConverter(int.MaxValue, static (a, b) => a.Equals(b), converter);
            TestConverter(1L, static (a, b) => a.Equals(b), converter);
            TestConverter(0L, static (a, b) => a.Equals(b), converter);
        }

        [Test]
        public void Regression_0xa00000()
        {
            long result = JsonSerializer.Deserialize<long>("\"0xa00000\"", options);
            Assert.That(result, Is.EqualTo(10485760));
        }

        [Test]
        public void Can_read_0x0()
        {
            long result = JsonSerializer.Deserialize<long>("\"0x0\"", options);
            Assert.That(result, Is.EqualTo(long.Parse("0")));
        }

        [Test]
        public void Can_read_0x000()
        {
            long result = JsonSerializer.Deserialize<long>("\"0x0000\"", options);
            Assert.That(result, Is.EqualTo(long.Parse("0")));
        }

        [Test]
        public void Can_read_0()
        {
            long result = JsonSerializer.Deserialize<long>("0", options);
            Assert.That(result, Is.EqualTo(long.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            long result = JsonSerializer.Deserialize<long>("1", options);
            Assert.That(result, Is.EqualTo(long.Parse("1")));
        }

        [Test]
        public void Throws_on_null()
        {
            Assert.Throws<JsonException>(
                static () => JsonSerializer.Deserialize<long>("null", options));
        }

        [TestCase(0L, "\"0x0\"")]
        [TestCase(1L, "\"0x1\"")]
        [TestCase(15L, "\"0xf\"")]
        [TestCase(16L, "\"0x10\"")]
        [TestCase(255L, "\"0xff\"")]
        [TestCase(256L, "\"0x100\"")]
        [TestCase(0xabcdefL, "\"0xabcdef\"")]
        [TestCase(0x1L, "\"0x1\"")]
        [TestCase(0x10L, "\"0x10\"")]
        [TestCase(0x100L, "\"0x100\"")]
        [TestCase(0x1000L, "\"0x1000\"")]
        [TestCase(0x10000L, "\"0x10000\"")]
        [TestCase(0x100000L, "\"0x100000\"")]
        [TestCase(0x1000000L, "\"0x1000000\"")]
        [TestCase(0x10000000L, "\"0x10000000\"")]
        [TestCase(int.MaxValue, "\"0x7fffffff\"")]
        [TestCase(long.MaxValue, "\"0x7fffffffffffffff\"")]
        [TestCase(-1L, "\"0xffffffffffffffff\"")]
        [TestCase(-9223372036854775808L, "\"0x8000000000000000\"")] // long.MinValue
        public void Writes_correct_hex(long value, string expected)
        {
            string result = JsonSerializer.Serialize(value, options);
            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void Writes_hex_roundtrip_all_nibble_counts()
        {
            // Test every nibble count from 1 to 16
            for (int nibbles = 1; nibbles <= 16; nibbles++)
            {
                long value = nibbles <= 15
                    ? 1L << ((nibbles - 1) * 4)
                    : unchecked((long)0x8000000000000000UL);

                string json = JsonSerializer.Serialize(value, options);
                long deserialized = JsonSerializer.Deserialize<long>(json, options);
                Assert.That(deserialized, Is.EqualTo(value), $"Roundtrip failed for nibbles={nibbles}, value=0x{(ulong)value:x}");
            }
        }
    }
}
