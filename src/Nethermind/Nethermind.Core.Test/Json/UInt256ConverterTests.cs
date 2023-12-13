// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class UInt256ConverterTests : ConverterTestBase<UInt256>
    {
        static readonly UInt256Converter converter = new();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        [TestCase(NumberConversion.Raw)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [TestCase((NumberConversion)99)]
        public void Undefined_not_supported(NumberConversion notSupportedConversion)
        {
            ForcedNumberConversion.ForcedConversion.Value = notSupportedConversion;

            UInt256Converter converter = new();
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(UInt256.One, (integer, bigInteger) => integer.Equals(bigInteger), converter));

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
        }

        [Test]
        public void Raw_works_with_zero_and_this_is_ok()
        {
            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Raw;
            UInt256Converter converter = new();
            TestConverter(0, (integer, bigInteger) => integer.Equals(bigInteger), converter);

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
                () => JsonSerializer.Deserialize<UInt256>("null", options));
        }
    }
}
