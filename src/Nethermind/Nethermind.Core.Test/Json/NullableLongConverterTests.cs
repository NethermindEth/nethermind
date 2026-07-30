// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class NullableLongConverterTests : ConverterTestBase<long?>
{
    static readonly NullableLongConverter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [TestCase(int.MaxValue)]
    [TestCase(1L)]
    [TestCase(0L)]
    public void Test_roundtrip(long value) => TestConverter((long?)value, static (a, b) => a.Equals(b), converter);

    [TestCase("\"0xa00000\"", 10485760L)]
    [TestCase("\"0x0\"", 0L)]
    [TestCase("0", 0L)]
    [TestCase("1", 1L)]
    [TestCase("-1", -1L)]
    public void Can_read_value(string json, long expected)
    {
        long? result = JsonSerializer.Deserialize<long?>(json, options);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Can_read_null()
    {
        long? result = JsonSerializer.Deserialize<long?>("null", options);
        Assert.That(result, Is.EqualTo(null));
    }

    [Test]
    public void Inner_converter_receives_underlying_type_not_nullable()
    {
        Type? receivedType = null;
        TypeCapturingConverter inner = new(t => receivedType = t);
        NullableJsonConverter<long> nullable = new(inner);
        JsonSerializerOptions opts = new() { Converters = { nullable } };
        JsonSerializer.Deserialize<long?>("1", opts);
        Assert.That(receivedType, Is.EqualTo(typeof(long)));
    }

    [TestCase("\"0x0b\"")]
    [TestCase("\"0x00\"")]
    [TestCase("\"0x0ff\"")]
    public void StrictQuantity_rejects_leading_zero(string json)
    {
        JsonSerializerOptions strictOpts = new() { Converters = { new NullableLongConverter(strictQuantity: true) } };
        Assert.That(() => JsonSerializer.Deserialize<long?>(json, strictOpts), Throws.InstanceOf<FormatException>());
    }

    [Test]
    public void StrictQuantity_rejects_json_number() =>
        Assert.That(
            () => JsonSerializer.Deserialize<long?>("11", new JsonSerializerOptions { Converters = { new NullableLongConverter(strictQuantity: true) } }),
            Throws.InstanceOf<JsonException>());

    [TestCase("\"0x0\"", 0L)]
    [TestCase("\"0xb\"", 11L)]
    [TestCase("\"0xff\"", 255L)]
    public void StrictQuantity_accepts_valid_quantity(string json, long expected)
    {
        JsonSerializerOptions strictOpts = new() { Converters = { new NullableLongConverter(strictQuantity: true) } };
        long? result = JsonSerializer.Deserialize<long?>(json, strictOpts);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("\"0x0000\"")]
    [TestCase("\"0x0b\"")]
    public void Lenient_accepts_leading_zero(string json) =>
        Assert.That(() => JsonSerializer.Deserialize<long?>(json, options), Throws.Nothing);

    private class TypeCapturingConverter(Action<Type> capture) : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            capture(typeToConvert);
            return reader.GetInt64();
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            throw new NotImplementedException();
    }
}
