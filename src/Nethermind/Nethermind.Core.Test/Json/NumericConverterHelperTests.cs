// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class NumericConverterHelperTests
{
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

    [TestCase("g")]
    [TestCase("12345g78")]
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
