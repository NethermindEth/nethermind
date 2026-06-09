// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class ValueHash256ConverterTests
{
    private const string ValidHex = "0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string Hex31Bytes = "0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e";
    private const string Hex33Bytes = "0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    private static readonly JsonSerializerOptions _options = new() { Converters = { new ValueHash256Converter() } };

    [Test]
    public void Roundtrips_valid_32_byte_hash()
    {
        byte[] data = new byte[ValueHash256.MemorySize];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        ValueHash256 hash = new(data);

        string json = JsonSerializer.Serialize(hash, _options);
        ValueHash256 deserialized = JsonSerializer.Deserialize<ValueHash256>(json, _options);

        Assert.That(deserialized, Is.EqualTo(hash));
    }

    [TestCase("\"0x01\"", false, TestName = "Rejects_one_byte_hex_at_root")]
    [TestCase("\"" + Hex31Bytes + "\"", false, TestName = "Rejects_31_byte_hex_at_root")]
    [TestCase("\"" + Hex33Bytes + "\"", false, TestName = "Rejects_33_byte_hex_at_root")]
    [TestCase("null", false, TestName = "Rejects_null_token_at_root")]
    [TestCase("\"0x01\"", true, TestName = "Rejects_short_hex_in_nullable_property")]
    public void Rejects_invalid_input(string innerJson, bool wrapInProperty)
    {
        Action act = wrapInProperty
            ? () => JsonSerializer.Deserialize<Container>($$"""{"Hash":{{innerJson}}}""", _options)
            : () => JsonSerializer.Deserialize<ValueHash256>(innerJson, _options);

        Assert.That(act, Throws.TypeOf<JsonException>());
    }

    [TestCase("\"0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1\"", TestName = "Rejects_63_hex_odd_short")]
    [TestCase("\"0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2\"", TestName = "Rejects_65_hex_odd_long")]
    public void Rejects_odd_length_hex(string json) =>
        Assert.That(() => JsonSerializer.Deserialize<ValueHash256>(json, _options), Throws.InstanceOf<FormatException>());

    [TestCase("null", null, TestName = "Null_JSON_yields_null_property_without_invoking_converter")]
    [TestCase("\"" + ValidHex + "\"", ValidHex, TestName = "Valid_hex_populates_nullable_property")]
    public void Nullable_property_accepts(string innerJson, string? expectedHex)
    {
        Container? container = JsonSerializer.Deserialize<Container>($$"""{"Hash":{{innerJson}}}""", _options);
        Assert.That(container!.Hash?.ToString(true), Is.EqualTo(expectedHex));
    }

    private sealed class Container
    {
        public ValueHash256? Hash { get; set; }
    }
}
