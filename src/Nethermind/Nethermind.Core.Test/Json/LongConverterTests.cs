// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
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

        public void Test_roundtrip()
        {
            TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter);
            TestConverter(1L, (a, b) => a.Equals(b), converter);
            TestConverter(0L, (a, b) => a.Equals(b), converter);
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
                () => JsonSerializer.Deserialize<long>("null", options));
        }
    }
}
