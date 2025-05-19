// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using System.Text.Json;
using NUnit.Framework;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class UInt256ConverterTests : ConverterTestBase<UInt256>
    {
        static readonly UInt256Converter converter = new();
        static readonly JsonSerializerOptions options = new() { Converters = { converter } };
        static bool Equals(UInt256 integer, UInt256 bigInteger) => integer.Equals(bigInteger);

        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        [TestCase(NumberConversion.Raw)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            TestConverter(int.MaxValue, static (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.One, static (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.Zero, static (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Test_Deserialize(
            [ValueSource(typeof(UInt256ConverterTests), nameof(ValuesCaseSource))] UInt256 item,
            [ValueSource(typeof(UInt256ConverterTests), nameof(FormatsCaseSource))] string format)
        {
            // fix of epic design choice made in BigInteger.ToString method that appends hex of any positive number with zero
            string asString = new Regex("0x0(.{64})|0x(.+)").Replace(string.Format(format, (BigInteger)item), (s) => $"0x{(s.Groups[1].Success ? s.Groups[1].Value : (s.Groups[2].Success ? s.Groups[2].Value : s.Groups[0].Value))}");

            JsonSerializerOptions options = new() { Converters = { converter } };

            Container? deserialized = JsonSerializer.Deserialize<Container>($"{{ \"Number\" : {asString} }}", options);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(Equals(item, deserialized.Number));

            UInt256? deserializedUint256 = JsonSerializer.Deserialize<UInt256>(asString, options);

            Assert.That(deserializedUint256, Is.Not.Null);
            Assert.That(Equals(item, deserializedUint256.Value));
        }

        public static IEnumerable<UInt256> ValuesCaseSource()
        {
            yield return UInt256.MaxValue;
            yield return UInt256.MaxValue - 1;
            yield return UInt256.MaxValue >> 8;
            yield return new UInt256(ulong.MaxValue, ulong.MaxValue);
            yield return new UInt256(ulong.MaxValue, 1);
            yield return int.MaxValue;
            yield return UInt256.One;
            yield return UInt256.Zero;
        }

        public static IEnumerable<string> FormatsCaseSource()
        {
            // lower case hex
            yield return "\"0x{0:x}\"";
            // upper case hex
            yield return "\"0x{0:X}\"";
            // hex with leading zeros
            yield return "\"0x{0:X64}\"";
            // decimal
            yield return "{0:D}";
        }

        class Container
        {
            public UInt256 Number { get; set; }
        }

        [TestCase((NumberConversion)99)]
        public void Undefined_not_supported(NumberConversion notSupportedConversion)
        {
            ForcedNumberConversion.ForcedConversion.Value = notSupportedConversion;

            UInt256Converter converter = new();
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, Equals, converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(UInt256.One, Equals, converter));

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
        }

        [Test]
        public void Raw_works_with_zero_and_this_is_ok()
        {
            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Raw;
            UInt256Converter converter = new();
            TestConverter(0, Equals, converter);

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
        }

        [Test]
        public void Regression_0xa00000()
        {
            UInt256 result = JsonSerializer.Deserialize<UInt256>("\"0xa00000\"", options);
            Assert.That(result, Is.EqualTo(UInt256.Parse("10485760")));
        }

        [Test]
        public void Can_read_0x0()
        {
            UInt256 result = JsonSerializer.Deserialize<UInt256>("\"0x0\"", options);
            Assert.That(result, Is.EqualTo(UInt256.Parse("0")));
        }

        [Test]
        public void Can_read_0x000()
        {
            UInt256 result = JsonSerializer.Deserialize<UInt256>("\"0x0000\"", options);
            Assert.That(result, Is.EqualTo(UInt256.Parse("0")));
        }

        [Test]
        public void Can_read_0()
        {
            UInt256 result = JsonSerializer.Deserialize<UInt256>("0", options);
            Assert.That(result, Is.EqualTo(UInt256.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            UInt256 result = JsonSerializer.Deserialize<UInt256>("1", options);
            Assert.That(result, Is.EqualTo(UInt256.Parse("1")));
        }

        [Test]
        public void Can_read_unmarked_hex()
        {
            UInt256 result = JsonSerializer.Deserialize<UInt256>("\"de\"", options);
            Assert.That(result, Is.EqualTo(UInt256.Parse("de", NumberStyles.HexNumber)));
        }

        [Test]
        public void Throws_on_null()
        {
            Assert.Throws<JsonException>(
                static () => JsonSerializer.Deserialize<UInt256>("null", options));
        }
    }
}
