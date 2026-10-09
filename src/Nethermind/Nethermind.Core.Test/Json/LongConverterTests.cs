// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class LongConverterTests : ConverterTestBase<long>
{
    static readonly LongConverter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [Test]
    public void Test_roundtrip([Values(int.MaxValue, 1L, 0L)] long value) => TestConverter(value, static (a, b) => a.Equals(b), converter);

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

    [Test]
    public void StrictQuantity_rejects_leading_zero([Values("\"0x0b\"", "\"0x00\"", "\"0x0ff\"")] string json)
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

    [Test]
    public void Lenient_accepts_leading_zero([Values("\"0x0000\"", "\"0x0b\"")] string json) =>
        Assert.That(() => JsonSerializer.Deserialize<long>(json, options), Throws.Nothing);

    // Mixed case and every nibble value; each digit count takes its prefix.
    private const string Digits = "fEdCbA9876543210";

    [Test]
    public void Parse_hex_matches_runtime_parser_at_every_digit_count([Range(1, 16)] int digitCount)
    {
        string digits = Digits[..digitCount];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Parse<ulong>(digits), Is.EqualTo(ulong.Parse(digits, NumberStyles.AllowHexSpecifier)));
            Assert.That(Parse<long>(digits), Is.EqualTo(long.Parse(digits, NumberStyles.AllowHexSpecifier)));
            if (digitCount <= 8)
            {
                Assert.That(Parse<int>(digits), Is.EqualTo(int.Parse(digits, NumberStyles.AllowHexSpecifier)));
                Assert.That(Parse<uint>(digits), Is.EqualTo(uint.Parse(digits, NumberStyles.AllowHexSpecifier)));
            }
        }
    }

    [TestCase("0000000000000000001")]
    [TestCase("00000000000000000000ff")]
    public void Parse_hex_accepts_leading_zeros_past_the_width(string digits) =>
        Assert.That(Parse<ulong>(digits), Is.EqualTo(ulong.Parse(digits, NumberStyles.AllowHexSpecifier)));

    [TestCase("1\0")]
    [TestCase("ffffffffffffffff\0\0")]
    [TestCase("0000000000000000001\0")]
    public void Parse_hex_ignores_trailing_nuls_like_the_runtime_parser(string digits) =>
        Assert.That(Parse<ulong>(digits), Is.EqualTo(ulong.Parse(digits, NumberStyles.AllowHexSpecifier)));

    [TestCase("")]
    [TestCase("\0")]
    [TestCase("g")]
    [TestCase("12345g78")]
    [TestCase("12345678g")]
    [TestCase("123456789abcde:")]
    [TestCase("1234567 ")]
    [TestCase("123456789abcdef:")]
    [TestCase("10000000000000000")]
    public void Parse_hex_rejects_invalid_or_overflowing_digits(string digits) =>
        Assert.Throws<JsonException>(() => Parse<ulong>(digits));

    [Test]
    public void Parse_hex_rejects_32_bit_overflow()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<JsonException>(() => Parse<int>("100000000"));
            Assert.Throws<JsonException>(() => Parse<uint>("100000000"));
        }
    }

    private static T Parse<T>(string digits) where T : struct, INumberBase<T> =>
        NumericConverterHelper.Parse<T>(System.Text.Encoding.ASCII.GetBytes("0x" + digits));
}
