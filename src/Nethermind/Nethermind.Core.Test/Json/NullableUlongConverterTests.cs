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

        [TestCase((NumberConversion)99)]
        public void Unknown_not_supported(NumberConversion notSupportedConversion)
        {
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(1L, (a, b) => a.Equals(b), converter));
        }

        [Test]
        public void Regression_0xa00000()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("0xa00000", options);
            Assert.AreEqual(10485760, result);
        }

        [Test]
        public void Can_read_0x0()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("0x0", options);
            Assert.AreEqual(ulong.Parse("0"), result);
        }

        [Test]
        public void Can_read_0x000()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("0x000", options);
            Assert.AreEqual(ulong.Parse("0"), result);
        }

        [Test]
        public void Can_read_0()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("0", options);
            Assert.AreEqual(ulong.Parse("0"), result);
        }

        [Test]
        public void Can_read_1()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("1", options);
            Assert.AreEqual(ulong.Parse("1"), result);
        }

        [Test]
        public void Can_read_null()
        {
            ulong? result = JsonSerializer.Deserialize<ulong?>("null", options);
            Assert.AreEqual(null, result);
        }

        [Test]
        public void Throws_on_negative_numbers()
        {
            Assert.Throws<OverflowException>(
                () => JsonSerializer.Deserialize<ulong?>("-1", options));
        }
    }
}
