// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class IdConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip_big()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":123498132871289317239813219}");
        }

        [Test]
        public void Can_handle_int()
        {
            IdConverter converter = new();
            converter.Write(new Utf8JsonWriter(new MemoryStream()), 1, null);
        }

        [Test]
        public void Throws_on_writing_decimal()
        {
            IdConverter converter = new();
            Assert.Throws<NotSupportedException>(
                () => converter.Write(new Utf8JsonWriter(new MemoryStream()), 1.1, null));
        }

        [TestCase(typeof(int))]
        [TestCase(typeof(string))]
        [TestCase(typeof(long))]
        [TestCase(typeof(BigInteger))]
        [TestCase(typeof(BigInteger?))]
        [TestCase(typeof(UInt256?))]
        [TestCase(typeof(UInt256))]
        public void It_supports_the_types_that_it_needs_to_support(Type type)
        {
            IdConverter converter = new();
            converter.CanConvert(type).Should().Be(true);
        }

        [TestCase(typeof(object))]
        [TestCase(typeof(IdConverterTests))]
        public void It_supports_all_silly_types_and_we_can_live_with_it(Type type)
        {
            IdConverter converter = new();
            converter.CanConvert(type).Should().Be(true);
        }

        [Test]
        public void Can_do_roundtrip_long()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":1234}");
        }

        [Test]
        public void Can_do_roundtrip_string()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":\"asdasdasd\"}");
        }

        [Test]
        public void Can_do_roundtrip_null()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":null}");
        }

        [Test]
        public void Decimal_not_supported()
        {
            Assert.Throws<NotSupportedException>(() =>
                TestRoundtrip<SomethingWithId>("{\"id\":2.1}"));

            Assert.Throws<NotSupportedException>(() =>
                TestRoundtrip<SomethingWithDecimalId>("{\"id\":2.1}"));
        }

        // ReSharper disable once ClassNeverInstantiated.Global
        // ReSharper disable once MemberCanBePrivate.Global
        public class SomethingWithId
        {
            [JsonConverter(typeof(IdConverter))]
            [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
            public object Id { get; set; } = null!;

            public string Something { get; set; } = null!;
        }

        public class SomethingWithDecimalId
        {
            [JsonConverter(typeof(IdConverter))]
            [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
            public decimal Id { get; set; }

            public string Something { get; set; } = null!;
        }
    }
}
