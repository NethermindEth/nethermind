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
        private static readonly JsonSerializerOptions _jsonRpcIdOptions = new()
        {
            Converters = { new JsonRpcIdConverter() }
        };

        [TestCase("{\"id\":1234}", TestName = "Long")]
        [TestCase("{\"id\":123498132871289317239813219}", TestName = "BigInteger")]
        [TestCase("{\"id\":\"test\"}", TestName = "String")]
        [TestCase("{\"id\":null}", TestName = "Null")]
        public void Can_do_roundtrip_object_id(string json) => TestRoundtrip<SomethingWithId>(json);

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

        [TestCase("{\"id\":1234}")]
        [TestCase("{\"id\":123498132871289317239813219}")]
        [TestCase("{\"id\":\"test\"}")]
        [TestCase("{\"id\":null}")]
        public void JsonRpcId_can_do_roundtrip(string json) => TestRoundtrip<SomethingWithJsonRpcId>(json);

        [Test]
        public void JsonRpcId_preserves_missing_and_explicit_null_states()
        {
            JsonRpcId.Missing.IsMissing.Should().BeTrue();
            JsonRpcId.Null.IsNull.Should().BeTrue();
            JsonRpcId.Missing.Should().NotBe(JsonRpcId.Null);
            Serialize(JsonRpcId.Missing).Should().Be("null");
            Serialize(JsonRpcId.Null).Should().Be("null");
        }

        [Test]
        public void JsonRpcId_deserializes_absent_id_as_missing()
        {
            SomethingWithJsonRpcId? value = JsonSerializer.Deserialize<SomethingWithJsonRpcId>("{}", _jsonRpcIdOptions);

            value!.Id.IsMissing.Should().BeTrue();
        }

        [Test]
        public void JsonRpcId_escapes_string_values()
        {
            const string idValue = "a\"\\\n\u263A";
            JsonRpcId id = new(idValue);

            using JsonDocument document = JsonDocument.Parse(Serialize(id));
            document.RootElement.GetString().Should().Be(idValue);
        }

        [Test]
        public void JsonRpcId_bridges_legacy_object_values()
        {
            JsonRpcId.FromObject(null).Should().Be(JsonRpcId.Null);
            JsonRpcId.FromObject(1).ToObject().Should().Be(1L);
            JsonRpcId.FromObject(2L).ToObject().Should().Be(2L);
            JsonRpcId.FromObject(3m).ToObject().Should().Be(3m);
            JsonRpcId.FromObject("test").ToObject().Should().Be("test");
        }

        [Test]
        public void JsonRpcId_object_equals_never_matches_null()
        {
            JsonRpcId.Missing.Equals((object?)null).Should().BeFalse();
            JsonRpcId.Null.Equals((object?)null).Should().BeFalse();
        }

        [TestCaseSource(nameof(JsonRpcResponseIdCases))]
        public void JsonRpcResponse_serializes_typed_ids(JsonRpcId id, string expectedIdJson)
        {
            JsonRpcSuccessResponse response = new() { Id = id, Result = "0x1" };

            TestToJson(response, $"{{\"jsonrpc\":\"2.0\",\"result\":\"0x1\",\"id\":{expectedIdJson}}}");
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

        public class SomethingWithJsonRpcId
        {
            [JsonConverter(typeof(JsonRpcIdConverter))]
            [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
            public JsonRpcId Id { get; set; }

            public string Something { get; set; } = null!;
        }

        private static string Serialize(JsonRpcId id) => JsonSerializer.Serialize(id, _jsonRpcIdOptions);

        private static readonly TestCaseData[] JsonRpcResponseIdCases =
        [
            new TestCaseData(JsonRpcId.Missing, "null").SetName("Missing"),
            new TestCaseData(JsonRpcId.Null, "null").SetName("ExplicitNull"),
            new TestCaseData(new JsonRpcId(1), "1").SetName("Long"),
            new TestCaseData(new JsonRpcId(1234m), "1234").SetName("DecimalInteger"),
            new TestCaseData(new JsonRpcId(12345678901234567890m), "12345678901234567890").SetName("LargeDecimalInteger"),
            new TestCaseData(new JsonRpcId("test"), "\"test\"").SetName("String")
        ];
    }
}
