// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class NullableULongConverterTests : ConverterTestBase<ulong?>
{
    static readonly NullableULongConverter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [Test]
    public void Test_roundtrip([Values((ulong)int.MaxValue, 1UL, 0UL)] ulong value) => TestConverter((ulong?)value, static (a, b) => a.Equals(b), converter);

    [TestCase("\"0xa00000\"", 10485760UL)]
    [TestCase("\"0x0\"", 0UL)]
    [TestCase("0", 0UL)]
    [TestCase("1", 1UL)]
    public void Can_read_value(string json, ulong expected)
    {
        ulong? result = JsonSerializer.Deserialize<ulong?>(json, options);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Can_read_null()
    {
        ulong? result = JsonSerializer.Deserialize<ulong?>("null", options);
        Assert.That(result, Is.EqualTo(null));
    }

    [Test]
    public void Throws_on_negative_numbers() => Assert.Throws<JsonException>(
            static () => JsonSerializer.Deserialize<ulong?>("-1", options));

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

    [Test]
    public void StrictQuantity_rejects_leading_zero([Values("\"0x0b\"", "\"0x00\"", "\"0x0ff\"")] string json)
    {
        JsonSerializerOptions strictOpts = new() { Converters = { new NullableULongConverter(strictQuantity: true) } };
        Assert.That(() => JsonSerializer.Deserialize<ulong?>(json, strictOpts), Throws.InstanceOf<FormatException>());
    }

    [Test]
    public void StrictQuantity_rejects_json_number() =>
        Assert.That(
            () => JsonSerializer.Deserialize<ulong?>("11", new JsonSerializerOptions { Converters = { new NullableULongConverter(strictQuantity: true) } }),
            Throws.InstanceOf<JsonException>());

    [TestCase("\"0x0\"", 0UL)]
    [TestCase("\"0xb\"", 11UL)]
    [TestCase("\"0xff\"", 255UL)]
    public void StrictQuantity_accepts_valid_quantity(string json, ulong expected)
    {
        JsonSerializerOptions strictOpts = new() { Converters = { new NullableULongConverter(strictQuantity: true) } };
        ulong? result = JsonSerializer.Deserialize<ulong?>(json, strictOpts);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Lenient_accepts_leading_zero([Values("\"0x0000\"", "\"0x0b\"")] string json) =>
        Assert.That(() => JsonSerializer.Deserialize<ulong?>(json, options), Throws.Nothing);
}
