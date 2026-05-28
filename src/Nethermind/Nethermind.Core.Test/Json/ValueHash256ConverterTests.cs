// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class ValueHash256ConverterTests
{
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

    [TestCase("\"0x01\"", TestName = "Rejects_one_byte_hex")]
    [TestCase("\"0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e\"", TestName = "Rejects_31_byte_hex")]
    [TestCase("\"0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20\"", TestName = "Rejects_33_byte_hex")]
    public void Rejects_non_32_byte_input(string json) =>
        Assert.That(() => JsonSerializer.Deserialize<ValueHash256>(json, _options),
            Throws.TypeOf<JsonException>(),
            "ValueHash256 must reject input whose byte length is not 32");

    // ValueHash256 is a non-nullable struct; a `null` token must throw, not silently become Hash256.Zero
    // via the unconstrained-generic T? -> default(T) override quirk on JsonConverter<T>.Read.
    [Test]
    public void Rejects_null_token() =>
        Assert.That(() => JsonSerializer.Deserialize<ValueHash256>("null", _options),
            Throws.TypeOf<JsonException>(),
            "null token must not deserialize to default(ValueHash256)");
}
