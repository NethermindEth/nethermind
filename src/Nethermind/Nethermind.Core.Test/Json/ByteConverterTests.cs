// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class ByteConverterTests : ConverterTestBase<byte>
{
    [TestCase((byte)0, "\"0x0\"")]
    [TestCase((byte)1, "\"0x1\"")]
    [TestCase((byte)0x10, "\"0x10\"")]
    [TestCase(byte.MaxValue, "\"0xff\"")]
    public void Serializes_as_hex_quantity(byte value, string expectedJson) =>
        TestConverter(value, expectedJson, new ByteConverter());

    [TestCase("3", (byte)3)]
    [TestCase("255", byte.MaxValue)]
    [TestCase("\"0x3\"", (byte)3)]
    [TestCase("\"3\"", (byte)3)]
    public void Reads_a_number_or_a_quantity(string json, byte expected) =>
        Assert.That(JsonSerializer.Deserialize<byte>(json, new JsonSerializerOptions { Converters = { new ByteConverter() } }), Is.EqualTo(expected));

    [Test]
    public void Rejects_out_of_range_and_non_numeric_tokens([Values("256", "-1", "\"0x100\"", "\"0xzz\"", "null", "true", "{}")] string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<byte>(json, new JsonSerializerOptions { Converters = { new ByteConverter() } }));
}
