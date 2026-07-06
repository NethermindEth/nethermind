// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class LongConverterTests : ConverterTestBase<long>
{
    static readonly LongConverter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [TestCase(int.MaxValue)]
    [TestCase(1L)]
    [TestCase(0L)]
    public void Test_roundtrip(long value) => TestConverter(value, static (a, b) => a.Equals(b), converter);

    [TestCase("\"0xa00000\"", 10485760L)]
    [TestCase("\"0x0\"", 0L)]
    [TestCase("0", 0L)]
    [TestCase("1", 1L)]
    public void Can_read_value(string json, long expected)
    {
        long result = JsonSerializer.Deserialize<long>(json, options);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Throws_on_null() => Assert.Throws<JsonException>(
            static () => JsonSerializer.Deserialize<long>("null", options));

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

    [TestCase("\"0x0b\"")]
    [TestCase("\"0x00\"")]
    [TestCase("\"0x0ff\"")]
    public void StrictQuantity_rejects_leading_zero(string json)
    {
        JsonSerializerOptions strictOpts = new() { Converters = { new LongConverter(strictQuantity: true) } };
        Assert.That(() => JsonSerializer.Deserialize<long>(json, strictOpts), Throws.InstanceOf<FormatException>());
    }

    [Test]
    public void StrictQuantity_rejects_json_number() =>
        Assert.That(
            () => JsonSerializer.Deserialize<long>("11", new JsonSerializerOptions { Converters = { new LongConverter(strictQuantity: true) } }),
            Throws.InstanceOf<JsonException>());

    [TestCase("\"0x0\"", 0L)]
    [TestCase("\"0xb\"", 11L)]
    [TestCase("\"0xff\"", 255L)]
    public void StrictQuantity_accepts_valid_quantity(string json, long expected)
    {
        JsonSerializerOptions strictOpts = new() { Converters = { new LongConverter(strictQuantity: true) } };
        long result = JsonSerializer.Deserialize<long>(json, strictOpts);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("\"0x0000\"")]
    [TestCase("\"0x0b\"")]
    public void Lenient_accepts_leading_zero(string json) =>
        Assert.That(() => JsonSerializer.Deserialize<long>(json, options), Throws.Nothing);
}
