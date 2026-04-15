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
    [TestCase("\"0x0000\"", 0L)]
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
