// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            Assert.That(converter.CanConvert(type), Is.EqualTo(true));
        }

        [TestCase(typeof(object))]
        [TestCase(typeof(IdConverterTests))]
        public void It_supports_all_silly_types_and_we_can_live_with_it(Type type)
        {
            IdConverter converter = new();
            Assert.That(converter.CanConvert(type), Is.EqualTo(true));
        }

        [TestCase("{\"id\":1234}")]
        [TestCase("{\"id\":123498132871289317239813219}")]
        [TestCase("{\"id\":\"test\"}")]
        [TestCase("{\"id\":null}")]
        public void JsonRpcId_can_do_roundtrip(string json) => TestRoundtrip<SomethingWithJsonRpcId>(json);

        [TestCase("1e2", "100")]
        [TestCase("12345678901234567890", "12345678901234567890")]
        public void JsonRpcId_preserves_decimal_raw_writeback(string idJson, string expectedValue)
        {
            JsonRpcId id = DeserializeId(idJson);

            Assert.That(id.TryGetDecimal(out decimal value), Is.True);
            Assert.That(value, Is.EqualTo(decimal.Parse(expectedValue, CultureInfo.InvariantCulture)));
            Assert.That(Serialize(id), Is.EqualTo(idJson));
        }

        [TestCase("2.1")]
        public void JsonRpcId_rejects_fractional_decimal(string idJson)
        {
            Action deserialize = () => DeserializeId(idJson);

            Assert.That(deserialize, Throws.TypeOf<NotSupportedException>());
        }

        [Test]
        public void JsonRpcId_preserves_missing_and_explicit_null_states()
        {
            Assert.That(JsonRpcId.Missing.IsMissing, Is.True);
            Assert.That(JsonRpcId.Null.IsNull, Is.True);
            Assert.That(JsonRpcId.Missing, Is.Not.EqualTo(JsonRpcId.Null));
            Assert.That(Serialize(JsonRpcId.Missing), Is.EqualTo("null"));
            Assert.That(Serialize(JsonRpcId.Null), Is.EqualTo("null"));
        }

        [Test]
        public void JsonRpcId_deserializes_absent_id_as_missing()
        {
            SomethingWithJsonRpcId? value = JsonSerializer.Deserialize<SomethingWithJsonRpcId>("{}", _jsonRpcIdOptions);

            Assert.That(value!.Id.IsMissing, Is.True);
        }

        [Test]
        public void JsonRpcId_escapes_string_values()
        {
            const string idValue = "a\"\\\n\u263A";
            JsonRpcId id = new(idValue);

            using JsonDocument document = JsonDocument.Parse(Serialize(id));
            Assert.That(document.RootElement.GetString(), Is.EqualTo(idValue));
        }

        [Test]
        public void JsonRpcId_bridges_legacy_object_values()
        {
            string stringId = new(['t', 'e', 's', 't']);

            Assert.That(JsonRpcId.FromObject(null), Is.EqualTo(JsonRpcId.Null));
            Assert.That(JsonRpcId.FromObject(1).ToObject(), Is.EqualTo(1L));
            Assert.That(JsonRpcId.FromObject(2L).ToObject(), Is.EqualTo(2L));
            Assert.That(JsonRpcId.FromObject(3m).ToObject(), Is.EqualTo(3m));
            Assert.That(JsonRpcId.FromObject(stringId).ToObject(), Is.SameAs(stringId));
        }

        [Test]
        public void JsonRpcId_object_equals_never_matches_null()
        {
            Assert.That(JsonRpcId.Missing.Equals((object?)null), Is.False);
            Assert.That(JsonRpcId.Null.Equals((object?)null), Is.False);
        }

        [TestCaseSource(nameof(JsonRpcIdEqualityCases))]
        public void JsonRpcId_equality_and_hashing(JsonRpcId left, JsonRpcId right, bool expected)
        {
            Assert.That(left.Equals(right), Is.EqualTo(expected));
            Assert.That(right.Equals(left), Is.EqualTo(expected));

            if (expected)
            {
                Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
            }
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

        private static JsonRpcId DeserializeId(string json) => JsonSerializer.Deserialize<JsonRpcId>(json, _jsonRpcIdOptions);

        private static readonly TestCaseData[] JsonRpcIdEqualityCases =
        [
            new TestCaseData(JsonRpcId.Missing, JsonRpcId.Missing, true).SetName("Missing"),
            new TestCaseData(JsonRpcId.Null, JsonRpcId.Null, true).SetName("ExplicitNull"),
            new TestCaseData(JsonRpcId.Missing, JsonRpcId.Null, false).SetName("MissingVsNull"),
            new TestCaseData(DeserializeId("\"\\u0041\\n\""), new JsonRpcId("A\n"), true).SetName("EscapedString"),
            new TestCaseData(DeserializeId("1e2"), new JsonRpcId(100m), true).SetName("RawDecimal"),
            new TestCaseData(new JsonRpcId(1), new JsonRpcId(1m), false).SetName("LongVsDecimalKind")
        ];

        private static readonly TestCaseData[] JsonRpcResponseIdCases =
        [
            new TestCaseData(JsonRpcId.Missing, "null").SetName("Missing"),
            new TestCaseData(JsonRpcId.Null, "null").SetName("ExplicitNull"),
            new TestCaseData(new JsonRpcId(1), "1").SetName("Long"),
            new TestCaseData(new JsonRpcId(1234m), "1234").SetName("DecimalInteger"),
            new TestCaseData(DeserializeId("1e2"), "1e2").SetName("RawDecimalInteger"),
            new TestCaseData(new JsonRpcId(12345678901234567890m), "12345678901234567890").SetName("LargeDecimalInteger"),
            new TestCaseData(new JsonRpcId("840b55c4-18b0-431c-be1d-6d22198b53f2"), "\"840b55c4-18b0-431c-be1d-6d22198b53f2\"").SetName("GuidString"),
            new TestCaseData(new JsonRpcId("a\"\\\n\u263A"), "\"a\\u0022\\\\\\n\\u263A\"").SetName("EscapedString"),
            new TestCaseData(new JsonRpcId("test"), "\"test\"").SetName("String")
        ];
    }
}
